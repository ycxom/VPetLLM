namespace VPetLLM.Infrastructure.Logging
{
    /// <summary>
    /// 结构化日志记录器接口
    /// </summary>
    public interface IStructuredLogger
    {
        /// <summary>
        /// 记录跟踪级别日志
        /// </summary>
        void LogTrace(string message, object context = null);

        /// <summary>
        /// 记录调试级别日志
        /// </summary>
        void LogDebug(string message, object context = null);

        /// <summary>
        /// 记录信息级别日志
        /// </summary>
        void LogInformation(string message, object context = null);

        /// <summary>
        /// 记录警告级别日志
        /// </summary>
        void LogWarning(string message, object context = null);

        /// <summary>
        /// 记录错误级别日志
        /// </summary>
        void LogError(Exception exception, string message, object context = null);

        /// <summary>
        /// 记录错误级别日志（无异常）
        /// </summary>
        void LogError(string message, object context = null);

        /// <summary>
        /// 记录严重错误级别日志
        /// </summary>
        void LogCritical(Exception exception, string message, object context = null);

        /// <summary>
        /// 记录严重错误级别日志（无异常）
        /// </summary>
        void LogCritical(string message, object context = null);

        /// <summary>
        /// 检查指定日志级别是否启用
        /// </summary>
        bool IsEnabled(LogLevel logLevel);

        /// <summary>
        /// 开始日志作用域
        /// </summary>
        IDisposable BeginScope<TState>(TState state);
    }

    /// <summary>
    /// 日志级别枚举
    /// </summary>
    public enum LogLevel
    {
        /// <summary>
        /// 跟踪级别，最详细的日志
        /// </summary>
        Trace = 0,

        /// <summary>
        /// 调试级别，用于开发调试
        /// </summary>
        Debug = 1,

        /// <summary>
        /// 信息级别，一般信息
        /// </summary>
        Information = 2,

        /// <summary>
        /// 警告级别，潜在问题
        /// </summary>
        Warning = 3,

        /// <summary>
        /// 错误级别，错误但不影响应用继续运行
        /// </summary>
        Error = 4,

        /// <summary>
        /// 严重错误级别，可能导致应用终止
        /// </summary>
        Critical = 5,

        /// <summary>
        /// 无日志
        /// </summary>
        None = 6
    }

    /// <summary>
    /// 日志条目
    /// </summary>
    public class LogEntry
    {
        /// <summary>
        /// 日志ID
        /// </summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>
        /// 时间戳
        /// </summary>
        public DateTime Timestamp { get; } = DateTime.UtcNow;

        /// <summary>
        /// 日志级别
        /// </summary>
        public LogLevel Level { get; set; }

        /// <summary>
        /// 日志消息
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// 异常信息
        /// </summary>
        public Exception Exception { get; set; }

        /// <summary>
        /// 上下文信息
        /// </summary>
        public Dictionary<string, object> Context { get; set; } = new();

        /// <summary>
        /// 日志来源
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// 线程ID
        /// </summary>
        public int ThreadId { get; set; }

        /// <summary>
        /// 作用域信息
        /// </summary>
        public List<object> Scopes { get; set; } = new();
    }

    /// <summary>
    /// 日志配置
    /// </summary>
    public class LoggingConfiguration
    {
        /// <summary>
        /// 最小日志级别
        /// </summary>
        public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

        /// <summary>
        /// 是否包含异常堆栈跟踪
        /// </summary>
        public bool IncludeStackTrace { get; set; } = true;

        /// <summary>
        /// 是否包含作用域信息
        /// </summary>
        public bool IncludeScopes { get; set; } = true;

        /// <summary>
        /// 日志格式模板
        /// </summary>
        public string MessageTemplate { get; set; } = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level}] [{Source}] {Message}";

        /// <summary>
        /// 最大日志条目数量
        /// </summary>
        public int MaxLogEntries { get; set; } = 10000;

        /// <summary>
        /// 是否启用异步日志
        /// </summary>
        public bool EnableAsyncLogging { get; set; } = true;

        /// <summary>
        /// 日志输出目标
        /// </summary>
        public List<LogTarget> Targets { get; set; } = new();
    }

    /// <summary>
    /// 日志输出目标
    /// </summary>
    public enum LogTarget
    {
        /// <summary>
        /// 控制台输出
        /// </summary>
        Console,

        /// <summary>
        /// 文件输出
        /// </summary>
        File,

        /// <summary>
        /// 内存缓冲区
        /// </summary>
        Memory,

        /// <summary>
        /// 调试输出
        /// </summary>
        Debug,

        /// <summary>
        /// 事件日志
        /// </summary>
        EventLog
    }
}