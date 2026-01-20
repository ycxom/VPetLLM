using System.Collections.Concurrent;

namespace VPetLLM.Core.Integration.UnifiedTTS.Utils
{
    /// <summary>
    /// TTS 专用错误日志记录器
    /// </summary>
    public interface ITTSErrorLogger
    {
        /// <summary>
        /// 记录错误
        /// </summary>
        /// <param name="error">错误信息</param>
        /// <param name="context">错误上下文</param>
        Task LogErrorAsync(TTSError error, ErrorContext context = null);

        /// <summary>
        /// 记录重试信息
        /// </summary>
        /// <param name="requestId">请求ID</param>
        /// <param name="attemptCount">尝试次数</param>
        /// <param name="error">错误信息</param>
        Task LogRetryAsync(string requestId, int attemptCount, TTSError error);

        /// <summary>
        /// 记录性能信息
        /// </summary>
        /// <param name="requestId">请求ID</param>
        /// <param name="ttsType">TTS类型</param>
        /// <param name="processingTime">处理时间</param>
        /// <param name="success">是否成功</param>
        Task LogPerformanceAsync(string requestId, TTSType ttsType, TimeSpan processingTime, bool success);

        /// <summary>
        /// 获取错误统计信息
        /// </summary>
        /// <param name="timeRange">时间范围（小时）</param>
        /// <returns>错误统计</returns>
        Task<ErrorStatistics> GetErrorStatisticsAsync(int timeRange = 24);

        /// <summary>
        /// 清理旧日志
        /// </summary>
        /// <param name="retentionDays">保留天数</param>
        Task CleanupOldLogsAsync(int retentionDays = 7);
    }

    /// <summary>
    /// 错误统计信息
    /// </summary>
    public class ErrorStatistics
    {
        /// <summary>
        /// 总错误数
        /// </summary>
        public int TotalErrors { get; set; }

        /// <summary>
        /// 按错误代码分组的统计
        /// </summary>
        public Dictionary<string, int> ErrorsByCode { get; set; }

        /// <summary>
        /// 按TTS类型分组的统计
        /// </summary>
        public Dictionary<TTSType, int> ErrorsByTTSType { get; set; }

        /// <summary>
        /// 按严重程度分组的统计
        /// </summary>
        public Dictionary<TTSErrorSeverity, int> ErrorsBySeverity { get; set; }

        /// <summary>
        /// 重试统计
        /// </summary>
        public RetryStatistics RetryStats { get; set; }

        /// <summary>
        /// 性能统计
        /// </summary>
        public PerformanceStatistics PerformanceStats { get; set; }

        /// <summary>
        /// 统计时间范围
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// 统计结束时间
        /// </summary>
        public DateTime EndTime { get; set; }

        public ErrorStatistics()
        {
            ErrorsByCode = new Dictionary<string, int>();
            ErrorsByTTSType = new Dictionary<TTSType, int>();
            ErrorsBySeverity = new Dictionary<TTSErrorSeverity, int>();
            RetryStats = new RetryStatistics();
            PerformanceStats = new PerformanceStatistics();
        }
    }

    /// <summary>
    /// 重试统计信息
    /// </summary>
    public class RetryStatistics
    {
        /// <summary>
        /// 总重试次数
        /// </summary>
        public int TotalRetries { get; set; }

        /// <summary>
        /// 重试成功次数
        /// </summary>
        public int SuccessfulRetries { get; set; }

        /// <summary>
        /// 平均重试次数
        /// </summary>
        public double AverageRetryCount { get; set; }

        /// <summary>
        /// 最大重试次数
        /// </summary>
        public int MaxRetryCount { get; set; }
    }

    /// <summary>
    /// 性能统计信息
    /// </summary>
    public class PerformanceStatistics
    {
        /// <summary>
        /// 总请求数
        /// </summary>
        public int TotalRequests { get; set; }

        /// <summary>
        /// 成功请求数
        /// </summary>
        public int SuccessfulRequests { get; set; }

        /// <summary>
        /// 平均处理时间（毫秒）
        /// </summary>
        public double AverageProcessingTime { get; set; }

        /// <summary>
        /// 最大处理时间（毫秒）
        /// </summary>
        public double MaxProcessingTime { get; set; }

        /// <summary>
        /// 最小处理时间（毫秒）
        /// </summary>
        public double MinProcessingTime { get; set; }

        /// <summary>
        /// 成功率
        /// </summary>
        public double SuccessRate => TotalRequests > 0 ? (double)SuccessfulRequests / TotalRequests * 100 : 0;
    }

    /// <summary>
    /// 日志条目
    /// </summary>
    public class LogEntry
    {
        /// <summary>
        /// 时间戳
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// 日志类型
        /// </summary>
        public LogType Type { get; set; }

        /// <summary>
        /// 请求ID
        /// </summary>
        public string RequestId { get; set; }

