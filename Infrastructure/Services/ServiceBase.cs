using VPetLLM.Infrastructure.Events;
using VPetLLM.Infrastructure.Logging;

namespace VPetLLM.Infrastructure.Services
{
    /// <summary>
    /// 服务基类，提供通用的服务实现
    /// </summary>
    public abstract class ServiceBase : IService
    {
        protected readonly IStructuredLogger _logger;
        protected readonly IEventBus _eventBus;
        private bool _disposed = false;
        private InfraServiceStatus _status = InfraServiceStatus.NotInitialized;
        private readonly object _statusLock = new object();

        /// <summary>
        /// 服务名称
        /// </summary>
        public abstract string ServiceName { get; }

        /// <summary>
        /// 服务状态
        /// </summary>
        public InfraServiceStatus Status
        {
            get
            {
                lock (_statusLock)
                {
                    return _status;
                }
            }
            protected set
            {
                ServiceStatus oldStatus;
                lock (_statusLock)
                {
                    oldStatus = _status;
                    _status = value;
                }

                if (oldStatus != value)
                {
                    OnStatusChanged(new ServiceStatusChangedEventArgs(ServiceName, oldStatus, value));
                }
            }
        }

        /// <summary>
        /// 服务版本
        /// </summary>
        public virtual Version Version => new Version(1, 0, 0);

        /// <summary>
        /// 服务依赖项
        /// </summary>
        public virtual IEnumerable<Type> Dependencies => Array.Empty<Type>();

        /// <summary>
        /// 服务状态变更事件
        /// </summary>
        public event EventHandler<ServiceStatusChangedEventArgs> StatusChanged;

        protected ServiceBase(IStructuredLogger logger = null, IEventBus eventBus = null)
        {
            _logger = logger;
            _eventBus = eventBus;
        }

        // Logging helper methods
        protected void LogInformation(string message, object context = null)
        {
            _logger?.LogInformation(message, context);
        }

        protected void LogDebug(string message, object context = null)
        {
            _logger?.LogDebug(message, context);
        }

        protected void LogWarning(string message, object context = null)
        {
            _logger?.LogWarning(message, context);
        }

        protected void LogError(string message, Exception ex = null, object context = null)
        {
            _logger?.LogError(ex, message, context);
        }

        protected IStructuredLogger Logger => _logger;

        // Event publishing helper
        protected async Task PublishEventAsync<T>(T eventData) where T : class
        {
            if (_eventBus is not null)
            {
                await _eventBus.PublishAsync(eventData);
            }
        }

        /// <summary>
        /// 初始化服务
        /// </summary>
        public virtual async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (Status != InfraServiceStatus.NotInitialized)
            {
                throw new InvalidOperationException($"Service {ServiceName} is already initialized");
            }

            Status = InfraServiceStatus.Initializing;

            try
            {
                await OnInitializeAsync(cancellationToken);
                Status = InfraServiceStatus.Initialized;
                _logger?.LogInformation("Service initialized", new { ServiceName });
            }
            catch (Exception ex)
            {
                Status = InfraServiceStatus.Error;
                _logger?.LogError(ex, "Failed to initialize service", new { ServiceName });
                throw;
            }
        }

        /// <summary>
        /// 启动服务
        /// </summary>
        public virtual async Task StartAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (Status != InfraServiceStatus.Initialized && Status != InfraServiceStatus.Stopped)
            {
                throw new InvalidOperationException($"Service {ServiceName} must be initialized before starting");
            }

            Status = InfraServiceStatus.Starting;

