using VPetLLM.Utils.System;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// Handler 注册表实现
    /// </summary>
    public class HandlerRegistry : IHandlerRegistry
    {
        private readonly Dictionary<string, IActionHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        /// <summary>
        /// 全局单例实例
        /// </summary>
        public static HandlerRegistry Instance { get; } = new();

        /// <inheritdoc/>
        public void Register(string actionName, IActionHandler handler)
        {
            if (string.IsNullOrWhiteSpace(actionName))
            {
                throw new ArgumentException("Action name cannot be null or empty", nameof(actionName));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            lock (_lock)
            {
                if (_handlers.ContainsKey(actionName))
                {
                    Logger.Log($"Handler for action '{actionName}' is being replaced");
                }
                _handlers[actionName] = handler;
                Logger.Log($"Handler registered for action: {actionName}");
            }
        }

        /// <inheritdoc/>
        public void Register<T>(T handler) where T : IActionHandler
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            // 使用类型名称作为默认动作名称（去掉 "Handler" 后缀）
            var typeName = typeof(T).Name;
            var actionName = typeName.EndsWith("Handler", StringComparison.OrdinalIgnoreCase)
                ? typeName.Substring(0, typeName.Length - 7)
                : typeName;

            Register(actionName, handler);
        }

        /// <inheritdoc/>
        public IActionHandler? GetHandler(string actionName)
        {
            if (string.IsNullOrWhiteSpace(actionName))
            {
                return null;
            }

            lock (_lock)
            {
                return _handlers.TryGetValue(actionName, out var handler) ? handler : null;
            }
        }

        /// <inheritdoc/>
        public IEnumerable<IActionHandler> GetAllHandlers()
        {
            lock (_lock)
            {
                return _handlers.Values.ToList();
            }
        }

        /// <inheritdoc/>
        public IEnumerable<string> GetRegisteredActionNames()
        {
            lock (_lock)
            {
                return _handlers.Keys.ToList();
            }
        }

        /// <inheritdoc/>
        public bool IsRegistered(string actionName)
        {
            if (string.IsNullOrWhiteSpace(actionName))
            {
                return false;
            }

            lock (_lock)
            {
                return _handlers.ContainsKey(actionName);
            }
        }

        /// <inheritdoc/>
        public bool Unregister(string actionName)
        {
            if (string.IsNullOrWhiteSpace(actionName))
            {
                return false;
            }

            lock (_lock)
            {
                var removed = _handlers.Remove(actionName);
                if (removed)
                {
                    Logger.Log($"Handler unregistered for action: {actionName}");
                }
                return removed;
            }
        }

        /// <inheritdoc/>
        public void Clear()
        {
            lock (_lock)
            {
                _handlers.Clear();
                Logger.Log("All handlers cleared from registry");
            }
        }
    }
}
