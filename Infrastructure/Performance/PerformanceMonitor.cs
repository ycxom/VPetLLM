using System.Collections.Concurrent;
using System.Diagnostics;
using VPetLLM.Infrastructure.Logging;

namespace VPetLLM.Infrastructure.Performance
{
    /// <summary>
    /// 性能指标
    /// </summary>
    public class PerformanceMetrics
    {
        public string Name { get; set; } = "";
        public long TotalExecutions { get; set; }
        public long TotalErrors { get; set; }
        public double AverageExecutionTimeMs { get; set; }
        public double MinExecutionTimeMs { get; set; }
        public double MaxExecutionTimeMs { get; set; }
        public double P50ExecutionTimeMs { get; set; }
        public double P95ExecutionTimeMs { get; set; }
        public double P99ExecutionTimeMs { get; set; }
        public long CurrentMemoryUsageMB { get; set; }
        public long PeakMemoryUsageMB { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// 性能监控器
    /// 收集和报告性能指标
    /// </summary>
    public class PerformanceMonitor
    {
        private readonly ConcurrentDictionary<string, MetricCollector> _collectors;
        private readonly IStructuredLogger? _logger;
        private readonly Timer _reportTimer;
        private readonly TimeSpan _reportInterval;

        public PerformanceMonitor(TimeSpan? reportInterval = null, IStructuredLogger? logger = null)
        {
            _collectors = new ConcurrentDictionary<string, MetricCollector>();
            _logger = logger;
            _reportInterval = reportInterval ?? TimeSpan.FromMinutes(5);

            // 定期报告性能指标
            _reportTimer = new Timer(ReportMetrics, null, _reportInterval, _reportInterval);
        }

        /// <summary>
        /// 测量操作执行时间
        /// </summary>
        public async Task<T> MeasureAsync<T>(
            string operationName,
            Func<Task<T>> operation)
        {
            var collector = _collectors.GetOrAdd(operationName, _ => new MetricCollector(operationName));
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var result = await operation();
                stopwatch.Stop();
                collector.RecordSuccess(stopwatch.Elapsed.TotalMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                collector.RecordError(stopwatch.Elapsed.TotalMilliseconds);
                _logger?.LogError($"Operation '{operationName}' failed", ex);
                throw;
            }
        }

        /// <summary>
        /// 测量操作执行时间（无返回值）
        /// </summary>
        public async Task MeasureAsync(
            string operationName,
            Func<Task> operation)
        {
            await MeasureAsync(operationName, async () =>
            {
                await operation();
                return 0; // Dummy return value
            });
        }

        /// <summary>
        /// 获取指定操作的性能指标
        /// </summary>
        public PerformanceMetrics? GetMetrics(string operationName)
        {
            if (_collectors.TryGetValue(operationName, out var collector))
            {
                return collector.GetMetrics();
            }
            return null;
        }

        /// <summary>
        /// 获取所有性能指标
        /// </summary>
        public IEnumerable<PerformanceMetrics> GetAllMetrics()
        {
            return _collectors.Values.Select(c => c.GetMetrics());
        }

        /// <summary>
        /// 重置指定操作的指标
        /// </summary>
        public void ResetMetrics(string operationName)
        {
            if (_collectors.TryGetValue(operationName, out var collector))
            {
                collector.Reset();
                _logger?.LogInformation($"PerformanceMonitor: Reset metrics for '{operationName}'");
            }
        }

        /// <summary>
        /// 重置所有指标
        /// </summary>
        public void ResetAllMetrics()
        {
            foreach (var collector in _collectors.Values)
            {
                collector.Reset();
            }
            _logger?.LogInformation("PerformanceMonitor: Reset all metrics");
        }

        /// <summary>
        /// 定期报告性能指标
        /// </summary>
        private void ReportMetrics(object? state)
        {
            try
            {
                var allMetrics = GetAllMetrics().ToList();

                if (allMetrics.Count == 0)
                    return;

                _logger?.LogInformation("=== Performance Report ===");

                foreach (var metrics in allMetrics)
                {
                    _logger?.LogInformation(
                        $"Operation: {metrics.Name}\n" +
                        $"  Executions: {metrics.TotalExecutions} (Errors: {metrics.TotalErrors})\n" +
                        $"  Avg Time: {metrics.AverageExecutionTimeMs:F2}ms\n" +
                        $"  Min/Max: {metrics.MinExecutionTimeMs:F2}ms / {metrics.MaxExecutionTimeMs:F2}ms\n" +
                        $"  P50/P95/P99: {metrics.P50ExecutionTimeMs:F2}ms / {metrics.P95ExecutionTimeMs:F2}ms / {metrics.P99ExecutionTimeMs:F2}ms\n" +
                        $"  Memory: {metrics.CurrentMemoryUsageMB}MB (Peak: {metrics.PeakMemoryUsageMB}MB)"
                    );
                }

                _logger?.LogInformation("========================");
            }
            catch (Exception ex)
            {
                _logger?.LogError("Failed to report performance metrics", ex);
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            _reportTimer?.Dispose();
        }
    }

    /// <summary>
    /// 指标收集器
    /// </summary>
    internal class MetricCollector
    {
        private readonly string _name;
        private readonly ConcurrentBag<double> _executionTimes;
        private long _totalExecutions;
        private long _totalErrors;
        private long _peakMemoryUsageMB;
        private readonly object _lock = new object();

        public MetricCollector(string name)
        {
            _name = name;
            _executionTimes = new ConcurrentBag<double>();
            _totalExecutions = 0;
            _totalErrors = 0;
            _peakMemoryUsageMB = 0;
        }

        public void RecordSuccess(double executionTimeMs)
        {
            _executionTimes.Add(executionTimeMs);
            Interlocked.Increment(ref _totalExecutions);
            UpdateMemoryUsage();
        }

        public void RecordError(double executionTimeMs)
        {
            _executionTimes.Add(executionTimeMs);
            Interlocked.Increment(ref _totalExecutions);
            Interlocked.Increment(ref _totalErrors);
            UpdateMemoryUsage();
        }

        private void UpdateMemoryUsage()
        {
            var currentMemory = GC.GetTotalMemory(false) / 1024 / 1024; // Convert to MB

            lock (_lock)
            {
                if (currentMemory > _peakMemoryUsageMB)
                {
                    _peakMemoryUsageMB = currentMemory;
                }
            }
        }

        public PerformanceMetrics GetMetrics()
        {
            var times = _executionTimes.ToArray();

            if (times.Length == 0)
            {
                return new PerformanceMetrics
                {
                    Name = _name,
                    TotalExecutions = _totalExecutions,
                    TotalErrors = _totalErrors,
                    LastUpdated = DateTime.UtcNow
                };
            }

            Array.Sort(times);

            return new PerformanceMetrics
            {
                Name = _name,
                TotalExecutions = _totalExecutions,
                TotalErrors = _totalErrors,
                AverageExecutionTimeMs = times.Average(),
                MinExecutionTimeMs = times.Min(),
                MaxExecutionTimeMs = times.Max(),
                P50ExecutionTimeMs = GetPercentile(times, 0.50),
                P95ExecutionTimeMs = GetPercentile(times, 0.95),
                P99ExecutionTimeMs = GetPercentile(times, 0.99),
                CurrentMemoryUsageMB = GC.GetTotalMemory(false) / 1024 / 1024,
                PeakMemoryUsageMB = _peakMemoryUsageMB,
                LastUpdated = DateTime.UtcNow
            };
        }

        private double GetPercentile(double[] sortedValues, double percentile)
        {
            if (sortedValues.Length == 0)
                return 0;

            int index = (int)Math.Ceiling(percentile * sortedValues.Length) - 1;
            index = Math.Max(0, Math.Min(sortedValues.Length - 1, index));
            return sortedValues[index];
        }

        public void Reset()
        {
            _executionTimes.Clear();
            Interlocked.Exchange(ref _totalExecutions, 0);
            Interlocked.Exchange(ref _totalErrors, 0);

            lock (_lock)
            {
                _peakMemoryUsageMB = 0;
            }
        }
    }

    /// <summary>
    /// 性能计时器（用于 using 语句）
    /// </summary>
    public class PerformanceTimer : IDisposable
    {
        private readonly Stopwatch _stopwatch;
        private readonly Action<TimeSpan> _onComplete;

        public PerformanceTimer(Action<TimeSpan> onComplete)
        {
            _onComplete = onComplete;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            _onComplete?.Invoke(_stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// 性能监控扩展方法
    /// </summary>
    public static class PerformanceMonitorExtensions
    {
        /// <summary>
        /// 创建性能计时器
        /// </summary>
        public static PerformanceTimer StartTimer(this PerformanceMonitor monitor, string operationName)
        {
            return new PerformanceTimer(elapsed =>
            {
                // 可以在这里记录到监控器
            });
        }
    }
}
