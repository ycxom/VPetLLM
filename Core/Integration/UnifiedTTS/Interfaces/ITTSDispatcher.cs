namespace VPetLLM.Core.Integration.UnifiedTTS.Interfaces
{
    /// <summary>
    /// 统一的 TTS 调度器接口
    /// 提供统一的 TTS 请求处理入口，支持外部 TTS 和内置 TTS 的双路径处理
    /// </summary>
    public interface ITTSDispatcher
    {
        /// <summary>
        /// 处理 TTS 请求
        /// </summary>
        /// <param name="request">TTS 请求对象</param>
        /// <returns>TTS 响应结果</returns>
        Task<TTSResponse> ProcessRequestAsync(ModelsTTSRequest request);

        /// <summary>
        /// 获取服务状态
        /// </summary>
        /// <returns>当前服务状态信息</returns>
        Task<ModelsServiceStatus> GetServiceStatusAsync();

        /// <summary>
        /// 更新配置
        /// </summary>
        /// <param name="config">新的配置对象</param>
        /// <returns>是否更新成功</returns>
        Task<bool> UpdateConfigurationAsync(TTSConfiguration config);

        /// <summary>
        /// 验证请求参数
        /// </summary>
        /// <param name="request">要验证的请求</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateRequest(ModelsTTSRequest request);

        /// <summary>
        /// 取消当前处理中的 TTS 请求
        /// </summary>
        void CancelCurrentRequest();

        /// <summary>
        /// 重置调度器状态
        /// </summary>
        void Reset();

        /// <summary>
        /// 检查调度器是否处于活跃状态
        /// </summary>
        /// <returns>是否活跃</returns>
        bool IsActive();

        /// <summary>
        /// 执行健康检查
        /// </summary>
        /// <returns>健康检查结果</returns>
        Task<HealthCheckResult> PerformHealthCheckAsync();

        /// <summary>
        /// 获取性能指标
        /// </summary>
        /// <returns>性能指标</returns>
        Task<PerformanceMetrics> GetPerformanceMetricsAsync();

        /// <summary>
        /// 获取活跃请求列表
        /// </summary>
        /// <returns>活跃请求列表</returns>
        Task<System.Collections.Generic.IEnumerable<ActiveRequestInfo>> GetActiveRequestsAsync();

        /// <summary>
        /// 获取资源使用情况
        /// </summary>
        /// <returns>资源使用情况</returns>
        Task<ResourceUsage> GetResourceUsageAsync();

        /// <summary>
        /// 重置统计信息
        /// </summary>
        /// <returns>重置任务</returns>
        Task ResetStatisticsAsync();
    }
}