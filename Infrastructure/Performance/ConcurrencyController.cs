using VPetLLM.Infrastructure.Logging;

namespace VPetLLM.Infrastructure.Performance
{
    /// <summary>
    /// 并发控制器
    /// 限制并发任务数量，防止资源耗尽
    /// </summary>
    public class ConcurrencyController
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly IStructuredLogger? _logger;
        private readonly int _maxConcurrency;
        private int _activeCount;
        private long _totalExecuted;

        /// <summary>
        /// 当前活跃任务数
        /// </summary>
        public int ActiveCount => _activeCount;

        /// <summary>
        /// 已执行的任务总数
        /// </summary>
        public long TotalExecuted => _totalExecuted;

        /// <summary>
        /// 最大并发数
        /// </summary>
        public int MaxConcurrency => _maxConcurrency;

        public ConcurrencyController(int maxConcurrency, IStructuredLogger? logger = null)
        {
            if (maxConcurrency <= 0)
                throw new ArgumentException("Max concurrency must be greater than 0", nameof(maxConcurrency));

            _maxConcurrency = maxConcurrency;
            _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            _logger = logger;
            _activeCount = 0;
            _totalExecuted = 0;
        }

        /// <summary>
        /// 执行异步操作（带并发控制）
        /// </summary>
        public async Task<T> ExecuteAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);

            Interlocked.Increment(ref _activeCount);
            _logger?.LogDebug($"ConcurrencyController: Task started (Active: {_activeCount}/{_maxConcurrency})");

            try
            {
                var result = await operation();
                Interlocked.Increment(ref _totalExecuted);
                return result;
            }
            finally
            {
                Interlocked.Decrement(ref _activeCount);
                _semaphore.Release();
                _logger?.LogDebug($"ConcurrencyController: Task completed (Active: {_activeCount}/{_maxConcurrency})");
            }
        }

        /// <summary>
        /// 执行异步操作（无返回值）
        /// </summary>
        public async Task ExecuteAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
        {
            await ExecuteAsync(async () =>
            {
                await operation();
                return 0; // Dummy return value
            }, cancellationToken);
        }

        /// <summary>
        /// 批量执行异步操作（带并发控制）
        /// </summary>
        public async Task<IEnumerable<T>> ExecuteBatchAsync<T>(
            IEnumerable<Func<Task<T>>> operations,
            CancellationToken cancellationToken = default)
        {
            _logger?.LogInformation($"ConcurrencyController: Starting batch execution");

            var tasks = new List<Task<T>>();

            foreach (var operation in operations)
            {
                var task = ExecuteAsync(operation, cancellationToken);
                tasks.Add(task);
            }

            var results = await Task.WhenAll(tasks);

            _logger?.LogInformation($"ConcurrencyController: Batch execution completed ({results.Length} tasks)");

            return results;
        }

        /// <summary>
        /// 等待所有活跃任务完成
        /// </summary>
        public async Task WaitForCompletionAsync(TimeSpan timeout)
        {
            var startTime = DateTime.UtcNow;

            while (_activeCount > 0)
            {
                if (DateTime.UtcNow - startTime > timeout)
                {
                    _logger?.LogWarning($"ConcurrencyController: Timeout waiting for completion (Active: {_activeCount})");
                    throw new TimeoutException($"Timeout waiting for {_activeCount} active tasks to complete");
                }

                await Task.Delay(100);
            }

            _logger?.LogInformation("ConcurrencyController: All tasks completed");
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            _semaphore?.Dispose();
        }
    }

    /// <summary>
    /// 速率限制器
    /// 限制操作的执行频率
    /// </summary>
    public class RateLimiter
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly int _maxRequests;
        private readonly TimeSpan _timeWindow;
        private readonly Queue<DateTime> _requestTimes;
        private readonly object _lock = new object();
        private readonly IStructuredLogger? _logger;

        public RateLimiter(int maxRequests, TimeSpan timeWindow, IStructuredLogger? logger = null)
        {
            if (maxRequests <= 0)
                throw new ArgumentException("Max requests must be greater than 0", nameof(maxRequests));

            _maxRequests = maxRequests;
            _timeWindow = timeWindow;
            _requestTimes = new Queue<DateTime>(maxRequests);
            _semaphore = new SemaphoreSlim(1, 1);
            _logger = logger;
        }

        /// <summary>
        /// 等待直到可以执行操作
        /// </summary>
        public async Task WaitAsync(CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);

            try
            {
                lock (_lock)
                {
                    var now = DateTime.UtcNow;

                    // 移除过期的请求时间
                    while (_requestTimes.Count > 0 && now - _requestTimes.Peek() > _timeWindow)
                    {
                        _requestTimes.Dequeue();
                    }

                    // 如果达到限制，等待
                    if (_requestTimes.Count >= _maxRequests)
                    {
                        var oldestRequest = _requestTimes.Peek();
                        var waitTime = _timeWindow - (now - oldestRequest);

                        if (waitTime > TimeSpan.Zero)
                        {
                            _logger?.LogDebug($"RateLimiter: Rate limit reached, waiting {waitTime.TotalMilliseconds}ms");
                            Thread.Sleep(waitTime);
                        }

                        // 重新检查并移除过期请求
                        now = DateTime.UtcNow;
                        while (_requestTimes.Count > 0 && now - _requestTimes.Peek() > _timeWindow)
                        {
                            _requestTimes.Dequeue();
                        }
                    }

                    // 记录新请求
                    _requestTimes.Enqueue(now);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// 执行操作（带速率限制）
        /// </summary>
        public async Task<T> ExecuteAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            await WaitAsync(cancellationToken);
            return await operation();
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            _semaphore?.Dispose();
        }
    }

    /// <summary>
    /// 任务调度器
    /// 管理任务队列和执行
    /// </summary>
    public class TaskScheduler
    {
        private readonly ConcurrencyController _concurrencyController;
        private readonly Queue<Func<Task>> _taskQueue;
        private readonly object _lock = new object();
        private readonly IStructuredLogger? _logger;
        private bool _isRunning;

        public TaskScheduler(int maxConcurrency, IStructuredLogger? logger = null)
        {
            _concurrencyController = new ConcurrencyController(maxConcurrency, logger);
            _taskQueue = new Queue<Func<Task>>();
            _logger = logger;
            _isRunning = false;
        }

        /// <summary>
        /// 添加任务到队列
        /// </summary>
        public void Enqueue(Func<Task> task)
        {
            lock (_lock)
            {
                _taskQueue.Enqueue(task);
                _logger?.LogDebug($"TaskScheduler: Task enqueued (Queue size: {_taskQueue.Count})");
            }
        }

        /// <summary>
        /// 开始处理队列
        /// </summary>
        public async Task ProcessQueueAsync(CancellationToken cancellationToken = default)
        {
            if (_isRunning)
            {
                _logger?.LogWarning("TaskScheduler: Already running");
                return;
            }

            _isRunning = true;
            _logger?.LogInformation("TaskScheduler: Started processing queue");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Func<Task>? task = null;

                    lock (_lock)
                    {
                        if (_taskQueue.Count > 0)
                        {
                            task = _taskQueue.Dequeue();
                        }
                    }

                    if (task != null)
                    {
                        await _concurrencyController.ExecuteAsync(task, cancellationToken);
                    }
                    else
                    {
                        // 队列为空，等待一段时间
                        await Task.Delay(100, cancellationToken);
                    }
                }
            }
            finally
            {
                _isRunning = false;
                _logger?.LogInformation("TaskScheduler: Stopped processing queue");
            }
        }

        /// <summary>
        /// 获取队列大小
        /// </summary>
        public int QueueSize
        {
            get
            {
                lock (_lock)
                {
                    return _taskQueue.Count;
                }
            }
        }
    }
}
