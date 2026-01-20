using System.Collections.Concurrent;
using VPetLLM.Infrastructure.Logging;

namespace VPetLLM.Infrastructure.Performance
{
    /// <summary>
    /// 通用对象池实现
    /// 用于频繁创建和销毁的对象，减少 GC 压力
    /// </summary>
    /// <typeparam name="T">池化对象类型</typeparam>
    public class ObjectPool<T> where T : class
    {
        private readonly ConcurrentBag<T> _objects;
        private readonly Func<T> _objectFactory;
        private readonly Action<T>? _resetAction;
        private readonly int _maxSize;
        private int _currentSize;
        private readonly IStructuredLogger? _logger;

        /// <summary>
        /// 当前池中对象数量
        /// </summary>
        public int Count => _objects.Count;

        /// <summary>
        /// 已创建的对象总数
        /// </summary>
        public int TotalCreated => _currentSize;

        /// <summary>
        /// 创建对象池
        /// </summary>
        /// <param name="objectFactory">对象创建工厂</param>
        /// <param name="resetAction">对象重置操作（可选）</param>
        /// <param name="maxSize">最大池大小</param>
        /// <param name="logger">日志记录器（可选）</param>
        public ObjectPool(
            Func<T> objectFactory,
            Action<T>? resetAction = null,
            int maxSize = 100,
            IStructuredLogger? logger = null)
        {
            _objectFactory = objectFactory ?? throw new ArgumentNullException(nameof(objectFactory));
            _resetAction = resetAction;
            _maxSize = maxSize;
            _logger = logger;
            _objects = new ConcurrentBag<T>();
            _currentSize = 0;
        }

        /// <summary>
        /// 从池中获取对象
        /// </summary>
        public T Get()
        {
            if (_objects.TryTake(out var item))
            {
                _logger?.LogDebug($"ObjectPool: Reused object from pool (Count: {_objects.Count})");
                return item;
            }

            // 池中没有可用对象，创建新对象
            var newItem = _objectFactory();
            Interlocked.Increment(ref _currentSize);

            _logger?.LogDebug($"ObjectPool: Created new object (Total: {_currentSize})");
            return newItem;
        }

        /// <summary>
        /// 将对象归还到池中
        /// </summary>
        public void Return(T item)
        {
            if (item is null)
                return;

            // 如果池已满，不再归还
            if (_objects.Count >= _maxSize)
            {
                _logger?.LogDebug($"ObjectPool: Pool is full, discarding object");

                // 如果对象实现了 IDisposable，释放它
                if (item is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                return;
            }

            // 重置对象状态
            try
            {
                _resetAction?.Invoke(item);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"ObjectPool: Failed to reset object: {ex.Message}");
                return;
            }

            _objects.Add(item);
            _logger?.LogDebug($"ObjectPool: Returned object to pool (Count: {_objects.Count})");
        }

        /// <summary>
        /// 清空对象池
        /// </summary>
        public void Clear()
        {
            while (_objects.TryTake(out var item))
            {
                if (item is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            _logger?.LogInformation("ObjectPool: Cleared all objects");
        }

        /// <summary>
        /// 预热对象池（预先创建指定数量的对象）
        /// </summary>
        public void Prewarm(int count)
        {
            count = Math.Min(count, _maxSize);

            for (int i = 0; i < count; i++)
            {
                var item = _objectFactory();
                Interlocked.Increment(ref _currentSize);
                _objects.Add(item);
            }

            _logger?.LogInformation($"ObjectPool: Prewarmed with {count} objects");
        }
    }

    /// <summary>
    /// 对象池租用包装器
    /// 使用 using 语句自动归还对象
    /// </summary>
    public struct PooledObject<T> : IDisposable where T : class
    {
        private readonly ObjectPool<T> _pool;
        private readonly T _object;
        private bool _disposed;

        public T Object => _object;

        internal PooledObject(ObjectPool<T> pool, T obj)
        {
            _pool = pool;
            _object = obj;
            _disposed = false;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _pool.Return(_object);
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// 对象池扩展方法
    /// </summary>
    public static class ObjectPoolExtensions
    {
        /// <summary>
        /// 租用对象（使用 using 语句自动归还）
        /// </summary>
        public static PooledObject<T> Rent<T>(this ObjectPool<T> pool) where T : class
        {
            var obj = pool.Get();
            return new PooledObject<T>(pool, obj);
        }
    }
}
