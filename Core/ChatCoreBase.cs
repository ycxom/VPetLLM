using System;
using System.Collections.Generic;
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
            // Load history from file
        }

        public virtual void SaveHistory()
        {
            // Save history to file
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
        }
    }

    public class Message
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
    }
}