namespace VPetLLM.Infrastructure.DependencyInjection
{
    /// <summary>
    /// 依赖注入容器接口，管理服务的注册、解析和生命周期
    /// </summary>
    public interface IDependencyContainer : IDisposable
    {
        /// <summary>
        /// 注册单例服务
        /// </summary>
        void RegisterSingleton<TInterface, TImplementation>()
            where TImplementation : class, TInterface;

        /// <summary>
        /// 注册单例服务实例
        /// </summary>
        void RegisterSingleton<TInterface>(TInterface instance)
            where TInterface : class;

        /// <summary>
        /// 注册瞬态服务
        /// </summary>
        void RegisterTransient<TInterface, TImplementation>()
            where TImplementation : class, TInterface;

        /// <summary>
        /// 注册作用域服务
        /// </summary>
        void RegisterScoped<TInterface, TImplementation>()
            where TImplementation : class, TInterface;

        /// <summary>
        /// 解析服务实例
        /// </summary>
        T Resolve<T>();

        /// <summary>
        /// 解析服务实例
        /// </summary>
        object Resolve(Type type);

        /// <summary>
        /// 尝试解析服务实例
        /// </summary>
        bool TryResolve<T>(out T service);

        /// <summary>
        /// 检查服务是否已注册
        /// </summary>
        bool IsRegistered<T>();

        /// <summary>
        /// 检查服务是否已注册
        /// </summary>
        bool IsRegistered(Type type);

        /// <summary>
        /// 验证所有依赖关系
        /// </summary>
        void ValidateDependencies();

        /// <summary>
        /// 获取所有已注册的服务类型
        /// </summary>
        IEnumerable<Type> GetRegisteredTypes();
    }

    /// <summary>
    /// 服务生命周期枚举
    /// </summary>
    public enum ServiceLifetime
    {
        /// <summary>
        /// 单例：整个应用程序生命周期内只创建一个实例
        /// </summary>
        Singleton,

        /// <summary>
        /// 瞬态：每次请求都创建新实例
        /// </summary>
        Transient,

        /// <summary>
        /// 作用域：在同一作用域内共享实例
        /// </summary>
        Scoped
    }

    /// <summary>
    /// 循环依赖异常
    /// </summary>
    public class CircularDependencyException : Exception
    {
        public Type ServiceType { get; }
        public List<Type> DependencyChain { get; }

        public CircularDependencyException(Type serviceType, List<Type> dependencyChain)
            : base($"Circular dependency detected for service {serviceType.Name}. Dependency chain: {string.Join(" -> ", dependencyChain)}")
        {
            ServiceType = serviceType;
            DependencyChain = dependencyChain;
        }
    }

    /// <summary>
    /// 服务未注册异常
    /// </summary>
    public class ServiceNotRegisteredException : Exception
    {
        public Type ServiceType { get; }

        public ServiceNotRegisteredException(Type serviceType)
            : base($"Service of type {serviceType.Name} is not registered")
        {
            ServiceType = serviceType;
        }
    }
}