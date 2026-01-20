using VPetLLM.Infrastructure.Services;

namespace VPetLLM.Infrastructure.Events
{
    /// <summary>
    /// 服务状态变更事件（用于事件总线）
    /// </summary>
    public class InfraServiceStatusChangedEvent
    {
        /// <summary>
        /// 服务类型
        /// </summary>
        public Type ServiceType { get; set; }

        /// <summary>
        /// 服务名称
        /// </summary>
        public string ServiceName { get; set; }

        /// <summary>
        /// 旧状态
        /// </summary>
        public InfraServiceStatus OldStatus { get; set; }

        /// <summary>
        /// 新状态
        /// </summary>
        public InfraServiceStatus NewStatus { get; set; }

        /// <summary>
        /// 状态
        /// </summary>
        public InfraServiceStatus Status { get; set; }

        /// <summary>
        /// 变更时间
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 变更原因
        /// </summary>
        public string Reason { get; set; }
    }

    /// <summary>
    /// 服务健康状态变更事件（用于事件总线）
    /// </summary>
    public class ServiceHealthChangedEvent
    {
        /// <summary>
        /// 服务类型
        /// </summary>
        public Type ServiceType { get; set; }

        /// <summary>
        /// 服务名称
        /// </summary>
        public string ServiceName { get; set; }

        /// <summary>
        /// 旧健康状态
        /// </summary>
        public ServiceHealth OldHealth { get; set; }

        /// <summary>
        /// 新健康状态
        /// </summary>
        public ServiceHealth NewHealth { get; set; }

        /// <summary>
        /// 健康状态
        /// </summary>
        public ServiceHealth Health { get; set; }

        /// <summary>
        /// 健康检查结果
        /// </summary>
        public ServiceHealthStatus HealthStatus { get; set; }

        /// <summary>
        /// 变更时间
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// 服务初始化事件
    /// </summary>
    public class ServiceInitializedEvent
    {
        /// <summary>
        /// 服务类型
        /// </summary>
        public Type ServiceType { get; set; }

        /// <summary>
        /// 服务名称
        /// </summary>
        public string ServiceName { get; set; }

        /// <summary>
        /// 初始化时间
        /// </summary>
        public DateTime InitializedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 初始化耗时（毫秒）
        /// </summary>
        public long DurationMs { get; set; }
    }

    /// <summary>
    /// 服务启动事件
    /// </summary>
    public class ServiceStartedEvent
    {
        /// <summary>
        /// 服务类型
        /// </summary>
        public Type ServiceType { get; set; }

        /// <summary>
        /// 服务名称
        /// </summary>
        public string ServiceName { get; set; }

        /// <summary>
        /// 启动时间
        /// </summary>
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 启动耗时（毫秒）
        /// </summary>
        public long DurationMs { get; set; }
    }

    /// <summary>
    /// 服务停止事件
    /// </summary>
    public class ServiceStoppedEvent
    {
        /// <summary>
        /// 服务类型
        /// </summary>
        public Type ServiceType { get; set; }

        /// <summary>
        /// 服务名称
        /// </summary>
        public string ServiceName { get; set; }

        /// <summary>
        /// 停止时间
        /// </summary>
        public DateTime StoppedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 停止耗时（毫秒）
        /// </summary>
        public long DurationMs { get; set; }
    }

    /// <summary>
    /// 服务错误事件
    /// </summary>
    public class ServiceErrorEvent
    {
        /// <summary>
        /// 服务类型
        /// </summary>
        public Type ServiceType { get; set; }

        /// <summary>
        /// 服务名称
        /// </summary>
        public string ServiceName { get; set; }

        /// <summary>
        /// 错误消息
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// 异常
        /// </summary>
        public Exception Exception { get; set; }

        /// <summary>
        /// 发生时间
        /// </summary>
        public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 错误阶段
        /// </summary>
        public string Stage { get; set; }
    }
}
