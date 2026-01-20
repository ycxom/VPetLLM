using System.Diagnostics;
using VPetLLM.Models;
using VPetLLM.Utils.System;

namespace VPetLLM.Core.UnifiedTTS
{
    /// <summary>
    /// 统一的 TTS 调度器实现
    /// 提供统一的 TTS 请求处理入口，支持外部 TTS 和内置 TTS 的双路径处理
    /// </summary>
    public class TTSDispatcher : ITTSDispatcher
    {
        private readonly IConfigurationManager _configManager;
        private readonly IExternalTTSAdapter _externalAdapter;
        private readonly IBuiltInTTSAdapter _builtInAdapter;
        private readonly IErrorHandlingStrategy _errorHandlingStrategy;
        private readonly ITTSErrorLogger _errorLogger;
        private readonly ITTSStateManager _stateManager;
        private readonly TimeoutManager _timeoutManager;
        private readonly object _dispatcherLock = new object();

        private ServiceStatus _serviceStatus;
        private TTSRequest _currentRequest;
        private CancellationTokenSource _currentCancellationTokenSource;
        private bool _isInitialized = false;

        public TTSDispatcher(
            IConfigurationManager configManager,
            IExternalTTSAdapter externalAdapter,
            IBuiltInTTSAdapter builtInAdapter,
            IErrorHandlingStrategy errorHandlingStrategy = null,
            ITTSErrorLogger errorLogger = null,
            ITTSStateManager stateManager = null)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _externalAdapter = externalAdapter ?? throw new ArgumentNullException(nameof(externalAdapter));
            _builtInAdapter = builtInAdapter ?? throw new ArgumentNullException(nameof(builtInAdapter));
            _errorHandlingStrategy = errorHandlingStrategy ?? new DefaultErrorHandlingStrategy();
            _errorLogger = errorLogger ?? new TTSErrorLogger();
            _stateManager = stateManager ?? new TTSStateManager();
            _timeoutManager = new TimeoutManager();

            InitializeServiceStatus();
            SubscribeToConfigurationChanges();
        }

        /// <summary>
        /// 处理 TTS 请求
        /// </summary>
        /// <param name="request">TTS 请求对象</param>
        /// <returns>TTS 响应结果</returns>
        public async Task<TTSResponse> ProcessRequestAsync(TTSRequest request)
        {
            if (request == null)
            {
                Logger.Log("TTSDispatcher: Request is null");
                return TTSResponse.CreateError(null, TTSErrorCodes.InvalidRequest, "Request cannot be null");
            }

            var stopwatch = Stopwatch.StartNew();
            var context = new ErrorContext
            {
                RequestId = request.RequestId,
                StartTime = DateTime.UtcNow,
                TimeoutMs = 30000 // 30秒超时
            };

            try
            {
                Logger.Log($"TTSDispatcher: Processing request {request.RequestId} with text length {request.Text?.Length}");

                // 验证请求
                var validationResult = ValidateRequest(request);
                if (!validationResult.IsValid)
                {
                    Logger.Log($"TTSDispatcher: Request validation failed: {validationResult.ErrorMessage}");
                    var validationError = new TTSError(TTSErrorCodes.ValidationFailed, validationResult.ErrorMessage, request.RequestId)
                    {
                        Details = string.Join("; ", validationResult.Errors),
                        Source = "TTSDispatcher"
                    };
                    await _errorLogger.LogErrorAsync(validationError, context);
                    return TTSResponse.CreateError(request.RequestId, TTSErrorCodes.ValidationFailed,
                        validationResult.ErrorMessage, string.Join("; ", validationResult.Errors));
                }

                // 获取当前配置
                var configuration = _configManager.GetCurrentConfiguration();
                if (configuration == null)
                {
                    Logger.Log("TTSDispatcher: No configuration available");
                    var configError = new TTSError(TTSErrorCodes.ConfigurationError, "No TTS configuration available", request.RequestId)
                    {
                        Source = "TTSDispatcher"
                    };
                    await _errorLogger.LogErrorAsync(configError, context);
                    return TTSResponse.CreateError(request.RequestId, TTSErrorCodes.ConfigurationError,
                        "No TTS configuration available");
                }

                context.TTSType = configuration.Type;

                // 使用重试逻辑处理请求
                var response = await ProcessRequestWithRetryAsync(request, configuration, context);

                // 记录性能信息
                await _errorLogger.LogPerformanceAsync(request.RequestId, configuration.Type, stopwatch.Elapsed, response.Success);

                Logger.Log($"TTSDispatcher: Request {request.RequestId} processed in {stopwatch.ElapsedMilliseconds}ms, success: {response.Success}");
                return response;
            }
            catch (OperationCanceledException)
            {
                Logger.Log($"TTSDispatcher: Request {request.RequestId} was cancelled");
                var cancelError = new TTSError(TTSErrorCodes.ProcessingError, "Request was cancelled", request.RequestId)
                {
                    Source = "TTSDispatcher"
                };
                await _errorLogger.LogErrorAsync(cancelError, context);
                return TTSResponse.CreateError(request.RequestId, TTSErrorCodes.ProcessingError, "Request was cancelled");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSDispatcher: Error processing request {request.RequestId}: {ex.Message}");
                var error = TTSError.FromException(ex, request.RequestId, "TTSDispatcher");
                await _errorLogger.LogErrorAsync(error, context);
                return TTSResponse.CreateError(request.RequestId, error.Code, error.Message, error.Details);
            }
            finally
            {
                stopwatch.Stop();

                // 清理超时令牌
                _timeoutManager.CleanupTimeout(request.RequestId);

                // 清理当前请求状态
                lock (_dispatcherLock)
                {
                    _currentRequest = null;
                    _currentCancellationTokenSource?.Dispose();
                    _currentCancellationTokenSource = null;
                    _serviceStatus.ActiveRequests = 0;
                }
            }
        }

