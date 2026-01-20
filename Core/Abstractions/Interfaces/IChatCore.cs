namespace VPetLLM.Core.Abstractions.Interfaces
{
    public interface IChatCore
    {
        string Name { get; }
        HistoryManager HistoryManager { get; }
        RecordManager RecordManager { get; }
        Task<string> Chat(string prompt);
        Task<string> Chat(string prompt, bool isFunctionCall);

        /// <summary>
        /// 发送带图像的多模态消息
        /// </summary>
        /// <param name="prompt">文本提示</param>
        /// <param name="imageData">图像数据</param>
        /// <returns>响应内容</returns>
        Task<string> ChatWithImage(string prompt, byte[] imageData);

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
        List<Message> GetHistoryForEditing();

        /// <summary>
        /// 更新聊天历史（用户编辑后）
        /// </summary>
        void UpdateHistory(List<Message> editedHistory);

        /// <summary>
        /// 获取当前聊天历史记录（用于切换提供商时保存）
        /// </summary>
        List<Message> GetChatHistory();

        /// <summary>
        /// 获取当前聊天历史的Token数量估算
        /// </summary>
        int GetCurrentTokenCount();

        /// <summary>
        /// 设置聊天历史记录（用于切换提供商时恢复）
        /// </summary>
        void SetChatHistory(List<Message> history);
        void AddPlugin(IVPetLLMPlugin plugin);
        void RemovePlugin(IVPetLLMPlugin plugin);
        System.Net.IWebProxy GetProxy(string? requestType = null);
    }
}
