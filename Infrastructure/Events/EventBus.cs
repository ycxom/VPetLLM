using System.Collections.Concurrent;
using VPetLLM.Utils.System;

namespace VPetLLM.Infrastructure.Events
{
    /// <summary>
    /// 事件总线实现
    /// </summary>
    public class EventBus : IEventBus
    {
        private readonly ConcurrentDictionary<Type, ConcurrentBag<EventSubscription>> _strongTypedSubscriptions = new();
        private readonly ConcurrentDictionary<string, ConcurrentBag<WeakEventSubscription>> _weakTypedSubscriptions = new();
        private readonly SemaphoreSlim _publishSemaphore = new(Environment.ProcessorCount * 2);
        private bool _disposed = false;

        public async Task PublishAsync<T>(T eventData, CancellationToken cancellationToken = default) where T : class
        {
            ThrowIfDisposed();
            if (eventData == null)
                throw new ArgumentNullException(nameof(eventData));

            var eventType = typeof(T);
            var eventTypeName = eventType.Name;

            Logger.Log($"Publishing event: {eventTypeName}");

            await _publishSemaphore.WaitAsync(cancellationToken);
            try
            {
                var tasks = new List<Task>();

                // 处理强类型订阅
                if (_strongTypedSubscriptions.TryGetValue(eventType, out var strongSubscriptions))
                {
                    var sortedSubscriptions = strongSubscriptions.OrderByDescending(s => s.Priority).ToList();
                    foreach (var subscription in sortedSubscriptions)
                    {
                        tasks.Add(HandleSubscriptionAsync(subscription, eventData, eventTypeName, cancellationToken));
                    }
                }

                // 处理弱类型订阅
                if (_weakTypedSubscriptions.TryGetValue(eventTypeName, out var weakSubscriptions))
                {
                    var sortedSubscriptions = weakSubscriptions.OrderByDescending(s => s.Priority).ToList();
                    foreach (var subscription in sortedSubscriptions)
                    {
                        if (subscription.Filter == null || subscription.Filter(eventData))
                        {
                            tasks.Add(HandleWeakSubscriptionAsync(subscription, eventData, eventTypeName, cancellationToken));
                        }
                    }
                }

                if (tasks.Any())
                {
                    await Task.WhenAll(tasks);
                }

                Logger.Log($"Event published successfully: {eventTypeName}, Handlers: {tasks.Count}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error publishing event {eventTypeName}: {ex.Message}");
                throw new EventPublishException(eventTypeName, eventData, "Failed to publish event", ex);
            }
            finally
            {
                _publishSemaphore.Release();
            }
        }

        public async Task PublishAsync(string eventType, object eventData, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(eventType))
                throw new ArgumentException("Event type cannot be null or empty", nameof(eventType));

            Logger.Log($"Publishing weak-typed event: {eventType}");

            await _publishSemaphore.WaitAsync(cancellationToken);
            try
            {
                var tasks = new List<Task>();

                if (_weakTypedSubscriptions.TryGetValue(eventType, out var subscriptions))
                {
                    var sortedSubscriptions = subscriptions.OrderByDescending(s => s.Priority).ToList();
                    foreach (var subscription in sortedSubscriptions)
                    {
                        if (subscription.Filter == null || subscription.Filter(eventData))
                        {
                            tasks.Add(HandleWeakSubscriptionAsync(subscription, eventData, eventType, cancellationToken));
                        }
                    }
                }

                if (tasks.Any())
                {
                    await Task.WhenAll(tasks);
                }

                Logger.Log($"Weak-typed event published successfully: {eventType}, Handlers: {tasks.Count}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error publishing weak-typed event {eventType}: {ex.Message}");
                throw new EventPublishException(eventType, eventData, "Failed to publish weak-typed event", ex);
            }
            finally
            {
                _publishSemaphore.Release();
            }
        }

        public void Subscribe<T>(Func<T, Task> handler) where T : class
        {
            Subscribe(handler, 0);
        }

        public Task SubscribeAsync<T>(Func<T, Task> handler) where T : class
        {
            Subscribe(handler, 0);
            return Task.CompletedTask;
        }

        public void Subscribe<T>(Func<T, Task> handler, int priority = 0) where T : class
        {
            ThrowIfDisposed();
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var eventType = typeof(T);
            var subscription = new EventSubscription(handler, priority);

            _strongTypedSubscriptions.AddOrUpdate(
                eventType,
                new ConcurrentBag<EventSubscription> { subscription },
                (key, existing) =>
                {
                    existing.Add(subscription);
                    return existing;
                });

            Logger.Log($"Subscribed to event: {eventType.Name}, Priority: {priority}");
        }

        public void Subscribe<T>(IEventHandler<T> handler) where T : class
        {
            ThrowIfDisposed();
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            Subscribe<T>(eventData => handler.HandleAsync(eventData, CancellationToken.None), handler.Priority);
        }

        public void Subscribe(string eventType, Func<object, Task> handler)
        {
            Subscribe(eventType, handler, 0, null);
        }

