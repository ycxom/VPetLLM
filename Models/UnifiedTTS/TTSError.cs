namespace VPetLLM.Models
{
    /// <summary>
    /// TTS 错误信息
    /// </summary>
    public class TTSError
    {
        /// <summary>
        /// 错误代码
        /// </summary>
        public string Code { get; set; }

        /// <summary>
        /// 错误消息
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// 详细错误信息
        /// </summary>
        public string Details { get; set; }

        /// <summary>
        /// 错误时间戳
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// 请求标识符
        /// </summary>
        public string RequestId { get; set; }

        /// <summary>
        /// 错误来源
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// 错误严重程度
        /// </summary>
        public TTSErrorSeverity Severity { get; set; }

        /// <summary>
        /// 是否可重试
        /// </summary>
        public bool IsRetryable { get; set; }

        /// <summary>
        /// 内部异常信息
        /// </summary>
        public string InnerException { get; set; }

        public TTSError()
        {
            Timestamp = DateTime.UtcNow;
            Severity = TTSErrorSeverity.Error;
        }

        public TTSError(string code, string message, string requestId = null) : this()
        {
            Code = code;
            Message = message;
            RequestId = requestId;
        }

        /// <summary>
        /// 从异常创建 TTS 错误
        /// </summary>
        /// <param name="exception">异常对象</param>
        /// <param name="requestId">请求ID</param>
        /// <param name="source">错误来源</param>
        /// <returns>TTS 错误对象</returns>
        public static TTSError FromException(Exception exception, string requestId = null, string source = null)
        {
            return new TTSError
            {
                Code = TTSErrorCodes.ProcessingError,
                Message = exception.Message,
                Details = exception.ToString(),
                RequestId = requestId,
                Source = source,
                InnerException = exception.InnerException?.Message,
                IsRetryable = IsRetryableException(exception),
                Severity = GetSeverityFromException(exception)
            };
        }

        /// <summary>
        /// 判断异常是否可重试
        /// </summary>
        /// <param name="exception">异常对象</param>
        /// <returns>是否可重试</returns>
        private static bool IsRetryableException(Exception exception)
        {
            return exception is TimeoutException ||
                   exception is System.Net.Http.HttpRequestException ||
                   exception is System.Net.NetworkInformation.NetworkInformationException ||
                   (exception is InvalidOperationException && exception.Message.Contains("timeout"));
        }

        /// <summary>
        /// 从异常获取错误严重程度
        /// </summary>
        /// <param name="exception">异常对象</param>
        /// <returns>错误严重程度</returns>
        private static TTSErrorSeverity GetSeverityFromException(Exception exception)
        {
            if (exception is ArgumentException || exception is ArgumentNullException)
                return TTSErrorSeverity.Warning;

            if (exception is TimeoutException)
                return TTSErrorSeverity.Error;

            if (exception is OutOfMemoryException || exception is StackOverflowException)
                return TTSErrorSeverity.Critical;

            return TTSErrorSeverity.Error;
        }

        /// <summary>
        /// 获取用户友好的错误消息
        /// </summary>
        /// <returns>用户友好的错误消息</returns>
        public string GetUserFriendlyMessage()
        {
            return Code switch
            {
                TTSErrorCodes.InvalidRequest => "请求参数无效，请检查输入内容",
                TTSErrorCodes.ServiceUnavailable => "TTS 服务暂时不可用，请稍后重试",
                TTSErrorCodes.Timeout => "请求处理超时，请稍后重试",
                TTSErrorCodes.ConfigurationError => "配置错误，请检查 TTS 设置",
                TTSErrorCodes.ProcessingError => "处理过程中发生错误，请稍后重试",
                _ => Message ?? "发生未知错误"
            };
        }

        /// <summary>
        /// 转换为字符串表示
        /// </summary>
        /// <returns>字符串表示</returns>
        public override string ToString()
        {
            var result = $"[{Code}] {Message}";

            if (!string.IsNullOrEmpty(RequestId))
                result += $" (RequestId: {RequestId})";

            if (!string.IsNullOrEmpty(Source))
                result += $" (Source: {Source})";

            return result;
        }
    }

    /// <summary>
    /// TTS 错误严重程度
    /// </summary>
    public enum TTSErrorSeverity
    {
        /// <summary>
        /// 信息
        /// </summary>
        Info,

        /// <summary>
        /// 警告
        /// </summary>
        Warning,

        /// <summary>
        /// 错误
        /// </summary>
        Error,

        /// <summary>
        /// 严重错误
        /// </summary>
        Critical
    }

    /// <summary>
    /// TTS 错误代码常量
    /// </summary>
    public static class TTSErrorCodes
    {
        /// <summary>
        /// 无效请求
        /// </summary>
        public const string InvalidRequest = "TTS_INVALID_REQUEST";

        /// <summary>
        /// 服务不可用
        /// </summary>
        public const string ServiceUnavailable = "TTS_SERVICE_UNAVAILABLE";

        /// <summary>
        /// 超时
        /// </summary>
        public const string Timeout = "TTS_TIMEOUT";

        /// <summary>
        /// 配置错误
        /// </summary>
        public const string ConfigurationError = "TTS_CONFIG_ERROR";

        /// <summary>
        /// 处理错误
        /// </summary>
        public const string ProcessingError = "TTS_PROCESSING_ERROR";

        /// <summary>
        /// 适配器不可用
        /// </summary>
        public const string AdapterUnavailable = "TTS_ADAPTER_UNAVAILABLE";

        /// <summary>
        /// 初始化失败
        /// </summary>
        public const string InitializationFailed = "TTS_INITIALIZATION_FAILED";

        /// <summary>
        /// 验证失败
        /// </summary>
        public const string ValidationFailed = "TTS_VALIDATION_FAILED";

        /// <summary>
        /// 网络错误
        /// </summary>
        public const string NetworkError = "TTS_NETWORK_ERROR";

        /// <summary>
        /// 认证失败
        /// </summary>
        public const string AuthenticationFailed = "TTS_AUTH_FAILED";

        /// <summary>
        /// 配额超限
        /// </summary>
        public const string QuotaExceeded = "TTS_QUOTA_EXCEEDED";

        /// <summary>
        /// 不支持的格式
        /// </summary>
        public const string UnsupportedFormat = "TTS_UNSUPPORTED_FORMAT";
    }
}