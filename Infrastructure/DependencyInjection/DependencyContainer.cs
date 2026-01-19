using System.Collections.Concurrent;
using VPetLLM.Utils.System;


namespace VPetLLM.Infrastructure.DependencyInjection
{
    /// <summary>
    /// 依赖注入容器实现
    /// </summary>
    public class DependencyContainer : IDependencyContainer
    {
        private readonly ConcurrentDictionary<Type, ServiceDescriptor> _services = new();
        private readonly ConcurrentDictionary<Type, object> _singletonInstances = new();
        private readonly ThreadLocal<Dictionary<Type, object>> _scopedInstances = new(() => new Dictionary<Type, object>());
        private readonly object _lock = new object();
        private bool _disposed = false;

        public void RegisterSingleton<TInterface, TImplementation>()
            where TImplementation : class, TInterface
        {
            ThrowIfDisposed();
            var descriptor = new ServiceDescriptor(typeof(TInterface), typeof(TImplementation), ServiceLifetime.Singleton);
            _services.AddOrUpdate(typeof(TInterface), descriptor, (key, existing) => descriptor);
            Logger.Log($"Registered singleton service: {typeof(TInterface).Name} -> {typeof(TImplementation).Name}");
        }

        public void RegisterSingleton<TInterface>(TInterface instance)
            where TInterface : class
        {
            ThrowIfDisposed();
            if (instance == null)
                throw new ArgumentNullException(nameof(instance));

            var descriptor = new ServiceDescriptor(typeof(TInterface), instance);
            _services.AddOrUpdate(typeof(TInterface), descriptor, (key, existing) => descriptor);
            _singletonInstances.AddOrUpdate(typeof(TInterface), instance, (key, existing) => instance);
            Logger.Log($"Registered singleton instance: {typeof(TInterface).Name}");
        }

        public void RegisterTransient<TInterface, TImplementation>()
            where TImplementation : class, TInterface
        {
            ThrowIfDisposed();
            var descriptor = new ServiceDescriptor(typeof(TInterface), typeof(TImplementation), ServiceLifetime.Transient);
            _services.AddOrUpdate(typeof(TInterface), descriptor, (key, existing) => descriptor);
            Logger.Log($"Registered transient service: {typeof(TInterface).Name} -> {typeof(TImplementation).Name}");
        }

        public void RegisterScoped<TInterface, TImplementation>()
            where TImplementation : class, TInterface
        {
            ThrowIfDisposed();
            var descriptor = new ServiceDescriptor(typeof(TInterface), typeof(TImplementation), ServiceLifetime.Scoped);
            _services.AddOrUpdate(typeof(TInterface), descriptor, (key, existing) => descriptor);
            Logger.Log($"Registered scoped service: {typeof(TInterface).Name} -> {typeof(TImplementation).Name}");
        }

        public T Resolve<T>()
        {
            return (T)Resolve(typeof(T));
        }

        public object Resolve(Type type)
        {
            ThrowIfDisposed();
            return ResolveInternal(type, new HashSet<Type>());
        }

        public bool TryResolve<T>(out T service)
        {
            try
            {
                service = Resolve<T>();
                return true;
            }
            catch
            {
                service = default(T);
                return false;
            }
        }

        public bool IsRegistered<T>()
        {
            return IsRegistered(typeof(T));
        }

        public bool IsRegistered(Type type)
        {
            ThrowIfDisposed();
            return _services.ContainsKey(type);
        }

        public void ValidateDependencies()
        {
            ThrowIfDisposed();
            var validationErrors = new List<string>();

            foreach (var kvp in _services)
            {
                try
                {
                    ValidateServiceDependencies(kvp.Value, new HashSet<Type>());
                }
                catch (Exception ex)
                {
                    validationErrors.Add($"Service {kvp.Key.Name}: {ex.Message}");
                }
            }

            if (validationErrors.Any())
            {
                throw new InvalidOperationException($"Dependency validation failed:\n{string.Join("\n", validationErrors)}");
            }

            Logger.Log("All dependencies validated successfully");
        }

        public IEnumerable<Type> GetRegisteredTypes()
        {
            ThrowIfDisposed();
            return _services.Keys.ToList();
        }

        private object ResolveInternal(Type type, HashSet<Type> resolutionPath)
        {
            if (resolutionPath.Contains(type))
            {
                var chain = resolutionPath.ToList();
                chain.Add(type);
                throw new CircularDependencyException(type, chain);
            }

            if (!_services.TryGetValue(type, out var descriptor))
            {
                throw new ServiceNotRegisteredException(type);
            }

            resolutionPath.Add(type);

            try
            {
                return CreateInstance(descriptor, resolutionPath);
            }
            finally
            {
                resolutionPath.Remove(type);
            }
        }

