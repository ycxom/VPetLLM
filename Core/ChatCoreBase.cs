using System;
using System.Collections.Generic;
using System.IO;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers;
using System.Linq;
using System.Windows;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace VPetLLM.Core
{
    public abstract class ChatCoreBase : IChatCore
    {
        public abstract string Name { get; }
        protected List<Message> History { get; } = new List<Message>();
        protected Setting? Settings { get; }
        protected IMainWindow? MainWindow { get; }
        protected ActionProcessor? ActionProcessor { get; }
        public abstract Task<string> Chat(string prompt);

        protected string GetSystemMessage()
        {
            if (Settings == null || MainWindow == null || ActionProcessor == null) return "";

            var basePrompt = $"你的名字是{Settings.AiName}，我的名字是{Settings.UserName}。";
            var parts = new List<string> { basePrompt, Settings.Role };

            if (Settings.EnableState)
            {
                var core = MainWindow.Core;
                var status = $"当前状态: 等级({core.Save.Level}), 金钱({core.Save.Money:F2}), 体力({core.Save.Strength:F0}/{core.Save.StrengthMax:F0}), 健康({core.Save.Health:F0}), 心情({core.Save.Feeling:F0}/{core.Save.FeelingMax:F0}), 好感度({core.Save.Likability:F0}/{core.Save.LikabilityMax:F0}), 饱食度({core.Save.StrengthFood:F0}/{core.Save.StrengthMax:F0}), 口渴度({core.Save.StrengthDrink:F0}/{core.Save.StrengthMax:F0})";
                if (Settings.EnableTime)
                {
                    status += $", 当前时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                }
                parts.Add(status);
            }

            var stateInstructions = new List<string>();
            var bodyInstructions = new List<string>();

            foreach (var handler in ActionProcessor.Handlers)
            {
                if (handler.ActionType == ActionType.State)
                {
                    if ((handler.Keyword == "buy" && Settings.EnableBuy) || (handler.Keyword != "buy" && Settings.EnableAction))
                    {
                        stateInstructions.Add(handler.Keyword);
                    }
                }
                else if (handler.ActionType == ActionType.Body)
                {
                    if ((handler.Keyword == "action" && Settings.EnableActionExecution) || (handler.Keyword == "move" && Settings.EnableMove))
                    {
                        bodyInstructions.Add(handler.Keyword);
                    }
                }
            }

            if (Settings.EnableBuy)
            {
                var items = string.Join(",", MainWindow.Foods.Select(f => f.Name));
                parts.Add($"可购买物品列表:{items}。");
            }

            if (stateInstructions.Any())
            {
                parts.Add($"你可以通过特定指令影响我的状态，格式为[:state(指令(参数))]。可用状态指令: {string.Join(", ", stateInstructions)}。例如[:state(Happy(10))]。");
            }

            if (bodyInstructions.Any())
            {
                var bodyParts = new List<string>();
                if (bodyInstructions.Contains("action"))
                {
                    bodyParts.Add("Action指令可用于播放动画，可用动作: TouchHead, TouchBody, Sleep, Idel。例如[:body(Action(TouchHead))]");
                }
                if (bodyInstructions.Contains("move"))
                {
                    bodyParts.Add($"Move指令用于移动，需提供x,y坐标。例如[:body(Move(100,200))]。当前坐标:({MainWindow.Main.Core.Controller.GetWindowsDistanceLeft():F0},{MainWindow.Main.Core.Controller.GetWindowsDistanceUp():F0})，屏幕尺寸:({SystemParameters.PrimaryScreenWidth},{SystemParameters.PrimaryScreenHeight})。");
                }
                parts.Add($"你可以通过特定指令控制我的身体，格式为[:body(指令(参数))]。{string.Join(" ", bodyParts)}");
            }

            return string.Join("\n", parts);
        }

        // 统一的上下文模式状态，确保切换提供商时状态保持一致
        protected bool _keepContext = true;
        
        protected ChatCoreBase(Setting? settings, IMainWindow? mainWindow, ActionProcessor? actionProcessor)
        {
            Settings = settings;
            MainWindow = mainWindow;
            ActionProcessor = actionProcessor;
        }
        
        /// <summary>
        /// 设置是否保持上下文（统一管理，确保切换提供商时状态一致）
        /// </summary>
        public virtual void SetContextMode(bool keepContext)
        {
            _keepContext = keepContext;
        }
        
        /// <summary>
        /// 获取当前上下文模式状态
        /// </summary>
        public virtual bool GetContextMode()
        {
            return _keepContext;
        }

        public virtual void LoadHistory()
        {
            try
            {
                // 检查并处理历史文件迁移（从统一模式切换到独立模式时）
                CheckAndMigrateHistoryFile();
                
                var historyFile = GetHistoryFilePath();
                if (File.Exists(historyFile))
                {
                    var json = File.ReadAllText(historyFile);
                    var messages = JsonConvert.DeserializeObject<List<Message>>(json);
                    if (messages != null && messages.Count > 0)
                    {
                        // 先备份当前历史，防止加载失败时数据丢失
                        var backupHistory = new List<Message>(History);
                        try
                        {
                            // 先创建新列表，验证数据有效性后再替换
                            var newHistory = new List<Message>();
                            var normalizedMessages = ChatHistoryCompatibility.NormalizeRoles(messages);
                            newHistory.AddRange(normalizedMessages);
                            
                            // 验证通过后替换原历史记录
                            History.Clear();
                            History.AddRange(newHistory);
                            
                            Console.WriteLine($"成功加载 {messages.Count} 条历史消息（已标准化角色名称）");
                            
                            // 如果检测到角色不一致，自动修复历史文件
                            if (ChatHistoryCompatibility.HasRoleInconsistencies(messages))
                            {
                                ChatHistoryCompatibility.FixRoleInconsistencies(historyFile);
                            }
                        }
                        catch
                        {
                            // 如果加载失败，恢复备份数据
                            History.Clear();
                            History.AddRange(backupHistory);
                            throw;
                        }
                    }
                    else
                    {
                        Console.WriteLine("历史记录为空或格式不正确");
                    }
                }
                else
                {
                    Console.WriteLine($"历史文件不存在: {historyFile}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载聊天历史失败: {ex.Message}");
            }
        }

        public virtual void SaveHistory()
        {
            // 检查是否启用了聊天历史保存功能
            if (Settings != null && !Settings.EnableChatHistory)
            {
                Console.WriteLine("聊天历史保存功能已禁用，跳过保存");
                return;
            }
            
            try
            {
                var historyFile = GetHistoryFilePath();
                var directory = Path.GetDirectoryName(historyFile);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                // 直接保存当前历史列表
                var json = JsonConvert.SerializeObject(History, Formatting.Indented);
                
                // 使用临时文件确保写入完整性
                var tempFile = historyFile + ".tmp";
                File.WriteAllText(tempFile, json);
                
                // 原子替换
                if (File.Exists(historyFile))
                {
                    File.Replace(tempFile, historyFile, null);
                }
                else
                {
                    File.Move(tempFile, historyFile);
                }
                
                Console.WriteLine($"成功保存聊天历史到: {historyFile}");
                Console.WriteLine($"保存了{History.Count} 条消息");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存聊天历史失败: {ex.Message}");
            }
        }

        private string GetHistoryFilePath()
        {
            // 使用相对路径，智能识别当前Mod目录
            // 从当前程序集位置向上查找Mod目录结构
            var currentAssemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var currentDirectory = Path.GetDirectoryName(currentAssemblyPath);
            
            // 向上查找包含data文件夹的Mod目录
            var modDataPath = FindModDataDirectory(currentDirectory);
            
            // 根据配置决定是否按提供商分离聊天记录
            if (Settings != null && Settings.SeparateChatByProvider)
            {
                // 分离模式：每个提供商使用独立的文件
                return Path.Combine(modDataPath, $"chat_history_{Name.ToLower()}.json");
            }
            else
            {
                // 统一模式：所有提供商使用同一个文件
                return Path.Combine(modDataPath, "chat_history.json");
            }
        }
        
        /// <summary>
        /// 检查并处理历史文件迁移（从统一模式切换到独立模式时）
        /// </summary>
        private void CheckAndMigrateHistoryFile()
        {
            if (Settings == null || !Settings.SeparateChatByProvider) return;
            
            var unifiedFile = Path.Combine(FindModDataDirectory(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)), "chat_history.json");
            var providerFile = GetHistoryFilePath();
            
            // 如果统一模式文件存在，但提供商特定文件不存在
            if (File.Exists(unifiedFile) && !File.Exists(providerFile))
            {
                // 检查是否启用了自动迁移
                if (Settings.AutoMigrateChatHistory)
                {
                    try
                    {
                        File.Move(unifiedFile, providerFile);
                        Console.WriteLine($"已将聊天历史从统一文件迁移到提供商特定文件: {Path.GetFileName(providerFile)}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"迁移聊天历史文件失败: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"检测到统一聊天历史文件，但当前使用独立模式。");
                    Console.WriteLine($"统一文件: {Path.GetFileName(unifiedFile)}");
                    Console.WriteLine($"提供商文件: {Path.GetFileName(providerFile)}");
                    Console.WriteLine($"如需自动迁移，请在设置中启用\"自动迁移聊天历史\"选项。");
                }
            }
        }
        
        private string FindModDataDirectory(string startDirectory)
        {
            var directory = new DirectoryInfo(startDirectory);
            
            // 向上查找直到找到包含data文件夹的目录
            while (directory != null)
            {
                var dataPath = Path.Combine(directory.FullName, "data");
                if (Directory.Exists(dataPath))
                {
                    return dataPath;
                }
                
                // 如果当前目录有plugin子目录（Mod结构特征），则使用当前目录的data文件夹
                var pluginPath = Path.Combine(directory.FullName, "plugin");
                if (Directory.Exists(pluginPath))
                {
                    dataPath = Path.Combine(directory.FullName, "data");
                    return dataPath;
                }
                
                directory = directory.Parent;
            }
            
            // 如果找不到，使用当前目录下的data文件夹
            return Path.Combine(startDirectory, "data");
        }
        public virtual List<string> GetModels()
        {
            return new List<string>();
        }

        /// <summary>
        /// 清除聊天历史上下文
        /// </summary>
        public virtual void ClearContext()
        {
            History.RemoveAll(m => m.Role != "system"); // 保留系统角色消息
            // 不再自动保存，由调用者决定何时保存
        }

        /// <summary>
        /// 获取聊天历史用于编辑
        /// </summary>
        public virtual List<Message> GetHistoryForEditing()
        {
            return new List<Message>(History);
        }

        /// <summary>
        /// 更新聊天历史（用户编辑后）
        /// </summary>
        public virtual void UpdateHistory(List<Message> editedHistory)
        {
            History.Clear();
            History.AddRange(editedHistory);
            SaveHistory(); // 更新后立即保存
        }


        /// <summary>
        /// 设置聊天历史记录（用于切换提供商时恢复）
        /// </summary>
        public virtual void SetChatHistory(List<Message> history)
        {
            History.Clear();
            History.AddRange(history);
        }

        public List<Message> GetChatHistory()
        {
            return History;
        }
    }

    public class Message
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
        
        /// <summary>
        /// 标准化角色名称，确保不同提供商API的角色名称一致
        /// </summary>
        public string NormalizedRole 
        { 
            get 
            {
                return Role?.ToLower() switch
                {
                    "model" => "assistant",  // Gemini使用"model"，标准化为"assistant"
                    _ => Role ?? "user"
                };
            }
        }
    }
}

// 聊天历史兼容化处理器（移到ChatCoreBase类外部）
namespace VPetLLM.Core
{
    /// <summary>
    /// 聊天历史兼容化处理器
    /// </summary>
    public static class ChatHistoryCompatibility
    {
        /// <summary>
        /// 标准化消息角色，确保不同提供商API的角色名称一致
        /// </summary>
        public static List<Message> NormalizeRoles(List<Message> messages)
        {
            if (messages == null) return new List<Message>();
            
            return messages.Select(m => new Message 
            { 
                Role = m.Role?.ToLower() switch
                {
                    "model" => "assistant",  // Gemini使用"model"，标准化为"assistant"
                    _ => m.Role
                },
                Content = m.Content
            }).ToList();
        }
        
        /// <summary>
        /// 修复历史文件中的角色名称不一致问题
        /// </summary>
        public static void FixRoleInconsistencies(string historyFile)
        {
            if (!File.Exists(historyFile)) return;
            
            try
            {
                var json = File.ReadAllText(historyFile);
                var messages = JsonConvert.DeserializeObject<List<Message>>(json);
                
                if (messages != null && messages.Count > 0)
                {
                    var normalizedMessages = NormalizeRoles(messages);
                    
                    // 只有在确实需要修复时才重写文件
                    if (HasRoleInconsistencies(messages))
                    {
                        var normalizedJson = JsonConvert.SerializeObject(normalizedMessages, Formatting.Indented);
                        
                        // 使用临时文件确保写入完整性
                        var tempFile = historyFile + ".fix.tmp";
                        File.WriteAllText(tempFile, normalizedJson);
                        
                        // 原子替换
                        File.Replace(tempFile, historyFile, null);
                        
                        Console.WriteLine($"已修复聊天历史文件中的角色不一致问题: {Path.GetFileName(historyFile)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"修复角色不一致失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 检查是否存在角色名称不一致
        /// </summary>
        public static bool HasRoleInconsistencies(List<Message> messages)
        {
            return messages.Any(m => m.Role?.ToLower() == "model");
        }
    }
}
