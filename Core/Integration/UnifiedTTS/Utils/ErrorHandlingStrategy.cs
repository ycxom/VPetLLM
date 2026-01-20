namespace VPetLLM.Core.Integration.UnifiedTTS.Utils
{
    /// <summary>
    /// TTS 错误处理策略接口
    /// </summary>
    public interface IErrorHandlingStrategy
    {
        /// <summary>
        /// 处理错误
        /// </summary>
        /// <param name="error">错误信息</param>
        /// <param name="context">错误上下文</param>
        /// <returns>错误处理结果</returns>
        Task<ErrorHandlingResult> HandleErrorAsync(TTSError error, ErrorContext context);

        /// <summary>
        /// 判断是否应该重试
        /// </summary>
        /// <param name="error">错误信息</param>
        /// <param name="attemptCount">已尝试次数</param>
        /// <returns>是否应该重试</returns>
        bool ShouldRetry(TTSError error, int attemptCount);

        /// <summary>
        /// 获取重试延迟时间
        /// </summary>
        /// <param name="attemptCount">已尝试次数</param>
        /// <returns>延迟时间（毫秒）</returns>
        int GetRetryDelay(int attemptCount);
    }

    /// <summary>
    /// 错误处理结果
    /// </summary>
    public class ErrorHandlingResult
    {
        /// <summary>
        /// 是否应该重试
        /// </summary>
        public bool ShouldRetry { get; set; }

        /// <summary>
        /// 重试延迟（毫秒）
        /// </summary>
        public int RetryDelay { get; set; }

        /// <summary>
        /// 处理后的错误信息
        /// </summary>
        public TTSError ProcessedError { get; set; }

        /// <summary>
        /// 是否已记录日志
        /// </summary>
        public bool IsLogged { get; set; }

        /// <summary>
        /// 附加处理信息
        /// </summary>
        public string AdditionalInfo { get; set; }

        public static ErrorHandlingResult NoRetry(TTSError error, string additionalInfo = null)
        {
            return new ErrorHandlingResult
            {
                ShouldRetry = false,
                ProcessedError = error,
                IsLogged = true,
                AdditionalInfo = additionalInfo
            };
        }

        public static ErrorHandlingResult Retry(TTSError error, int delay, string additionalInfo = null)
        {
            return new ErrorHandlingResult
            {
                ShouldRetry = true,
                RetryDelay = delay,
                ProcessedError = error,
                IsLogged = true,
                AdditionalInfo = additionalInfo
            };
        }
    }

    /// <summary>
    /// 错误上下文信息
    /// </summary>
    public class ErrorContext
    {
        /// <summary>
        /// 请求ID
        /// </summary>
        public string RequestId { get; set; }

        /// <summary>
        /// TTS 类型
        /// </summary>
        public TTSType TTSType { get; set; }

        /// <summary>
        /// 已尝试次数
        /// </summary>
        public int AttemptCount { get; set; }

        /// <summary>
        /// 最大重试次数
        /// </summary>
        public int MaxRetries { get; set; }

        /// <summary>
        /// 操作开始时间
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// 超时时间（毫秒）
        /// </summary>
        public int TimeoutMs { get; set; }

        /// <summary>
        /// 附加上下文数据
        /// </summary>
        public Dictionary<string, object> AdditionalData { get; set; }

        public ErrorContext()
        {
            AdditionalData = new Dictionary<string, object>();
            StartTime = DateTime.UtcNow;
            MaxRetries = 3;
            TimeoutMs = 30000; // 30秒默认超时
        }

        /// <summary>
        /// 检查是否已超时
        /// </summary>
        /// <returns>是否已超时</returns>
        public bool IsTimedOut()
        {
            return (DateTime.UtcNow - StartTime).TotalMilliseconds > TimeoutMs;
        }

        /// <summary>
        /// 检查是否已达到最大重试次数
        /// </summary>
        /// <returns>是否已达到最大重试次数</returns>
        public bool HasExceededMaxRetries()
        {
            return AttemptCount >= MaxRetries;
        }
    }

    /// <summary>
    /// 默认的 TTS 错误处理策略实现
    /// </summary>
    public class DefaultErrorHandlingStrategy : IErrorHandlingStrategy
    {
        private readonly int _maxRetries;
        private readonly int _baseDelayMs;
        private readonly bool _enableExponentialBackoff;

        public DefaultErrorHandlingStrategy(int maxRetries = 3, int baseDelayMs = 1000, bool enableExponentialBackoff = true)
        {
            _maxRetries = maxRetries;
            _baseDelayMs = baseDelayMs;
            _enableExponentialBackoff = enableExponentialBackoff;
        }

        /// <summary>
        /// 处理错误
        /// </summary>
        public async Task<ErrorHandlingResult> HandleErrorAsync(TTSError error, ErrorContext context)
        {
            try
            {
                // 记录错误日志
                LogError(error, context);

                // 检查是否应该重试
                if (!ShouldRetry(error, context.AttemptCount))
                {
                    return ErrorHandlingResult.NoRetry(error, "Error is not retryable or max retries exceeded");
                }

                // 检查是否已超时
                if (context.IsTimedOut())
                {
                    var timeoutError = new TTSError(TTSErrorCodes.Timeout,
                        "Operation timed out", context.RequestId)
                    {
                        Source = "ErrorHandlingStrategy",
                        Severity = TTSErrorSeverity.Error
                    };
                    return ErrorHandlingResult.NoRetry(timeoutError, "Operation timed out");
                }

                // 计算重试延迟
                var delay = GetRetryDelay(context.AttemptCount);

                Logger.Log($"ErrorHandlingStrategy: Will retry request {context.RequestId} after {delay}ms (attempt {context.AttemptCount + 1}/{context.MaxRetries})");

                return ErrorHandlingResult.Retry(error, delay, $"Retrying after {delay}ms");
            }
            catch (Exception ex)
            {
                Logger.Log($"ErrorHandlingStrategy: Error in error handling for request {context.RequestId}: {ex.Message}");

                var handlingError = TTSError.FromException(ex, context.RequestId, "ErrorHandlingStrategy");
                return ErrorHandlingResult.NoRetry(handlingError, "Error occurred during error handling");
            }
        }

        /// <summary>
        /// 判断是否应该重试
        /// </summary>
        public bool ShouldRetry(TTSError error, int attemptCount)
        {
            // 检查重试次数限制
            if (attemptCount >= _maxRetries)
            {
                return false;
            }

            // 检查错误是否可重试
            if (!error.IsRetryable)
            {
                return false;
            }

            // 根据错误代码判断是否可重试
            return error.Code switch
            {
                TTSErrorCodes.Timeout => true,
                TTSErrorCodes.NetworkError => true,
                TTSErrorCodes.ServiceUnavailable => true,
                TTSErrorCodes.ProcessingError => true,
                TTSErrorCodes.AdapterUnavailable => true,
                TTSErrorCodes.InvalidRequest => false,
                TTSErrorCodes.ValidationFailed => false,
                TTSErrorCodes.ConfigurationError => false,
                TTSErrorCodes.AuthenticationFailed => false,
                TTSErrorCodes.UnsupportedFormat => false,
                _ => error.Severity != TTSErrorSeverity.Critical
            };
        }

        /// <summary>
        /// 获取重试延迟时间
        /// </summary>
        public int GetRetryDelay(int attemptCount)
        {
            if (!_enableExponentialBackoff)
            {
                return _baseDelayMs;
            }

            // 指数退避算法：delay = baseDelay * 2^attemptCount
            var delay = _baseDelayMs * Math.Pow(2, attemptCount);

            // 限制最大延迟为30秒
            return Math.Min((int)delay, 30000);
        }

        /// <summary>
        /// 记录错误日志
        /// </summary>
        private void LogError(TTSError error, ErrorContext context)
        {
            var logLevel = error.Severity switch
            {
                TTSErrorSeverity.Critical => "CRITICAL",
                TTSErrorSeverity.Error => "ERROR",
                TTSErrorSeverity.Warning => "WARNING",
                TTSErrorSeverity.Info => "INFO",
                _ => "ERROR"
            };

            var message = $"[{logLevel}] TTS Error in {context.TTSType} - {error}";

            if (context.AttemptCount > 0)
            {
                message += $" (Attempt {context.AttemptCount}/{context.MaxRetries})";
            }

            if (!string.IsNullOrEmpty(error.Details))
            {
                message += $" Details: {error.Details}";
            }

            Logger.Log(message);
        }
    }

    /// <summary>
    /// 超时管理器
    /// </summary>
    public class TimeoutManager
    {
        private readonly Dictionary<string, CancellationTokenSource> _timeoutTokens;
        private readonly object _lock = new object();

        public TimeoutManager()
        {
            _timeoutTokens = new Dictionary<string, CancellationTokenSource>();
        }

        /// <summary>
        /// 创建超时令牌
        /// </summary>
        /// <param name="requestId">请求ID</param>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        /// <returns>取消令牌</returns>
        public CancellationToken CreateTimeoutToken(string requestId, int timeoutMs)
        {
            lock (_lock)
            {
                // 清理已存在的令牌
                if (_timeoutTokens.TryGetValue(requestId, out var existingToken))
                {
                    existingToken.Cancel();
                    existingToken.Dispose();
                }

                // 创建新的超时令牌
                var tokenSource = new CancellationTokenSource(timeoutMs);
                _timeoutTokens[requestId] = tokenSource;

                Logger.Log($"TimeoutManager: Created timeout token for request {requestId} with {timeoutMs}ms timeout");
                return tokenSource.Token;
            }
        }

        /// <summary>
        /// 取消超时令牌
        /// </summary>
        /// <param name="requestId">请求ID</param>
        public void CancelTimeout(string requestId)
        {
            lock (_lock)
            {
                if (_timeoutTokens.TryGetValue(requestId, out var tokenSource))
                {
                    tokenSource.Cancel();
                    tokenSource.Dispose();
                    _timeoutTokens.Remove(requestId);

                    Logger.Log($"TimeoutManager: Cancelled timeout for request {requestId}");
                }
            }
        }

        /// <summary>
        /// 清理超时令牌
        /// </summary>
        /// <param name="requestId">请求ID</param>
        public void CleanupTimeout(string requestId)
        {
            lock (_lock)
            {
                if (_timeoutTokens.TryGetValue(requestId, out var tokenSource))
                {
                    tokenSource.Dispose();
                    _timeoutTokens.Remove(requestId);

                    Logger.Log($"TimeoutManager: Cleaned up timeout for request {requestId}");
                }
            }
        }

        /// <summary>
        /// 清理所有超时令牌
        /// </summary>
        public void CleanupAll()
        {
            lock (_lock)
            {
                foreach (var tokenSource in _timeoutTokens.Values)
                {
                    tokenSource.Cancel();
                    tokenSource.Dispose();
                }

                _timeoutTokens.Clear();
                Logger.Log("TimeoutManager: Cleaned up all timeout tokens");
            }
        }

        /// <summary>
        /// 获取活跃的超时令牌数量
        /// </summary>
        /// <returns>活跃令牌数量</returns>
        public int GetActiveTimeoutCount()
        {
            lock (_lock)
            {
                return _timeoutTokens.Count;
            }
        }
    }
}