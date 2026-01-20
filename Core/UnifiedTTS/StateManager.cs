using System.Collections.Concurrent;
using VPetLLM.Models;
using VPetLLM.Utils.System;

namespace VPetLLM.Core.UnifiedTTS
{
    /// <summary>
    /// TTS 状态管理器接口
    /// </summary>
    public interface ITTSStateManager
    {
        /// <summary>
        /// 获取当前服务状态
        /// </summary>
        /// <returns>服务状态</returns>
        Task<ServiceStatus> GetServiceStatusAsync();

        /// <summary>
        /// 更新服务状态
        /// </summary>
        /// <param name="status">新的服务状态</param>
        Task UpdateServiceStatusAsync(ServiceStatus status);

        /// <summary>
        /// 执行健康检查
        /// </summary>
        /// <returns>健康检查结果</returns>
        Task<HealthCheckResult> PerformHealthCheckAsync();

        /// <summary>
        /// 获取活跃请求信息
        /// </summary>
        /// <returns>活跃请求列表</returns>
        Task<IEnumerable<ActiveRequestInfo>> GetActiveRequestsAsync();

        /// <summary>
        /// 注册活跃请求
        /// </summary>
        /// <param name="requestInfo">请求信息</param>
        Task RegisterActiveRequestAsync(ActiveRequestInfo requestInfo);

        /// <summary>
        /// 注销活跃请求
        /// </summary>
        /// <param name="requestId">请求ID</param>
        Task UnregisterActiveRequestAsync(string requestId);

        /// <summary>
        /// 获取性能指标
        /// </summary>
        /// <returns>性能指标</returns>
        Task<PerformanceMetrics> GetPerformanceMetricsAsync();

        /// <summary>
        /// 重置统计信息
        /// </summary>
        Task ResetStatisticsAsync();

        /// <summary>
        /// 获取系统资源使用情况
        /// </summary>
        /// <returns>资源使用情况</returns>
        Task<ResourceUsage> GetResourceUsageAsync();
    }

    /// <summary>
    /// 健康检查结果
    /// </summary>
    public class HealthCheckResult
    {
        /// <summary>
        /// 是否健康
        /// </summary>
        public bool IsHealthy { get; set; }

        /// <summary>
        /// 检查时间
        /// </summary>
        public DateTime CheckTime { get; set; }

        /// <summary>
        /// 检查详情
        /// </summary>
        public Dictionary<string, HealthCheckDetail> Details { get; set; }

        /// <summary>
        /// 总体状态消息
        /// </summary>
        public string OverallMessage { get; set; }

        /// <summary>
        /// 检查耗时（毫秒）
        /// </summary>
        public double CheckDurationMs { get; set; }

        public HealthCheckResult()
        {
            CheckTime = DateTime.UtcNow;
            Details = new Dictionary<string, HealthCheckDetail>();
        }

