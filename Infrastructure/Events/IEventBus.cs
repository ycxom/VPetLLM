namespace VPetLLM.Infrastructure.Events
{
    /// <summary>
    /// 事件总线接口，提供事件发布和订阅功能
    /// </summary>
    public interface IEventBus : IDisposable
    {
        /// <summary>
        /// 发布强类型事件
        /// </summary>
        Task PublishAsync<T>(T eventData, CancellationToken cancellationToken = default) where T : class;

        /// <summary>
        /// 发布弱类型事件
        /// </summary>
        Task PublishAsync(string eventType, object eventData, CancellationToken cancellationToken = default);

        /// <summary>
        /// 订阅强类型事件
        /// </summary>
        void Subscribe<T>(Func<T, Task> handler) where T : class;

        /// <summary>
        /// 订阅强类型事件（异步版本）
        /// </summary>
        Task SubscribeAsync<T>(Func<T, Task> handler) where T : class;

        /// <summary>
        /// 订阅强类型事件（带优先级）
        /// </summary>
        void Subscribe<T>(Func<T, Task> handler, int priority = 0) where T : class;

        /// <summary>
        /// 订阅强类型事件处理器
        /// </summary>
        void Subscribe<T>(IEventHandler<T> handler) where T : class;

        /// <summary>
        /// 订阅弱类型事件
        /// </summary>
        void Subscribe(string eventType, Func<object, Task> handler);

        /// <summary>
        /// 订阅弱类型事件（带优先级和过滤器）
        /// </summary>
        void Subscribe(string eventType, Func<object, Task> handler, int priority = 0, Func<object, bool> filter = null);

        /// <summary>
        /// 取消订阅强类型事件处理器
        /// </summary>
        void Unsubscribe<T>(IEventHandler<T> handler) where T : class;

        /// <summary>
        /// 取消订阅弱类型事件
        /// </summary>
        void Unsubscribe(string eventType, Func<object, Task> handler);

        /// <summary>
        /// 取消所有订阅
        /// </summary>
        void UnsubscribeAll();

        /// <summary>
        /// 获取订阅者数量
        /// </summary>
        int GetSubscriberCount<T>() where T : class;

        /// <summary>
        /// 获取订阅者数量
        /// </summary>
        int GetSubscriberCount(string eventType);
    }

    /// <summary>
    /// 强类型事件处理器接口
    /// </summary>
    public interface IEventHandler<in T> where T : class
    {
        /// <summary>
        /// 处理事件
        /// </summary>
        Task HandleAsync(T eventData, CancellationToken cancellationToken = default);

        /// <summary>
        /// 处理器优先级（数值越大优先级越高）
        /// </summary>
        int Priority { get; }
    }

    /// <summary>
    /// 基础事件类
    /// </summary>
    public abstract class BaseEvent
    {
        /// <summary>
        /// 事件ID
        /// </summary>
        public Guid EventId { get; } = Guid.NewGuid();

        /// <summary>
        /// 事件时间戳
        /// </summary>
        public DateTime Timestamp { get; } = DateTime.UtcNow;

        /// <summary>
        /// 事件源
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// 事件元数据
        /// </summary>
        public System.Collections.Generic.Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// 事件发布异常
    /// </summary>
    public class EventPublishException : Exception
    {
        public string EventType { get; }
        public object EventData { get; }

        public EventPublishException(string eventType, object eventData, string message, Exception innerException = null)
            : base(message, innerException)
        {
            EventType = eventType;
            EventData = eventData;
        }
    }

    /// <summary>
    /// 事件处理异常
    /// </summary>
    public class EventHandlerException : Exception
    {
        public string EventType { get; }
        public object EventData { get; }
        public string HandlerName { get; }

        public EventHandlerException(string eventType, object eventData, string handlerName, string message, Exception innerException = null)
            : base(message, innerException)
        {
            EventType = eventType;
            EventData = eventData;
            HandlerName = handlerName;
        }
    }
}