            try
            {
                await OnStartAsync(cancellationToken);
                Status = InfraServiceStatus.Running;
                _logger?.LogInformation("Service started", new { ServiceName });
            }
            catch (Exception ex)
            {
                Status = InfraServiceStatus.Error;
                _logger?.LogError(ex, "Failed to start service", new { ServiceName });
                throw;
            }
        }

        /// <summary>
        /// 停止服务
        /// </summary>
        public virtual async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed || Status == InfraServiceStatus.Stopped)
                return;

            Status = InfraServiceStatus.Stopping;

            try
            {
                await OnStopAsync(cancellationToken);
                Status = InfraServiceStatus.Stopped;
                _logger?.LogInformation("Service stopped", new { ServiceName });
            }
            catch (Exception ex)
            {
                Status = InfraServiceStatus.Error;
                _logger?.LogError(ex, "Failed to stop service", new { ServiceName });
                throw;
            }
        }

        /// <summary>
        /// 检查服务健康状态
        /// </summary>
        public virtual async Task<ServiceHealthStatus> CheckHealthAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Call the simple health check first
                await OnHealthCheckAsync(CancellationToken.None);

                // Then get detailed status
                var healthStatus = await OnCheckHealthAsync();
                stopwatch.Stop();

                healthStatus.ServiceName = ServiceName;
                healthStatus.ResponseTimeMs = stopwatch.ElapsedMilliseconds;

                return healthStatus;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                return new ServiceHealthStatus
                {
                    ServiceName = ServiceName,
                    Status = HealthStatus.Unhealthy,
                    Description = "Health check failed",
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    ResponseTimeMs = stopwatch.ElapsedMilliseconds
                };
            }
        }

        /// <summary>
        /// 子类实现的初始化逻辑
        /// </summary>
        protected abstract Task OnInitializeAsync(CancellationToken cancellationToken);

        /// <summary>
        /// 子类实现的启动逻辑
        /// </summary>
        protected abstract Task OnStartAsync(CancellationToken cancellationToken);

        /// <summary>
        /// 子类实现的停止逻辑
        /// </summary>
        protected abstract Task OnStopAsync(CancellationToken cancellationToken);

        /// <summary>
        /// 子类实现的健康检查逻辑
        /// </summary>
        protected virtual Task OnHealthCheckAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// 子类实现的健康检查逻辑（返回详细状态）
        /// </summary>
        protected virtual Task<ServiceHealthStatus> OnCheckHealthAsync()
        {
            return Task.FromResult(new ServiceHealthStatus
            {
                Status = Status == InfraServiceStatus.Running ? HealthStatus.Healthy : HealthStatus.Unhealthy,
                Description = $"Service is {Status}"
            });
        }

        /// <summary>
        /// 触发状态变更事件
        /// </summary>
        protected virtual void OnStatusChanged(ServiceStatusChangedEventArgs e)
        {
            StatusChanged?.Invoke(this, e);
        }

        /// <summary>
        /// 检查是否已释放
        /// </summary>
        protected void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(ServiceName);
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public virtual void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Status = InfraServiceStatus.Disposed;

            try
            {
                if (Status == InfraServiceStatus.Running)
                {
                    StopAsync().Wait(TimeSpan.FromSeconds(30));
                }
            }
            catch
            {
                // 忽略停止时的异常
            }

            OnDispose();
        }

        /// <summary>
        /// 子类实现的释放逻辑
        /// </summary>
        protected virtual void OnDispose()
        {
        }
    }

    /// <summary>
    /// 带配置的服务基类
    /// </summary>
    /// <typeparam name="TConfig">配置类型</typeparam>
    public abstract class ServiceBase<TConfig> : ServiceBase where TConfig : class
    {
        protected TConfig _configuration;

        /// <summary>
        /// 服务配置
        /// </summary>
        protected TConfig Configuration => _configuration;

        protected ServiceBase(TConfig configuration, IStructuredLogger logger = null, IEventBus eventBus = null)
            : base(logger, eventBus)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        /// <summary>
        /// 更新配置
        /// </summary>
        public virtual async Task UpdateConfigurationAsync(TConfig configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            await OnConfigurationChangedAsync();
        }

        /// <summary>
        /// 配置变更时调用
        /// </summary>
        protected virtual Task OnConfigurationChangedAsync()
        {
            return Task.CompletedTask;
        }
    }
}