        public void Subscribe(string eventType, Func<object, Task> handler, int priority = 0, Func<object, bool> filter = null)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(eventType))
                throw new ArgumentException("Event type cannot be null or empty", nameof(eventType));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var subscription = new WeakEventSubscription(handler, priority, filter);

            _weakTypedSubscriptions.AddOrUpdate(
                eventType,
                new ConcurrentBag<WeakEventSubscription> { subscription },
                (key, existing) =>
                {
                    existing.Add(subscription);
                    return existing;
                });

            Logger.Log($"Subscribed to weak-typed event: {eventType}, Priority: {priority}");
        }

        public void Unsubscribe<T>(IEventHandler<T> handler) where T : class
        {
            ThrowIfDisposed();
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var eventType = typeof(T);
            if (_strongTypedSubscriptions.TryGetValue(eventType, out var subscriptions))
            {
                // Note: ConcurrentBag doesn't support removal, so we create a new bag without the handler
                var newSubscriptions = new ConcurrentBag<EventSubscription>();
                foreach (var subscription in subscriptions)
                {
                    if (!ReferenceEquals(subscription.Handler.Target, handler))
                    {
                        newSubscriptions.Add(subscription);
                    }
                }
                _strongTypedSubscriptions.TryUpdate(eventType, newSubscriptions, subscriptions);
            }

            Logger.Log($"Unsubscribed from event: {eventType.Name}");
        }

        public void Unsubscribe(string eventType, Func<object, Task> handler)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(eventType))
                throw new ArgumentException("Event type cannot be null or empty", nameof(eventType));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (_weakTypedSubscriptions.TryGetValue(eventType, out var subscriptions))
            {
                var newSubscriptions = new ConcurrentBag<WeakEventSubscription>();
                foreach (var subscription in subscriptions)
                {
                    if (!ReferenceEquals(subscription.Handler, handler))
                    {
                        newSubscriptions.Add(subscription);
                    }
                }
                _weakTypedSubscriptions.TryUpdate(eventType, newSubscriptions, subscriptions);
            }

            Logger.Log($"Unsubscribed from weak-typed event: {eventType}");
        }

        public void UnsubscribeAll()
        {
            ThrowIfDisposed();
            _strongTypedSubscriptions.Clear();
            _weakTypedSubscriptions.Clear();
            Logger.Log("All subscriptions cleared");
        }

        public int GetSubscriberCount<T>() where T : class
        {
            ThrowIfDisposed();
            var eventType = typeof(T);
            return _strongTypedSubscriptions.TryGetValue(eventType, out var subscriptions) ? subscriptions.Count : 0;
        }

        public int GetSubscriberCount(string eventType)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(eventType))
                return 0;

            return _weakTypedSubscriptions.TryGetValue(eventType, out var subscriptions) ? subscriptions.Count : 0;
        }

        private async Task HandleSubscriptionAsync<T>(EventSubscription subscription, T eventData, string eventTypeName, CancellationToken cancellationToken)
        {
            try
            {
                if (subscription.Handler is Func<T, Task> typedHandler)
                {
                    await typedHandler(eventData);
                }
                else if (subscription.Handler is Func<object, Task> objectHandler)
                {
                    await objectHandler(eventData);
                }
            }
            catch (Exception ex)
            {
                var handlerName = subscription.Handler.Method.DeclaringType?.Name ?? "Unknown";
                Logger.Log($"Error in event handler {handlerName} for event {eventTypeName}: {ex.Message}");

                // 异常隔离：不重新抛出异常，避免影响其他处理器
                // 但记录详细的异常信息
                var handlerException = new EventHandlerException(eventTypeName, eventData, handlerName, "Event handler failed", ex);
                Logger.Log($"Event handler exception details: {handlerException}");
            }
        }

        private async Task HandleWeakSubscriptionAsync(WeakEventSubscription subscription, object eventData, string eventTypeName, CancellationToken cancellationToken)
        {
            try
            {
                await subscription.Handler(eventData);
            }
            catch (Exception ex)
            {
                var handlerName = subscription.Handler.Method.DeclaringType?.Name ?? "Unknown";
                Logger.Log($"Error in weak-typed event handler {handlerName} for event {eventTypeName}: {ex.Message}");

                // 异常隔离：不重新抛出异常，避免影响其他处理器
                var handlerException = new EventHandlerException(eventTypeName, eventData, handlerName, "Weak-typed event handler failed", ex);
                Logger.Log($"Weak-typed event handler exception details: {handlerException}");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(EventBus));
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            UnsubscribeAll();
            _publishSemaphore?.Dispose();

            Logger.Log("EventBus disposed");
        }

        /// <summary>
        /// 强类型事件订阅
        /// </summary>
        private class EventSubscription
        {
            public Delegate Handler { get; }
            public int Priority { get; }

            public EventSubscription(Delegate handler, int priority)
            {
                Handler = handler;
                Priority = priority;
            }
        }

        /// <summary>
        /// 弱类型事件订阅
        /// </summary>
        private class WeakEventSubscription
        {
            public Func<object, Task> Handler { get; }
            public int Priority { get; }
            public Func<object, bool> Filter { get; }

            public WeakEventSubscription(Func<object, Task> handler, int priority, Func<object, bool> filter)
            {
                Handler = handler;
                Priority = priority;
                Filter = filter;
            }
        }
    }
}