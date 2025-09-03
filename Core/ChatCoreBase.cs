using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
                    var messages = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Message>>(json);
                    if (messages != null)
                    {
                        History.Clear();
                        History.AddRange(messages);
                    }
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
                
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(History, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(historyFile, json);
            }
            catch (Exception ex)
            {
                Logger.Log($"保存聊天历史失败: {ex.Message}");
            }
        }

        private string GetHistoryFilePath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "VPetLLM", "chat_history.json");
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
            SaveHistory(); // 清除后立即保存
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