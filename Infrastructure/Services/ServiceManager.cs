using System.Collections.Concurrent;
using VPetLLM.Infrastructure.DependencyInjection;
using VPetLLM.Infrastructure.Events;
using VPetLLM.Infrastructure.Exceptions;
using VPetLLM.Infrastructure.Logging;

namespace VPetLLM.Infrastructure.Services
{
    /// <summary>
    /// 服务管理器实�?
    /// </summary>
    public class ServiceManager : IServiceManager
    {
        private readonly IDependencyContainer _container;
        private readonly IEventBus _eventBus;
        private readonly IStructuredLogger _logger;
        private readonly ConcurrentDictionary<Type, IService> _services = new();
        private readonly ConcurrentDictionary<Type, ServiceMetadata> _serviceMetadata = new();
        private readonly Timer _healthCheckTimer;
        private readonly SemaphoreSlim _operationLock = new(1, 1);
        private bool _disposed = false;
        private bool _isShuttingDown = false;

        public event EventHandler<ServiceStatusChangedEventArgs> ServiceStatusChanged;
        public event EventHandler<ServiceHealthChangedEventArgs> ServiceHealthChanged;

        public ServiceManager(IDependencyContainer container, IEventBus eventBus, IStructuredLogger logger = null)
        {
            _container = container ?? throw new ArgumentNullException(nameof(container));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _logger = logger;

            // 启动健康检查定时器（每30秒检查一次）
            _healthCheckTimer = new Timer(PerformHealthChecks, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            _logger?.LogInformation("ServiceManager initialized");
        }

        public async Task<T> GetServiceAsync<T>() where T : class, IService
        {
            ThrowIfDisposed();

            if (_services.TryGetValue(typeof(T), out var existingService))
            {
                return (T)existingService;
            }

            await _operationLock.WaitAsync();
            try
            {
                // 双重检查锁�?
                if (_services.TryGetValue(typeof(T), out existingService))
                {
                    return (T)existingService;
                }

                // 创建服务实例
                var service = _container.Resolve<T>();
                if (service is null)
                {
                    throw new ServiceException($"Failed to resolve service of type {typeof(T).Name}", $"Failed to resolve service of type {typeof(T).Name}", null);
                }

                // 注册服务
                _services[typeof(T)] = service;
                _serviceMetadata[typeof(T)] = new ServiceMetadata
                {
                    ServiceType = typeof(T),
                    Status = InfraServiceStatus.Created,
                    Health = ServiceHealth.Unknown,
                    CreatedAt = DateTime.UtcNow,
                    LastHealthCheck = DateTime.UtcNow
                };

                _logger?.LogInformation("Service created", new { ServiceType = typeof(T).Name });
                OnServiceStatusChanged(new ServiceStatusChangedEventArgs(typeof(T).Name, InfraServiceStatus.NotInitialized, InfraServiceStatus.Created));

                return service;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        public async Task StartServiceAsync<T>() where T : class, IService
        {
            ThrowIfDisposed();

            var service = await GetServiceAsync<T>();
            var metadata = _serviceMetadata[typeof(T)];

            if (metadata.Status == InfraServiceStatus.Running)
            {
                _logger?.LogWarning("Service is already running", new { ServiceType = typeof(T).Name });
                return;
            }

            await _operationLock.WaitAsync();
            try
            {
                // 检查依赖服�?
                await StartDependentServicesAsync(typeof(T));

                // 启动服务
                await service.StartAsync();

                metadata.Status = InfraServiceStatus.Running;
                metadata.StartedAt = DateTime.UtcNow;
                metadata.Health = ServiceHealth.Healthy;
                metadata.LastHealthCheck = DateTime.UtcNow;

                _logger?.LogInformation("Service started successfully", new { ServiceType = typeof(T).Name });
                OnServiceStatusChanged(new ServiceStatusChangedEventArgs(typeof(T).Name, InfraServiceStatus.Starting, InfraServiceStatus.Running));
                OnServiceHealthChanged(new ServiceHealthChangedEventArgs(typeof(T), ServiceHealth.Healthy, service));
            }
            catch (Exception ex)
            {
                metadata.Status = InfraServiceStatus.Failed;
                metadata.Health = ServiceHealth.Unhealthy;
                metadata.LastError = ex;
                metadata.LastHealthCheck = DateTime.UtcNow;

                _logger?.LogError(ex, "Failed to start service", new { ServiceType = typeof(T).Name });
                OnServiceStatusChanged(new ServiceStatusChangedEventArgs(typeof(T).Name, InfraServiceStatus.Starting, InfraServiceStatus.Failed));
                OnServiceHealthChanged(new ServiceHealthChangedEventArgs(typeof(T), ServiceHealth.Unhealthy, service));

                throw new ServiceException(typeof(T).Name, $"Failed to start service {typeof(T).Name}", ex);
            }
            finally
            {
                _operationLock.Release();
            }
        }

        public async Task StopServiceAsync<T>() where T : class, IService
        {
            ThrowIfDisposed();

            if (!_services.TryGetValue(typeof(T), out var service))
            {
                _logger?.LogWarning("Service not found for stopping", new { ServiceType = typeof(T).Name });
                return;
            }

            var metadata = _serviceMetadata[typeof(T)];
            if (metadata.Status != InfraServiceStatus.Running)
            {
                _logger?.LogWarning("Service is not running", new { ServiceType = typeof(T).Name, Status = metadata.Status });
                return;
            }

            await _operationLock.WaitAsync();
            try
            {
                // 停止依赖于此服务的其他服�?
                await StopDependentServicesAsync(typeof(T));

                // 停止服务
                await service.StopAsync();

                metadata.Status = InfraServiceStatus.Stopped;
                metadata.StoppedAt = DateTime.UtcNow;
                metadata.Health = ServiceHealth.Unknown;

                _logger?.LogInformation("Service stopped successfully", new { ServiceType = typeof(T).Name });
                OnServiceStatusChanged(new ServiceStatusChangedEventArgs(typeof(T).Name, InfraServiceStatus.Running, InfraServiceStatus.Stopped));
            }
            catch (Exception ex)
            {
                metadata.Status = InfraServiceStatus.Failed;
                metadata.Health = ServiceHealth.Unhealthy;
                metadata.LastError = ex;

                _logger?.LogError(ex, "Failed to stop service", new { ServiceType = typeof(T).Name });
                OnServiceStatusChanged(new ServiceStatusChangedEventArgs(typeof(T).Name, InfraServiceStatus.Stopping, InfraServiceStatus.Failed));

                throw new ServiceException($"Failed to stop service {typeof(T).Name}", $"Failed to stop service {typeof(T).Name}", ex);
            }
            finally
            {
                _operationLock.Release();
            }
        }

        public async Task RestartServiceAsync<T>() where T : class, IService
        {
            ThrowIfDisposed();

            _logger?.LogInformation("Restarting service", new { ServiceType = typeof(T).Name });

            await StopServiceAsync<T>();
            await Task.Delay(1000); // 等待1秒确保服务完全停�?
            await StartServiceAsync<T>();

            _logger?.LogInformation("Service restarted successfully", new { ServiceType = typeof(T).Name });
        }

        public InfraServiceStatus GetServiceStatus<T>() where T : class, IService
        {
            ThrowIfDisposed();

            if (_serviceMetadata.TryGetValue(typeof(T), out var metadata))
            {
                return metadata.Status;
            }

            return InfraServiceStatus.NotRegistered;
        }

        public ServiceHealth GetServiceHealth<T>() where T : class, IService
        {
            ThrowIfDisposed();

            if (_serviceMetadata.TryGetValue(typeof(T), out var metadata))
            {
                return metadata.Health;
            }

            return ServiceHealth.Unknown;
        }

        public IEnumerable<Type> GetRegisteredServices()
        {
            ThrowIfDisposed();
            return _services.Keys.ToList();
        }

        public async Task StartAllServicesAsync()
        {
            ThrowIfDisposed();

            _logger?.LogInformation("Starting all services");

            var serviceTypes = _services.Keys.ToList();
            var sortedServices = TopologicalSort(serviceTypes);

            foreach (var serviceType in sortedServices)
            {
                try
                {
                    var startMethod = typeof(ServiceManager).GetMethod(nameof(StartServiceAsync))?.MakeGenericMethod(serviceType);
                    if (startMethod is not null)
                    {
                        var task = (Task)startMethod.Invoke(this, null);
                        await task;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to start service during bulk start", new { ServiceType = serviceType.Name });
                    // 继续启动其他服务
                }
            }

            _logger?.LogInformation("All services start process completed");
        }

        public async Task StopAllServicesAsync()
        {
            ThrowIfDisposed();

            _isShuttingDown = true;
            _logger?.LogInformation("Stopping all services");

            var serviceTypes = _services.Keys.ToList();
            var sortedServices = TopologicalSort(serviceTypes);
            var reversedServices = sortedServices.AsEnumerable().Reverse(); // 反向停止

            foreach (var serviceType in reversedServices)
            {
                try
                {
                    var stopMethod = typeof(ServiceManager).GetMethod(nameof(StopServiceAsync))?.MakeGenericMethod(serviceType);
                    if (stopMethod is not null)
                    {
                        var task = (Task)stopMethod.Invoke(this, null);
                        await task;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to stop service during bulk stop", new { ServiceType = serviceType.Name });
                    // 继续停止其他服务
                }
            }

            _logger?.LogInformation("All services stop process completed");
        }

        private async Task StartDependentServicesAsync(Type serviceType)
        {
            var dependencies = GetServiceDependencies(serviceType);
            foreach (var dependency in dependencies)
            {
                if (_serviceMetadata.TryGetValue(dependency, out var metadata) && metadata.Status != InfraServiceStatus.Running)
                {
                    var startMethod = typeof(ServiceManager).GetMethod(nameof(StartServiceAsync))?.MakeGenericMethod(dependency);
                    if (startMethod is not null)
                    {
                        var task = (Task)startMethod.Invoke(this, null);
                        await task;
                    }
                }
            }
        }

        private async Task StopDependentServicesAsync(Type serviceType)
        {
            if (_isShuttingDown) return; // 在全局关闭过程中跳过依赖停�?

            var dependents = GetServiceDependents(serviceType);
            foreach (var dependent in dependents)
            {
                if (_serviceMetadata.TryGetValue(dependent, out var metadata) && metadata.Status == InfraServiceStatus.Running)
                {
                    var stopMethod = typeof(ServiceManager).GetMethod(nameof(StopServiceAsync))?.MakeGenericMethod(dependent);
                    if (stopMethod is not null)
                    {
                        var task = (Task)stopMethod.Invoke(this, null);
                        await task;
                    }
                }
            }
        }

        private List<Type> GetServiceDependencies(Type serviceType)
        {
            var dependencies = new List<Type>();

            // 通过属性或接口获取依赖关系
            var dependsOnAttributes = serviceType.GetCustomAttributes(typeof(DependsOnAttribute), true);
            foreach (DependsOnAttribute attr in dependsOnAttributes)
            {
                dependencies.AddRange(attr.Dependencies);
            }

            return dependencies;
        }

        private List<Type> GetServiceDependents(Type serviceType)
        {
            var dependents = new List<Type>();

            foreach (var kvp in _serviceMetadata)
            {
                var dependencies = GetServiceDependencies(kvp.Key);
                if (dependencies.Contains(serviceType))
                {
                    dependents.Add(kvp.Key);
                }
            }

            return dependents;
        }

        private List<Type> TopologicalSort(List<Type> serviceTypes)
        {
            var sorted = new List<Type>();
            var visited = new HashSet<Type>();
            var visiting = new HashSet<Type>();

            foreach (var serviceType in serviceTypes)
            {
                if (!visited.Contains(serviceType))
                {
                    TopologicalSortVisit(serviceType, visited, visiting, sorted);
                }
            }

            return sorted;
        }

        private void TopologicalSortVisit(Type serviceType, HashSet<Type> visited, HashSet<Type> visiting, List<Type> sorted)
        {
            if (visiting.Contains(serviceType))
            {
                throw new ServiceException(serviceType.Name, $"Circular dependency detected involving service {serviceType.Name}");
            }

            if (visited.Contains(serviceType))
            {
                return;
            }

            visiting.Add(serviceType);

            var dependencies = GetServiceDependencies(serviceType);
            foreach (var dependency in dependencies)
            {
                TopologicalSortVisit(dependency, visited, visiting, sorted);
            }

            visiting.Remove(serviceType);
            visited.Add(serviceType);
            sorted.Add(serviceType);
        }

        private async void PerformHealthChecks(object state)
        {
            if (_disposed || _isShuttingDown) return;

            try
            {
                var healthCheckTasks = new List<Task>();

                foreach (var kvp in _services.ToList())
                {
                    var serviceType = kvp.Key;
                    var service = kvp.Value;
                    var metadata = _serviceMetadata[serviceType];

                    if (metadata.Status == InfraServiceStatus.Running)
                    {
                        healthCheckTasks.Add(CheckServiceHealthAsync(serviceType, service, metadata));
                    }
                }

                await Task.WhenAll(healthCheckTasks);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during health checks");
            }
        }

        private async Task CheckServiceHealthAsync(Type serviceType, IService service, ServiceMetadata metadata)
        {
            try
            {
                var healthResult = await service.CheckHealthAsync();
                var isHealthy = healthResult.Status == HealthStatus.Healthy;
                var newHealth = isHealthy ? ServiceHealth.Healthy : ServiceHealth.Unhealthy;

                if (metadata.Health != newHealth)
                {
                    metadata.Health = newHealth;
                    metadata.LastHealthCheck = DateTime.UtcNow;

                    _logger?.LogInformation("Service health changed", new
                    {
                        ServiceType = serviceType.Name,
                        Health = newHealth
                    });

                    OnServiceHealthChanged(new ServiceHealthChangedEventArgs(serviceType, newHealth, service));

                    // 如果服务不健康，尝试重启
                    if (newHealth == ServiceHealth.Unhealthy && metadata.AutoRestart)
                    {
                        _logger?.LogWarning("Attempting to restart unhealthy service", new { ServiceType = serviceType.Name });

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var restartMethod = typeof(ServiceManager).GetMethod(nameof(RestartServiceAsync))?.MakeGenericMethod(serviceType);
                                if (restartMethod is not null)
                                {
                                    var task = (Task)restartMethod.Invoke(this, null);
                                    await task;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogError(ex, "Failed to auto-restart service", new { ServiceType = serviceType.Name });
                            }
                        });
                    }
                }
                else
                {
                    metadata.LastHealthCheck = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                metadata.Health = ServiceHealth.Unhealthy;
                metadata.LastError = ex;
                metadata.LastHealthCheck = DateTime.UtcNow;

                _logger?.LogError(ex, "Health check failed for service", new { ServiceType = serviceType.Name });
                OnServiceHealthChanged(new ServiceHealthChangedEventArgs(serviceType, ServiceHealth.Unhealthy, service));
            }
        }

        private void OnServiceStatusChanged(ServiceStatusChangedEventArgs e)
        {
            ServiceStatusChanged?.Invoke(this, e);

            // 发布事件到事件总线
            _ = _eventBus?.PublishAsync(new ServiceStatusChangedEvent
            {
                ServiceType = null, // 无法从事件参数获取Type信息
                ServiceName = e.ServiceName,
                OldStatus = (InfraServiceStatus)e.OldStatus,
                NewStatus = (InfraServiceStatus)e.NewStatus,
                Timestamp = DateTime.UtcNow
            });
        }

        private void OnServiceHealthChanged(ServiceHealthChangedEventArgs e)
        {
            ServiceHealthChanged?.Invoke(this, e);

            // 发布事件到事件总线
            _ = _eventBus?.PublishAsync(new ServiceHealthChangedEvent
            {
                ServiceType = e.ServiceType,
                ServiceName = e.ServiceName,
                OldHealth = (ServiceHealth)e.OldStatus,
                NewHealth = e.Health,
                Timestamp = DateTime.UtcNow
            });
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ServiceManager));
            }
        }

        // IServiceManager interface implementations
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            return StartAllServicesAsync();
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return StopAllServicesAsync();
        }

