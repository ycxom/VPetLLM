namespace VPetLLM.Core
{
    /// <summary>
    /// 服务容器接口，提供简单的依赖注入功能
    /// </summary>
    public interface IServiceContainer
    {
        /// <summary>
        /// 获取已注册的服务实例
        /// </summary>
        /// <typeparam name="T">服务类型</typeparam>
        /// <returns>服务实例，如果未注册则返回 null</returns>
        T? GetService<T>() where T : class;

        /// <summary>
        /// 获取已注册的服务实例，如果未注册则抛出异常
        /// </summary>
        /// <typeparam name="T">服务类型</typeparam>
        /// <returns>服务实例</returns>
        /// <exception cref="InvalidOperationException">服务未注册时抛出</exception>
        T GetRequiredService<T>() where T : class;

        /// <summary>
        /// 注册服务实例
        /// </summary>
        /// <typeparam name="T">服务类型</typeparam>
        /// <param name="instance">服务实例</param>
        void RegisterService<T>(T instance) where T : class;

        /// <summary>
        /// 注册服务工厂
        /// </summary>
        /// <typeparam name="T">服务类型</typeparam>
        /// <param name="factory">创建服务实例的工厂方法</param>
        void RegisterFactory<T>(Func<T> factory) where T : class;

        /// <summary>
        /// 检查服务是否已注册
        /// </summary>
        /// <typeparam name="T">服务类型</typeparam>
        /// <returns>如果服务已注册则返回 true</returns>
        bool IsRegistered<T>() where T : class;

        /// <summary>
        /// 移除已注册的服务
        /// </summary>
        /// <typeparam name="T">服务类型</typeparam>
        /// <returns>如果成功移除则返回 true</returns>
        bool Unregister<T>() where T : class;
    }
}
