namespace VPetLLM.Core.Abstractions.Interfaces
{
    public interface IChatCore
    {
        string Name { get; }
        HistoryManager HistoryManager { get; }
        RecordManager RecordManager { get; }
        SkillManager SkillManager { get; }
        Task<string> Chat(string prompt);

        /// <summary>
        /// 发送对话。
        /// </summary>
        /// <param name="prompt">提示词</param>
        /// <param name="isRetry">
        /// 重试递归守卫：true 表示「本次调用本身就是一次重试」，失败时不要再自动重试。
        /// 目前只有 Free 渠道真正用到（5xx / 网络异常时自动重试一次），
        /// OpenAI 仅在故障转移里透传，Gemini / LMStudio / Ollama 未使用。
        /// ResultAggregator 回灌时传 true —— 工具回执失败就算了，不值得再打一次。
        /// 注意：它与「函数调用」无关，早期曾被命名为 isFunctionCall。
        /// </param>
        Task<string> Chat(string prompt, bool isRetry);
        /// <summary>
        /// 生成总结。失败时抛 <see cref="VPetLLM.Infrastructure.Exceptions.SummarizeFailedException"/>，
        /// 异常消息是可直接展示的文案。
        /// </summary>
        /// <remarks>
        /// 实现绝不能把错误文案当返回值：调用方无法与真总结区分，OverflowManager 曾因此
        /// 把一行报错当成 2151 条消息的总结提交，并推进检查点把原文永久丢掉。
        /// 成功时允许返回空串，调用方按「无内容」处理。
        /// </remarks>
        Task<string> Summarize(string systemPrompt, string userContent);

        /// <summary>
        /// 对单条文本取 L2 归一化向量，供插件做向量索引/检索。
        /// 向量化未启用或后端不可用时返回 null。
        /// </summary>
        Task<float[]?> EmbedTextAsync(string text);

        /// <summary>
        /// 发送带图像的多模态消息
        /// </summary>
        /// <param name="prompt">文本提示</param>
        /// <param name="imageData">图像数据</param>
        /// <returns>响应内容</returns>
        Task<string> ChatWithImage(string prompt, byte[] imageData);

        /// <summary>
        /// 发送带多张图像的多模态消息（一次请求内一并送出）
        /// </summary>
        /// <param name="prompt">文本提示</param>
        /// <param name="images">图像数据列表，按顺序附在提示词之后</param>
        /// <returns>响应内容</returns>
        Task<string> ChatWithImages(string prompt, IReadOnlyList<byte[]> images);

        /// <summary>
        /// 最近一次调用是否失败。错误文本经 ReportFailure 送出（优先走错误通道）、
        /// 正常回复经 ResponseHandler 送出，随后一律 return ""，
        /// 调用方只能靠这个标志位区分两者。
        /// </summary>
        bool LastCallFailed { get; }

        /// <summary>
        /// 给历史中本轮的助手回复补上"被用户中断"标记，供模型下一轮自我纠正。
        /// 回复尚未入库时不做任何事（保存时会自动带上标记）。
        /// </summary>
        void MarkLastResponseInterrupted();

        void SetResponseHandler(Action<string> handler);

        /// <summary>
        /// 挂错误通道：调用失败的错误文本走这里，与模型回复管线分离
        /// ——否则错误会被当回复拆成 say 命令送去 TTS。未挂时错误退回 ResponseHandler。
        /// </summary>
        void SetErrorHandler(Action<string> handler);
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
