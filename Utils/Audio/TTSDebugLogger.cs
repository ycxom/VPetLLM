namespace VPetLLM.Utils.Audio
{
    /// <summary>
    /// TTS调试日志记录器，提供详细的TTS协调过程日志记录
    /// 支持多级别日志、性能监控和状态跟踪
    /// </summary>
    public class TTSDebugLogger
    {
        private static readonly Lazy<TTSDebugLogger> _instance = new Lazy<TTSDebugLogger>(() => new TTSDebugLogger());
        public static TTSDebugLogger Instance => _instance.Value;

        private readonly object _lock = new object();
        private readonly Queue<LogEntry> _logBuffer = new Queue<LogEntry>();
        private readonly Dictionary<string, OperationTrace> _activeOperations = new Dictionary<string, OperationTrace>();
        private readonly Timer _flushTimer;
        private string _logFilePath;
        private bool _isEnabled;
        private LogLevel _minLogLevel = LogLevel.Info;

        /// <summary>
        /// 日志级别
        /// </summary>
        public enum LogLevel
        {
            Trace = 0,
            Debug = 1,
            Info = 2,
            Warning = 3,
            Error = 4,
            Critical = 5
        }

        /// <summary>
        /// 日志条目
        /// </summary>
        private class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public LogLevel Level { get; set; }
            public string Category { get; set; }
            public string Message { get; set; }
            public string OperationId { get; set; }
            public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// 操作跟踪
        /// </summary>
        private class OperationTrace
        {
            public string OperationId { get; set; }
            public string OperationType { get; set; }
            public DateTime StartTime { get; set; }
            public List<TraceEvent> Events { get; set; } = new List<TraceEvent>();
            public Dictionary<string, object> Context { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// 跟踪事件
        /// </summary>
        private class TraceEvent
        {
            public DateTime Timestamp { get; set; }
            public string EventType { get; set; }
            public string Message { get; set; }
            public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
        }

        private TTSDebugLogger()
        {
            InitializeLogger();

            // 每5秒刷新一次日志缓冲区
            _flushTimer = new Timer(FlushLogs, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 初始化日志记录器
        /// </summary>
        private void InitializeLogger()
        {
            try
            {
                _isEnabled = TTSCoordinationSettings.Instance.EnableDebugLogging;
                _minLogLevel = (LogLevel)TTSCoordinationSettings.Instance.DebugLogLevel;

                if (_isEnabled)
                {
                    var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPetLLM", "Logs");
                    Directory.CreateDirectory(logDir);

                    var logFileName = $"TTS_Debug_{DateTime.Now:yyyyMMdd_HHmmss}.log";
                    _logFilePath = Path.Combine(logDir, logFileName);

                    WriteLogEntry(LogLevel.Info, "System", "TTS调试日志记录器已启动", null, new Dictionary<string, object>
                    {
                        ["LogLevel"] = _minLogLevel.ToString(),
                        ["LogFile"] = _logFilePath
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TTSDebugLogger初始化失败: {ex.Message}");
                _isEnabled = false;
            }
        }

        /// <summary>
        /// 记录跟踪级别日志
        /// </summary>
        public void LogTrace(string category, string message, string operationId = null, Dictionary<string, object> properties = null)
        {
            WriteLogEntry(LogLevel.Trace, category, message, operationId, properties);
        }

        /// <summary>
        /// 记录调试级别日志
        /// </summary>
        public void LogDebug(string category, string message, string operationId = null, Dictionary<string, object> properties = null)
        {
            WriteLogEntry(LogLevel.Debug, category, message, operationId, properties);
        }

        /// <summary>
        /// 记录信息级别日志
        /// </summary>
        public void LogInfo(string category, string message, string operationId = null, Dictionary<string, object> properties = null)
        {
            WriteLogEntry(LogLevel.Info, category, message, operationId, properties);
        }

        /// <summary>
        /// 记录警告级别日志
        /// </summary>
        public void LogWarning(string category, string message, string operationId = null, Dictionary<string, object> properties = null)
        {
            WriteLogEntry(LogLevel.Warning, category, message, operationId, properties);
        }

        /// <summary>
        /// 记录错误级别日志
        /// </summary>
        public void LogError(string category, string message, string operationId = null, Dictionary<string, object> properties = null)
        {
            WriteLogEntry(LogLevel.Error, category, message, operationId, properties);
        }

        /// <summary>
        /// 记录严重错误级别日志
        /// </summary>
        public void LogCritical(string category, string message, string operationId = null, Dictionary<string, object> properties = null)
        {
            WriteLogEntry(LogLevel.Critical, category, message, operationId, properties);
        }

        /// <summary>
        /// 开始操作跟踪
        /// </summary>
        public void StartOperation(string operationId, string operationType, Dictionary<string, object> context = null)
        {
            if (!_isEnabled) return;

            lock (_lock)
            {
                var trace = new OperationTrace
                {
                    OperationId = operationId,
                    OperationType = operationType,
                    StartTime = DateTime.Now,
                    Context = context ?? new Dictionary<string, object>()
                };

                _activeOperations[operationId] = trace;

                WriteLogEntry(LogLevel.Debug, "OperationTrace", $"开始操作跟踪: {operationType}", operationId, new Dictionary<string, object>
                {
                    ["OperationType"] = operationType,
                    ["Context"] = context
                });
            }
        }

        /// <summary>
        /// 添加操作事件
        /// </summary>
        public void AddOperationEvent(string operationId, string eventType, string message, Dictionary<string, object> data = null)
        {
            if (!_isEnabled) return;

            lock (_lock)
            {
                if (_activeOperations.TryGetValue(operationId, out var trace))
                {
                    trace.Events.Add(new TraceEvent
                    {
                        Timestamp = DateTime.Now,
                        EventType = eventType,
                        Message = message,
                        Data = data ?? new Dictionary<string, object>()
                    });

                    WriteLogEntry(LogLevel.Trace, "OperationEvent", $"[{eventType}] {message}", operationId, data);
                }
            }
        }

        /// <summary>
        /// 完成操作跟踪
        /// </summary>
        public void CompleteOperation(string operationId, bool success, string result = null)
        {
            if (!_isEnabled) return;

            lock (_lock)
            {
                if (_activeOperations.TryGetValue(operationId, out var trace))
                {
                    var duration = (DateTime.Now - trace.StartTime).TotalMilliseconds;

                    WriteLogEntry(LogLevel.Info, "OperationTrace", $"操作完成: {trace.OperationType}", operationId, new Dictionary<string, object>
                    {
                        ["Success"] = success,
                        ["Duration"] = duration,
                        ["Result"] = result,
                        ["EventCount"] = trace.Events.Count
                    });

                    _activeOperations.Remove(operationId);
                }
            }
        }

        /// <summary>
        /// 记录性能指标
        /// </summary>
        public void LogPerformance(string category, string metricName, double value, string unit = null, string operationId = null)
        {
            WriteLogEntry(LogLevel.Debug, $"Performance.{category}", $"{metricName}: {value}{unit}", operationId, new Dictionary<string, object>
            {
                ["MetricName"] = metricName,
                ["Value"] = value,
                ["Unit"] = unit
            });
        }

        /// <summary>
        /// 记录状态变化
        /// </summary>
        public void LogStateChange(string component, string fromState, string toState, string reason = null, string operationId = null)
        {
            WriteLogEntry(LogLevel.Debug, $"StateChange.{component}", $"{fromState} -> {toState}", operationId, new Dictionary<string, object>
            {
                ["FromState"] = fromState,
                ["ToState"] = toState,
                ["Reason"] = reason
            });
        }

        /// <summary>
        /// 记录TTS请求详情
        /// </summary>
        public void LogTTSRequest(string operationId, string text, int textLength, string requestType)
        {
            WriteLogEntry(LogLevel.Debug, "TTSRequest", $"TTS请求: {requestType}", operationId, new Dictionary<string, object>
            {
                ["TextLength"] = textLength,
                ["RequestType"] = requestType,
                ["TextPreview"] = text?.Length > 50 ? text.Substring(0, 50) + "..." : text
            });
        }

        /// <summary>
        /// 记录播放器状态
        /// </summary>
        public void LogPlayerStatus(string playerId, string status, string details = null, string operationId = null)
        {
            WriteLogEntry(LogLevel.Debug, "PlayerStatus", $"播放器 {playerId}: {status}", operationId, new Dictionary<string, object>
            {
                ["PlayerId"] = playerId,
                ["Status"] = status,
                ["Details"] = details
            });
        }

        /// <summary>
        /// 记录队列状态
        /// </summary>
        public void LogQueueStatus(string queueName, int queueLength, bool isProcessing, string operationId = null)
        {
            WriteLogEntry(LogLevel.Trace, "QueueStatus", $"队列 {queueName}: 长度={queueLength}, 处理中={isProcessing}", operationId, new Dictionary<string, object>
            {
                ["QueueName"] = queueName,
                ["QueueLength"] = queueLength,
                ["IsProcessing"] = isProcessing
            });
        }

        /// <summary>
        /// 写入日志条目
        /// </summary>
        private void WriteLogEntry(LogLevel level, string category, string message, string operationId, Dictionary<string, object> properties)
        {
            if (!_isEnabled || level < _minLogLevel) return;

            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Category = category,
                Message = message,
                OperationId = operationId,
                Properties = properties ?? new Dictionary<string, object>()
            };

            lock (_lock)
            {
                _logBuffer.Enqueue(entry);

                // 如果缓冲区太大，立即刷新
                if (_logBuffer.Count > 100)
                {
                    FlushLogsInternal();
                }
            }

            // 同时输出到控制台（仅限警告及以上级别）
            if (level >= LogLevel.Warning)
            {
                var consoleMessage = FormatLogEntry(entry, false);
                Console.WriteLine(consoleMessage);
            }
        }

        /// <summary>
        /// 刷新日志缓冲区
        /// </summary>
        private void FlushLogs(object state)
        {
            lock (_lock)
            {
                FlushLogsInternal();
            }
        }

        /// <summary>
        /// 内部刷新日志方法
        /// </summary>
        private void FlushLogsInternal()
        {
            if (!_isEnabled || _logBuffer.Count == 0 || string.IsNullOrEmpty(_logFilePath))
                return;

            try
            {
                var sb = new StringBuilder();

                while (_logBuffer.Count > 0)
                {
                    var entry = _logBuffer.Dequeue();
                    sb.AppendLine(FormatLogEntry(entry, true));
                }

                File.AppendAllText(_logFilePath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"刷新TTS调试日志失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 格式化日志条目
        /// </summary>
        private string FormatLogEntry(LogEntry entry, bool includeProperties)
        {
            var sb = new StringBuilder();

            // 基本信息
            sb.Append($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} ");
            sb.Append($"[{entry.Level.ToString().ToUpper()}] ");
            sb.Append($"[{entry.Category}] ");

            if (!string.IsNullOrEmpty(entry.OperationId))
            {
                sb.Append($"[{entry.OperationId}] ");
            }

            sb.Append(entry.Message);

            // 属性信息
            if (includeProperties && entry.Properties.Count > 0)
            {
                sb.Append(" | ");
                var properties = new List<string>();

                foreach (var kvp in entry.Properties)
                {
                    if (kvp.Value is not null)
                    {
                        properties.Add($"{kvp.Key}={kvp.Value}");
                    }
                }

                sb.Append(string.Join(", ", properties));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 生成调试报告
        /// </summary>
        public string GenerateDebugReport()
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== TTS调试报告 ===");
                sb.AppendLine($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"日志启用: {_isEnabled}");
                sb.AppendLine($"最小日志级别: {_minLogLevel}");
                sb.AppendLine($"日志文件: {_logFilePath}");
                sb.AppendLine($"缓冲区大小: {_logBuffer.Count}");
                sb.AppendLine($"活跃操作数: {_activeOperations.Count}");
                sb.AppendLine();

                if (_activeOperations.Count > 0)
                {
                    sb.AppendLine("活跃操作:");
                    foreach (var kvp in _activeOperations)
                    {
                        var trace = kvp.Value;
                        var duration = (DateTime.Now - trace.StartTime).TotalMilliseconds;
                        sb.AppendLine($"  {trace.OperationId}: {trace.OperationType} (运行 {duration:F0}ms, {trace.Events.Count} 事件)");
                    }
                }

                return sb.ToString();
            }
        }

        /// <summary>
        /// 清理资源
        /// </summary>
        public void Dispose()
        {
            try
            {
                _flushTimer?.Dispose();

                lock (_lock)
                {
                    FlushLogsInternal();
                }

                WriteLogEntry(LogLevel.Info, "System", "TTS调试日志记录器已关闭", null, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TTSDebugLogger释放资源失败: {ex.Message}");
            }
        }
    }
}