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
        public HistoryManager HistoryManager { get; }
        protected Setting? Settings { get; }
        protected IMainWindow? MainWindow { get; }
        protected ActionProcessor? ActionProcessor { get; }
        protected SystemMessageProvider SystemMessageProvider { get; }
        protected Action<string> ResponseHandler;
    public abstract Task<string> Chat(string prompt);
    public abstract Task<string> Chat(string prompt, bool isFunctionCall);
    public abstract Task<string> Summarize(string text);

        protected string GetSystemMessage()
        {
            return SystemMessageProvider.GetSystemMessage();
        }

        // 统一的上下文模式状态，确保切换提供商时状态保持一致
        protected bool _keepContext = true;
        
      protected ChatCoreBase(Setting? settings, IMainWindow? mainWindow, ActionProcessor? actionProcessor)
      {
          Settings = settings;
          MainWindow = mainWindow;
          ActionProcessor = actionProcessor;
          HistoryManager = new HistoryManager(settings, Name, this);
          SystemMessageProvider = new SystemMessageProvider(settings, mainWindow, actionProcessor);
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

        public virtual List<string> GetModels()
        {
            return new List<string>();
        }

        /// <summary>
        /// 清除聊天历史上下文
        /// </summary>
       public virtual void ClearContext()
       {
           HistoryManager.ClearHistory();
       }

        /// <summary>
        /// 获取聊天历史用于编辑
        /// </summary>
       public virtual List<Message> GetHistoryForEditing()
       {
           return HistoryManager.GetHistory();
       }

       public List<Message> GetChatHistory()
       {
           return HistoryManager.GetHistory();
       }

        /// <summary>
        /// 更新聊天历史（用户编辑后）
        /// </summary>
       public virtual void UpdateHistory(List<Message> editedHistory)
       {
           HistoryManager.GetHistory().Clear();
           HistoryManager.GetHistory().AddRange(editedHistory);
           HistoryManager.SaveHistory();
       }


        /// <summary>
        /// 设置聊天历史记录（用于切换提供商时恢复）
        /// </summary>
       public virtual void SetChatHistory(List<Message> history)
       {
           HistoryManager.GetHistory().Clear();
           HistoryManager.GetHistory().AddRange(history);
       }


      public void SaveHistory()
      {
          HistoryManager.SaveHistory();
      }

      public void LoadHistory()
      {
          HistoryManager.LoadHistory();
      }
        public void SetResponseHandler(Action<string> handler)
        {
            ResponseHandler = handler;
        }

        public virtual void AddPlugin(IVPetLLMPlugin plugin)
        {
            SystemMessageProvider.AddPlugin(plugin);
        }

        public virtual void RemovePlugin(IVPetLLMPlugin plugin)
        {
            SystemMessageProvider.RemovePlugin(plugin);
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
        public string DisplayContent
        {
            get
            {
                if (Role == "assistant")
                {
                    var match = System.Text.RegularExpressions.Regex.Match(Content ?? "", @"(?<=say\("")(?:[^""\\]|\\.)*(?=""\))");
                    if (match.Success)
                    {
                        return match.Value;
                    }
                }
                return Content ?? "";
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
