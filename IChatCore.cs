using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VPetLLM
{
    public interface IChatCore
    {
        string Name { get; }
        Task<string> Chat(string prompt);
        void SaveHistory();
        void LoadHistory();
        List<string> GetModels();
        /// <summary>
        /// 清除聊天历史上下文
        /// </summary>
        void ClearContext();

        /// <summary>
        /// 获取聊天历史用于编辑
        /// </summary>
        List<Core.Message> GetHistoryForEditing();

        /// <summary>
        /// 更新聊天历史（用户编辑后）
        /// </summary>
        void UpdateHistory(List<Core.Message> editedHistory);
    }
}