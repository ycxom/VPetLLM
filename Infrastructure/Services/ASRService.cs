using VPetLLM.Core;
using VPetLLM.Infrastructure.Configuration;
using VPetLLM.Infrastructure.Configuration.Configurations;
using VPetLLM.Infrastructure.Events;
using VPetLLM.Infrastructure.Exceptions;
using VPetLLM.Infrastructure.Logging;

namespace VPetLLM.Infrastructure.Services
{
    /// <summary>
    /// ASR服务 - 管理所有ASR核心的统一服务
    /// </summary>
    public class ASRService : ServiceBase
    {
        public override string ServiceName => "ASRService";

        private readonly IConfigurationManager _configurationManager;
        private new readonly IEventBus _eventBus;
        private readonly CoreFactory _coreFactory;
        private readonly Dictionary<string, ASRCoreBase> _asrCores;
        private ASRCoreBase _currentASRCore;
        private ASRConfiguration _asrConfig;
        private readonly Queue<ASRRequest> _requestQueue;
        private readonly SemaphoreSlim _queueSemaphore;
        private CancellationTokenSource _processingCancellation;

        public ASRService(
            IConfigurationManager configurationManager,
            IEventBus eventBus,
            IStructuredLogger logger,
            CoreFactory coreFactory = null) : base(logger)
        {
            _configurationManager = configurationManager ?? throw new ArgumentNullException(nameof(configurationManager));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _coreFactory = coreFactory ?? new CoreFactory(logger);
            _asrCores = new Dictionary<string, ASRCoreBase>();
            _requestQueue = new Queue<ASRRequest>();
            _queueSemaphore = new SemaphoreSlim(1, 1);
        }

        protected override async Task OnInitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                Logger?.LogInformation("Initializing ASRService");

                // 加载配置
                _asrConfig = _configurationManager.GetConfiguration<ASRConfiguration>();

                // 订阅配置变更事件
                _configurationManager.ConfigurationChanged += OnConfigurationChanged;

                // 初始化ASR核心
                await InitializeASRCoresAsync();

                // 设置当前ASR核心
                await SetCurrentASRCoreAsync(_asrConfig.Provider);

                // 启动请求处理
                _processingCancellation = new CancellationTokenSource();
                _ = Task.Run(() => ProcessRequestQueueAsync(_processingCancellation.Token));