        public async Task<ServiceHealthStatus> CheckHealthAsync(string serviceName)
        {
            ThrowIfDisposed();

            var service = GetService(serviceName);
            if (service is null)
            {
                return new ServiceHealthStatus
                {
                    ServiceName = serviceName,
                    Status = HealthStatus.Unknown,
                    Description = "Service not found"
                };
            }

            return await service.CheckHealthAsync();
        }

        public async Task<Dictionary<string, ServiceHealthStatus>> CheckAllHealthAsync()
        {
            ThrowIfDisposed();

            var results = new Dictionary<string, ServiceHealthStatus>();
            foreach (var service in _services.Values)
            {
                var health = await service.CheckHealthAsync();
                results[service.ServiceName] = health;
            }

            return results;
        }

        public void RegisterService<T>(T service) where T : class, IService
        {
            RegisterService(service, 0);
        }

        public void RegisterService<T>(T service, int priority) where T : class, IService
        {
            ThrowIfDisposed();

            if (service is null)
                throw new ArgumentNullException(nameof(service));

            var serviceType = typeof(T);
            _services[serviceType] = service;
            _serviceMetadata[serviceType] = new ServiceMetadata
            {
                ServiceType = serviceType,
                Status = InfraServiceStatus.NotInitialized,
                Health = ServiceHealth.Unknown
            };

            _logger?.LogInformation("Service registered", new { ServiceType = serviceType.Name, Priority = priority });
        }

