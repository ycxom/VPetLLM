namespace VPetLLM.Utils.System
{
    /// <summary>
    /// 运行时上下文，用于标记当前是否处于单条 AI 回复的动作处理阶段
    /// </summary>
    public static class ExecutionContext
    {
        /// <summary>
        /// 当前消息处理会话 Id。非空表示正在处理同一条 AI 回复的动作。
        /// </summary>
        public static AsyncLocal<Guid?> CurrentMessageId { get; } = new AsyncLocal<Guid?>();
    }
}