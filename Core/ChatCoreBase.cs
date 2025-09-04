using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace VPetLLM.Core
{
    public abstract class ChatCoreBase : IChatCore
    {
        public abstract string Name { get; }
        protected List<Message> History { get; } = new List<Message>();
        public abstract Task<string> Chat(string prompt);

        public virtual void LoadHistory()
        {
            try
            {
                var historyFile = GetHistoryFilePath();
                if (File.Exists(historyFile))
                {
                    var json = File.ReadAllText(historyFile);
                    var historyData = JsonConvert.DeserializeObject<Dictionary<string, List<Message>>>(json);
                    if (historyData != null && historyData.TryGetValue(Name, out var messages) && messages != null)
                    {
                        // 先备份当前历史，防止加载失败时数据丢失
                        var backupHistory = new List<Message>(History);
                        try
                        {
                            History.Clear();
                            History.AddRange(messages);
                            Logger.Log($"成功加载 {messages.Count} 条历史消息");
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
                        Logger.Log($"未找到 {Name} 的历史记录或历史记录为空");
                    }
                }
                else
                {
                    Logger.Log($"历史文件不存在: {historyFile}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"加载聊天历史失败: {ex.Message}");
            }
        }

        public virtual void SaveHistory()
        {
            try
            {
                var historyFile = GetHistoryFilePath();
                var directory = Path.GetDirectoryName(historyFile);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                // 首先确保完整读取现有文件内容
                Dictionary<string, List<Message>> existingData = new Dictionary<string, List<Message>>();
                if (File.Exists(historyFile))
                {
                    try
                    {
                        string fileContent = File.ReadAllText(historyFile);
                        if (!string.IsNullOrWhiteSpace(fileContent))
                        {
                            var deserialized = JsonConvert.DeserializeObject<Dictionary<string, List<Message>>>(fileContent);
                            if (deserialized != null)
                            {
                                existingData = deserialized;
                            }
                        }
                    }
                    catch (Exception readEx)
                    {
                        Logger.Log($"读取现有历史文件失败，将创建新文件: {readEx.Message}");
                        // 继续使用空字典，不抛出异常
                    }
                }
                
                // 创建要保存的数据副本，确保不修改原始数据
                var dataToSave = new Dictionary<string, List<Message>>();
                
                // 复制所有现有的提供商数据（除了当前要更新的）
                foreach (var kvp in existingData)
                {
                    if (kvp.Key != Name && kvp.Value != null && kvp.Value.Count > 0)
                    {
                        dataToSave[kvp.Key] = new List<Message>(kvp.Value);
                    }
                }
                
                // 添加或更新当前提供商的数据
                if (History.Count > 0)
                {
                    dataToSave[Name] = new List<Message>(History);
                }
                else
                {
                    // 如果当前历史为空，不保存该提供商的数据
                    dataToSave.Remove(Name);
                }
                
                // 序列化并保存
                var json = JsonConvert.SerializeObject(dataToSave, Formatting.Indented);
                
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
                
                Logger.Log($"成功保存聊天历史到: {historyFile}");
                Logger.Log($"保存了 {History.Count} 条消息，总共 {dataToSave.Count} 个提供商");
            }
            catch (Exception ex)
            {
                Logger.Log($"保存聊天历史失败: {ex.Message}");
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
            
            return Path.Combine(modDataPath, "chat_history.json");
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
    }

    public class Message
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
    }
}