        /// <summary>
        /// 添加检查详情
        /// </summary>
        /// <param name="component">组件名称</param>
        /// <param name="isHealthy">是否健康</param>
        /// <param name="message">消息</param>
        /// <param name="responseTime">响应时间</param>
        public void AddDetail(string component, bool isHealthy, string message, double? responseTime = null)
        {
            Details[component] = new HealthCheckDetail
            {
                IsHealthy = isHealthy,
                Message = message,
                ResponseTime = responseTime,
                CheckTime = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// 健康检查详情
    /// </summary>
    public class HealthCheckDetail
    {
        /// <summary>
        /// 是否健康
        /// </summary>
        public bool IsHealthy { get; set; }

        /// <summary>
        /// 状态消息
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// 响应时间（毫秒）
        /// </summary>
        public double? ResponseTime { get; set; }

        /// <summary>
        /// 检查时间
        /// </summary>
        public DateTime CheckTime { get; set; }
    }

    /// <summary>
    /// 活跃请求信息
    /// </summary>
    public class ActiveRequestInfo
    {
        /// <summary>
        /// 请求ID
        /// </summary>
        public string RequestId { get; set; }

        /// <summary>
        /// TTS类型
        /// </summary>
        public TTSType TTSType { get; set; }

        /// <summary>
        /// 请求开始时间
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// 文本长度
        /// </summary>
        public int TextLength { get; set; }

        /// <summary>
        /// 当前状态
        /// </summary>
        public RequestStatus Status { get; set; }

        /// <summary>
        /// 已运行时间
        /// </summary>
        public TimeSpan ElapsedTime => DateTime.UtcNow - StartTime;

        /// <summary>
        /// 附加信息
        /// </summary>
        public Dictionary<string, object> AdditionalInfo { get; set; }

        public ActiveRequestInfo()
        {
            StartTime = DateTime.UtcNow;
            Status = RequestStatus.Processing;
            AdditionalInfo = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// 请求状态
    /// </summary>
    public enum RequestStatus
    {
        /// <summary>
        /// 排队中
        /// </summary>
        Queued,

        /// <summary>
        /// 处理中
        /// </summary>
        Processing,

        /// <summary>
        /// 重试中
        /// </summary>
        Retrying,

        /// <summary>
        /// 完成
        /// </summary>
        Completed,

        /// <summary>
        /// 失败
        /// </summary>
        Failed,

        /// <summary>
        /// 已取消
        /// </summary>
        Cancelled
    }

    /// <summary>
    /// 性能指标
    /// </summary>
    public class PerformanceMetrics
    {
        /// <summary>
        /// 请求吞吐量（每分钟）
        /// </summary>
        public double RequestsPerMinute { get; set; }

        /// <summary>
        /// 平均响应时间（毫秒）
        /// </summary>
        public double AverageResponseTime { get; set; }

        /// <summary>
        /// 95百分位响应时间（毫秒）
        /// </summary>
        public double P95ResponseTime { get; set; }

        /// <summary>
        /// 99百分位响应时间（毫秒）
        /// </summary>
        public double P99ResponseTime { get; set; }

        /// <summary>
        /// 错误率（百分比）
        /// </summary>
        public double ErrorRate { get; set; }

        /// <summary>
        /// 当前并发请求数
        /// </summary>
        public int ConcurrentRequests { get; set; }

        /// <summary>
        /// 最大并发请求数
        /// </summary>
        public int MaxConcurrentRequests { get; set; }

        /// <summary>
        /// 统计时间窗口（分钟）
        /// </summary>
        public int TimeWindowMinutes { get; set; }

        /// <summary>
        /// 按TTS类型分组的指标
        /// </summary>
        public Dictionary<TTSType, TTSTypeMetrics> MetricsByType { get; set; }

        public PerformanceMetrics()
        {
            MetricsByType = new Dictionary<TTSType, TTSTypeMetrics>();
            TimeWindowMinutes = 60; // 默认1小时窗口
        }
    }

    /// <summary>
    /// 按TTS类型的指标
    /// </summary>
    public class TTSTypeMetrics
    {
        /// <summary>
        /// 请求数量
        /// </summary>
        public long RequestCount { get; set; }

        /// <summary>
        /// 成功数量
        /// </summary>
        public long SuccessCount { get; set; }

        /// <summary>
        /// 平均响应时间
        /// </summary>
        public double AverageResponseTime { get; set; }

        /// <summary>
        /// 成功率
        /// </summary>
        public double SuccessRate => RequestCount > 0 ? (double)SuccessCount / RequestCount * 100 : 0;
    }

    /// <summary>
    /// 资源使用情况
    /// </summary>
    public class ResourceUsage
    {
        /// <summary>
        /// 内存使用量（MB）
        /// </summary>
        public double MemoryUsageMB { get; set; }

        /// <summary>
        /// CPU使用率（百分比）
        /// </summary>
        public double CpuUsagePercent { get; set; }

        /// <summary>
        /// 线程数量
        /// </summary>
        public int ThreadCount { get; set; }

        /// <summary>
        /// 句柄数量
        /// </summary>
        public int HandleCount { get; set; }

        /// <summary>
        /// 网络连接数
        /// </summary>
        public int NetworkConnections { get; set; }

        /// <summary>
        /// 检查时间
        /// </summary>
        public DateTime CheckTime { get; set; }

        public ResourceUsage()
        {
            CheckTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// TTS 状态管理器实现
    /// </summary>
    public class TTSStateManager : ITTSStateManager
    {
        private readonly ServiceStatus _serviceStatus;
        private readonly ConcurrentDictionary<string, ActiveRequestInfo> _activeRequests;
        private readonly ConcurrentQueue<RequestMetric> _recentRequests;
        private readonly object _statusLock = new object();
        private readonly Timer _healthCheckTimer;
        private readonly Timer _cleanupTimer;

        private const int MaxRecentRequests = 1000;
        private const int HealthCheckIntervalMs = 30000; // 30秒
        private const int CleanupIntervalMs = 300000; // 5分钟

        public TTSStateManager()
        {
            _serviceStatus = new ServiceStatus();
            _activeRequests = new ConcurrentDictionary<string, ActiveRequestInfo>();
            _recentRequests = new ConcurrentQueue<RequestMetric>();

            // 启动定时器
            _healthCheckTimer = new Timer(PerformPeriodicHealthCheck, null, HealthCheckIntervalMs, HealthCheckIntervalMs);
            _cleanupTimer = new Timer(PerformPeriodicCleanup, null, CleanupIntervalMs, CleanupIntervalMs);

            Logger.Log("TTSStateManager: State manager initialized");
        }

        /// <summary>
        /// 获取当前服务状态
        /// </summary>
        public async Task<ServiceStatus> GetServiceStatusAsync()
        {
            try
            {
                lock (_statusLock)
                {
                    _serviceStatus.ActiveRequests = _activeRequests.Count;
                    _serviceStatus.UpdateHealthCheck();
                    return _serviceStatus.CreateSnapshot();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSStateManager: Error getting service status: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 更新服务状态
        /// </summary>
        public async Task UpdateServiceStatusAsync(ServiceStatus status)
        {
            try
            {
                if (status == null)
                    throw new ArgumentNullException(nameof(status));

                lock (_statusLock)
                {
                    _serviceStatus.IsAvailable = status.IsAvailable;
                    _serviceStatus.CurrentType = status.CurrentType;
                    _serviceStatus.StatusMessage = status.StatusMessage;
                    _serviceStatus.ErrorMessage = status.ErrorMessage;
                    _serviceStatus.Version = status.Version;
                    _serviceStatus.ConfigurationVersion = status.ConfigurationVersion;
                    _serviceStatus.UpdateHealthCheck();
                }

                Logger.Log($"TTSStateManager: Service status updated - Available: {status.IsAvailable}, Type: {status.CurrentType}");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSStateManager: Error updating service status: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 执行健康检查
        /// </summary>
        public async Task<HealthCheckResult> PerformHealthCheckAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = new HealthCheckResult();

            try
            {
                // 检查服务状态
                lock (_statusLock)
                {
                    result.AddDetail("ServiceStatus", _serviceStatus.IsHealthy(),
                        _serviceStatus.IsHealthy() ? "Service is healthy" : _serviceStatus.ErrorMessage ?? "Service is not available");
                }

                // 检查活跃请求
                var activeCount = _activeRequests.Count;
                var hasStaleRequests = _activeRequests.Values.Any(r => r.ElapsedTime.TotalMinutes > 5);

                result.AddDetail("ActiveRequests", !hasStaleRequests,
                    $"Active requests: {activeCount}, Stale requests: {hasStaleRequests}");

                // 检查内存使用
                var memoryUsage = await GetMemoryUsageAsync();
                var memoryHealthy = memoryUsage < 500; // 500MB阈值

                result.AddDetail("Memory", memoryHealthy,
                    $"Memory usage: {memoryUsage:F1} MB");

                // 检查性能指标
                var metrics = await GetPerformanceMetricsAsync();
                var performanceHealthy = metrics.ErrorRate < 10; // 10%错误率阈值

                result.AddDetail("Performance", performanceHealthy,
                    $"Error rate: {metrics.ErrorRate:F1}%, Avg response: {metrics.AverageResponseTime:F0}ms");

                // 计算总体健康状态
                result.IsHealthy = result.Details.Values.All(d => d.IsHealthy);
                result.OverallMessage = result.IsHealthy ? "All systems healthy" : "Some systems require attention";

                stopwatch.Stop();
                result.CheckDurationMs = stopwatch.Elapsed.TotalMilliseconds;

                Logger.Log($"TTSStateManager: Health check completed - Healthy: {result.IsHealthy}, Duration: {result.CheckDurationMs:F0}ms");
                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.IsHealthy = false;
                result.OverallMessage = $"Health check failed: {ex.Message}";
                result.CheckDurationMs = stopwatch.Elapsed.TotalMilliseconds;

                Logger.Log($"TTSStateManager: Health check error: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// 获取活跃请求信息
        /// </summary>
        public async Task<IEnumerable<ActiveRequestInfo>> GetActiveRequestsAsync()
        {
            try
            {
                return _activeRequests.Values.ToList();
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSStateManager: Error getting active requests: {ex.Message}");
                return Enumerable.Empty<ActiveRequestInfo>();
            }
        }

        /// <summary>
        /// 注册活跃请求
        /// </summary>
        public async Task RegisterActiveRequestAsync(ActiveRequestInfo requestInfo)
        {
            try
            {
                if (requestInfo == null || string.IsNullOrEmpty(requestInfo.RequestId))
                    return;

                _activeRequests.TryAdd(requestInfo.RequestId, requestInfo);

                Logger.Log($"TTSStateManager: Registered active request {requestInfo.RequestId} ({requestInfo.TTSType})");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSStateManager: Error registering active request: {ex.Message}");
            }
        }

        /// <summary>
        /// 注销活跃请求
        /// </summary>
        public async Task UnregisterActiveRequestAsync(string requestId)
        {
            try
            {
                if (string.IsNullOrEmpty(requestId))
                    return;

                if (_activeRequests.TryRemove(requestId, out var requestInfo))
                {
                    // 记录请求指标
                    var metric = new RequestMetric
                    {
                        RequestId = requestId,
                        TTSType = requestInfo.TTSType,
                        StartTime = requestInfo.StartTime,
                        EndTime = DateTime.UtcNow,
                        Success = requestInfo.Status == RequestStatus.Completed,
                        TextLength = requestInfo.TextLength
                    };

                    _recentRequests.Enqueue(metric);

                    // 限制队列大小
                    while (_recentRequests.Count > MaxRecentRequests)
                    {
                        _recentRequests.TryDequeue(out _);
                    }

                    // 更新服务状态统计
                    lock (_statusLock)
                    {
                        _serviceStatus.IncrementRequestCount(metric.Success, metric.Duration.TotalMilliseconds);
                    }

                    Logger.Log($"TTSStateManager: Unregistered active request {requestId}, Duration: {metric.Duration.TotalMilliseconds:F0}ms");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSStateManager: Error unregistering active request: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取性能指标
        /// </summary>
        public async Task<PerformanceMetrics> GetPerformanceMetricsAsync()
        {
            try
            {
                var metrics = new PerformanceMetrics();
                var cutoffTime = DateTime.UtcNow.AddMinutes(-metrics.TimeWindowMinutes);

                var recentMetrics = _recentRequests.Where(m => m.StartTime >= cutoffTime).ToList();

                if (recentMetrics.Any())
                {
                    // 计算基本指标
                    metrics.RequestsPerMinute = recentMetrics.Count / (double)metrics.TimeWindowMinutes;
                    metrics.AverageResponseTime = recentMetrics.Average(m => m.Duration.TotalMilliseconds);
                    metrics.ErrorRate = (1.0 - (double)recentMetrics.Count(m => m.Success) / recentMetrics.Count) * 100;

                    // 计算百分位数
                    var sortedTimes = recentMetrics.Select(m => m.Duration.TotalMilliseconds).OrderBy(t => t).ToList();
                    if (sortedTimes.Any())
                    {
                        metrics.P95ResponseTime = GetPercentile(sortedTimes, 0.95);
                        metrics.P99ResponseTime = GetPercentile(sortedTimes, 0.99);
                    }

                    // 按类型分组统计
                    var groupedByType = recentMetrics.GroupBy(m => m.TTSType);
                    foreach (var group in groupedByType)
                    {
                        var typeMetrics = new TTSTypeMetrics
                        {
                            RequestCount = group.Count(),
                            SuccessCount = group.Count(m => m.Success),
                            AverageResponseTime = group.Average(m => m.Duration.TotalMilliseconds)
                        };
                        metrics.MetricsByType[group.Key] = typeMetrics;
                    }
                }

                // 当前并发数
                metrics.ConcurrentRequests = _activeRequests.Count;

                // 历史最大并发数（简化实现，实际应该持久化）
                metrics.MaxConcurrentRequests = Math.Max(metrics.ConcurrentRequests,
                    recentMetrics.GroupBy(m => m.StartTime.Minute).Max(g => g?.Count() ?? 0));

                return metrics;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSStateManager: Error getting performance metrics: {ex.Message}");
                return new PerformanceMetrics();
            }
        }

        /// <summary>
        /// 重置统计信息
        /// </summary>
        public async Task ResetStatisticsAsync()
        {
            try
            {
                lock (_statusLock)
                {
                    _serviceStatus.TotalProcessedRequests = 0;
                    _serviceStatus.SuccessfulRequests = 0;
                    _serviceStatus.FailedRequests = 0;
                    _serviceStatus.AverageResponseTimeMs = 0;
                    _serviceStatus.ServiceStartTime = DateTime.UtcNow;
                }

                // 清空最近请求队列
                while (_recentRequests.TryDequeue(out _)) { }

                Logger.Log("TTSStateManager: Statistics reset completed");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSStateManager: Error resetting statistics: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 获取系统资源使用情况
        /// </summary>
        public async Task<ResourceUsage> GetResourceUsageAsync()
        {
            try
            {
                var usage = new ResourceUsage();

                // 获取内存使用
                usage.MemoryUsageMB = await GetMemoryUsageAsync();

                // 获取线程数
                usage.ThreadCount = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;

                // 获取句柄数（Windows特定）
                try
                {
                    usage.HandleCount = System.Diagnostics.Process.GetCurrentProcess().HandleCount;
                }
                catch
                {
                    usage.HandleCount = -1; // 不支持的平台
                }

                // CPU使用率需要更复杂的实现，这里简化
                usage.CpuUsagePercent = -1; // 表示未实现

                // 网络连接数（简化实现）
                usage.NetworkConnections = _activeRequests.Count; // 近似值

                return usage;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSStateManager: Error getting resource usage: {ex.Message}");
                return new ResourceUsage();
            }
        }

        /// <summary>
        /// 获取内存使用量（MB）
        /// </summary>
        private async Task<double> GetMemoryUsageAsync()
        {
            try
            {
                var process = System.Diagnostics.Process.GetCurrentProcess();
                return process.WorkingSet64 / (1024.0 * 1024.0);
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// 计算百分位数
        /// </summary>
        private double GetPercentile(List<double> sortedValues, double percentile)
        {
            if (!sortedValues.Any())
                return 0;

            var index = (int)Math.Ceiling(sortedValues.Count * percentile) - 1;
            index = Math.Max(0, Math.Min(index, sortedValues.Count - 1));
            return sortedValues[index];
        }

        /// <summary>
        /// 定期健康检查
        /// </summary>
        private async void PerformPeriodicHealthCheck(object state)
        {
            try
            {
                var result = await PerformHealthCheckAsync();

                lock (_statusLock)
                {
                    if (!result.IsHealthy)
                    {
                        _serviceStatus.SetError($"Health check failed: {result.OverallMessage}");
                    }
                    else if (!string.IsNullOrEmpty(_serviceStatus.ErrorMessage))
                    {
                        _serviceStatus.ClearError();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSStateManager: Periodic health check error: {ex.Message}");
            }
        }

        /// <summary>
        /// 定期清理
        /// </summary>
        private async void PerformPeriodicCleanup(object state)
        {
            try
            {
                // 清理过期的活跃请求
                var expiredRequests = _activeRequests.Values
                    .Where(r => r.ElapsedTime.TotalMinutes > 10) // 10分钟超时
                    .ToList();

                foreach (var request in expiredRequests)
                {
                    _activeRequests.TryRemove(request.RequestId, out _);
                    Logger.Log($"TTSStateManager: Cleaned up expired request {request.RequestId}");
                }

                // 清理旧的请求指标
                var cutoffTime = DateTime.UtcNow.AddHours(-24); // 保留24小时
                var metricsToKeep = new List<RequestMetric>();

                while (_recentRequests.TryDequeue(out var metric))
                {
                    if (metric.StartTime >= cutoffTime)
                    {
                        metricsToKeep.Add(metric);
                    }
                }

                foreach (var metric in metricsToKeep)
                {
                    _recentRequests.Enqueue(metric);
                }

                if (expiredRequests.Any())
                {
                    Logger.Log($"TTSStateManager: Cleanup completed - Removed {expiredRequests.Count} expired requests");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSStateManager: Periodic cleanup error: {ex.Message}");
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            try
            {
                _healthCheckTimer?.Dispose();
                _cleanupTimer?.Dispose();

                Logger.Log("TTSStateManager: Disposed");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSStateManager: Error during disposal: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 请求指标
    /// </summary>
    internal class RequestMetric
    {
        public string RequestId { get; set; }
        public TTSType TTSType { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public bool Success { get; set; }
        public int TextLength { get; set; }
        public TimeSpan Duration => EndTime - StartTime;
    }
}