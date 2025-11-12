namespace VPetLLM
{
    public interface IChatCore
    {
        string Name { get; }
        Core.HistoryManager HistoryManager { get; }
        Core.RecordManager RecordManager { get; }
        Task<string> Chat(string prompt);
        Task<string> Chat(string prompt, bool isFunctionCall);
        void SetResponseHandler(Action<string> handler);
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

        /// <summary>
        /// 获取当前聊天历史记录（用于切换提供商时保存）
        /// </summary>
        List<Core.Message> GetChatHistory();

        /// <summary>
        /// 获取当前聊天历史的Token数量估算
        /// </summary>
        int GetCurrentTokenCount();

        /// <summary>
        /// 设置聊天历史记录（用于切换提供商时恢复）
        /// </summary>
        void SetChatHistory(List<Core.Message> history);
        void AddPlugin(Core.IVPetLLMPlugin plugin);
        void RemovePlugin(Core.IVPetLLMPlugin plugin);
        System.Net.IWebProxy GetProxy(string? requestType = null);
    }
}