        public T GetService<T>() where T : class, IService
        {
            ThrowIfDisposed();

            if (_services.TryGetValue(typeof(T), out var service))
            {
                return (T)service;
            }

            throw new InvalidOperationException($"Service {typeof(T).Name} not found");
        }

        public bool TryGetService<T>(out T service) where T : class, IService
        {
            ThrowIfDisposed();

            if (_services.TryGetValue(typeof(T), out var svc))
            {
                service = (T)svc;
                return true;
            }

            service = null;
            return false;
        }

        public IService GetService(string serviceName)
        {
            ThrowIfDisposed();

            return _services.Values.FirstOrDefault(s => s.ServiceName == serviceName);
        }

        public IEnumerable<IService> GetAllServices()
        {
            ThrowIfDisposed();

            return _services.Values.ToList();
        }

        public InfraServiceStatus GetServiceStatus(string serviceName)
        {
            ThrowIfDisposed();

            var service = GetService(serviceName);
            return service?.Status ?? InfraServiceStatus.NotInitialized;
        }

        public async Task RestartServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var service = GetService(serviceName);
            if (service is null)
            {
                throw new InvalidOperationException($"Service {serviceName} not found");
            }

            await service.StopAsync(cancellationToken);
            await service.StartAsync(cancellationToken);
        }

