using VPetLLM.Infrastructure.Configuration;
using VPetLLM.Infrastructure.Configuration.Configurations;
using VPetLLM.Infrastructure.Events;
using VPetLLM.Infrastructure.Exceptions;
using VPetLLM.Infrastructure.Logging;

namespace VPetLLM.Infrastructure.Services
{
    /// <summary>
    /// TTS服务 - 管理所有TTS核心的统一服务
    /// </summary>
    public class TTSService : ServiceBase
    {
        public override string ServiceName => "TTSService";

        private readonly InfraConfigManager _configurationManager;
        private new readonly IEventBus _eventBus;
        private readonly CoreFactory _coreFactory;
        private readonly Dictionary<string, TTSCoreBase> _ttsCores;
        private TTSCoreBase _currentTTSCore;
        private InfraTTSConfiguration _ttsConfig;
        private readonly Queue<TTSRequest> _requestQueue;
        private readonly SemaphoreSlim _queueSemaphore;
        private CancellationTokenSource _processingCancellation;
        private readonly EventHandler<InfraConfigChangedEventArgs> _configChangedHandler;

        public TTSService(
            InfraConfigManager configurationManager,
            IEventBus eventBus,
            IStructuredLogger logger,
            CoreFactory coreFactory = null) : base(logger)
        {
            _configurationManager = configurationManager ?? throw new ArgumentNullException(nameof(configurationManager));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _coreFactory = coreFactory ?? new CoreFactory(logger);
            _ttsCores = new Dictionary<string, TTSCoreBase>();
            _requestQueue = new Queue<TTSRequest>();
            _queueSemaphore = new SemaphoreSlim(1, 1);
            _configChangedHandler = OnConfigurationChanged;
        }

        protected override async Task OnInitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                Logger?.LogInformation("Initializing TTSService");

                // 加载配置
                _ttsConfig = _configurationManager.GetConfiguration<InfraTTSConfiguration>();

                // 订阅配置变更事件
                _configurationManager.ConfigurationChanged += _configChangedHandler;

                // 初始化TTS核心
                await InitializeTTSCoresAsync();

                // 设置当前TTS核心
                await SetCurrentTTSCoreAsync(_ttsConfig.Provider);

                // 启动请求处理
                _processingCancellation = new CancellationTokenSource();
                _ = Task.Run(() => ProcessRequestQueueAsync(_processingCancellation.Token));

