using VPetLLM.Handlers.Actions;

namespace VPetLLM.Handlers.Infrastructure
{
    /// <summary>
    /// Handler 注册表接口，用于统一管理和发现 Handler
    /// </summary>
    public interface IHandlerRegistry
    {
        /// <summary>
        /// 注册 Handler
        /// </summary>
        /// <param name="actionName">动作名称</param>
        /// <param name="handler">Handler 实例</param>
        void Register(string actionName, IActionHandler handler);

        /// <summary>
        /// 注册 Handler（使用 Handler 的默认名称）
        /// </summary>
        /// <typeparam name="T">Handler 类型</typeparam>
        /// <param name="handler">Handler 实例</param>
        void Register<T>(T handler) where T : IActionHandler;

        /// <summary>
        /// 获取指定动作的 Handler
        /// </summary>
        /// <param name="actionName">动作名称</param>
        /// <returns>Handler 实例，如果未找到则返回 null</returns>
        IActionHandler? GetHandler(string actionName);

        /// <summary>
        /// 获取所有已注册的 Handler
        /// </summary>
        /// <returns>所有 Handler 的集合</returns>
        IEnumerable<IActionHandler> GetAllHandlers();

        /// <summary>
        /// 获取所有已注册的动作名称
        /// </summary>
        /// <returns>动作名称集合</returns>
        IEnumerable<string> GetRegisteredActionNames();

        /// <summary>
        /// 检查指定动作是否已注册
        /// </summary>
        /// <param name="actionName">动作名称</param>
        /// <returns>如果已注册则返回 true</returns>
        bool IsRegistered(string actionName);

        /// <summary>
        /// 移除指定动作的 Handler
        /// </summary>
        /// <param name="actionName">动作名称</param>
        /// <returns>如果成功移除则返回 true</returns>
        bool Unregister(string actionName);

        /// <summary>
        /// 清除所有已注册的 Handler
        /// </summary>
        void Clear();
    }
}