        public void EnableAutoRestart(string serviceName, TimeSpan checkInterval, int maxRetries = 3)
        {
            ThrowIfDisposed();

            // TODO: Implement auto-restart logic
            _logger?.LogInformation("Auto-restart enabled", new { ServiceName = serviceName, CheckInterval = checkInterval, MaxRetries = maxRetries });
        }

        public void DisableAutoRestart(string serviceName)
        {
            ThrowIfDisposed();

            // TODO: Implement auto-restart logic
            _logger?.LogInformation("Auto-restart disabled", new { ServiceName = serviceName });
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _isShuttingDown = true;

            try
            {
                // 停止健康检�?
                _healthCheckTimer?.Dispose();

                // 停止所有服�?
                StopAllServicesAsync().Wait(TimeSpan.FromSeconds(30));

                // 释放服务实例
                foreach (var service in _services.Values)
                {
                    if (service is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }

                _services.Clear();
                _serviceMetadata.Clear();
                _operationLock?.Dispose();

                _logger?.LogInformation("ServiceManager disposed");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during ServiceManager disposal");
            }
        }
    }

    // 服务元数�?
    internal class ServiceMetadata
    {
        public Type ServiceType { get; set; }
        public InfraServiceStatus Status { get; set; }
        public ServiceHealth Health { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? StoppedAt { get; set; }
        public DateTime LastHealthCheck { get; set; }
        public Exception LastError { get; set; }
        public bool AutoRestart { get; set; } = true;
    }

    // 依赖关系属�?
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class DependsOnAttribute : Attribute
    {
        public Type[] Dependencies { get; }

        public DependsOnAttribute(params Type[] dependencies)
        {
            Dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        }
    }
}