        /// <summary>
        /// TTS类型
        /// </summary>
        public TTSType? TTSType { get; set; }

        /// <summary>
        /// 错误信息
        /// </summary>
        public TTSError Error { get; set; }

        /// <summary>
        /// 尝试次数
        /// </summary>
        public int? AttemptCount { get; set; }

        /// <summary>
        /// 处理时间（毫秒）
        /// </summary>
        public double? ProcessingTime { get; set; }

        /// <summary>
        /// 是否成功
        /// </summary>
        public bool? Success { get; set; }

        /// <summary>
        /// 附加数据
        /// </summary>
        public Dictionary<string, object> AdditionalData { get; set; }

        public LogEntry()
        {
            Timestamp = DateTime.UtcNow;
            AdditionalData = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// 日志类型
    /// </summary>
    public enum LogType
    {
        Error,
        Retry,
        Performance
    }

    /// <summary>
    /// TTS 错误日志记录器实现
    /// </summary>
    public class TTSErrorLogger : ITTSErrorLogger
    {
        private readonly ConcurrentQueue<LogEntry> _logQueue;
        private readonly string _logDirectory;
        private readonly int _maxMemoryLogs;
        private readonly bool _enableFileLogging;

        public TTSErrorLogger(string logDirectory = null, int maxMemoryLogs = 1000, bool enableFileLogging = true)
        {
            _logQueue = new ConcurrentQueue<LogEntry>();
            _logDirectory = logDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPetLLM", "Logs", "TTS");
            _maxMemoryLogs = maxMemoryLogs;
            _enableFileLogging = enableFileLogging;

            if (_enableFileLogging)
            {
                EnsureLogDirectoryExists();
            }
        }

        /// <summary>
        /// 记录错误
        /// </summary>
        public async Task LogErrorAsync(TTSError error, ErrorContext context = null)
        {
            try
            {
                var logEntry = new LogEntry
                {
                    Type = LogType.Error,
                    RequestId = error.RequestId,
                    TTSType = context?.TTSType,
                    Error = error,
                    AttemptCount = context?.AttemptCount
                };

                if (context is not null)
                {
                    logEntry.AdditionalData["MaxRetries"] = context.MaxRetries;
                    logEntry.AdditionalData["TimeoutMs"] = context.TimeoutMs;
                    logEntry.AdditionalData["IsTimedOut"] = context.IsTimedOut();
                }

                await AddLogEntryAsync(logEntry);

                // 同时使用系统日志记录器
                var message = $"TTS Error [{error.Severity}]: {error.Message}";
                if (!string.IsNullOrEmpty(error.RequestId))
                {
                    message += $" (RequestId: {error.RequestId})";
                }
                if (context?.TTSType is not null)
                {
                    message += $" (Type: {context.TTSType})";
                }

                Logger.Log(message);
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSErrorLogger: Failed to log error: {ex.Message}");
            }
        }

        /// <summary>
        /// 记录重试信息
        /// </summary>
        public async Task LogRetryAsync(string requestId, int attemptCount, TTSError error)
        {
            try
            {
                var logEntry = new LogEntry
                {
                    Type = LogType.Retry,
                    RequestId = requestId,
                    Error = error,
                    AttemptCount = attemptCount
                };

                await AddLogEntryAsync(logEntry);

                Logger.Log($"TTS Retry: Request {requestId} attempt {attemptCount} - {error.Message}");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSErrorLogger: Failed to log retry: {ex.Message}");
            }
        }

        /// <summary>
        /// 记录性能信息
        /// </summary>
        public async Task LogPerformanceAsync(string requestId, TTSType ttsType, TimeSpan processingTime, bool success)
        {
            try
            {
                var logEntry = new LogEntry
                {
                    Type = LogType.Performance,
                    RequestId = requestId,
                    TTSType = ttsType,
                    ProcessingTime = processingTime.TotalMilliseconds,
                    Success = success
                };

                await AddLogEntryAsync(logEntry);

                if (!success || processingTime.TotalSeconds > 5) // 只记录失败或慢请求
                {
                    Logger.Log($"TTS Performance: Request {requestId} ({ttsType}) - {processingTime.TotalMilliseconds:F0}ms, Success: {success}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSErrorLogger: Failed to log performance: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取错误统计信息
        /// </summary>
        public async Task<ErrorStatistics> GetErrorStatisticsAsync(int timeRange = 24)
        {
            try
            {
                var cutoffTime = DateTime.UtcNow.AddHours(-timeRange);
                var relevantLogs = _logQueue.Where(log => log.Timestamp >= cutoffTime).ToList();

                var statistics = new ErrorStatistics
                {
                    StartTime = cutoffTime,
                    EndTime = DateTime.UtcNow
                };

                // 错误统计
                var errorLogs = relevantLogs.Where(log => log.Type == LogType.Error).ToList();
                statistics.TotalErrors = errorLogs.Count;

                foreach (var errorLog in errorLogs)
                {
                    if (errorLog.Error is not null)
                    {
                        // 按错误代码统计
                        var code = errorLog.Error.Code ?? "Unknown";
                        statistics.ErrorsByCode[code] = statistics.ErrorsByCode.GetValueOrDefault(code, 0) + 1;

                        // 按严重程度统计
                        statistics.ErrorsBySeverity[errorLog.Error.Severity] =
                            statistics.ErrorsBySeverity.GetValueOrDefault(errorLog.Error.Severity, 0) + 1;
                    }

                    // 按TTS类型统计
                    if (errorLog.TTSType.HasValue)
                    {
                        statistics.ErrorsByTTSType[errorLog.TTSType.Value] =
                            statistics.ErrorsByTTSType.GetValueOrDefault(errorLog.TTSType.Value, 0) + 1;
                    }
                }

                // 重试统计
                var retryLogs = relevantLogs.Where(log => log.Type == LogType.Retry).ToList();
                statistics.RetryStats.TotalRetries = retryLogs.Count;

                if (retryLogs.Any())
                {
                    var retryGroups = retryLogs.GroupBy(log => log.RequestId);
                    statistics.RetryStats.AverageRetryCount = retryGroups.Average(g => g.Count());
                    statistics.RetryStats.MaxRetryCount = retryGroups.Max(g => g.Count());
                }

                // 性能统计
                var performanceLogs = relevantLogs.Where(log => log.Type == LogType.Performance).ToList();
                statistics.PerformanceStats.TotalRequests = performanceLogs.Count;
                statistics.PerformanceStats.SuccessfulRequests = performanceLogs.Count(log => log.Success == true);

                if (performanceLogs.Any(log => log.ProcessingTime.HasValue))
                {
                    var processingTimes = performanceLogs.Where(log => log.ProcessingTime.HasValue)
                                                       .Select(log => log.ProcessingTime.Value);

                    statistics.PerformanceStats.AverageProcessingTime = processingTimes.Average();
                    statistics.PerformanceStats.MaxProcessingTime = processingTimes.Max();
                    statistics.PerformanceStats.MinProcessingTime = processingTimes.Min();
                }

                return statistics;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSErrorLogger: Failed to get error statistics: {ex.Message}");
                return new ErrorStatistics();
            }
        }

        /// <summary>
        /// 清理旧日志
        /// </summary>
        public async Task CleanupOldLogsAsync(int retentionDays = 7)
        {
            try
            {
                var cutoffTime = DateTime.UtcNow.AddDays(-retentionDays);

                // 清理内存中的日志
                var logsToKeep = new List<LogEntry>();
                while (_logQueue.TryDequeue(out var log))
                {
                    if (log.Timestamp >= cutoffTime)
                    {
                        logsToKeep.Add(log);
                    }
                }

                foreach (var log in logsToKeep)
                {
                    _logQueue.Enqueue(log);
                }

                // 清理文件日志
                if (_enableFileLogging && Directory.Exists(_logDirectory))
                {
                    var logFiles = Directory.GetFiles(_logDirectory, "*.json");
                    foreach (var file in logFiles)
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.CreationTime < cutoffTime)
                        {
                            File.Delete(file);
                        }
                    }
                }

                Logger.Log($"TTSErrorLogger: Cleaned up logs older than {retentionDays} days");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSErrorLogger: Failed to cleanup old logs: {ex.Message}");
            }
        }

        /// <summary>
        /// 添加日志条目
        /// </summary>
        private async Task AddLogEntryAsync(LogEntry logEntry)
        {
            // 添加到内存队列
            _logQueue.Enqueue(logEntry);

            // 限制内存中的日志数量
            while (_logQueue.Count > _maxMemoryLogs)
            {
                _logQueue.TryDequeue(out _);
            }

            // 写入文件（如果启用）
            if (_enableFileLogging)
            {
                await WriteLogToFileAsync(logEntry);
            }
        }

        /// <summary>
        /// 写入日志到文件
        /// </summary>
        private async Task WriteLogToFileAsync(LogEntry logEntry)
        {
            try
            {
                var fileName = $"tts-{logEntry.Timestamp:yyyy-MM-dd}.json";
                var filePath = Path.Combine(_logDirectory, fileName);

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var logJson = JsonSerializer.Serialize(logEntry, jsonOptions);
                await File.AppendAllTextAsync(filePath, logJson + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSErrorLogger: Failed to write log to file: {ex.Message}");
            }
        }

        /// <summary>
        /// 确保日志目录存在
        /// </summary>
        private void EnsureLogDirectoryExists()
        {
            try
            {
                if (!Directory.Exists(_logDirectory))
                {
                    Directory.CreateDirectory(_logDirectory);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSErrorLogger: Failed to create log directory: {ex.Message}");
            }
        }
    }
}