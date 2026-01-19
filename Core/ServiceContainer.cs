namespace VPetLLM.Core
{
    /// <summary>
    /// 简单的服务容器实现，提供服务注册和检索功能
    /// </summary>
    public class ServiceContainer : IServiceContainer
    {
        private readonly Dictionary<Type, object> _services = new();
        private readonly Dictionary<Type, Func<object>> _factories = new();
        private readonly object _lock = new();

        /// <summary>
        /// 全局单例实例
        /// </summary>
        public static ServiceContainer Instance { get; } = new();

        /// <inheritdoc/>
        public T? GetService<T>() where T : class
        {
            var type = typeof(T);

            lock (_lock)
            {
                // 首先检查是否有已注册的实例
                if (_services.TryGetValue(type, out var service))
                {
                    return (T)service;
                }

                // 然后检查是否有工厂方法
                if (_factories.TryGetValue(type, out var factory))
                {
                    var instance = (T)factory();
                    // 缓存工厂创建的实例
                    _services[type] = instance;
                    return instance;
                }
            }

            return null;
        }

        /// <inheritdoc/>
        public T GetRequiredService<T>() where T : class
        {
            var service = GetService<T>();
            if (service == null)
            {
                throw new InvalidOperationException(
                    $"Service of type '{typeof(T).FullName}' is not registered.");
            }
            return service;
        }

        /// <inheritdoc/>
        public void RegisterService<T>(T instance) where T : class
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            var type = typeof(T);

            lock (_lock)
            {
                _services[type] = instance;
                // 移除可能存在的工厂，因为现在有了具体实例
                _factories.Remove(type);
            }
        }

        /// <inheritdoc/>
        public void RegisterFactory<T>(Func<T> factory) where T : class
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            var type = typeof(T);

            lock (_lock)
            {
                _factories[type] = () => factory();
                // 移除可能存在的实例，让工厂在下次请求时创建新实例
                _services.Remove(type);
            }
        }

        /// <inheritdoc/>
        public bool IsRegistered<T>() where T : class
        {
            var type = typeof(T);

            lock (_lock)
            {
                return _services.ContainsKey(type) || _factories.ContainsKey(type);
            }
        }

        /// <inheritdoc/>
        public bool Unregister<T>() where T : class
        {
            var type = typeof(T);

            lock (_lock)
            {
                var removedService = _services.Remove(type);
                var removedFactory = _factories.Remove(type);
                return removedService || removedFactory;
            }
        }

        /// <summary>
        /// 清除所有已注册的服务
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _services.Clear();
                _factories.Clear();
            }
        }

        /// <summary>
        /// 获取所有已注册的服务类型
        /// </summary>
        public IEnumerable<Type> GetRegisteredTypes()
        {
            lock (_lock)
            {
                return _services.Keys.Union(_factories.Keys).ToList();
            }
        }
    }
}
