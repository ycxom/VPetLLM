namespace VPetLLM.Core.Integration.UnifiedTTS.Interfaces
{
    /// <summary>
    /// TTS 适配器接口
    /// 为不同的 TTS 实现提供统一的接口
    /// </summary>
    public interface ITTSAdapter
    {
        /// <summary>
        /// 适配器类型标识
        /// </summary>
        string AdapterType { get; }

        /// <summary>
        /// 处理 TTS 请求
        /// </summary>
        /// <param name="request">TTS 请求对象</param>
        /// <returns>TTS 响应结果</returns>
        Task<TTSResponse> ProcessAsync(ModelsTTSRequest request);

        /// <summary>
        /// 检查适配器是否可用
        /// </summary>
        /// <returns>是否可用</returns>
        Task<bool> IsAvailableAsync();

        /// <summary>
        /// 初始化适配器
        /// </summary>
        /// <param name="configuration">配置信息</param>
        /// <returns>是否初始化成功</returns>
        Task<bool> InitializeAsync(object configuration);

        /// <summary>
        /// 清理资源
        /// </summary>
        /// <returns>清理任务</returns>
        Task CleanupAsync();

        /// <summary>
        /// 获取适配器健康状态
        /// </summary>
        /// <returns>健康状态信息</returns>
        Task<AdapterHealthStatus> GetHealthStatusAsync();
    }

    /// <summary>
    /// 适配器健康状态
    /// </summary>
    public class AdapterHealthStatus
    {
        /// <summary>
        /// 是否健康
        /// </summary>
        public bool IsHealthy { get; set; }

        /// <summary>
        /// 状态消息
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// 最后检查时间
        /// </summary>
        public DateTime LastCheckTime { get; set; }

        /// <summary>
        /// 响应时间（毫秒）
        /// </summary>
        public int ResponseTimeMs { get; set; }

        public AdapterHealthStatus()
        {
            LastCheckTime = DateTime.UtcNow;
        }
    }
}