using System.Collections.Concurrent;
using VPetLLM.Infrastructure.Logging;

namespace VPetLLM.Infrastructure.Performance
{
    /// <summary>
    /// 资源管理器
    /// 跟踪和管理可释放资源
    /// </summary>
    public class ResourceManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, IDisposable> _resources;
        private readonly IStructuredLogger? _logger;
        private readonly Timer _cleanupTimer;
        private bool _disposed;

        public ResourceManager(IStructuredLogger? logger = null)
        {
            _resources = new ConcurrentDictionary<string, IDisposable>();
            _logger = logger;

            // 定期清理资源
            _cleanupTimer = new Timer(PerformCleanup, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        /// <summary>
        /// 注册资源
        /// </summary>
        public void Register(string name, IDisposable resource)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ResourceManager));

            if (_resources.TryAdd(name, resource))
            {
                _logger?.LogDebug($"ResourceManager: Registered resource '{name}'");
            }
            else
            {
                _logger?.LogWarning($"ResourceManager: Resource '{name}' already registered");
            }
        }

        /// <summary>
        /// 注销并释放资源
        /// </summary>
        public void Unregister(string name)
        {
            if (_resources.TryRemove(name, out var resource))
            {
                try
                {
                    resource.Dispose();
                    _logger?.LogDebug($"ResourceManager: Unregistered and disposed resource '{name}'");
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"ResourceManager: Failed to dispose resource '{name}'", ex);
                }
            }
        }

        /// <summary>
        /// 获取资源
        /// </summary>
        public T? GetResource<T>(string name) where T : class, IDisposable
        {
            if (_resources.TryGetValue(name, out var resource))
            {
                return resource as T;
            }
            return null;
        }

        /// <summary>
        /// 检查资源是否存在
        /// </summary>
        public bool HasResource(string name)
        {
            return _resources.ContainsKey(name);
        }

        /// <summary>
        /// 获取所有资源名称
        /// </summary>
        public IEnumerable<string> GetResourceNames()
        {
            return _resources.Keys.ToList();
        }

        /// <summary>
        /// 定期清理资源
        /// </summary>
        private void PerformCleanup(object? state)
        {
            try
            {
                _logger?.LogDebug($"ResourceManager: Performing cleanup (Resources: {_resources.Count})");

                // 强制垃圾回收
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                _logger?.LogDebug("ResourceManager: Cleanup completed");
            }
            catch (Exception ex)
            {
                _logger?.LogError("ResourceManager: Cleanup failed", ex);
            }
        }

        /// <summary>
        /// 释放所有资源
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _cleanupTimer?.Dispose();

            _logger?.LogInformation($"ResourceManager: Disposing {_resources.Count} resources");

            foreach (var kvp in _resources)
            {
                try
                {
                    kvp.Value.Dispose();
                    _logger?.LogDebug($"ResourceManager: Disposed resource '{kvp.Key}'");
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"ResourceManager: Failed to dispose resource '{kvp.Key}'", ex);
                }
            }

            _resources.Clear();
            _logger?.LogInformation("ResourceManager: All resources disposed");
        }
    }

    /// <summary>
    /// 资源租用包装器
    /// 自动管理资源生命周期
    /// </summary>
    public class ResourceLease<T> : IDisposable where T : class, IDisposable
    {
        private readonly T _resource;
        private readonly Action<T>? _onDispose;
        private bool _disposed;

        public T Resource => _resource;

        public ResourceLease(T resource, Action<T>? onDispose = null)
        {
            _resource = resource ?? throw new ArgumentNullException(nameof(resource));
            _onDispose = onDispose;
            _disposed = false;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _onDispose?.Invoke(_resource);
                _resource.Dispose();
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// 内存监控器
    /// 监控内存使用情况
    /// </summary>
    public class MemoryMonitor
    {
        private readonly IStructuredLogger? _logger;
        private readonly long _warningThresholdMB;
        private readonly long _criticalThresholdMB;
        private readonly Timer _monitorTimer;

        public MemoryMonitor(
            long warningThresholdMB = 500,
            long criticalThresholdMB = 1000,
            IStructuredLogger? logger = null)
        {
            _warningThresholdMB = warningThresholdMB;
            _criticalThresholdMB = criticalThresholdMB;
            _logger = logger;

            // 定期监控内存
            _monitorTimer = new Timer(CheckMemory, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        /// <summary>
        /// 获取当前内存使用量（MB）
        /// </summary>
        public long GetCurrentMemoryUsageMB()
        {
            return GC.GetTotalMemory(false) / 1024 / 1024;
        }

        /// <summary>
        /// 获取内存使用信息
        /// </summary>
        public MemoryInfo GetMemoryInfo()
        {
            var currentMemory = GetCurrentMemoryUsageMB();
            var gen0Collections = GC.CollectionCount(0);
            var gen1Collections = GC.CollectionCount(1);
            var gen2Collections = GC.CollectionCount(2);

            return new MemoryInfo
            {
                CurrentMemoryMB = currentMemory,
                WarningThresholdMB = _warningThresholdMB,
                CriticalThresholdMB = _criticalThresholdMB,
                Gen0Collections = gen0Collections,
                Gen1Collections = gen1Collections,
                Gen2Collections = gen2Collections,
                IsWarning = currentMemory >= _warningThresholdMB,
                IsCritical = currentMemory >= _criticalThresholdMB
            };
        }

        /// <summary>
        /// 检查内存使用情况
        /// </summary>
        private void CheckMemory(object? state)
        {
            try
            {
                var info = GetMemoryInfo();

                if (info.IsCritical)
                {
                    _logger?.LogError($"CRITICAL: Memory usage is {info.CurrentMemoryMB}MB (Threshold: {_criticalThresholdMB}MB)");

                    // 强制垃圾回收
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    var newMemory = GetCurrentMemoryUsageMB();
                    _logger?.LogInformation($"After GC: Memory usage is {newMemory}MB (Freed: {info.CurrentMemoryMB - newMemory}MB)");
                }
                else if (info.IsWarning)
                {
                    _logger?.LogWarning($"WARNING: Memory usage is {info.CurrentMemoryMB}MB (Threshold: {_warningThresholdMB}MB)");
                }
                else
                {
                    _logger?.LogDebug($"Memory usage: {info.CurrentMemoryMB}MB (GC: Gen0={info.Gen0Collections}, Gen1={info.Gen1Collections}, Gen2={info.Gen2Collections})");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Failed to check memory", ex);
            }
        }

        /// <summary>
        /// 强制垃圾回收
        /// </summary>
        public void ForceGarbageCollection()
        {
            _logger?.LogInformation("Forcing garbage collection");

            var beforeMemory = GetCurrentMemoryUsageMB();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var afterMemory = GetCurrentMemoryUsageMB();
            var freed = beforeMemory - afterMemory;

            _logger?.LogInformation($"Garbage collection completed: Freed {freed}MB (Before: {beforeMemory}MB, After: {afterMemory}MB)");
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            _monitorTimer?.Dispose();
        }
    }

    /// <summary>
    /// 内存信息
    /// </summary>
    public class MemoryInfo
    {
        public long CurrentMemoryMB { get; set; }
        public long WarningThresholdMB { get; set; }
        public long CriticalThresholdMB { get; set; }
        public int Gen0Collections { get; set; }
        public int Gen1Collections { get; set; }
        public int Gen2Collections { get; set; }
        public bool IsWarning { get; set; }
        public bool IsCritical { get; set; }
    }
}
