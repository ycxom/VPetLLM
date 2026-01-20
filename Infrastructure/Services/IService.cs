namespace VPetLLM.Infrastructure.Services
{
    /// <summary>
    /// 服务基础接口
    /// </summary>
    public interface IService : IDisposable
    {
        /// <summary>
        /// 服务名称
        /// </summary>
        string ServiceName { get; }

        /// <summary>
        /// 服务状态
        /// </summary>
        InfraServiceStatus Status { get; }

        /// <summary>
        /// 服务版本
        /// </summary>
        Version Version { get; }

        /// <summary>
        /// 服务依赖项
        /// </summary>
        IEnumerable<Type> Dependencies { get; }

        /// <summary>
        /// 初始化服务
        /// </summary>
        Task InitializeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 启动服务
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 停止服务
        /// </summary>
        Task StopAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 检查服务健康状态
        /// </summary>
        Task<ServiceHealthStatus> CheckHealthAsync();

        /// <summary>
        /// 服务状态变更事件
        /// </summary>
        event EventHandler<ServiceStatusChangedEventArgs> StatusChanged;
    }

    /// <summary>
    /// 服务状态枚举
    /// </summary>
    public enum ServiceStatus
    {
        /// <summary>
        /// 未初始化
        /// </summary>
        NotInitialized,

        /// <summary>
        /// 已创建
        /// </summary>
        Created,

        /// <summary>
        /// 初始化中
        /// </summary>
        Initializing,

        /// <summary>
        /// 已初始化
        /// </summary>
        Initialized,

        /// <summary>
        /// 启动中
        /// </summary>
        Starting,

        /// <summary>
        /// 运行中
        /// </summary>
        Running,

        /// <summary>
        /// 停止中
        /// </summary>
        Stopping,

        /// <summary>
        /// 已停止
        /// </summary>
        Stopped,

        /// <summary>
        /// 错误状态
        /// </summary>
        Error,

        /// <summary>
        /// 失败状态
        /// </summary>
        Failed,

        /// <summary>
        /// 未注册
        /// </summary>
        NotRegistered,

        /// <summary>
        /// 已释放
        /// </summary>
        Disposed
    }

    /// <summary>
    /// 服务健康状态
    /// </summary>
    public class ServiceHealthStatus
    {
        /// <summary>
        /// 服务名称
        /// </summary>
        public string ServiceName { get; set; }

        /// <summary>
        /// 健康状态
        /// </summary>
        public HealthStatus Status { get; set; }

        /// <summary>
        /// 状态描述
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// 健康指标
        /// </summary>
        public Dictionary<string, object> Metrics { get; set; } = new();

        /// <summary>
        /// 检查时间
        /// </summary>
        public DateTime CheckTime { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 响应时间（毫秒）
        /// </summary>
        public long ResponseTimeMs { get; set; }

        /// <summary>
        /// 错误信息
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// 异常详情
        /// </summary>
        public Exception Exception { get; set; }
    }

    /// <summary>
    /// 健康状态枚举
    /// </summary>
    public enum HealthStatus
    {
        /// <summary>
        /// 未知状态
        /// </summary>
        Unknown,

        /// <summary>
        /// 健康
        /// </summary>
        Healthy,

        /// <summary>
        /// 降级
        /// </summary>
        Degraded,

        /// <summary>
        /// 不健康
        /// </summary>
        Unhealthy
    }

    /// <summary>
    /// 服务健康状态枚举（ServiceManager使用）
    /// </summary>
    public enum ServiceHealth
    {
        /// <summary>
        /// 健康
        /// </summary>
        Healthy,

        /// <summary>
        /// 降级
        /// </summary>
        Degraded,

        /// <summary>
        /// 不健康
        /// </summary>
        Unhealthy,

        /// <summary>
        /// 未知
        /// </summary>
        Unknown
    }

    /// <summary>
    /// 服务状态变更事件参数
    /// </summary>
    public class ServiceStatusChangedEventArgs : EventArgs
    {
        /// <summary>
        /// 服务名称
        /// </summary>
        public string ServiceName { get; }

        /// <summary>
        /// 旧状态
        /// </summary>
        public InfraServiceStatus OldStatus { get; }

        /// <summary>
        /// 新状态
        /// </summary>
        public InfraServiceStatus NewStatus { get; }

        /// <summary>
        /// 变更时间
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// 变更原因
        /// </summary>
        public string Reason { get; }

        public ServiceStatusChangedEventArgs(string serviceName, InfraServiceStatus oldStatus, InfraServiceStatus newStatus, string reason = null)
        {
            ServiceName = serviceName;
            OldStatus = oldStatus;
            NewStatus = newStatus;
            Timestamp = DateTime.UtcNow;
            Reason = reason;
        }
    }
}