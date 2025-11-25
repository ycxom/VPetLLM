using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// 安全的 Handler 执行器，提供统一的错误处理
    /// </summary>
    public class SafeHandlerExecutor
    {
        private readonly IHandlerRegistry _registry;

        public SafeHandlerExecutor(IHandlerRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        /// <summary>
        /// 安全执行指定动作的 Handler（无参数）
        /// </summary>
        public async Task<HandlerResult> ExecuteAsync(string actionName, IMainWindow mainWindow)
        {
            if (string.IsNullOrWhiteSpace(actionName))
            {
                return HandlerResult.Fail("Action name cannot be null or empty");
            }

            var handler = _registry.GetHandler(actionName);
            if (handler == null)
            {
                return HandlerResult.Fail($"No handler registered for action: {actionName}");
            }

            return await ExecuteAsync(handler, mainWindow);
        }

        /// <summary>
        /// 安全执行指定动作的 Handler（整数参数）
        /// </summary>
        public async Task<HandlerResult> ExecuteAsync(string actionName, int value, IMainWindow mainWindow)
        {
            if (string.IsNullOrWhiteSpace(actionName))
            {
                return HandlerResult.Fail("Action name cannot be null or empty");
            }

            var handler = _registry.GetHandler(actionName);
            if (handler == null)
            {
                return HandlerResult.Fail($"No handler registered for action: {actionName}");
            }

            return await ExecuteAsync(handler, value, mainWindow);
        }

        /// <summary>
        /// 安全执行指定动作的 Handler（字符串参数）
        /// </summary>
        public async Task<HandlerResult> ExecuteAsync(string actionName, string value, IMainWindow mainWindow)
        {
            if (string.IsNullOrWhiteSpace(actionName))
            {
                return HandlerResult.Fail("Action name cannot be null or empty");
            }

            var handler = _registry.GetHandler(actionName);
            if (handler == null)
            {
                return HandlerResult.Fail($"No handler registered for action: {actionName}");
            }

            return await ExecuteAsync(handler, value, mainWindow);
        }

        /// <summary>
        /// 安全执行指定的 Handler（无参数）
        /// </summary>
        public async Task<HandlerResult> ExecuteAsync(IActionHandler handler, IMainWindow mainWindow)
        {
            if (handler == null)
            {
                return HandlerResult.Fail("Handler cannot be null");
            }

            if (mainWindow == null)
            {
                return HandlerResult.Fail("MainWindow cannot be null");
            }

            try
            {
                Logger.Log($"Executing handler: {handler.GetType().Name}");
                await handler.Execute(mainWindow);
                Logger.Log($"Handler {handler.GetType().Name} executed successfully");
                return HandlerResult.Ok($"Handler {handler.GetType().Name} executed successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"Handler {handler.GetType().Name} failed: {ex.Message}");
                return HandlerResult.Fail($"Handler execution failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 安全执行指定的 Handler（整数参数）
        /// </summary>
        public async Task<HandlerResult> ExecuteAsync(IActionHandler handler, int value, IMainWindow mainWindow)
        {
            if (handler == null)
            {
                return HandlerResult.Fail("Handler cannot be null");
            }

            if (mainWindow == null)
            {
                return HandlerResult.Fail("MainWindow cannot be null");
            }

            try
            {
                Logger.Log($"Executing handler: {handler.GetType().Name} with value: {value}");
                await handler.Execute(value, mainWindow);
                Logger.Log($"Handler {handler.GetType().Name} executed successfully");
                return HandlerResult.Ok($"Handler {handler.GetType().Name} executed successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"Handler {handler.GetType().Name} failed: {ex.Message}");
                return HandlerResult.Fail($"Handler execution failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 安全执行指定的 Handler（字符串参数）
        /// </summary>
        public async Task<HandlerResult> ExecuteAsync(IActionHandler handler, string value, IMainWindow mainWindow)
        {
            if (handler == null)
            {
                return HandlerResult.Fail("Handler cannot be null");
            }

            if (mainWindow == null)
            {
                return HandlerResult.Fail("MainWindow cannot be null");
            }

            try
            {
                Logger.Log($"Executing handler: {handler.GetType().Name} with value: {value}");
                await handler.Execute(value, mainWindow);
                Logger.Log($"Handler {handler.GetType().Name} executed successfully");
                return HandlerResult.Ok($"Handler {handler.GetType().Name} executed successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"Handler {handler.GetType().Name} failed: {ex.Message}");
                return HandlerResult.Fail($"Handler execution failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 尝试执行动作，如果失败则返回默认结果
        /// </summary>
        public async Task<HandlerResult> TryExecuteAsync(
            string actionName,
            IMainWindow mainWindow,
            HandlerResult? defaultResult = null)
        {
            try
            {
                return await ExecuteAsync(actionName, mainWindow);
            }
            catch
            {
                return defaultResult ?? HandlerResult.Fail("Execution failed with unknown error");
            }
        }
    }
}
