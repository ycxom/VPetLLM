namespace VPetLLM.Infrastructure.Services
{
    /// <summary>
    /// 服务管理器接口
    /// </summary>
    public interface IServiceManager : IDisposable
    {
        /// <summary>
        /// 启动所有服务
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 停止所有服务
        /// </summary>
        Task StopAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取服务（异步）
        /// </summary>
        Task<T> GetServiceAsync<T>() where T : class, IService;

        /// <summary>
        /// 启动指定服务
        /// </summary>
        Task StartServiceAsync<T>() where T : class, IService;

        /// <summary>
        /// 停止指定服务
        /// </summary>
        Task StopServiceAsync<T>() where T : class, IService;

        /// <summary>
        /// 获取服务状态（泛型）
        /// </summary>
        InfraServiceStatus GetServiceStatus<T>() where T : class, IService;

        /// <summary>
        /// 获取服务健康状态（泛型）
        /// </summary>
        ServiceHealth GetServiceHealth<T>() where T : class, IService;

        /// <summary>
        /// 停止所有服务（异步）
        /// </summary>
        Task StopAllServicesAsync();

        /// <summary>
        /// 检查指定服务的健康状态
        /// </summary>
        Task<ServiceHealthStatus> CheckHealthAsync(string serviceName);

        /// <summary>
        /// 检查所有服务的健康状态
        /// </summary>
        Task<Dictionary<string, ServiceHealthStatus>> CheckAllHealthAsync();

        /// <summary>
        /// 注册服务
        /// </summary>
        void RegisterService<T>(T service) where T : class, IService;

        /// <summary>
        /// 注册服务（带优先级）
        /// </summary>
        void RegisterService<T>(T service, int priority) where T : class, IService;

        /// <summary>
        /// 获取服务
        /// </summary>
        T GetService<T>() where T : class, IService;

        /// <summary>
        /// 尝试获取服务
        /// </summary>
        bool TryGetService<T>(out T service) where T : class, IService;

        /// <summary>
        /// 获取服务（按名称）
        /// </summary>
        IService GetService(string serviceName);

        /// <summary>
        /// 获取所有服务
        /// </summary>
        IEnumerable<IService> GetAllServices();

        /// <summary>
        /// 获取服务状态
        /// </summary>
        InfraServiceStatus GetServiceStatus(string serviceName);

        /// <summary>
        /// 重启服务
        /// </summary>
        Task RestartServiceAsync(string serviceName, CancellationToken cancellationToken = default);

        /// <summary>
        /// 启用自动重启
        /// </summary>
        void EnableAutoRestart(string serviceName, TimeSpan checkInterval, int maxRetries = 3);

        /// <summary>
        /// 禁用自动重启
        /// </summary>
        void DisableAutoRestart(string serviceName);

        /// <summary>
        /// 服务状态变更事件
        /// </summary>
        event EventHandler<ServiceStatusChangedEventArgs> ServiceStatusChanged;

        /// <summary>
        /// 服务健康状态变更事件
        /// </summary>
        event EventHandler<ServiceHealthChangedEventArgs> ServiceHealthChanged;
    }

    /// <summary>
    /// 服务健康状态变更事件参数
    /// </summary>
    public class ServiceHealthChangedEventArgs : EventArgs
    {
        /// <summary>
        /// 服务名称
        /// </summary>
        public string ServiceName { get; }

        /// <summary>
        /// 服务类型
        /// </summary>
        public Type ServiceType { get; }

        /// <summary>
        /// 旧健康状态
        /// </summary>
        public HealthStatus OldStatus { get; }

        /// <summary>
        /// 新健康状态
        /// </summary>
        public HealthStatus NewStatus { get; }

        /// <summary>
        /// 健康状态
        /// </summary>
        public ServiceHealth Health { get; }

        /// <summary>
        /// 健康检查结果
        /// </summary>
        public ServiceHealthStatus HealthStatus { get; }

        /// <summary>
        /// 变更时间
        /// </summary>
        public DateTime Timestamp { get; }

        public ServiceHealthChangedEventArgs(string serviceName, HealthStatus oldStatus, HealthStatus newStatus, ServiceHealthStatus healthStatus)
        {
            ServiceName = serviceName;
            ServiceType = Type.GetType(serviceName) ?? typeof(object);
            OldStatus = oldStatus;
            NewStatus = newStatus;
            Health = (ServiceHealth)newStatus;
            HealthStatus = healthStatus;
            Timestamp = DateTime.UtcNow;
        }

        public ServiceHealthChangedEventArgs(Type serviceType, ServiceHealth health, IService service)
        {
            ServiceName = serviceType.Name;
            ServiceType = serviceType;
            Health = health;
            NewStatus = (HealthStatus)health;
            HealthStatus = new ServiceHealthStatus { ServiceName = serviceType.Name, Status = (HealthStatus)health };
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 服务注册信息
    /// </summary>
    public class ServiceRegistrationInfo
    {
        /// <summary>
        /// 服务实例
        /// </summary>
        public IService Service { get; }

        /// <summary>
        /// 注册优先级
        /// </summary>
        public int Priority { get; }

        /// <summary>
        /// 注册时间
        /// </summary>
        public DateTime RegisterTime { get; }

        /// <summary>
        /// 是否启用自动重启
        /// </summary>
        public bool AutoRestartEnabled { get; set; }

        /// <summary>
        /// 自动重启检查间隔
        /// </summary>
        public TimeSpan AutoRestartCheckInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// 最大重试次数
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// 当前重试次数
        /// </summary>
        public int CurrentRetries { get; set; }

        /// <summary>
        /// 最后健康检查时间
        /// </summary>
        public DateTime LastHealthCheck { get; set; }

        /// <summary>
        /// 最后健康状态
        /// </summary>
        public HealthStatus LastHealthStatus { get; set; } = HealthStatus.Unknown;

        public ServiceRegistrationInfo(IService service, int priority)
        {
            Service = service ?? throw new ArgumentNullException(nameof(service));
            Priority = priority;
            RegisterTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 服务启动配置
    /// </summary>
    public class ServiceStartupConfiguration
    {
        /// <summary>
        /// 启动超时时间
        /// </summary>
        public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// 停止超时时间
        /// </summary>
        public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// 健康检查间隔
        /// </summary>
        public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// 是否并行启动服务
        /// </summary>
        public bool ParallelStartup { get; set; } = false;

        /// <summary>
        /// 是否在启动失败时继续启动其他服务
        /// </summary>
        public bool ContinueOnStartupFailure { get; set; } = true;

        /// <summary>
        /// 是否启用依赖关系检查
        /// </summary>
        public bool EnableDependencyCheck { get; set; } = true;
    }
}