                Logger?.LogInformation("TTSService initialized successfully");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to initialize TTSService");
                throw new ServiceException(ServiceName, "Failed to initialize TTSService", ex);
            }
        }

        protected override async Task OnStartAsync(CancellationToken cancellationToken)
        {
            try
            {
                Logger?.LogInformation("Starting TTSService");

                // 发布服务启动事件
                await _eventBus.PublishAsync(new TTSServiceStartedEvent
                {
                    ServiceName = ServiceName,
                    CurrentProvider = _ttsConfig.Provider,
                    IsEnabled = _ttsConfig.IsEnabled
                });

                Logger?.LogInformation("TTSService started successfully");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to start TTSService");
                throw new ServiceException(ServiceName, "Failed to start TTSService", ex);
            }
        }

        protected override async Task OnStopAsync(CancellationToken cancellationToken)
        {
            try
            {
                Logger?.LogInformation("Stopping TTSService");

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
                _configurationManager.ConfigurationChanged -= _configChangedHandler;

                // 发布服务停止事件
                await _eventBus.PublishAsync(new TTSServiceStoppedEvent
                {
                    ServiceName = ServiceName
                });

                Logger?.LogInformation("TTSService stopped successfully");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error stopping TTSService");
                throw new ServiceException(ServiceName, "Error stopping TTSService", ex);
            }
        }

        public override async Task<ServiceHealthStatus> CheckHealthAsync()
        {
            try
            {
                var metrics = new Dictionary<string, object>
                {
                    ["IsEnabled"] = _ttsConfig?.IsEnabled ?? false,
                    ["CurrentProvider"] = _ttsConfig?.Provider ?? "Unknown",
                    ["AvailableCores"] = _ttsCores.Count,
                    ["CurrentCoreActive"] = _currentTTSCore is not null,
                    ["QueueLength"] = _requestQueue.Count
                };

                if (_currentTTSCore is not null)
                {
                    metrics["CurrentCoreName"] = _currentTTSCore.Name;
                    metrics["AudioFormat"] = _currentTTSCore.GetAudioFormat();
                }

                var status = HealthStatus.Healthy;
                var description = "Service is healthy";

                if (!(_ttsConfig?.IsEnabled ?? false))
                {
                    status = HealthStatus.Degraded;
                    description = "TTS is disabled";
                }
                else if (_currentTTSCore is null)
                {
                    status = HealthStatus.Unhealthy;
                    description = "No active TTS core";
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
                Logger?.LogError(ex, "Health check failed for TTSService");
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
        /// 生成语音音频
        /// </summary>
        public async Task<byte[]> GenerateAudioAsync(string text)
        {
            if (!(_ttsConfig?.IsEnabled ?? false))
            {
                throw new ServiceException(ServiceName, "TTS service is disabled");
            }

            if (_currentTTSCore is null)
            {
                throw new ServiceException(ServiceName, "No active TTS core available");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("Text cannot be null or empty", nameof(text));
            }

            try
            {
                // 发布TTS开始事�?
                await _eventBus.PublishAsync(new TTSRequestStartedEvent
                {
                    Provider = _currentTTSCore.Name,
                    Text = text,
                    TextLength = text.Length
                });

                byte[] audioData;

                if (_ttsConfig.UseQueueDownload)
                {
                    // 使用队列模式
                    audioData = await GenerateAudioWithQueueAsync(text);
                }
                else
                {
                    // 直接生成
                    audioData = await _currentTTSCore.GenerateAudioAsync(text);
                }

                // 发布TTS完成事件
                await _eventBus.PublishAsync(new TTSRequestCompletedEvent
                {
                    Provider = _currentTTSCore.Name,
                    Text = text,
                    TextLength = text.Length,
                    AudioSize = audioData.Length,
                    AudioFormat = _currentTTSCore.GetAudioFormat()
                });

                return audioData;
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "TTS request failed", new { Provider = _currentTTSCore.Name, Text = text });

                // 发布TTS错误事件
                await _eventBus.PublishAsync(new TTSRequestFailedEvent
                {
                    Provider = _currentTTSCore.Name,
                    Text = text,
                    Error = ex.Message
                });

                throw new ServiceException(ServiceName, $"TTS request failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 切换TTS提供�?
        /// </summary>
        public async Task SwitchProviderAsync(string providerName)
        {
            try
            {
                Logger?.LogInformation("Switching TTS provider", new { From = _currentTTSCore?.Name, To = providerName });

                await SetCurrentTTSCoreAsync(providerName);

                // 发布提供商切换事�?
                await _eventBus.PublishAsync(new TTSProviderSwitchedEvent
                {
                    OldProvider = _currentTTSCore?.Name,
                    NewProvider = providerName
                });

                Logger?.LogInformation("TTS provider switched successfully", new { NewProvider = providerName });
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to switch TTS provider", new { TargetProvider = providerName });
                throw new ServiceException(ServiceName, $"Failed to switch to provider {providerName}", ex);
            }
        }

        /// <summary>
        /// 获取当前TTS核心
        /// </summary>
        public TTSCoreBase GetCurrentTTSCore()
        {
            return _currentTTSCore;
        }

        /// <summary>
        /// 获取可用的TTS提供商列�?
        /// </summary>
        public List<string> GetAvailableProviders()
        {
            return new List<string>(_ttsCores.Keys);
        }

        /// <summary>
        /// 启用或禁用TTS服务
        /// </summary>
        public async Task SetEnabledAsync(bool enabled)
        {
            if (_ttsConfig.IsEnabled != enabled)
            {
                _ttsConfig.IsEnabled = enabled;
                await _configurationManager.SaveConfigurationAsync(_ttsConfig);

                // 发布启用状态变更事�?
                await _eventBus.PublishAsync(new TTSEnabledChangedEvent
                {
                    IsEnabled = enabled
                });

                Logger?.LogInformation("TTS enabled status changed", new { Enabled = enabled });
            }
        }

        private async Task<byte[]> GenerateAudioWithQueueAsync(string text)
        {
            var request = new TTSRequest
            {
                Text = text,
                CompletionSource = new TaskCompletionSource<byte[]>()
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
                    TTSRequest request = null;

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

                    if (request is not null && _currentTTSCore is not null)
                    {
                        try
                        {
                            var audioData = await _currentTTSCore.GenerateAudioAsync(request.Text);
                            request.CompletionSource.SetResult(audioData);
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
                    Logger?.LogError(ex, "Error processing TTS request queue");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        private async Task InitializeTTSCoresAsync()
        {
            Logger?.LogInformation("Initializing TTS cores");

            try
            {
                // 使用CoreFactory创建TTS核心实例
                var cores = _coreFactory.CreateTTSCores(_ttsConfig);

                // 清空现有核心并添加新创建的核�?
                _ttsCores.Clear();
                foreach (var kvp in cores)
                {
                    _ttsCores[kvp.Key] = kvp.Value;
                }

                Logger?.LogInformation("TTS cores initialized", new { Count = _ttsCores.Count });
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to initialize TTS cores");
                throw;
            }
        }

        private async Task SetCurrentTTSCoreAsync(string providerName)
        {
            if (_ttsCores.TryGetValue(providerName, out var ttsCore))
            {
                _currentTTSCore = ttsCore;
                Logger?.LogInformation("Current TTS core set", new { Provider = providerName });
            }
            else
            {
                Logger?.LogWarning("TTS core not found", new { Provider = providerName });
                throw new ServiceException(ServiceName, $"TTS core '{providerName}' not found");
            }
        }

        private async void OnConfigurationChanged(object sender, InfraConfigChangedEventArgs e)
        {
            if (e.ConfigurationType == typeof(InfraTTSConfiguration))
            {
                try
                {
                    Logger?.LogInformation("TTS configuration changed, reloading");

                    _ttsConfig = (InfraTTSConfiguration)e.NewConfiguration;

                    // 如果提供商发生变化，切换TTS核心
                    if (_currentTTSCore?.Name != _ttsConfig.Provider)
                    {
                        await SwitchProviderAsync(_ttsConfig.Provider);
                    }

                    // 发布配置变更事件
                    await _eventBus.PublishAsync(new InfraTTSConfigurationChangedEvent
                    {
                        OldConfiguration = (InfraTTSConfiguration)e.OldConfiguration,
                        NewConfiguration = _ttsConfig
                    });
                }
                catch (Exception ex)
                {
                    Logger?.LogError(ex, "Failed to handle TTS configuration change");
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

    // TTS请求�?
    internal class TTSRequest
    {
        public string Text { get; set; }
        public TaskCompletionSource<byte[]> CompletionSource { get; set; }
    }

    // TTS服务相关事件
    public class TTSServiceStartedEvent
    {
        public string ServiceName { get; set; }
        public string CurrentProvider { get; set; }
        public bool IsEnabled { get; set; }
    }

    public class TTSServiceStoppedEvent
    {
        public string ServiceName { get; set; }
    }

    public class TTSRequestStartedEvent
    {
        public string Provider { get; set; }
        public string Text { get; set; }
        public int TextLength { get; set; }
    }

    public class TTSRequestCompletedEvent
    {
        public string Provider { get; set; }
        public string Text { get; set; }
        public int TextLength { get; set; }
        public int AudioSize { get; set; }
        public string AudioFormat { get; set; }
    }

    public class TTSRequestFailedEvent
    {
        public string Provider { get; set; }
        public string Text { get; set; }
        public string Error { get; set; }
    }

    public class TTSProviderSwitchedEvent
    {
        public string OldProvider { get; set; }
        public string NewProvider { get; set; }
    }

    public class TTSEnabledChangedEvent
    {
        public bool IsEnabled { get; set; }
    }

    public class InfraTTSConfigurationChangedEvent
    {
        public InfraTTSConfiguration OldConfiguration { get; set; }
        public InfraTTSConfiguration NewConfiguration { get; set; }
    }
}