                Logger?.LogInformation("ASRService initialized successfully");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to initialize ASRService");
                throw new ServiceException(ServiceName, "Failed to initialize ASRService", ex);
            }
        }

        protected override async Task OnStartAsync(CancellationToken cancellationToken)
        {
            try
            {
                Logger?.LogInformation("Starting ASRService");

                // 发布服务启动事件
                await _eventBus.PublishAsync(new ASRServiceStartedEvent
                {
                    ServiceName = ServiceName,
                    CurrentProvider = _asrConfig.Provider,
                    IsEnabled = _asrConfig.IsEnabled
                });

                Logger?.LogInformation("ASRService started successfully");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to start ASRService");
                throw new ServiceException(ServiceName, "Failed to start ASRService", ex);
            }
        }

        protected override async Task OnStopAsync(CancellationToken cancellationToken)
        {
            try
            {
                Logger?.LogInformation("Stopping ASRService");

                // 停止请求处理
                _processingCancellation?.Cancel();

                // 清空请求队列
                await _queueSemaphore.WaitAsync(cancellationToken);
                try
                {
                    _requestQueue.Clear();
                }
                finally
                {
                    _queueSemaphore.Release();
                }

                // 取消订阅配置变更事件
                _configurationManager.ConfigurationChanged -= OnConfigurationChanged;

                // 发布服务停止事件
                await _eventBus.PublishAsync(new ASRServiceStoppedEvent
                {
                    ServiceName = ServiceName
                });

                Logger?.LogInformation("ASRService stopped successfully");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error stopping ASRService");
                throw new ServiceException(ServiceName, "Error stopping ASRService", ex);
            }
        }

        public override async Task<ServiceHealthStatus> CheckHealthAsync()
        {
            try
            {
                var metrics = new Dictionary<string, object>
                {
                    ["IsEnabled"] = _asrConfig?.IsEnabled ?? false,
                    ["CurrentProvider"] = _asrConfig?.Provider ?? "Unknown",
                    ["AvailableCores"] = _asrCores.Count,
                    ["CurrentCoreActive"] = _currentASRCore != null,
                    ["QueueLength"] = _requestQueue.Count
                };

                if (_currentASRCore != null)
                {
                    metrics["CurrentCoreName"] = _currentASRCore.Name;

                    // 获取可用模型
                    try
                    {
                        var models = await _currentASRCore.GetModelsAsync();
                        metrics["AvailableModels"] = models.Count;
                    }
                    catch
                    {
                        metrics["AvailableModels"] = 0;
                    }
                }

                var status = HealthStatus.Healthy;
                var description = "Service is healthy";

                if (!(_asrConfig?.IsEnabled ?? false))
                {
                    status = HealthStatus.Degraded;
                    description = "ASR is disabled";
                }
                else if (_currentASRCore == null)
                {
                    status = HealthStatus.Unhealthy;
                    description = "No active ASR core";
                }

                return new ServiceHealthStatus
                {
                    ServiceName = ServiceName,
                    Status = status,
                    Description = description,
                    Metrics = metrics,
                    CheckTime = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Health check failed for ASRService");
                return new ServiceHealthStatus
                {
                    ServiceName = ServiceName,
                    Status = HealthStatus.Unhealthy,
                    Description = $"Health check failed: {ex.Message}",
                    CheckTime = DateTime.UtcNow
                };
            }
        }

        /// <summary>
        /// 转录音频数据
        /// </summary>
        public async Task<string> TranscribeAsync(byte[] audioData)
        {
            if (!(_asrConfig?.IsEnabled ?? false))
            {
                throw new ServiceException(ServiceName, "ASR service is disabled");
            }

            if (_currentASRCore == null)
            {
                throw new ServiceException(ServiceName, "No active ASR core available");
            }

            if (audioData == null || audioData.Length == 0)
            {
                throw new ArgumentException("Audio data cannot be null or empty", nameof(audioData));
            }

            try
            {
                // 发布ASR开始事�?
                await _eventBus.PublishAsync(new ASRRequestStartedEvent
                {
                    Provider = _currentASRCore.Name,
                    AudioSize = audioData.Length
                });

                string transcription;

                if (_asrConfig.UseQueueProcessing)
                {
                    // 使用队列模式
                    transcription = await TranscribeWithQueueAsync(audioData);
                }
                else
                {
                    // 直接转录
                    transcription = await _currentASRCore.TranscribeAsync(audioData);
                }

                // 发布ASR完成事件
                await _eventBus.PublishAsync(new ASRRequestCompletedEvent
                {
                    Provider = _currentASRCore.Name,
                    AudioSize = audioData.Length,
                    Transcription = transcription,
                    TranscriptionLength = transcription?.Length ?? 0
                });

                return transcription ?? string.Empty;
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "ASR request failed", new { Provider = _currentASRCore.Name, AudioSize = audioData.Length });

                // 发布ASR错误事件
                await _eventBus.PublishAsync(new ASRRequestFailedEvent
                {
                    Provider = _currentASRCore.Name,
                    AudioSize = audioData.Length,
                    Error = ex.Message
                });

                throw new ServiceException(ServiceName, $"ASR request failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 切换ASR提供�?
        /// </summary>
        public async Task SwitchProviderAsync(string providerName)
        {
            try
            {
                Logger?.LogInformation("Switching ASR provider", new { From = _currentASRCore?.Name, To = providerName });

                await SetCurrentASRCoreAsync(providerName);

                // 发布提供商切换事�?
                await _eventBus.PublishAsync(new ASRProviderSwitchedEvent
                {
                    OldProvider = _currentASRCore?.Name,
                    NewProvider = providerName
                });

                Logger?.LogInformation("ASR provider switched successfully", new { NewProvider = providerName });
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to switch ASR provider", new { TargetProvider = providerName });
                throw new ServiceException(ServiceName, $"Failed to switch to provider {providerName}", ex);
            }
        }

        /// <summary>
        /// 获取当前ASR核心
        /// </summary>
        public ASRCoreBase GetCurrentASRCore()
        {
            return _currentASRCore;
        }

        /// <summary>
        /// 获取可用的ASR提供商列�?
        /// </summary>
        public List<string> GetAvailableProviders()
        {
            return new List<string>(_asrCores.Keys);
        }

        /// <summary>
        /// 获取当前提供商的可用模型列表
        /// </summary>
        public async Task<List<string>> GetAvailableModelsAsync()
        {
            if (_currentASRCore == null)
            {
                return new List<string>();
            }

            try
            {
                return await _currentASRCore.GetModelsAsync();
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to get available models", new { Provider = _currentASRCore.Name });
                return new List<string>();
            }
        }

        /// <summary>
        /// 启用或禁用ASR服务
        /// </summary>
        public async Task SetEnabledAsync(bool enabled)
        {
            if (_asrConfig.IsEnabled != enabled)
            {
                _asrConfig.IsEnabled = enabled;
                await _configurationManager.SaveConfigurationAsync(_asrConfig);

                // 发布启用状态变更事�?
                await _eventBus.PublishAsync(new ASREnabledChangedEvent
                {
                    IsEnabled = enabled
                });

                Logger?.LogInformation("ASR enabled status changed", new { Enabled = enabled });
            }
        }

        private async Task<string> TranscribeWithQueueAsync(byte[] audioData)
        {
            var request = new ASRRequest
            {
                AudioData = audioData,
                CompletionSource = new TaskCompletionSource<string>()
            };

            await _queueSemaphore.WaitAsync();
            try
            {
                _requestQueue.Enqueue(request);
            }
            finally
            {
                _queueSemaphore.Release();
            }

            return await request.CompletionSource.Task;
        }

        private async Task ProcessRequestQueueAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    ASRRequest request = null;

                    await _queueSemaphore.WaitAsync(cancellationToken);
                    try
                    {
                        if (_requestQueue.Count > 0)
                        {
                            request = _requestQueue.Dequeue();
                        }
                    }
                    finally
                    {
                        _queueSemaphore.Release();
                    }

                    if (request != null && _currentASRCore != null)
                    {
                        try
                        {
                            var transcription = await _currentASRCore.TranscribeAsync(request.AudioData);
                            request.CompletionSource.SetResult(transcription ?? string.Empty);
                        }
                        catch (Exception ex)
                        {
                            request.CompletionSource.SetException(ex);
                        }
                    }
                    else
                    {
                        // 没有请求时等待一段时�?
                        await Task.Delay(100, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger?.LogError(ex, "Error processing ASR request queue");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        private async Task InitializeASRCoresAsync()
        {
            Logger?.LogInformation("Initializing ASR cores");

            try
            {
                // 使用CoreFactory创建ASR核心实例
                var cores = _coreFactory.CreateASRCores(_asrConfig);

                // 清空现有核心并添加新创建的核�?
                _asrCores.Clear();
                foreach (var kvp in cores)
                {
                    _asrCores[kvp.Key] = kvp.Value;
                }

                Logger?.LogInformation("ASR cores initialized", new { Count = _asrCores.Count });
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to initialize ASR cores");
                throw;
            }
        }

        private async Task SetCurrentASRCoreAsync(string providerName)
        {
            if (_asrCores.TryGetValue(providerName, out var asrCore))
            {
                _currentASRCore = asrCore;
                Logger?.LogInformation("Current ASR core set", new { Provider = providerName });
            }
            else
            {
                Logger?.LogWarning("ASR core not found", new { Provider = providerName });
                throw new ServiceException(ServiceName, $"ASR core '{providerName}' not found");
            }
        }

        private async void OnConfigurationChanged(object sender, ConfigurationChangedEventArgs e)
        {
            if (e.ConfigurationType == typeof(ASRConfiguration))
            {
                try
                {
                    Logger?.LogInformation("ASR configuration changed, reloading");

                    _asrConfig = (ASRConfiguration)e.NewConfiguration;

                    // 如果提供商发生变化，切换ASR核心
                    if (_currentASRCore?.Name != _asrConfig.Provider)
                    {
                        await SwitchProviderAsync(_asrConfig.Provider);
                    }

                    // 发布配置变更事件
                    await _eventBus.PublishAsync(new ASRConfigurationChangedEvent
                    {
                        OldConfiguration = (ASRConfiguration)e.OldConfiguration,
                        NewConfiguration = _asrConfig
                    });
                }
                catch (Exception ex)
                {
                    Logger?.LogError(ex, "Failed to handle ASR configuration change");
                }
            }
        }

        protected override void OnDispose()
        {
            {
                _processingCancellation?.Cancel();
                _processingCancellation?.Dispose();
                _queueSemaphore?.Dispose();
            }
            // Disposed by base class
        }
    }

    // ASR请求�?
    internal class ASRRequest
    {
        public byte[] AudioData { get; set; }
        public TaskCompletionSource<string> CompletionSource { get; set; }
    }

    // ASR服务相关事件
    public class ASRServiceStartedEvent
    {
        public string ServiceName { get; set; }
        public string CurrentProvider { get; set; }
        public bool IsEnabled { get; set; }
    }

    public class ASRServiceStoppedEvent
    {
        public string ServiceName { get; set; }
    }

    public class ASRRequestStartedEvent
    {
        public string Provider { get; set; }
        public int AudioSize { get; set; }
    }

    public class ASRRequestCompletedEvent
    {
        public string Provider { get; set; }
        public int AudioSize { get; set; }
        public string Transcription { get; set; }
        public int TranscriptionLength { get; set; }
    }

    public class ASRRequestFailedEvent
    {
        public string Provider { get; set; }
        public int AudioSize { get; set; }
        public string Error { get; set; }
    }

    public class ASRProviderSwitchedEvent
    {
        public string OldProvider { get; set; }
        public string NewProvider { get; set; }
    }

    public class ASREnabledChangedEvent
    {
        public bool IsEnabled { get; set; }
    }

    public class ASRConfigurationChangedEvent
    {
        public ASRConfiguration OldConfiguration { get; set; }
        public ASRConfiguration NewConfiguration { get; set; }
    }
}