        private object CreateInstance(ServiceDescriptor descriptor, HashSet<Type> resolutionPath)
        {
            switch (descriptor.Lifetime)
            {
                case ServiceLifetime.Singleton:
                    return CreateSingletonInstance(descriptor, resolutionPath);

                case ServiceLifetime.Scoped:
                    return CreateScopedInstance(descriptor, resolutionPath);

                case ServiceLifetime.Transient:
                    return CreateTransientInstance(descriptor, resolutionPath);

                default:
                    throw new InvalidOperationException($"Unknown service lifetime: {descriptor.Lifetime}");
            }
        }

        private object CreateSingletonInstance(ServiceDescriptor descriptor, HashSet<Type> resolutionPath)
        {
            if (descriptor.Instance != null)
            {
                return descriptor.Instance;
            }

            return _singletonInstances.GetOrAdd(descriptor.ServiceType, _ =>
            {
                lock (_lock)
                {
                    // Double-check locking pattern
                    if (_singletonInstances.TryGetValue(descriptor.ServiceType, out var existingInstance))
                    {
                        return existingInstance;
                    }

                    return CreateInstanceInternal(descriptor.ImplementationType, resolutionPath);
                }
            });
        }

        private object CreateScopedInstance(ServiceDescriptor descriptor, HashSet<Type> resolutionPath)
        {
            var scopedInstances = _scopedInstances.Value;
            if (scopedInstances.TryGetValue(descriptor.ServiceType, out var instance))
            {
                return instance;
            }

            instance = CreateInstanceInternal(descriptor.ImplementationType, resolutionPath);
            scopedInstances[descriptor.ServiceType] = instance;
            return instance;
        }

        private object CreateTransientInstance(ServiceDescriptor descriptor, HashSet<Type> resolutionPath)
        {
            return CreateInstanceInternal(descriptor.ImplementationType, resolutionPath);
        }

        private object CreateInstanceInternal(Type implementationType, HashSet<Type> resolutionPath)
        {
            var constructors = implementationType.GetConstructors();
            var constructor = constructors.OrderByDescending(c => c.GetParameters().Length).First();

            var parameters = constructor.GetParameters();
            var args = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var parameterType = parameters[i].ParameterType;
                args[i] = ResolveInternal(parameterType, resolutionPath);
            }

            try
            {
                return Activator.CreateInstance(implementationType, args);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to create instance of {implementationType.Name}", ex);
            }
        }

        private void ValidateServiceDependencies(ServiceDescriptor descriptor, HashSet<Type> validationPath)
        {
            if (descriptor.Instance != null)
            {
                return; // Instance-based registrations don't need validation
            }

            var implementationType = descriptor.ImplementationType;
            if (validationPath.Contains(implementationType))
            {
                var chain = validationPath.ToList();
                chain.Add(implementationType);
                throw new CircularDependencyException(implementationType, chain);
            }

            validationPath.Add(implementationType);

            try
            {
                var constructors = implementationType.GetConstructors();
                var constructor = constructors.OrderByDescending(c => c.GetParameters().Length).First();

                foreach (var parameter in constructor.GetParameters())
                {
                    var parameterType = parameter.ParameterType;
                    if (!_services.TryGetValue(parameterType, out var dependencyDescriptor))
                    {
                        throw new ServiceNotRegisteredException(parameterType);
                    }

                    ValidateServiceDependencies(dependencyDescriptor, validationPath);
                }
            }
            finally
            {
                validationPath.Remove(implementationType);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DependencyContainer));
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // Dispose singleton instances
            foreach (var instance in _singletonInstances.Values)
            {
                if (instance is IDisposable disposable)
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error disposing singleton instance {instance.GetType().Name}: {ex.Message}");
                    }
                }
            }

            // Dispose scoped instances
            _scopedInstances.Dispose();

            _services.Clear();
            _singletonInstances.Clear();

            Logger.Log("DependencyContainer disposed");
        }

        /// <summary>
        /// 服务描述符
        /// </summary>
        private class ServiceDescriptor
        {
            public Type ServiceType { get; }
            public Type ImplementationType { get; }
            public ServiceLifetime Lifetime { get; }
            public object Instance { get; }

            public ServiceDescriptor(Type serviceType, Type implementationType, ServiceLifetime lifetime)
            {
                ServiceType = serviceType;
                ImplementationType = implementationType;
                Lifetime = lifetime;
                Instance = null;
            }

            public ServiceDescriptor(Type serviceType, object instance)
            {
                ServiceType = serviceType;
                ImplementationType = instance.GetType();
                Lifetime = ServiceLifetime.Singleton;
                Instance = instance;
            }
        }
    }
}