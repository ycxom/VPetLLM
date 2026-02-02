namespace VPetLLM.Core.Abstractions.Interfaces.Plugin
{
    /// <summary>
    /// 处理生命周期插件接口
    /// 允许插件在 VPetLLM 处理消息的不同阶段介入
    /// </summary>
    public interface IProcessingLifecyclePlugin : IVPetLLMPlugin
    {
        /// <summary>
        /// 在 VPetLLM 开始处理用户输入之前调用
        /// 此时用户输入已接收，但尚未发送给 LLM
        /// </summary>
        /// <param name="userInput">用户输入内容</param>
        /// <returns>处理上下文（可用于后续阶段）</returns>
        Task<object?> OnProcessingStartAsync(string userInput);

        /// <summary>
        /// 在 LLM 开始返回响应时调用
        /// 此时 LLM 已开始生成响应（流式或非流式）
        /// </summary>
        /// <param name="context">之前阶段返回的上下文</param>
        Task OnResponseStartAsync(object? context);

        /// <summary>
        /// 在 VPetLLM 完成处理后调用
        /// 此时所有操作（包括插件调用）都已完成
        /// </summary>
        /// <param name="context">之前阶段返回的上下文</param>
        Task OnProcessingCompleteAsync(object? context);

        /// <summary>
        /// 在处理过程中发生错误时调用
        /// </summary>
        /// <param name="context">之前阶段返回的上下文</param>
        /// <param name="exception">发生的异常</param>
        Task OnProcessingErrorAsync(object? context, Exception exception);
    }
}
