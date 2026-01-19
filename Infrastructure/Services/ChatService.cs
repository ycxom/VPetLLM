using VPetLLM.Infrastructure.Configuration;
using VPetLLM.Infrastructure.Configuration.Configurations;
using VPetLLM.Infrastructure.Events;
using VPetLLM.Infrastructure.Exceptions;
using VPetLLM.Infrastructure.Logging;

namespace VPetLLM.Infrastructure.Services
{
    /// <summary>
    /// 聊天服务 - 管理所有聊天核心的统一服务
    /// </summary>
    public class ChatService : ServiceBase
    {
        public override string ServiceName => "ChatService";

        private readonly IConfigurationManager _configurationManager;
        private new readonly IEventBus _eventBus;
        private readonly CoreFactory _coreFactory;
        private readonly Dictionary<string, IChatCore> _chatCores;
        private IChatCore _currentChatCore;
        private LLMConfiguration _llmConfig;

        public ChatService(
            IConfigurationManager configurationManager,
            IEventBus eventBus,
            IStructuredLogger logger,
            CoreFactory coreFactory = null) : base(logger)
        {
            _configurationManager = configurationManager ?? throw new ArgumentNullException(nameof(configurationManager));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _coreFactory = coreFactory ?? new CoreFactory(logger);
            _chatCores = new Dictionary<string, IChatCore>();
        }

        protected override async Task OnInitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                Logger?.LogInformation("Initializing ChatService");

                // 加载配置
                _llmConfig = _configurationManager.GetConfiguration<LLMConfiguration>();

                // 订阅配置变更事件
                _configurationManager.ConfigurationChanged += OnConfigurationChanged;

                // 初始化聊天核�?
                await InitializeChatCoresAsync();

                // 设置当前聊天核心
                await SetCurrentChatCoreAsync(_llmConfig.Provider.ToString());