        /// <summary>
        /// 使用重试逻辑处理请求
        /// </summary>
        /// <param name="request">TTS 请求</param>
        /// <param name="configuration">配置信息</param>
        /// <param name="context">错误上下文</param>
        /// <returns>TTS 响应</returns>
        private async Task<TTSResponse> ProcessRequestWithRetryAsync(TTSRequest request, TTSConfiguration configuration, ErrorContext context)
        {
            TTSResponse lastResponse = null;

            // 注册活跃请求
            var activeRequestInfo = new ActiveRequestInfo
            {
                RequestId = request.RequestId,
                TTSType = configuration.Type,
                TextLength = request.Text?.Length ?? 0,
                Status = RequestStatus.Processing
            };

            await _stateManager.RegisterActiveRequestAsync(activeRequestInfo);

            try
            {
                while (context.AttemptCount <= context.MaxRetries)
                {
                    try
                    {
                        // 更新请求状态
                        if (context.AttemptCount > 0)
                        {
                            activeRequestInfo.Status = RequestStatus.Retrying;
                        }

                        // 更新当前请求状态
                        lock (_dispatcherLock)
                        {
                            _currentRequest = request;
                            _currentCancellationTokenSource = new CancellationTokenSource();
                            _serviceStatus.ActiveRequests = 1;
                        }

                        // 创建超时令牌
                        var timeoutToken = _timeoutManager.CreateTimeoutToken(request.RequestId, context.TimeoutMs);

                        // 处理请求
                        lastResponse = await RouteRequestAsync(request, configuration, timeoutToken);

                        if (lastResponse.Success)
                        {
                            // 成功，更新状态并返回结果
                            activeRequestInfo.Status = RequestStatus.Completed;
                            return lastResponse;
                        }

                        // 失败，准备重试逻辑
                        var error = new TTSError(lastResponse.ErrorCode, lastResponse.ErrorMessage, request.RequestId)
                        {
                            Details = lastResponse.ErrorDetails,
                            Source = "TTSDispatcher"
                        };

                        context.AttemptCount++;

                        // 使用错误处理策略决定是否重试
                        var handlingResult = await _errorHandlingStrategy.HandleErrorAsync(error, context);

                        if (!handlingResult.ShouldRetry)
                        {
                            // 不重试，更新状态并返回最后的错误
                            activeRequestInfo.Status = RequestStatus.Failed;
                            return lastResponse;
                        }

                        // 记录重试信息
                        await _errorLogger.LogRetryAsync(request.RequestId, context.AttemptCount, error);

                        // 等待重试延迟
                        if (handlingResult.RetryDelay > 0)
                        {
                            await Task.Delay(handlingResult.RetryDelay);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // 请求被取消
                        activeRequestInfo.Status = RequestStatus.Cancelled;
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var error = TTSError.FromException(ex, request.RequestId, "TTSDispatcher");
                        context.AttemptCount++;

                        var handlingResult = await _errorHandlingStrategy.HandleErrorAsync(error, context);

                        if (!handlingResult.ShouldRetry)
                        {
                            activeRequestInfo.Status = RequestStatus.Failed;
                            throw;
                        }

                        await _errorLogger.LogRetryAsync(request.RequestId, context.AttemptCount, error);

                        if (handlingResult.RetryDelay > 0)
                        {
                            await Task.Delay(handlingResult.RetryDelay);
                        }

                        lastResponse = TTSResponse.CreateError(request.RequestId, error.Code, error.Message, error.Details);
                    }
                }

                // 达到最大重试次数，更新状态并返回最后的错误
                activeRequestInfo.Status = RequestStatus.Failed;
                return lastResponse ?? TTSResponse.CreateError(request.RequestId, TTSErrorCodes.ProcessingError, "Maximum retry attempts exceeded");
            }
            finally
            {
                // 注销活跃请求
                await _stateManager.UnregisterActiveRequestAsync(request.RequestId);
            }
        }

        /// <summary>
        /// 获取服务状态
        /// </summary>
        /// <returns>当前服务状态信息</returns>
        public async Task<ServiceStatus> GetServiceStatusAsync()
        {
            try
            {
                Logger.Log("TTSDispatcher: Getting service status");

                // 从状态管理器获取最新状态
                var status = await _stateManager.GetServiceStatusAsync();

                // 更新本地状态信息
                lock (_dispatcherLock)
                {
                    var configuration = _configManager.GetCurrentConfiguration();
                    if (configuration != null)
                    {
                        status.CurrentType = configuration.Type;
                        status.ConfigurationVersion = configuration.Version;
                    }

                    status.ActiveRequests = _currentRequest != null ? 1 : 0;
                }

                return status;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSDispatcher: Error getting service status: {ex.Message}");

                lock (_dispatcherLock)
                {
                    _serviceStatus.SetError($"Error getting status: {ex.Message}");
                    return _serviceStatus.CreateSnapshot();
                }
            }
        }

        /// <summary>
        /// 更新配置
        /// </summary>
        /// <param name="config">新的配置对象</param>
        /// <returns>是否更新成功</returns>
        public async Task<bool> UpdateConfigurationAsync(TTSConfiguration config)
        {
            try
            {
                Logger.Log("TTSDispatcher: Updating configuration");

                if (config == null)
                {
                    Logger.Log("TTSDispatcher: Configuration is null");
                    return false;
                }

                // 使用配置管理器更新配置
                var success = await _configManager.UpdateConfigurationAsync(config);

                if (success)
                {
                    Logger.Log($"TTSDispatcher: Configuration updated successfully. Type: {config.Type}");

                    // 更新服务状态
                    lock (_dispatcherLock)
                    {
                        _serviceStatus.CurrentType = config.Type;
                        _serviceStatus.ConfigurationVersion = config.Version;
                        _serviceStatus.ClearError();
                    }
                }
                else
                {
                    Logger.Log("TTSDispatcher: Failed to update configuration");
                }

                return success;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSDispatcher: Error updating configuration: {ex.Message}");

                lock (_dispatcherLock)
                {
                    _serviceStatus.SetError($"Configuration update failed: {ex.Message}");
                }

                return false;
            }
        }

        /// <summary>
        /// 验证请求参数
        /// </summary>
        /// <param name="request">要验证的请求</param>
        /// <returns>验证结果</returns>
        public ValidationResult ValidateRequest(TTSRequest request)
        {
            try
            {
                if (request == null)
                {
                    return ValidationResult.Failure("Request cannot be null");
                }

                // 使用请求对象自身的验证
                var result = request.Validate();

                // 添加调度器特定的验证
                if (!_isInitialized)
                {
                    result.AddError("TTS Dispatcher is not initialized");
                }

                var configuration = _configManager.GetCurrentConfiguration();
                if (configuration == null)
                {
                    result.AddError("No TTS configuration available");
                }

                return result;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSDispatcher: Error validating request: {ex.Message}");
                return ValidationResult.Failure($"Validation error: {ex.Message}");
            }
        }

        /// <summary>
        /// 取消当前处理中的 TTS 请求
        /// </summary>
        public void CancelCurrentRequest()
        {
            try
            {
                Logger.Log("TTSDispatcher: Cancelling current request");

                lock (_dispatcherLock)
                {
                    if (_currentRequest != null && _currentCancellationTokenSource != null)
                    {
                        _currentCancellationTokenSource.Cancel();
                        Logger.Log($"TTSDispatcher: Cancelled request {_currentRequest.RequestId}");
                    }
                    else
                    {
                        Logger.Log("TTSDispatcher: No active request to cancel");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSDispatcher: Error cancelling request: {ex.Message}");
            }
        }

        /// <summary>
        /// 重置调度器状态
        /// </summary>
        public void Reset()
        {
            try
            {
                Logger.Log("TTSDispatcher: Resetting dispatcher state");

                lock (_dispatcherLock)
                {
                    // 取消当前请求
                    _currentCancellationTokenSource?.Cancel();
                    _currentRequest = null;
                    _currentCancellationTokenSource?.Dispose();
                    _currentCancellationTokenSource = null;

                    // 重置服务状态
                    _serviceStatus.ActiveRequests = 0;
                    _serviceStatus.ClearError();
                    _serviceStatus.StatusMessage = "Reset completed";
                }

                Logger.Log("TTSDispatcher: Reset completed");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSDispatcher: Error during reset: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查调度器是否处于活跃状态
        /// </summary>
        /// <returns>是否活跃</returns>
        public bool IsActive()
        {
            lock (_dispatcherLock)
            {
                return _currentRequest != null && _serviceStatus.ActiveRequests > 0;
            }
        }

        /// <summary>
        /// 获取活跃请求信息
        /// </summary>
        /// <returns>活跃请求列表</returns>
        public async Task<IEnumerable<ActiveRequestInfo>> GetActiveRequestsAsync()
        {
            try
            {
                return await _stateManager.GetActiveRequestsAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSDispatcher: Error getting active requests: {ex.Message}");
                return Enumerable.Empty<ActiveRequestInfo>();
            }
        }

        /// <summary>
        /// 获取性能指标
        /// </summary>
        /// <returns>性能指标</returns>
        public async Task<PerformanceMetrics> GetPerformanceMetricsAsync()
        {
            try
            {
                return await _stateManager.GetPerformanceMetricsAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSDispatcher: Error getting performance metrics: {ex.Message}");
                return new PerformanceMetrics();
            }
        }

        /// <summary>
        /// 执行健康检查
        /// </summary>
        /// <returns>健康检查结果</returns>
        public async Task<HealthCheckResult> PerformHealthCheckAsync()
        {
            try
            {
                return await _stateManager.PerformHealthCheckAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSDispatcher: Error performing health check: {ex.Message}");
                return new HealthCheckResult
                {
                    IsHealthy = false,
                    OverallMessage = $"Health check failed: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 获取系统资源使用情况
        /// </summary>
        /// <returns>资源使用情况</returns>
        public async Task<ResourceUsage> GetResourceUsageAsync()
        {
            try
            {
                return await _stateManager.GetResourceUsageAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSDispatcher: Error getting resource usage: {ex.Message}");
                return new ResourceUsage();
            }
        }

        /// <summary>
        /// 重置统计信息
        /// </summary>
        public async Task ResetStatisticsAsync()
        {
            try
            {
                await _stateManager.ResetStatisticsAsync();

                lock (_dispatcherLock)
                {
                    _serviceStatus.TotalProcessedRequests = 0;
                    _serviceStatus.SuccessfulRequests = 0;
                    _serviceStatus.FailedRequests = 0;
                    _serviceStatus.AverageResponseTimeMs = 0;
                }

                Logger.Log("TTSDispatcher: Statistics reset completed");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSDispatcher: Error resetting statistics: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 初始化服务状态
        /// </summary>
        private void InitializeServiceStatus()
        {
            _serviceStatus = new ServiceStatus
            {
                IsAvailable = true,
                CurrentType = TTSType.External, // 默认值，会在配置加载后更新
                ActiveRequests = 0,
                ServiceStartTime = DateTime.UtcNow,
                Version = "1.0.0",
                StatusMessage = "TTS Dispatcher initialized"
            };

            _isInitialized = true;
            Logger.Log("TTSDispatcher: Service status initialized");
        }

        /// <summary>
        /// 订阅配置变更事件
        /// </summary>
        private void SubscribeToConfigurationChanges()
        {
            try
            {
                _configManager.ConfigurationChanged += OnConfigurationChanged;
                Logger.Log("TTSDispatcher: Subscribed to configuration changes");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSDispatcher: Error subscribing to configuration changes: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理配置变更事件
        /// </summary>
        /// <param name="sender">事件发送者</param>
        /// <param name="e">配置变更事件参数</param>
        private void OnConfigurationChanged(object sender, ConfigurationChangedEventArgs e)
        {
            try
            {
                Logger.Log($"TTSDispatcher: Configuration changed from {e.OldConfiguration?.Type} to {e.NewConfiguration?.Type}");

                lock (_dispatcherLock)
                {
                    if (e.NewConfiguration != null)
                    {
                        _serviceStatus.CurrentType = e.NewConfiguration.Type;
                        _serviceStatus.ConfigurationVersion = e.NewConfiguration.Version;
                        _serviceStatus.StatusMessage = $"Configuration updated to {e.NewConfiguration.Type}";
                    }
                }

                // 这里可以添加适配器重新初始化的逻辑
                _ = Task.Run(async () => await ReinitializeAdaptersAsync(e.NewConfiguration));
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSDispatcher: Error handling configuration change: {ex.Message}");
            }
        }

        /// <summary>
        /// 重新初始化适配器
        /// </summary>
        /// <param name="newConfiguration">新配置</param>
        /// <returns>重新初始化任务</returns>
        private async Task ReinitializeAdaptersAsync(TTSConfiguration newConfiguration)
        {
            try
            {
                Logger.Log("TTSDispatcher: Reinitializing adapters with new configuration");

                if (newConfiguration == null)
                {
                    Logger.Log("TTSDispatcher: New configuration is null, skipping reinitialization");
                    return;
                }

                // 根据配置类型初始化相应的适配器
                switch (newConfiguration.Type)
                {
                    case TTSType.External:
                        if (newConfiguration.ExternalSettings != null)
                        {
                            await _externalAdapter.InitializeAsync(newConfiguration.ExternalSettings);
                        }
                        break;

                    case TTSType.BuiltIn:
                        if (newConfiguration.BuiltInSettings != null)
                        {
                            await _builtInAdapter.InitializeAsync(newConfiguration.BuiltInSettings);
                        }
                        break;
                }

                Logger.Log("TTSDispatcher: Adapter reinitialization completed");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSDispatcher: Error reinitializing adapters: {ex.Message}");

                lock (_dispatcherLock)
                {
                    _serviceStatus.SetError($"Adapter reinitialization failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 更新请求统计信息
        /// </summary>
        /// <param name="success">是否成功</param>
        /// <param name="processingTime">处理时间</param>
        private void UpdateRequestStatistics(bool success, TimeSpan processingTime)
        {
            lock (_dispatcherLock)
            {
                _serviceStatus.IncrementRequestCount(success, processingTime.TotalMilliseconds);
            }
        }

        /// <summary>
        /// 根据配置路由请求到相应的适配器
        /// </summary>
        /// <param name="request">TTS 请求</param>
        /// <param name="configuration">当前配置</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>TTS 响应</returns>
        private async Task<TTSResponse> RouteRequestAsync(TTSRequest request, TTSConfiguration configuration, CancellationToken cancellationToken = default)
        {
            try
            {
                Logger.Log($"TTSDispatcher: Routing request {request.RequestId} to {configuration.Type} adapter");

                ITTSAdapter targetAdapter = configuration.Type switch
                {
                    TTSType.External => _externalAdapter,
                    TTSType.BuiltIn => _builtInAdapter,
                    _ => throw new InvalidOperationException($"Unsupported TTS type: {configuration.Type}")
                };

                // 检查目标适配器是否可用
                if (!await targetAdapter.IsAvailableAsync())
                {
                    Logger.Log($"TTSDispatcher: {configuration.Type} adapter is not available");
                    return TTSResponse.CreateError(request.RequestId, TTSErrorCodes.AdapterUnavailable,
                        $"{configuration.Type} TTS adapter is not available");
                }

                // 确保适配器已初始化
                var activeSettings = configuration.GetActiveSettings();
                if (activeSettings != null)
                {
                    await targetAdapter.InitializeAsync(activeSettings);
                }

                // 处理请求（使用取消令牌）
                var response = await ProcessWithTimeoutAsync(targetAdapter, request, cancellationToken);

                Logger.Log($"TTSDispatcher: Request {request.RequestId} routed successfully to {configuration.Type} adapter");
                return response;
            }
            catch (OperationCanceledException)
            {
                Logger.Log($"TTSDispatcher: Request {request.RequestId} routing was cancelled");
                return TTSResponse.CreateError(request.RequestId, TTSErrorCodes.Timeout, "Request was cancelled or timed out");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSDispatcher: Error routing request {request.RequestId}: {ex.Message}");
                var error = TTSError.FromException(ex, request.RequestId, $"TTSDispatcher-{configuration.Type}");
                return TTSResponse.CreateError(request.RequestId, error.Code, error.Message, error.Details);
            }
        }

        /// <summary>
        /// 使用超时处理适配器请求
        /// </summary>
        /// <param name="adapter">TTS 适配器</param>
        /// <param name="request">TTS 请求</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>TTS 响应</returns>
        private async Task<TTSResponse> ProcessWithTimeoutAsync(ITTSAdapter adapter, TTSRequest request, CancellationToken cancellationToken)
        {
            try
            {
                // 组合超时令牌和外部取消令牌
                using var combinedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                // 处理请求
                var response = await adapter.ProcessAsync(request);

                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Logger.Log($"TTSDispatcher: Request {request.RequestId} was cancelled by timeout");
                return TTSResponse.CreateError(request.RequestId, TTSErrorCodes.Timeout, "Request timed out");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSDispatcher: Error processing request {request.RequestId} with adapter: {ex.Message}");
                var error = TTSError.FromException(ex, request.RequestId, "TTSAdapter");
                return TTSResponse.CreateError(request.RequestId, error.Code, error.Message, error.Details);
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            try
            {
                Logger.Log("TTSDispatcher: Disposing resources");

                // 取消订阅配置变更事件
                if (_configManager != null)
                {
                    _configManager.ConfigurationChanged -= OnConfigurationChanged;
                }

                // 取消当前请求
                CancelCurrentRequest();

                // 清理超时管理器
                _timeoutManager?.CleanupAll();

                // 清理状态管理器
                if (_stateManager is IDisposable disposableStateManager)
                {
                    disposableStateManager.Dispose();
                }

                // 清理错误日志记录器
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _errorLogger?.CleanupOldLogsAsync();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"TTSDispatcher: Error cleaning up error logger: {ex.Message}");
                    }
                });

                // 清理适配器
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _externalAdapter?.CleanupAsync();
                        await _builtInAdapter?.CleanupAsync();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"TTSDispatcher: Error cleaning up adapters: {ex.Message}");
                    }
                });

                Logger.Log("TTSDispatcher: Disposal completed");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSDispatcher: Error during disposal: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 内置 TTS 适配器接口（用于依赖注入）
    /// </summary>
    public interface IExternalTTSAdapter : ITTSAdapter
    {
    }

    /// <summary>
    /// 外部 TTS 适配器接口（用于依赖注入）
    /// </summary>
    public interface IBuiltInTTSAdapter : ITTSAdapter
    {
    }
}