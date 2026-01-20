using System.Collections.Concurrent;
using System.Diagnostics;

namespace VPetLLM.Infrastructure.Logging
{
    /// <summary>
    /// 结构化日志记录器实现
    /// </summary>
    public class StructuredLogger : IStructuredLogger, IDisposable
    {
        private readonly LoggingConfiguration _configuration;
        private readonly ConcurrentQueue<LogEntry> _logQueue = new();
        private readonly ConcurrentQueue<LogEntry> _memoryBuffer = new();
        private readonly ThreadLocal<Stack<object>> _scopes = new(() => new Stack<object>());
        private readonly Timer _flushTimer;
        private readonly SemaphoreSlim _flushSemaphore = new(1, 1);
        private readonly string _logFilePath;
        private bool _disposed = false;

        public StructuredLogger(LoggingConfiguration configuration = null)
        {
            _configuration = configuration ?? new LoggingConfiguration();

            // 设置日志文件路径
            var logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            Directory.CreateDirectory(logDirectory);
            _logFilePath = Path.Combine(logDirectory, $"VPetLLM_{DateTime.Now:yyyyMMdd}.log");

            // 启动定时刷新
            if (_configuration.EnableAsyncLogging)
            {
                _flushTimer = new Timer(FlushLogs, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
        }

        public void LogTrace(string message, object context = null)
        {
            Log(LogLevel.Trace, message, null, context);
        }

        public void LogDebug(string message, object context = null)
        {
            Log(LogLevel.Debug, message, null, context);
        }

        public void LogInformation(string message, object context = null)
        {
            Log(LogLevel.Information, message, null, context);
        }

        public void LogWarning(string message, object context = null)
        {
            Log(LogLevel.Warning, message, null, context);
        }

        public void LogError(Exception exception, string message, object context = null)
        {
            Log(LogLevel.Error, message, exception, context);
        }

        public void LogError(string message, object context = null)
        {
            Log(LogLevel.Error, message, null, context);
        }

        public void LogCritical(Exception exception, string message, object context = null)
        {
            Log(LogLevel.Critical, message, exception, context);
        }

        public void LogCritical(string message, object context = null)
        {
            Log(LogLevel.Critical, message, null, context);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= _configuration.MinimumLevel;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            if (state is null)
                throw new ArgumentNullException(nameof(state));

            _scopes.Value.Push(state);
            return new LogScope(() => _scopes.Value.Pop());
        }

        private void Log(LogLevel level, string message, Exception exception, object context)
        {
            if (!IsEnabled(level) || _disposed)
                return;

            var logEntry = new LogEntry
            {
                Level = level,
                Message = message,
                Exception = exception,
                Source = GetCallerInfo(),
                ThreadId = Thread.CurrentThread.ManagedThreadId
            };

            // 添加上下文信息
            if (context is not null)
            {
                AddContextToLogEntry(logEntry, context);
            }

            // 添加作用域信息
            if (_configuration.IncludeScopes && _scopes.Value.Count > 0)
            {
                logEntry.Scopes.AddRange(_scopes.Value.ToArray().Reverse());
            }

            // 添加异常上下文
            if (exception is not null)
            {
                AddExceptionContext(logEntry, exception);
            }

            // 异步日志记录
            if (_configuration.EnableAsyncLogging)
            {
                _logQueue.Enqueue(logEntry);
            }
            else
            {
                WriteLogEntry(logEntry);
            }

            // 内存缓冲区
            if (_configuration.Targets.Contains(LogTarget.Memory))
            {
                _memoryBuffer.Enqueue(logEntry);

                // 限制内存缓冲区大小
                while (_memoryBuffer.Count > _configuration.MaxLogEntries)
                {
                    _memoryBuffer.TryDequeue(out _);
                }
            }
        }

        private void AddContextToLogEntry(LogEntry logEntry, object context)
        {
            try
            {
                if (context is Dictionary<string, object> dict)
                {
                    foreach (var kvp in dict)
                    {
                        logEntry.Context[kvp.Key] = kvp.Value;
                    }
                }
                else
                {
                    // 使用反射获取对象属性
                    var properties = context.GetType().GetProperties();
                    foreach (var property in properties)
                    {
                        try
                        {
                            var value = property.GetValue(context);
                            logEntry.Context[property.Name] = value;
                        }
                        catch
                        {
                            // 忽略无法访问的属性
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logEntry.Context["ContextError"] = $"Failed to serialize context: {ex.Message}";
            }
        }

        private void AddExceptionContext(LogEntry logEntry, Exception exception)
        {
            logEntry.Context["ExceptionType"] = exception.GetType().Name;
            logEntry.Context["ExceptionMessage"] = exception.Message;

            if (_configuration.IncludeStackTrace)
            {
                logEntry.Context["StackTrace"] = exception.StackTrace;
            }

            if (exception.InnerException is not null)
            {
                logEntry.Context["InnerException"] = exception.InnerException.Message;
            }

            // 添加异常数据
            if (exception.Data.Count > 0)
            {
                var exceptionData = new Dictionary<string, object>();
                foreach (var key in exception.Data.Keys)
                {
                    exceptionData[key.ToString()] = exception.Data[key];
                }
                logEntry.Context["ExceptionData"] = exceptionData;
            }
        }

        private string GetCallerInfo()
        {
            try
            {
                var stackTrace = new StackTrace(true);
                var frames = stackTrace.GetFrames();

                // 跳过日志框架的帧，找到实际调用者
                for (int i = 0; i < frames.Length; i++)
                {
                    var frame = frames[i];
                    var method = frame.GetMethod();
                    var declaringType = method.DeclaringType;

                    if (declaringType is not null &&
                        !declaringType.Namespace.StartsWith("VPetLLM.Infrastructure.Logging") &&
                        !declaringType.Name.Contains("Logger"))
                    {
                        return $"{declaringType.Name}.{method.Name}";
                    }
                }
            }
            catch
            {
                // 忽略获取调用者信息的异常
            }

            return "Unknown";
        }

        private void FlushLogs(object state)
        {
            if (_disposed || !_flushSemaphore.Wait(100))
                return;

            try
            {
                var entriesToFlush = new List<LogEntry>();

                // 批量获取日志条目
                while (_logQueue.TryDequeue(out var entry) && entriesToFlush.Count < 100)
                {
                    entriesToFlush.Add(entry);
                }

                if (entriesToFlush.Any())
                {
                    foreach (var entry in entriesToFlush)
                    {
                        WriteLogEntry(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                // 记录刷新日志时的异常到调试输出
                Debug.WriteLine($"Error flushing logs: {ex.Message}");
            }
            finally
            {
                _flushSemaphore.Release();
            }
        }

        private void WriteLogEntry(LogEntry logEntry)
        {
            try
            {
                foreach (var target in _configuration.Targets)
                {
                    switch (target)
                    {
                        case LogTarget.Console:
                            WriteToConsole(logEntry);
                            break;
                        case LogTarget.File:
                            WriteToFile(logEntry);
                            break;
                        case LogTarget.Debug:
                            WriteToDebug(logEntry);
                            break;
                        case LogTarget.EventLog:
                            WriteToEventLog(logEntry);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error writing log entry: {ex.Message}");
            }
        }

        private void WriteToConsole(LogEntry logEntry)
        {
            var message = FormatLogEntry(logEntry);

            // 根据日志级别设置控制台颜色
            var originalColor = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = GetConsoleColor(logEntry.Level);
                Console.WriteLine(message);
            }
            finally
            {
                Console.ForegroundColor = originalColor;
            }
        }

        private void WriteToFile(LogEntry logEntry)
        {
            var message = FormatLogEntry(logEntry);
            var jsonEntry = JsonSerializer.Serialize(new
            {
                logEntry.Id,
                logEntry.Timestamp,
                Level = logEntry.Level.ToString(),
                logEntry.Message,
                logEntry.Source,
                logEntry.ThreadId,
                logEntry.Context,
                Exception = logEntry.Exception?.ToString(),
                logEntry.Scopes
            }, new JsonSerializerOptions { WriteIndented = false });

            try
            {
                File.AppendAllText(_logFilePath, jsonEntry + Environment.NewLine);
            }
            catch
            {
                // 忽略文件写入异常，避免日志记录本身导致问题
            }
        }

        private void WriteToDebug(LogEntry logEntry)
        {
            var message = FormatLogEntry(logEntry);
            Debug.WriteLine(message);
        }

        private void WriteToEventLog(LogEntry logEntry)
        {
            try
            {
                var eventLogLevel = GetEventLogLevel(logEntry.Level);
                var message = FormatLogEntry(logEntry);

                // 注意：写入事件日志需要管理员权限，这里只是示例
                // 实际使用时可能需要检查权限或使用其他方式
                EventLog.WriteEntry("VPetLLM", message, eventLogLevel);
            }
            catch
            {
                // 忽略事件日志写入异常
            }
        }

        private string FormatLogEntry(LogEntry logEntry)
        {
            var template = _configuration.MessageTemplate;

            var formatted = template
                .Replace("{Timestamp:yyyy-MM-dd HH:mm:ss.fff}", logEntry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Replace("{Level}", logEntry.Level.ToString().ToUpper())
                .Replace("{Source}", logEntry.Source)
                .Replace("{Message}", logEntry.Message);

            if (logEntry.Exception is not null)
            {
                formatted += Environment.NewLine + logEntry.Exception.ToString();
            }

            if (logEntry.Context.Any())
            {
                var contextJson = JsonSerializer.Serialize(logEntry.Context, new JsonSerializerOptions { WriteIndented = true });
                formatted += Environment.NewLine + "Context: " + contextJson;
            }

            return formatted;
        }

        private ConsoleColor GetConsoleColor(LogLevel level)
        {
            return level switch
            {
                LogLevel.Trace => ConsoleColor.Gray,
                LogLevel.Debug => ConsoleColor.White,
                LogLevel.Information => ConsoleColor.Green,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Critical => ConsoleColor.Magenta,
                _ => ConsoleColor.White
            };
        }

        private EventLogEntryType GetEventLogLevel(LogLevel level)
        {
            return level switch
            {
                LogLevel.Warning => EventLogEntryType.Warning,
                LogLevel.Error => EventLogEntryType.Error,
                LogLevel.Critical => EventLogEntryType.Error,
                _ => EventLogEntryType.Information
            };
        }

        /// <summary>
        /// 获取内存中的日志条目
        /// </summary>
        public IEnumerable<LogEntry> GetMemoryLogs()
        {
            return _memoryBuffer.ToArray();
        }

        /// <summary>
        /// 清空内存日志
        /// </summary>
        public void ClearMemoryLogs()
        {
            while (_memoryBuffer.TryDequeue(out _)) { }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // 刷新剩余的日志
            FlushLogs(null);

            _flushTimer?.Dispose();
            _flushSemaphore?.Dispose();
            _scopes?.Dispose();
        }

        /// <summary>
        /// 日志作用域实现
        /// </summary>
        private class LogScope : IDisposable
        {
            private readonly Action _disposeAction;

            public LogScope(Action disposeAction)
            {
                _disposeAction = disposeAction;
            }

            public void Dispose()
            {
                _disposeAction?.Invoke();
            }
        }
    }
}