                Logger?.LogInformation("ChatService initialized successfully");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to initialize ChatService");
                throw new ServiceException(ServiceName, "Failed to initialize ChatService", ex);
            }
        }

        protected override async Task OnStartAsync(CancellationToken cancellationToken)
        {
            try
            {
                Logger?.LogInformation("Starting ChatService");

                // 启动当前聊天核心
                if (_currentChatCore != null)
                {
                    // 加载历史记录
                    _currentChatCore.LoadHistory();

                    // 发布服务启动事件
                    await _eventBus.PublishAsync(new ChatServiceStartedEvent
                    {
                        ServiceName = ServiceName,
                        CurrentProvider = _llmConfig.Provider.ToString()
                    });
                }

                Logger?.LogInformation("ChatService started successfully");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to start ChatService");
                throw new ServiceException(ServiceName, "Failed to start ChatService", ex);
            }
        }

        protected override async Task OnStopAsync(CancellationToken cancellationToken)
        {
            try
            {
                Logger?.LogInformation("Stopping ChatService");

                // 保存当前聊天历史
                if (_currentChatCore != null)
                {
                    _currentChatCore.SaveHistory();
                }

                // 取消订阅配置变更事件
                _configurationManager.ConfigurationChanged -= OnConfigurationChanged;

                // 发布服务停止事件
                await _eventBus.PublishAsync(new ChatServiceStoppedEvent
                {
                    ServiceName = ServiceName
                });

                Logger?.LogInformation("ChatService stopped successfully");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error stopping ChatService");
                throw new ServiceException(ServiceName, "Error stopping ChatService", ex);
            }
        }

        public override async Task<ServiceHealthStatus> CheckHealthAsync()
        {
            try
            {
                var metrics = new Dictionary<string, object>
                {
                    ["CurrentProvider"] = _llmConfig?.Provider.ToString() ?? "Unknown",
                    ["AvailableCores"] = _chatCores.Count,
                    ["CurrentCoreActive"] = _currentChatCore != null
                };

                if (_currentChatCore != null)
                {
                    metrics["CurrentCoreName"] = _currentChatCore.Name;
                    metrics["HistoryCount"] = _currentChatCore.GetChatHistory()?.Count ?? 0;
                    metrics["TokenCount"] = _currentChatCore.GetCurrentTokenCount();
                }

                return new ServiceHealthStatus
                {
                    ServiceName = ServiceName,
                    Status = _currentChatCore != null ? HealthStatus.Healthy : HealthStatus.Degraded,
                    Description = _currentChatCore != null ? "Service is healthy" : "No active chat core",
                    Metrics = metrics,
                    CheckTime = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Health check failed for ChatService");
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
        /// 发送聊天消�?
        /// </summary>
        public async Task<string> ChatAsync(string prompt, bool isFunctionCall = false)
        {
            if (_currentChatCore == null)
            {
                throw new ServiceException(ServiceName, "No active chat core available");
            }

            try
            {
                // 发布聊天开始事�?
                await _eventBus.PublishAsync(new ChatRequestStartedEvent
                {
                    Provider = _currentChatCore.Name,
                    Prompt = prompt,
                    IsFunctionCall = isFunctionCall
                });

                var response = await _currentChatCore.Chat(prompt, isFunctionCall);

                // 发布聊天完成事件
                await _eventBus.PublishAsync(new ChatRequestCompletedEvent
                {
                    Provider = _currentChatCore.Name,
                    Prompt = prompt,
                    Response = response,
                    IsFunctionCall = isFunctionCall
                });

                return response;
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Chat request failed", new { Provider = _currentChatCore.Name, Prompt = prompt });

                // 发布聊天错误事件
                await _eventBus.PublishAsync(new ChatRequestFailedEvent
                {
                    Provider = _currentChatCore.Name,
                    Prompt = prompt,
                    Error = ex.Message,
                    IsFunctionCall = isFunctionCall
                });

                throw new ServiceException(ServiceName, $"Chat request failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 发送带图像的多模态消�?
        /// </summary>
        public async Task<string> ChatWithImageAsync(string prompt, byte[] imageData)
        {
            if (_currentChatCore == null)
            {
                throw new ServiceException(ServiceName, "No active chat core available");
            }

            try
            {
                // 发布多模态聊天开始事�?
                await _eventBus.PublishAsync(new MultimodalChatRequestStartedEvent
                {
                    Provider = _currentChatCore.Name,
                    Prompt = prompt,
                    ImageSize = imageData.Length
                });

                var response = await _currentChatCore.ChatWithImage(prompt, imageData);

                // 发布多模态聊天完成事�?
                await _eventBus.PublishAsync(new MultimodalChatRequestCompletedEvent
                {
                    Provider = _currentChatCore.Name,
                    Prompt = prompt,
                    Response = response,
                    ImageSize = imageData.Length
                });

                return response;
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Multimodal chat request failed", new { Provider = _currentChatCore.Name, Prompt = prompt });

                // 发布多模态聊天错误事�?
                await _eventBus.PublishAsync(new MultimodalChatRequestFailedEvent
                {
                    Provider = _currentChatCore.Name,
                    Prompt = prompt,
                    Error = ex.Message,
                    ImageSize = imageData.Length
                });

                throw new ServiceException(ServiceName, $"Multimodal chat request failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 切换聊天提供�?
        /// </summary>
        public async Task SwitchProviderAsync(string providerName)
        {
            try
            {
                Logger?.LogInformation("Switching chat provider", new { From = _currentChatCore?.Name, To = providerName });

                // 保存当前历史
                var currentHistory = _currentChatCore?.GetChatHistory();

                // 切换到新的聊天核�?
                await SetCurrentChatCoreAsync(providerName);

                // 如果需要，恢复历史记录
                if (currentHistory != null && !_llmConfig.SeparateChatByProvider)
                {
                    _currentChatCore?.SetChatHistory(currentHistory);
                }

                // 发布提供商切换事�?
                await _eventBus.PublishAsync(new ChatProviderSwitchedEvent
                {
                    OldProvider = _currentChatCore?.Name,
                    NewProvider = providerName,
                    HistoryPreserved = currentHistory != null && !_llmConfig.SeparateChatByProvider
                });

                Logger?.LogInformation("Chat provider switched successfully", new { NewProvider = providerName });
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to switch chat provider", new { TargetProvider = providerName });
                throw new ServiceException(ServiceName, $"Failed to switch to provider {providerName}", ex);
            }
        }

        /// <summary>
        /// 获取当前聊天核心
        /// </summary>
        public IChatCore GetCurrentChatCore()
        {
            return _currentChatCore;
        }

        /// <summary>
        /// 获取可用的聊天提供商列表
        /// </summary>
        public List<string> GetAvailableProviders()
        {
            return new List<string>(_chatCores.Keys);
        }

        /// <summary>
        /// 清除聊天上下�?
        /// </summary>
        public async Task ClearContextAsync()
        {
            if (_currentChatCore != null)
            {
                _currentChatCore.ClearContext();

                // 发布上下文清除事�?
                await _eventBus.PublishAsync(new ChatContextClearedEvent
                {
                    Provider = _currentChatCore.Name
                });
            }
        }

        private async Task InitializeChatCoresAsync()
        {
            Logger?.LogInformation("Initializing chat cores");

            try
            {
                // 使用CoreFactory创建聊天核心实例
                var cores = _coreFactory.CreateChatCores(_llmConfig);

                // 清空现有核心并添加新创建的核�?
                _chatCores.Clear();
                foreach (var kvp in cores)
                {
                    _chatCores[kvp.Key] = kvp.Value;
                }

                Logger?.LogInformation("Chat cores initialized", new { Count = _chatCores.Count });
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to initialize chat cores");
                throw;
            }
        }

        private async Task SetCurrentChatCoreAsync(string providerName)
        {
            if (_chatCores.TryGetValue(providerName, out var chatCore))
            {
                _currentChatCore = chatCore;
                Logger?.LogInformation("Current chat core set", new { Provider = providerName });
            }
            else
            {
                Logger?.LogWarning("Chat core not found", new { Provider = providerName });
                throw new ServiceException(ServiceName, $"Chat core '{providerName}' not found");
            }
        }

        private async void OnConfigurationChanged(object sender, ConfigurationChangedEventArgs e)
        {
            if (e.ConfigurationType == typeof(LLMConfiguration))
            {
                try
                {
                    Logger?.LogInformation("LLM configuration changed, reloading");

                    _llmConfig = (LLMConfiguration)e.NewConfiguration;

                    // 如果提供商发生变化，切换聊天核心
                    if (_currentChatCore?.Name != _llmConfig.Provider.ToString())
                    {
                        await SwitchProviderAsync(_llmConfig.Provider.ToString());
                    }

                    // 发布配置变更事件
                    await _eventBus.PublishAsync(new ChatConfigurationChangedEvent
                    {
                        OldConfiguration = (LLMConfiguration)e.OldConfiguration,
                        NewConfiguration = _llmConfig
                    });
                }
                catch (Exception ex)
                {
                    Logger?.LogError(ex, "Failed to handle LLM configuration change");
                }
            }
        }
    }

    // 聊天服务相关事件
    public class ChatServiceStartedEvent
    {
        public string ServiceName { get; set; }
        public string CurrentProvider { get; set; }
    }

    public class ChatServiceStoppedEvent
    {
        public string ServiceName { get; set; }
    }

    public class ChatRequestStartedEvent
    {
        public string Provider { get; set; }
        public string Prompt { get; set; }
        public bool IsFunctionCall { get; set; }
    }

    public class ChatRequestCompletedEvent
    {
        public string Provider { get; set; }
        public string Prompt { get; set; }
        public string Response { get; set; }
        public bool IsFunctionCall { get; set; }
    }

    public class ChatRequestFailedEvent
    {
        public string Provider { get; set; }
        public string Prompt { get; set; }
        public string Error { get; set; }
        public bool IsFunctionCall { get; set; }
    }

    public class MultimodalChatRequestStartedEvent
    {
        public string Provider { get; set; }
        public string Prompt { get; set; }
        public int ImageSize { get; set; }
    }

    public class MultimodalChatRequestCompletedEvent
    {
        public string Provider { get; set; }
        public string Prompt { get; set; }
        public string Response { get; set; }
        public int ImageSize { get; set; }
    }

    public class MultimodalChatRequestFailedEvent
    {
        public string Provider { get; set; }
        public string Prompt { get; set; }
        public string Error { get; set; }
        public int ImageSize { get; set; }
    }

    public class ChatProviderSwitchedEvent
    {
        public string OldProvider { get; set; }
        public string NewProvider { get; set; }
        public bool HistoryPreserved { get; set; }
    }

    public class ChatContextClearedEvent
    {
        public string Provider { get; set; }
    }

    public class ChatConfigurationChangedEvent
    {
        public LLMConfiguration OldConfiguration { get; set; }
        public LLMConfiguration NewConfiguration { get; set; }
    }
}