using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers.Animation;
using VPetLLM.Infrastructure.Services.ApplicationServices;
using VPetLLM.Services;
using VPetLLM.UI.Windows;
using VPetLLM.Utils.Audio;
using VPetLLM.Utils.Configuration;
using VPetLLM.Utils.Data;
using VPetLLM.Utils.Localization;
using VPetLLM.Utils.Plugin;
using InfraConfigManagerImpl = VPetLLM.Infrastructure.Configuration.ConfigurationManager;
using SettingClass = VPetLLM.Setting;
using TTSServiceType = VPetLLM.Utils.Audio.TTSService;
using UnifiedTTSDispatcher = VPetLLM.Core.Integration.UnifiedTTS.Interfaces.ITTSDispatcher;

namespace VPetLLM
{
    /// <summary>
    /// VPetLLM 主插件类 - 重构版本（完整）
    /// 职责：核心协调和生命周期管理，具体功能委托给服务层
    /// </summary>
    public partial class VPetLLM : MainPlugin
    {
        #region Static Instance

        public static VPetLLM? Instance { get; private set; }

        #endregion

        #region Core Infrastructure

        private readonly IDependencyContainer _container;
        private readonly IServiceManager _serviceManager;
        private readonly InfraConfigManager _configurationManager;
        private readonly IEventBus _eventBus;
        private readonly IStructuredLogger _logger;

        #endregion

        #region Legacy Compatibility Properties

        /// <summary>
        /// 设置对象（向后兼容）
        /// </summary>
        public Setting Settings;

        /// <summary>
        /// 聊天核心（向后兼容）
        /// </summary>
        public IChatCore? ChatCore;

        /// <summary>
        /// 对话框（向后兼容）
        /// </summary>
        public UI.Windows.TalkBox? TalkBox;

        /// <summary>
        /// 设置窗口（向后兼容）
        /// </summary>
        public winSettingNew? SettingWindow;

        /// <summary>
        /// 动作处理器
        /// </summary>
        public ActionProcessor? ActionProcessor;

        /// <summary>
        /// 触摸交互处理器
        /// </summary>
        public TouchInteractionHandler? TouchInteractionHandler;

        /// <summary>
        /// TTS 服务（旧版）
        /// </summary>
        public TTSServiceType? TTSService;

        private volatile bool _isTTSServiceUnavailable;

        public bool IsTTSServiceUnavailable => _isTTSServiceUnavailable;

        public event EventHandler<bool>? TTSServiceAvailabilityChanged;

        private void OnTTSServiceAvailabilityChanged(object? sender, bool isUnavailable)
        {
            _isTTSServiceUnavailable = isUnavailable;
            TTSServiceAvailabilityChanged?.Invoke(this, isUnavailable);
        }

        /// <summary>
        /// 插件列表
        /// </summary>
        public List<IVPetLLMPlugin> Plugins => PluginManager.Plugins;

        /// <summary>
        /// 失败的插件列表
        /// </summary>
        public List<FailedPlugin> FailedPlugins => PluginManager.FailedPlugins;

        /// <summary>
        /// 插件路径
        /// </summary>
        public string PluginPath => PluginManager.PluginPath;

        /// <summary>
        /// 悬浮侧边栏管理器
        /// </summary>
        public FloatingSidebarManager? FloatingSidebarManager => _floatingSidebarManager;

        /// <summary>
        /// 处理生命周期管理器
        /// </summary>
        public Services.ProcessingLifecycleManager? ProcessingLifecycleManager => _processingLifecycleManager;

        /// <summary>
        /// VPet TTS 插件是否被检测到（带 TTL 缓存的准实时检测）
        /// </summary>
        public bool IsVPetTTSPluginDetected
        {
            get
            {
                // 走 TTL 缓存：本属性在消息处理管线中每条回复被读取数十次，
                // 不能每次都全量扫插件列表+反射；缓存过期后自动重新检测
                try
                {
                    var result = TTSPluginDetector.DetectAllOtherTTSPluginsWithCache(MW, TTS_DETECT_CACHE_BATCH);
                    var hasEnabledPlugin = result.HasOtherEnabledTTSPlugin;
                    
                    // 如果检测结果与缓存不同，更新缓存并记录日志
                    if (hasEnabledPlugin != _vpetTTSPluginDetected)
                    {
                        _vpetTTSPluginDetected = hasEnabledPlugin;
                        Logger.Log($"VPetTTS插件状态变化: {(hasEnabledPlugin ? "已启用" : "已禁用")} ({result.EnabledPluginNames})");
                    }
                    
                    return hasEnabledPlugin;
                }
                catch
                {
                    return _vpetTTSPluginDetected; // 出错时返回缓存值
                }
            }
        }

        /// <summary>
        /// VPetTTS 协调器（用于独占会话管理）
        /// </summary>
        public VPetTTSCoordinator? VPetTTSCoordinator { get; private set; }

        /// <summary>
        /// LLM 调用入口点（供插件和外部应用使用）
        /// </summary>
        public Core.LLMEntryPoint? LLMEntry { get; private set; }

        #endregion

        #region Private Fields

        private System.Timers.Timer _syncTimer;
        private System.Timers.Timer _freeConfigTimer;
        private IntelligentConfigurationOptimizer? _configurationOptimizer;
        private Infrastructure.Services.ApplicationServices.VoiceInputService? _voiceInputService;
        private Services.IScreenshotService? _screenshotService;
        private Infrastructure.Services.ApplicationServices.PurchaseService? _purchaseService;
        private Infrastructure.Services.ApplicationServices.MediaPlaybackService? _mediaPlaybackService;
        private DefaultPluginChecker? _defaultPluginChecker;
        private FloatingSidebarManager? _floatingSidebarManager;
        private Services.ProcessingLifecycleManager? _processingLifecycleManager;
        private bool _vpetTTSPluginDetected = false;
        private byte[]? _pendingImageData;

        // 用于追踪最近的 TakeItemHandle 事件
        private DateTime _lastTakeItemHandleTime = DateTime.MinValue;
        private readonly object _takeItemLock = new object();
        private const int TAKE_ITEM_WINDOW_MS = 100;

        // 已注册 Hook 的物品类型列表
        private readonly string[] _hookedItemTypes = { "Food", "Toy", "Tool", "Mail", "Item" };

        // TTS 插件探测的缓存批次键：热路径（消息管线每条回复读取数十次）走
        // TTSPluginDetector 的 TTL 缓存（默认 5 秒），插件开关切换最多延迟 5 秒被感知
        private const string TTS_DETECT_CACHE_BATCH = "realtime";

        private int _consecutiveAIFailureCount = 0;
        private readonly object _failureCountLock = new object();
        private const int MAX_CONSECUTIVE_FAILURES = 5;

        // 启动代理自动优化：每次进程仅运行一次
        private bool _startupProxyOptimizationRan = false;
        private bool _startupVersionCheckRan = false;
        private Task<VersionCheckResult>? _versionCheckTask;

        /// <summary>
        /// 更新提示的停留时长。VPet 自己的照片解锁提示用 5 秒，这里要读版本号，略放宽。
        /// </summary>
        private const int UpdateNoticeDurationMs = 6000;

        public int ConsecutiveAIFailureCount
        {
            get { lock (_failureCountLock) { return _consecutiveAIFailureCount; } }
        }

        #endregion

        #region Constructor

        public VPetLLM(IMainWindow mainwin) : base(mainwin)
        {
            Instance = this;

            // 初始化日志

            // **优先初始化 SQLite，避免后续数据库操作失败**
            if (!SQLiteHelper.Initialize())
            {
                Logger.Log($"WARNING: SQLite initialization failed: {SQLiteHelper.GetErrorMessage()}");
                Logger.Log("Database features may not work properly. Please check:");
                Logger.Log("1. Visual C++ Redistributable is installed");
                Logger.Log("2. e_sqlite3.dll exists in runtimes folder");
                Logger.Log("3. System architecture matches (x64/x86)");
            }

            // 加载设置 - 传递 PrefixSave 作为 instanceId
            var instanceId = mainwin?.PrefixSave ?? "";
            Settings = new Setting(ExtensionValue.BaseDirectory, instanceId);

            // 初始化语言和提示词
            InitializeLanguageAndPrompts();

            // 创建核心基础设施
            _container = new DependencyContainer();
            _eventBus = new EventBus();
            _logger = new StructuredLogger();
            _configurationManager = new InfraConfigManagerImpl(ExtensionValue.BaseDirectory, _logger);
            _serviceManager = new ServiceManager(_container, _eventBus, _logger);

            // 注册核心组件
            RegisterCoreComponents();

            // 初始化配置
            InitializeConfigurations();

            // 初始化 ActionProcessor
            InitializeActionProcessor();

            // 初始化 Free 服务配置
            InitializeFreeServices();

            // 初始化 ChatCore
            InitializeChatCore();

            // 初始化名称同步定时器
            InitializeSyncTimer();

            // 初始化 TTS 服务（旧版）
            InitializeLegacyTTSService();

            // 初始化配置优化器
            InitializeConfigurationOptimizer();

            // 初始化应用服务
            InitializeApplicationServices();

            // 注册服务到 DI 容器
            RegisterServices();

            // 加载插件
            LoadPlugins();

            // 初始化默认插件检查器
            _defaultPluginChecker = new DefaultPluginChecker(this);

            // 初始化 LLM 入口点
            InitializeLLMEntry();

            Logger.Log("VPetLLM plugin constructor finished.");
        }

        #endregion

        #region Initialization

        private void InitializeLanguageAndPrompts()
        {
            var dllPath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var langPath = Path.Combine(dllPath, "VPetLLM_lang", "Language.json");

            LanguageHelper.LoadLanguages(langPath);
            PromptHelper.LoadPrompts(langPath);

            // 设置默认语言
            if (string.IsNullOrEmpty(Settings.Language))
            {
                var culture = System.Globalization.CultureInfo.CurrentUICulture.Name.ToLower();
                Settings.Language = LanguageHelper.LanguageDisplayMap.ContainsKey(culture) ? culture : "en";
            }

            // 把语言同步给绑定层。此前 ChangeLanguage 只在设置窗口里调，
            // 用户没打开过设置时 LocalizationService.LangCode 一直停在默认的 zh-hans ——
            // 悬浮侧边栏那些走 LocalizationService 的文案对非中文用户就全是中文。
            Utils.Localization.LocalizationService.Instance.ChangeLanguage(Settings.Language);
        }

        private void InitializeActionProcessor()
        {
            try
            {
                ActionProcessor = new ActionProcessor(MW);
                ActionProcessor.SetSettings(Settings);
                _logger.LogInformation("ActionProcessor initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to initialize ActionProcessor", ex);
            }
        }

        private void InitializeFreeServices()
        {
            try
            {
                Logger.Log("开始初始化Free配置...");
                // 清理未加密的配置文件
                FreeConfigCleaner.CleanUnencryptedConfigs();
                // 同步等待配置初始化完成
                var configTask = FreeConfigManager.InitializeConfigsAsync();
                if (!configTask.Wait(TimeSpan.FromSeconds(8)))
                {
                    Logger.Log("Free配置初始化超时，后台继续拉取");
                    _ = configTask.ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            Logger.Log($"Free配置后台初始化失败: {t.Exception?.GetBaseException().Message}");
                        else
                            Logger.Log($"Free配置后台初始化完成: {t.Result}");
                    }, TaskScheduler.Default);
                }
                else
                {
                    Logger.Log($"Free配置初始化完成: {configTask.Result}");
                }

                // 初始化 Free ASR/TTS 认证委托
                InitializeFreeAuthProviders();

                // 启动Free配置自动检测更新定时器（每5分钟检查一次）
                InitializeFreeConfigTimer();
            }
            catch (Exception ex)
            {
                Logger.Log($"初始化Free配置失败: {ex.Message}");
            }
        }

        private void InitializeFreeConfigTimer()
        {
            try
            {
                _freeConfigTimer = new System.Timers.Timer(5 * 60 * 1000); // 5 分钟
                _freeConfigTimer.Elapsed += async (s, e) => await CheckFreeConfigUpdateAsync();
                _freeConfigTimer.AutoReset = true;
                _freeConfigTimer.Enabled = true;
                _logger.LogInformation("Free config auto-update timer initialized (5 min interval)");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to initialize Free config timer", ex);
            }
        }

        /// <summary>
        /// 立即检查Free配置更新（供定时器和设置UI打开时调用）
        /// </summary>
        public async Task CheckFreeConfigUpdateAsync()
        {
            try
            {
                var result = await FreeConfigManager.InitializeConfigsAsync();
                Logger.Log($"Free配置更新检查完成: {result}");

                // 工具调用策略要能热生效：配置每 5 分钟刷一次，但 FreeChatCore 不会重建
                // （LoadConfig 只在构造时跑一次），不补这一下的话云端改了也得等用户重启。
                // ApplyCloudConfig 只在策略真的变了时才作废探测结论，所以定时刷新不会
                // 反复清空判定。
                try
                {
                    var chatConfig = FreeConfigManager.GetChatConfig();
                    if (chatConfig is not null)
                    {
                        Core.Tools.FreeToolCapability.ApplyCloudConfig(chatConfig["EnableToolCall"]);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Free工具调用策略读取失败: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Free配置更新检查失败: {ex.Message}");
            }
        }

        private void InitializeFreeAuthProviders()
        {
            try
            {
                // 设置获取 SteamID 的委托
                Func<ulong> getSteamId = () =>
                {
                    try { return MW?.SteamID ?? 0; } catch { return 0; }
                };

                // 设置获取 AuthKey 的委托
                Func<Task<int>> getAuthKey = async () =>
                {
                    try { return MW is not null ? await MW.GenerateAuthKey() : 0; } catch { return 0; }
                };

                // 设置获取 ModId 的委托（从 VPet MOD 系统动态获取）
                Func<string> getModId = () =>
                {
                    try
                    {
                        var dllPath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                        if (string.IsNullOrEmpty(dllPath)) return "";

                        foreach (var mod in MW.OnModInfo)
                        {
                            if (mod.Path is not null && dllPath.StartsWith(mod.Path.FullName, StringComparison.OrdinalIgnoreCase))
                            {
                                if (mod.ItemID > 0)
                                    return mod.ItemID.ToString();
                            }
                        }
                        return "";
                    }
                    catch { return ""; }
                };

                // 初始化共享签名助手（只需调用一次）
                RequestSignatureHelper.Init(getSteamId, getAuthKey, getModId);

                Logger.Log("Free ASR/TTS 认证委托初始化完成");
            }
            catch (Exception ex)
            {
                Logger.Log($"初始化 Free ASR/TTS 认证委托失败: {ex.Message}");
            }
        }

        private void InitializeChatCore()
        {
            try
            {
                // 检查 SQLite 是否成功加载
                if (!SQLiteHelper.IsLoaded())
                {
                    Logger.Log($"WARNING: Initializing ChatCore without SQLite support: {SQLiteHelper.GetErrorMessage()}");
                    Logger.Log("Chat history and database features will be limited");
                }

                switch (Settings.Provider)
                {
                    case SettingClass.LLMType.Ollama:
                        var ollamaNode = Settings.Ollama.GetCurrentOllamaSetting();
                        if (ollamaNode != null)
                        {
                            ChatCore = new OllamaChatCore(ollamaNode, Settings, MW, ActionProcessor);
                            Logger.Log("Chat core set to Ollama.");
                        }
                        else
                        {
                            Logger.Log("WARNING: No enabled Ollama node found.");
                        }
                        break;
                    case SettingClass.LLMType.OpenAI:
                        ChatCore = new OpenAIChatCore(Settings.OpenAI, Settings, MW, ActionProcessor);
                        Logger.Log("Chat core set to OpenAI.");
                        break;
                    case SettingClass.LLMType.Gemini:
                        ChatCore = new GeminiChatCore(Settings.Gemini, Settings, MW, ActionProcessor);
                        Logger.Log("Chat core set to Gemini.");
                        break;
                    case SettingClass.LLMType.Free:
                        ChatCore = new FreeChatCore(Settings.Free, Settings, MW, ActionProcessor);
                        Logger.Log("Chat core set to Free.");
                        break;
                    case SettingClass.LLMType.LMStudio:
                        var lmStudioNode = Settings.LMStudio.GetCurrentLMStudioSetting();
                        if (lmStudioNode != null)
                        {
                            ChatCore = new LMStudioChatCore(lmStudioNode, Settings, MW, ActionProcessor);
                            Logger.Log("Chat core set to LM Studio.");
                        }
                        else
                        {
                            Logger.Log("WARNING: No enabled LM Studio node found.");
                        }
                        break;
                }
                
                if (ChatCore != null)
                {
                    _logger.LogInformation($"ChatCore initialized successfully: {ChatCore.GetType().Name}");
                }
                else
                {
                    Logger.Log("ERROR: ChatCore is null after initialization");
                }
            }
            catch (TypeInitializationException ex)
            {
                Logger.Log($"CRITICAL: ChatCore initialization failed due to type initialization error");
                Logger.Log($"  Error: {ex.Message}");
                Logger.Log($"  Inner Exception: {ex.InnerException?.Message}");
                
                if (ex.InnerException?.Message?.Contains("e_sqlite3") == true)
                {
                    Logger.Log("  This is a SQLite loading error. Possible solutions:");
                    Logger.Log("  1. Install Visual C++ Redistributable 2015-2022");
                    Logger.Log("  2. Check if e_sqlite3.dll exists in runtimes folder");
                    Logger.Log("  3. Verify system architecture (x64/x86) matches");
                }
                
                _logger.LogError("Failed to initialize ChatCore", ex);
                ChatCore = null;
            }
            catch (Exception ex)
            {
                Logger.Log($"ERROR: Failed to initialize ChatCore: {ex.Message}");
                Logger.Log($"  Stack trace: {ex.StackTrace}");
                _logger.LogError("Failed to initialize ChatCore", ex);
                ChatCore = null;
            }
        }

        private void InitializeSyncTimer()
        {
            try
            {
                _syncTimer = new System.Timers.Timer(5000); // 5 seconds
                _syncTimer.Elapsed += SyncNames;
                _syncTimer.AutoReset = true;
                _syncTimer.Enabled = true;
                _logger.LogInformation("Name sync timer initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to initialize sync timer", ex);
            }
        }

        private void SyncNames(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (!Settings.FollowVPetName)
                return;

            var aiName = MW.Core.Save.Name;
            var userName = MW.Core.Save.HostName;
            if (Settings.AiName == aiName && Settings.UserName == userName)
                return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                Settings.AiName = aiName;
                Settings.UserName = userName;
            });
        }

        private void InitializeLegacyTTSService()
        {
            try
            {
                // 创建统一TTS调度器（如果需要）
                UnifiedTTSDispatcher? unifiedDispatcher = null;

                // 这里可以根据配置决定是否使用统一TTS系统
                // 暂时保持传统模式，统一TTS系统将在主类级别管理

                // 保持旧版TTS服务用于兼容性，支持统一TTS注入
                TTSService = new TTSServiceType(Settings.TTS, Settings.Proxy, unifiedDispatcher);
                TTSService.ServiceAvailabilityChanged += OnTTSServiceAvailabilityChanged;
                _logger.LogInformation("Legacy TTS service initialized with dependency injection support");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to initialize legacy TTS service", ex);
            }
        }

        private void InitializeConfigurationOptimizer()
        {
            try
            {
                _configurationOptimizer = new IntelligentConfigurationOptimizer(Settings);
                _configurationOptimizer.PerformIntelligentOptimization();
                _logger.LogInformation("Configuration optimizer initialized and executed");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to initialize configuration optimizer", ex);
            }
        }

        private void InitializeApplicationServices()
        {
            try
            {
                var asr = Settings.ASR;
                var asrConfig = new Infrastructure.Configuration.Configurations.ASRConfiguration
                {
                    IsEnabled = asr.IsEnabled,
                    Provider = asr.Provider,
                    HotkeyModifiers = asr.HotkeyModifiers,
                    HotkeyKey = asr.HotkeyKey,
                    Language = asr.Language,
                    AutoSend = asr.AutoSend,
                    ShowTranscriptionWindow = asr.ShowTranscriptionWindow,
                    RecordingDeviceNumber = asr.RecordingDeviceNumber
                };
                _voiceInputService = new Infrastructure.Services.ApplicationServices.VoiceInputService(this, asrConfig, _logger, _eventBus);

                _screenshotService = new Services.ScreenshotService(this, Settings);
                _screenshotService.ScreenshotCaptured += OnScreenshotCaptured;
                _screenshotService.OCRCompleted += OnOCRCompleted;
                _screenshotService.ErrorOccurred += OnScreenshotError;
                if (_screenshotService is Services.ScreenshotService screenshotService)
                {
                    screenshotService.PreprocessingCompleted += OnPreprocessingCompleted;
                }

                var purchaseConfig = new Infrastructure.Services.ApplicationServices.PurchaseConfiguration();
                _purchaseService = new Infrastructure.Services.ApplicationServices.PurchaseService(this, purchaseConfig, _logger, _eventBus);

                InitializeMediaPlaybackService();

                _processingLifecycleManager = new Services.ProcessingLifecycleManager(this);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to initialize application services", ex);
            }
        }

        private void InitializeMediaPlaybackService()
        {
            try
            {
                var mpvPath = Settings.MediaPlayback?.MpvPath;
                if (string.IsNullOrWhiteSpace(mpvPath))
                {
                    var dllPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    var mpvDir = Path.Combine(dllPath ?? "", "mpv");
                    mpvPath = Path.Combine(mpvDir, "mpv.exe");
                }

                var mediaConfig = new Infrastructure.Services.ApplicationServices.MediaPlaybackConfiguration
                {
                    MpvExePath = mpvPath,
                    MpvPath = mpvPath
                };
                _mediaPlaybackService = new Infrastructure.Services.ApplicationServices.MediaPlaybackService(mediaConfig, _logger, _eventBus);
                // 直接传递（编译期类型检查）；此前用 `as` 转换而服务未实现该接口，
                // 结果恒为 null → PlayHandler 从未注册 → play 能力说明从未进入系统提示词
                ActionProcessor?.SetMediaPlaybackService(_mediaPlaybackService);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error initializing MediaPlaybackService: {ex.Message}");
            }
        }

        private void RegisterCoreComponents()
        {
            // 注册核心基础设施
            _container.RegisterSingleton<IDependencyContainer>(_container);
            _container.RegisterSingleton<IEventBus>(_eventBus);
            _container.RegisterSingleton<IStructuredLogger>(_logger);
            _container.RegisterSingleton<InfraConfigManager>(_configurationManager);
            _container.RegisterSingleton<IServiceManager>(_serviceManager);

            // 注册主插件实例
            _container.RegisterSingleton<VPetLLM>(this);
            _container.RegisterSingleton<IMainWindow>(MW);
        }

        private void InitializeConfigurations()
        {
            try
            {
                // 从旧设置迁移到新配置
                var legacyConfigPath = Path.Combine(ExtensionValue.BaseDirectory, "VPetLLM.json");
                var newConfigBasePath = Path.Combine(ExtensionValue.BaseDirectory, "Configurations");
                var migrator = new ConfigurationMigrator(legacyConfigPath, newConfigBasePath, _logger);
                migrator.MigrateAllConfigurations();

                _logger.LogInformation("Configurations initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to initialize configurations", ex);
            }
        }

        private void RegisterServices()
        {
            try
            {
                // 注册核心服务
                ServiceRegistration.RegisterCoreServices(_container, this, Settings);

                // 注册应用服务
                ServiceRegistration.RegisterApplicationServices(_container, this);

                _logger.LogInformation("Services registered successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to register services", ex);
            }
        }

        private void InitializeLLMEntry()
        {
            try
            {
                LLMEntry = new Core.LLMEntryPoint(this, _logger);
                _logger.LogInformation("LLM entry point initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to initialize LLM entry point", ex);
            }
        }

        #endregion

        #region Plugin Lifecycle

        public override void LoadPlugin()
        {
            try
            {
                Logger.Log("LoadPlugin started.");

                // 初始化气泡延迟控制（性能优化）
                InitializeBubbleDelayControl();

                // 装上气泡独占守卫：回复期间别人的气泡请求会被静默吞掉，顶不掉正在念的回复。
                // 放在这里只是先占好位；宿主中途换掉气泡实例时 BubbleGuard 会自愈重装
                Utils.UI.BubbleGuard.Install();

                // 检测 VPet.Plugin.VPetTTS 插件
                DetectAndHandleVPetTTSPlugin();

                // 初始化熔断器配置
                InitializeRateLimiter();

                // 加载聊天历史
                ChatCore?.LoadHistory();

                // 当前没有登记到 ServiceManager 的 IService，这里只是走空启动，不要堵 UI 线程
                _ = _serviceManager.StartAsync();

                // 订阅事件
                SubscribeToEvents();

                // 初始化UI组件
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Logger.Log("Dispatcher.Invoke started.");

                    // 创建和注册 TalkBox
                    if (TalkBox is not null)
                    {
                        MW.TalkAPI.Remove(TalkBox);
                    }
                    TalkBox = new UI.Windows.TalkBox(this);
                    MW.TalkAPI.Add(TalkBox);

                    // 添加菜单项
                    var menuItem = new MenuItem()
                    {
                        Header = "VPetLLM",
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                    };
                    menuItem.Click += (s, e) => this.Setting();
                    MW.Main.ToolBar.MenuMODConfig.Items.Add(menuItem);

                    // 在LoadPlugin阶段初始化TouchInteractionHandler，确保Main窗口已经完全加载
                    InitializeTouchInteractionHandler();

                    // 监听购买事件 - 使用TakeItemHandle以获取购买来源信息
                    RegisterTakeItemHandleEvent();
                    Logger.Log("Purchase event listener registered.");

                    // 注册物品使用监听 - 通过 Hook Item.UseAction 静态字典
                    RegisterItemUseHook();
                    Logger.Log("Item use hook registered.");

                    // 初始化语音输入快捷键
                    InitializeVoiceInputHotkey();

                    // 初始化截图快捷键
                    InitializeScreenshotHotkey();

                    // 初始化默认插件状态检查器
                    if (_defaultPluginChecker is not null)
                    {
                        _defaultPluginChecker.IsVPetLLMDefaultPlugin();
                        // 如果设置窗口已经打开，刷新窗口标题
                        _defaultPluginChecker.RefreshWindowTitle();
                    }

                    // 初始化动画协调器
                    InitializeAnimationCoordinator();

                    // 初始化悬浮侧边栏
                    InitializeFloatingSidebar();

                    Logger.Log("Dispatcher.Invoke finished.");
                });

                // 启动检查在后台串行执行，避免版本提示与代理优化窗口互相争抢焦点
                _ = RunStartupChecksAsync();

                Logger.Log("LoadPlugin finished.");
            }
            catch (Exception ex)
            {
                Logger.Log($"LoadPlugin failed: {ex.Message}");
                _logger.LogError("Failed to load plugin", ex);
            }
        }

        /// <summary>
        /// 检测并处理 VPet.Plugin.VPetTTS 插件，包括其他的
        /// 注意：现在 IsVPetTTSPluginDetected 属性已改为实时检测，此方法主要用于初始化协调器
        /// </summary>
        private void DetectAndHandleVPetTTSPlugin()
        {
            try
            {
                // 设置实时检测委托，每次调用 TTS 时都会检测插件状态
                TTSService?.SetVPetTTSPluginChecker(() => CheckAnyTTSPluginEnabled());

                // 执行一次初始检测并记录日志
                var allPluginsResult = TTSPluginDetector.DetectAllOtherTTSPlugins(MW);
                
                if (allPluginsResult.HasOtherEnabledTTSPlugin)
                {
                    Logger.Log($"检测到其他已启用的 TTS 插件 ({allPluginsResult.EnabledPluginNames})，内置 TTS 将自动避让");
                    _vpetTTSPluginDetected = true; // 初始化缓存值

                    // 初始化 VPetTTS 协调器（使用延迟重试机制）
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // 等待 VPetTTS 插件完成初始化（最多重试 10 次，每次间隔 500ms）
                            for (int i = 0; i < 10; i++)
                            {
                                Logger.Log($"尝试初始化 VPetTTS 协调器（第 {i + 1} 次）...");
                                
                                VPetTTSCoordinator = new VPetTTSCoordinator(MW);
                                if (VPetTTSCoordinator.Initialize())
                                {
                                    Logger.Log("VPetTTS 协调器初始化成功");
                                    return;
                                }
                                else
                                {
                                    Logger.Log($"VPetTTS 协调器初始化失败（第 {i + 1} 次），{(i < 9 ? "500ms 后重试" : "放弃重试")}");
                                    VPetTTSCoordinator = null;
                                    
                                    if (i < 9)
                                    {
                                        await Task.Delay(500);
                                    }
                                }
                            }
                            
                            Logger.Log("VPetTTS 协调器初始化失败：已达到最大重试次数");
                        }
                        catch (Exception coordEx)
                        {
                            Logger.Log($"初始化 VPetTTS 协调器时发生错误: {coordEx.Message}");
                            VPetTTSCoordinator = null;
                        }
                    });
                }
                else
                {
                    Logger.Log("未检测到其他已启用的 TTS 插件，保持内置 TTS 功能");
                    _vpetTTSPluginDetected = false; // 初始化缓存值
                }

                // 记录每个检测到的插件的详细信息
                foreach (var kvp in allPluginsResult.DetectedPlugins)
                {
                    var pluginName = kvp.Key;
                    var result = kvp.Value;
                    if (result.PluginEnabled)
                    {
                        Logger.Log($"  - {pluginName} (版本: {result.PluginVersion}) - 已启用，VPetLLM内置TTS将避让");
                    }
                    else
                    {
                        Logger.Log($"  - {pluginName} (版本: {result.PluginVersion}) - 未启用");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"检测 TTS 插件时发生错误: {ex.Message}");
                _vpetTTSPluginDetected = false; // 初始化缓存值
            }
        }

        /// <summary>
        /// 实时检测是否有任何 TTS 插件启用
        /// </summary>
        private bool CheckAnyTTSPluginEnabled()
        {
            try
            {
                var result = TTSPluginDetector.DetectAllOtherTTSPluginsWithCache(MW, TTS_DETECT_CACHE_BATCH);
                var hasEnabledPlugin = result.HasOtherEnabledTTSPlugin;

                // 如果检测结果与上次不同，更新状态并通知UI
                if (hasEnabledPlugin != _vpetTTSPluginDetected)
                {
                    _vpetTTSPluginDetected = hasEnabledPlugin;

                    if (hasEnabledPlugin)
                    {
                        Logger.Log($"检测到其他TTS插件已启用 ({result.EnabledPluginNames})，VPetLLM内置TTS自动避让");
                    }
                    else
                    {
                        Logger.Log("其他TTS插件已禁用，VPetLLM内置TTS恢复功能");
                    }

                    // 通知设置窗口更新UI
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SettingWindow?.RefreshTTSPluginStatus(hasEnabledPlugin, result.EnabledPluginNames);
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }

                return hasEnabledPlugin;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 初始化熔断器配置
        /// </summary>
        private void InitializeRateLimiter()
        {
            try
            {
                // 实现熔断器初始化逻辑
                _logger.LogInformation("Rate limiter initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to initialize rate limiter", ex);
            }
        }

        /// <summary>
        /// 初始化气泡延迟控制（性能优化）
        /// </summary>
        private void InitializeBubbleDelayControl()
        {
            try
            {
                Logger.Log("开始初始化气泡延迟控制系统...");
                
                // 启用延迟控制
                Utils.UI.DirectBubbleManager.EnableDelayControl(true);
                
                Logger.Log("气泡延迟控制系统初始化完成");
                Logger.Log("设备性能检测将在首次显示气泡时自动执行");
            }
            catch (Exception ex)
            {
                Logger.Log($"初始化气泡延迟控制系统时发生错误: {ex.Message}");
                _logger.LogError("Failed to initialize bubble delay control", ex);
            }
        }

        /// <summary>
        /// 初始化动画协调器
        /// </summary>
        private void InitializeAnimationCoordinator()
        {
            try
            {
                AnimationHelper.Initialize(MW);
            }
            catch (Exception ex)
            {
                Logger.Log($"初始化AnimationCoordinator时发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 初始化悬浮侧边栏
        /// </summary>
        private void InitializeFloatingSidebar()
        {
            try
            {
                _floatingSidebarManager = new FloatingSidebarManager(this);

                if (Settings.FloatingSidebar.IsEnabled)
                {
                    _floatingSidebarManager.Show();
                }

                // 劫持输入框的发送按钮：处理期间变成"中断"，给关掉侧边栏的用户留一个中断入口
                UI.Controls.TalkBoxInterruptButton.Attach(this);
            }
            catch (Exception ex)
            {
                Logger.Log($"初始化FloatingSidebar时发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 初始化触摸交互处理器
        /// </summary>
        private void InitializeTouchInteractionHandler()
        {
            try
            {
                if (MW?.Main is null)
                {
                    return;
                }

                TouchInteractionHandler = new TouchInteractionHandler(this);
            }
            catch (Exception ex)
            {
                Logger.Log($"初始化TouchInteractionHandler时发生错误: {ex.Message}");
                TouchInteractionHandler = null;
            }
        }

        /// <summary>
        /// 注册购买事件处理器
        /// </summary>
        private void RegisterTakeItemHandleEvent()
        {
            try
            {
                // Event_TakeItemHandle在MainWindow类中，不在IMainWindow接口中
                var eventInfo = MW.GetType().GetEvent("Event_TakeItemHandle");
                if (eventInfo is not null)
                {
                    var handler = new Action<Food, int, string>(OnTakeItemHandle);
                    eventInfo.AddEventHandler(MW, handler);
                    Logger.Log("Successfully registered Event_TakeItemHandle using reflection");
                }
                else
                {
                    Logger.Log("Warning: Event_TakeItemHandle not found in MainWindow, falling back to Event_TakeItem");
                    // 降级方案：使用Event_TakeItem（不包含来源信息）
                    MW.Event_TakeItem += OnTakeItemFallback;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error registering Event_TakeItemHandle: {ex.Message}");
                Logger.Log("Falling back to Event_TakeItem");
                MW.Event_TakeItem += OnTakeItemFallback;
            }
        }

        /// <summary>
        /// 注销购买事件处理器
        /// </summary>
        private void UnregisterTakeItemHandleEvent()
        {
            try
            {
                var eventInfo = MW.GetType().GetEvent("Event_TakeItemHandle");
                if (eventInfo is not null)
                {
                    var handler = new Action<Food, int, string>(OnTakeItemHandle);
                    eventInfo.RemoveEventHandler(MW, handler);
                    Logger.Log("Successfully unregistered Event_TakeItemHandle");
                }
                else
                {
                    MW.Event_TakeItem -= OnTakeItemFallback;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error unregistering Event_TakeItemHandle: {ex.Message}");
                MW.Event_TakeItem -= OnTakeItemFallback;
            }
        }

        /// <summary>
        /// 降级方案：处理Event_TakeItem事件（不包含来源信息）
        /// </summary>
        private void OnTakeItemFallback(Food food)
        {
            // 没有来源信息，假设为用户手动购买
            Logger.Log("Using fallback Event_TakeItem (no source information available)");
            OnTakeItemHandle(food, 1, "unknown");
        }

        /// <summary>
        /// 注册物品使用 Hook
        /// </summary>
        private void RegisterItemUseHook()
        {
            // TODO: 等待 VPet 更新 NuGet 包后启用此功能
            // Item.UseAction 在 NuGet 包 1.1.0.58 中不存在
            Logger.Log("Item use hook is disabled: Item.UseAction not available in NuGet package 1.1.0.58");
            Logger.Log("To enable this feature, wait for VPet to release a new NuGet package with UseAction support");
        }

        /// <summary>
        /// 注销物品使用 Hook
        /// </summary>
        private void UnregisterItemUseHook()
        {
            // 功能已禁用，无需注销
            Logger.Log("Item use hook unregistration skipped (feature disabled)");
        }

        /// <summary>
        /// 初始化语音输入快捷键
        /// </summary>
        private void InitializeVoiceInputHotkey()
        {
            // 委托给 VoiceInputService
            if (_voiceInputService is Infrastructure.Services.ApplicationServices.VoiceInputService voiceService)
            {
                // 调用服务的快捷键初始化方法
                try
                {
                    _ = voiceService.UpdateHotkeyAsync();
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to initialize voice input hotkey: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 初始化截图快捷键
        /// </summary>
        private void InitializeScreenshotHotkey()
        {
            // 委托给 ScreenshotService
            try
            {
                _screenshotService?.UpdateHotkey();
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to initialize screenshot hotkey: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理购买事件（带来源信息）
        /// </summary>
        private void OnTakeItemHandle(Food food, int count, string from)
        {
            try
            {
                // 检查是否为默认插件
                if (!IsVPetLLMDefaultPlugin())
                {
                    Logger.Log("Purchase event: VPetLLM不是默认插件，忽略购买事件");
                    return;
                }

                Logger.Log($"Purchase event detected: {food?.Name ?? "Unknown"}, count: {count}, from: {from}");

                if (MW is null)
                {
                    Logger.Log("Purchase event: MW is null, skipping");
                    return;
                }

                // 委托给 PurchaseService 处理
                _purchaseService?.HandlePurchase(food, count, from);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling purchase event: {ex.Message}");
                Logger.Log($"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 检查是否为默认插件
        /// </summary>
        public bool IsVPetLLMDefaultPlugin()
        {
            return _defaultPluginChecker?.IsVPetLLMDefaultPlugin() ?? false;
        }

        public override void Save()
        {
            Settings.Save();
            ChatCore?.SaveHistory();
            SavePluginStates();

            if (_isShuttingDown)
            {
                // 退出路径上不能"发出去就不管"：宿主的顺序是 EndGame → Save → Exit，
                // 而 Exit 最后一句就是 Environment.Exit(0)。进程当场就没了，
                // 后台那个保存任务大概率跑不完 —— 表现就是"退出前改的设置下次打开没了"。
                //
                // Task.Run 起头是为了让 await 之后的续体落在线程池而不是 UI 线程
                // （这里是 Window_Closed，本身就在 UI 线程上），避免自己等自己。
                // 超时兜底 3 秒：宿主那边还有个 10 秒硬杀，不能把预算耗光。
                if (!Task.Run(() => _configurationManager.SaveAllAsync()).Wait(TimeSpan.FromSeconds(3)))
                {
                    Logger.Log("关停：配置落盘超时（3 秒），可能有未保存的更改");
                }
                return;
            }

            // 平时（自动存档等）后台异步保存，不阻塞 UI
            _ = SaveConfigurationsAsync();
        }

        private async Task SaveConfigurationsAsync()
        {
            try
            {
                await _configurationManager.SaveAllAsync();
                Logger.Log("Configurations saved successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to save configurations", ex);
            }
        }

        public override void Setting()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (SettingWindow is null || !SettingWindow.IsVisible)
                {
                    SettingWindow = new winSettingNew(this);
                    SettingWindow.Show();
                }
                else
                {
                    SettingWindow.Activate();
                }
            });
        }

        /// <summary>宿主已经在走退出流程。<see cref="Save"/> 会据此改成同步等待落盘。</summary>
        private volatile bool _isShuttingDown;

        /// <summary>
        /// 本插件实例是否正在关停。窗口用它来判断"现在不该再弹确认框了"。
        ///
        /// 是实例属性不是静态的：VPet 支持多开，一个桌宠退出不代表另一个也在退出，
        /// 静态标志会把还活着那只的确认框一起吞掉。
        /// </summary>
        public bool IsExiting => _isShuttingDown;

        /// <summary>关停只做一次。<c>EndGame</c> 和 <c>Dispose</c> 两条路都会进来。</summary>
        private bool _shutdownDone;

        private readonly object _shutdownLock = new();

        /// <summary>
        /// 游戏退出。<b>这是宿主唯一会调的关停回调</b> —— 它不认识 <see cref="IDisposable"/>，
        /// 全仓也没有任何地方调过本类的 <see cref="Dispose"/>。以前没实现这个方法，
        /// 等于正常退出时本插件<b>一点清理都不做</b>：窗口不关、补丁不摘、定时器不停。
        ///
        /// 后果就是那个"游戏致命性错误 Win32Exception(1400) 无效的窗口句柄"：
        /// 我们的窗口一路活到 <c>Environment.Exit</c>，等 WPF 在
        /// <c>Dispatcher.ShutdownFinished</c> 里挨个 <c>HwndSource.Dispose</c> →
        /// <c>DestroyWindow</c> 时，句柄已经没了，Win32 报 1400，异常没人接，
        /// 弹的还是不点名 MOD 的"致命性错误"框。
        ///
        /// 注意时序：宿主是 <c>EndGame() → Save() → Exit()</c>，
        /// <b><see cref="Save"/> 在这之后才跑</b>，所以这里只能做"不影响存档"的关停，
        /// 容器和存储必须留到 <see cref="Dispose"/>。
        /// </summary>
        public override void EndGame()
        {
            _isShuttingDown = true;
            ShutdownUiAndHooks();
        }

        /// <summary>
        /// 关掉窗口、摘掉补丁、停掉定时器、退订宿主事件。
        /// 幂等，<c>EndGame</c> 和 <c>Dispose</c> 谁先到都行。
        ///
        /// 这里的每一项都不碰配置存储 —— 宿主会在 <c>EndGame</c> 之后才调 <see cref="Save"/>。
        /// </summary>
        private void ShutdownUiAndHooks()
        {
            lock (_shutdownLock)
            {
                if (_shutdownDone) return;
                _shutdownDone = true;
            }

            // 关掉自己开的窗口，必须早于其它清理 —— 这是 1400 的正解：
            // 主动 Close() 能让 HwndSource 在句柄还有效的时候有序释放
            Run(CloseOwnedWindows, "关闭插件窗口");

            if (MW is not null)
            {
                Run(UnregisterTakeItemHandleEvent, "退订 TakeItem 事件");
                Run(UnregisterItemUseHook, "退订物品使用钩子");

                // 摘掉气泡守卫的 Harmony 补丁。不摘的话宿主之后每次说话都会
                // 调进一个已经没人维护（甚至已卸载）的程序集
                Run(Utils.UI.BubbleGuard.Uninstall, "卸载气泡守卫");
            }

            // 关停动画协调器。
            // 这一步以前整个是缺的：AnimationCoordinator.Dispose 全代码库没有任何调用点，
            // 于是插件卸载后它的后台队列循环还在转，还攥着主窗口引用；
            // 再加上它是 Lazy 单例 + static 初始化标志，重载后会一直用着上一次那个死窗口。
            Run(Handlers.Animation.AnimationHelper.Shutdown, "关停 AnimationCoordinator");

            // UI 挂件
            Run(UI.Controls.TalkBoxInterruptButton.Detach, "摘掉打断按钮");
            Run(() => _floatingSidebarManager?.Dispose(), "关停悬浮侧栏");

            // 语音输入持有全局热键和自己的窗口
            Run(() => _voiceInputService?.Dispose(), "关停语音输入");

            // 停止定时器
            Run(() => { _syncTimer?.Stop(); _syncTimer?.Dispose(); }, "停止同步定时器");
            Run(() => { _freeConfigTimer?.Stop(); _freeConfigTimer?.Dispose(); }, "停止免费配置定时器");
        }

        /// <summary>
        /// 退出路径专用：任何一步失败都只记一笔，绝不能中断后面的清理。
        /// 少关一个窗口就是一次 1400 崩溃。
        /// </summary>
        private static void Run(Action step, string what)
        {
            try { step(); }
            catch (Exception ex) { Logger.Log($"关停[{what}]失败（忽略，继续）: {ex.Message}"); }
        }

        public void Dispose()
        {
            try
            {
                // 正常退出走的是 EndGame；这里是插件被禁用/重载的路径。
                // 两条路共用同一套关停，谁先到谁做
                ShutdownUiAndHooks();

                // 清理服务
                _purchaseService?.Dispose();
                TTSService?.Dispose();
                TouchInteractionHandler?.Dispose();

                // 停止所有服务。
                //
                // 这里过去是 _serviceManager.StopAsync().Wait()：在 UI 线程上无超时等待一个
                // 异步方法，而它 await 之后的续体又要回到同一个 UI 线程——典型的 async 死锁，
                // 一旦命中就是退出永久卡死。
                //
                // Task.Run 把异步流程挪到线程池启动，续体不再需要 UI 线程；再加上超时兜底，
                // 即使某个服务停不下来也不会拖住整个退出流程。
                if (!Task.Run(() => _serviceManager.StopAsync()).Wait(TimeSpan.FromSeconds(5)))
                {
                    _logger.LogWarning("Service shutdown timed out after 5s, continuing disposal");
                }

                _serviceManager.Dispose();
                _container.Dispose();

                _logger.LogInformation("Plugin disposed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError("Error during disposal", ex);
            }
        }

        /// <summary>
        /// 关闭插件自己开的窗口。退出路径专用，任何一个窗口关闭失败都不能中断后续清理。
        /// </summary>
        private void CloseOwnedWindows()
        {
            // 按"这个类型是不是本程序集的"来认领，而不是一个个数字段。
            //
            // 以前这里只关 SettingWindow，可我们有十来种独立顶层窗口：上下文编辑器、
            // 记录编辑器、语音输入、截图遮罩、截图编辑器、图片预览、诊断报告……
            // 它们都不是设置窗口的子窗口，关设置窗口一个也带不走。
            // 按程序集扫的另一个好处是以后新加窗口自动纳入，不用记得回来改这里。
            var app = Application.Current;
            if (app is null) return;

            var self = typeof(VPetLLM).Assembly;

            // 必须先快照：Close() 会把窗口从 app.Windows 里摘掉，边遍历边改会漏
            var mine = app.Dispatcher.Invoke(() =>
                app.Windows.OfType<System.Windows.Window>()
                   .Where(w => w.GetType().Assembly == self)
                   .ToArray());

            if (mine.Length > 0)
            {
                Logger.Log($"关停：需要关闭 {mine.Length} 个插件窗口 " +
                           $"[{string.Join(", ", mine.Select(w => w.GetType().Name))}]");
            }

            foreach (var window in mine)
            {
                CloseWindowSafely(window, window.GetType().Name);
            }

            // 别人不会碰它，但它是我们自己缓存的引用，得跟着失效
            SettingWindow = null;
        }

        private void CloseWindowSafely(System.Windows.Window? window, string name)
        {
            if (window is null)
                return;

            try
            {
                // 已经关闭的窗口再 Close() 是无害的空操作，但窗口可能属于别的线程
                window.Dispatcher.Invoke(window.Close);
            }
            catch (Exception ex)
            {
                Logger.Log($"关闭窗口 {name} 失败（退出流程继续）: {ex.Message}");
            }
        }

        #endregion

        #region Plugin Lifecycle Helper Methods

        #endregion

        #region Event Handling

        private void SubscribeToEvents()
        {
            // 订阅语音输入事件
            _eventBus.SubscribeAsync<VoiceInputTranscriptionCompletedEvent>(OnVoiceInputCompletedEvent);

            // 订阅截图事件
            _eventBus.SubscribeAsync<Infrastructure.Events.ScreenshotCapturedEvent>(OnScreenshotCapturedEvent);

            // 订阅购买事件
            _eventBus.SubscribeAsync<PurchaseBatchProcessedEvent>(OnPurchaseProcessedEvent);

            _logger.LogInformation("Event subscriptions completed");
        }

        private async Task OnVoiceInputCompletedEvent(VoiceInputTranscriptionCompletedEvent evt)
        {
            if (!string.IsNullOrWhiteSpace(evt.Transcription))
            {
                await SendChat(evt.Transcription);
            }
        }

        private async Task OnScreenshotCapturedEvent(Infrastructure.Events.ScreenshotCapturedEvent evt)
        {
            _logger.LogInformation("Screenshot captured event received");
            await Task.CompletedTask;
        }

        private async Task OnPurchaseProcessedEvent(PurchaseBatchProcessedEvent evt)
        {
            _logger.LogInformation($"Purchase batch processed: {evt.TotalCount} items");
            await Task.CompletedTask;
        }

        private void OnScreenshotCaptured(object? sender, Services.ScreenshotCapturedEventArgs e)
        {
            try
            {
                Logger.Log($"Screenshot captured, size: {e.ImageData.Length} bytes");

                var processingMode = Settings.Screenshot.ProcessingMode;

                if (processingMode == Configuration.ScreenshotProcessingMode.NativeMultimodal)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ShowScreenshotEditor(e.ImageData);
                    });
                }
                else if (processingMode == Configuration.ScreenshotProcessingMode.PreprocessingMultimodal
                         || processingMode == Configuration.ScreenshotProcessingMode.OCRApi)
                {
                    // OCR 与前置多模态是同一条链路：图 → 文字 → 与提问拼合 → 发主模型，
                    // 只是识别时用的提示词不同。所以共用同一个编辑器流程，
                    // 用户同样能补充提问、追加多张图。
                    // AutoSend 打开时才跳过编辑器，由 ScreenshotService.ProcessScreenshot 直接识别并发送。
                    if (!Settings.Screenshot.AutoSend)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            ShowScreenshotEditorForPreprocessing(e.ImageData);
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling screenshot: {ex.Message}");
            }
        }

        private void ShowScreenshotEditor(byte[] imageData)
        {
            try
            {
                var editor = new UI.Windows.winScreenshotEditor(this, imageData);

                editor.SendRequested += async (s, args) =>
                {
                    try
                    {
                        if (args.Images.Count > 0)
                        {
                            Logger.Log($"Sending {args.Images.Count} screenshot(s) with prompt: {args.Prompt}");

                            if (TalkBox is not null)
                            {
                                await TalkBox.SendChatWithImages(args.Prompt, args.Images);
                            }
                            else if (ChatCore is not null)
                            {
                                await ChatDispatcher.SubmitAsync(
                                    args.Prompt, ChatPriority.User, args.Images, "Screenshot.Editor",
                                    newRound: true);
                            }
                        }
                        else
                        {
                            if (!string.IsNullOrWhiteSpace(args.Prompt))
                            {
                                if (TalkBox is not null)
                                {
                                    await TalkBox.SendChat(args.Prompt);
                                }
                                else if (ChatCore is not null)
                                {
                                    await ChatDispatcher.SubmitAsync(
                                        args.Prompt, ChatPriority.User, source: "Screenshot.Editor",
                                        newRound: true);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error sending screenshot message: {ex.Message}");
                    }
                };

                editor.Cancelled += (s, args) =>
                {
                    Logger.Log("Screenshot editor cancelled");
                };

                editor.Show();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error showing screenshot editor: {ex.Message}");
            }
        }

        private void ShowScreenshotEditorForPreprocessing(byte[] imageData)
        {
            try
            {
                var editor = new UI.Windows.winScreenshotEditor(this, imageData);

                editor.SendRequested += async (s, args) =>
                {
                    try
                    {
                        if (args.Images.Count > 0)
                        {
                            Logger.Log($"Processing {args.Images.Count} screenshot(s) with preprocessing");

                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                TalkBox?.DisplayThink();
                                TalkBox?.StartThinkingAnimation();
                            });

                            if (_screenshotService is Services.ScreenshotService screenshotService)
                            {
                                _pendingImageData = args.ImageData;
                                await screenshotService.ProcessWithPreprocessingAsync(args.Images, args.Prompt);
                            }
                            else
                            {
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    TalkBox?.StopThinkingAnimationWithoutHide();
                                });
                            }
                        }
                        else
                        {
                            if (!string.IsNullOrWhiteSpace(args.Prompt))
                            {
                                if (TalkBox is not null)
                                {
                                    await TalkBox.SendChat(args.Prompt);
                                }
                                else if (ChatCore is not null)
                                {
                                    await ChatDispatcher.SubmitAsync(
                                        args.Prompt, ChatPriority.User, source: "Screenshot.Editor",
                                        newRound: true);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error processing screenshot: {ex.Message}");
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            TalkBox?.StopThinkingAnimationWithoutHide();
                        });
                    }
                };

                editor.Cancelled += (s, args) =>
                {
                    Logger.Log("Screenshot editor cancelled (preprocessing)");
                };

                editor.Show();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error showing screenshot editor: {ex.Message}");
            }
        }

        private void OnPreprocessingCompleted(object? sender, Services.PreprocessingCompletedEventArgs e)
        {
            try
            {
                if (e.Success)
                {
                    Logger.Log($"Preprocessing completed, provider: {e.UsedProvider}");

                    Application.Current.Dispatcher.Invoke(async () =>
                    {
                        try
                        {
                            if (ChatCore is not null && !string.IsNullOrWhiteSpace(e.CombinedMessage))
                            {
                                await ChatDispatcher.SubmitAsync(
                                    e.CombinedMessage, ChatPriority.User, source: "Screenshot.Preprocessed",
                                    newRound: true);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Error sending preprocessed message: {ex.Message}");
                            TalkBox?.StopThinkingAnimationWithoutHide();
                        }
                    });
                }
                else
                {
                    Logger.Log($"Preprocessing failed: {e.ErrorMessage}");
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        TalkBox?.StopThinkingAnimationWithoutHide();
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling preprocessing: {ex.Message}");
                Application.Current.Dispatcher.Invoke(() =>
                {
                    TalkBox?.StopThinkingAnimationWithoutHide();
                });
            }
        }

        private void OnOCRCompleted(object? sender, string text)
        {
            try
            {
                Logger.Log($"OCR completed, length: {text.Length}");

                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (Settings.Screenshot.AutoSend)
                        {
                            _ = ChatDispatcher.SubmitAsync(text, ChatPriority.User, source: "Screenshot.OCR", newRound: true);
                        }
                        else
                        {
                            Logger.Log($"OCR text ready: {text.Substring(0, Math.Min(50, text.Length))}...");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling OCR: {ex.Message}");
            }
        }

        private void OnScreenshotError(object? sender, string error)
        {
            Logger.Log($"Screenshot error: {error}");
        }

        #endregion

        #region Public API (Legacy Compatibility)

        // ============================================================================
        // 核心聊天功能
        // ============================================================================

        /// <summary>
        /// 发送聊天消息
        /// </summary>
        public async Task<string> SendChat(string prompt)
        {
            if (ChatCore is null)
            {
                _logger.LogWarning("ChatCore is null, cannot send message");
                return "错误：聊天核心未初始化。";
            }

            try
            {
                PromptHelper.ReloadPrompts();
                var response = await ChatDispatcher.SubmitAsync(
                    prompt, ChatPriority.User, source: "VPetLLM.SendChat", newRound: true);
                RecordAISuccess();
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to send chat message", ex);
                RecordAIFailure();
                return $"Error: {ex.Message}";
            }
        }

        public void RecordAISuccess()
        {
            lock (_failureCountLock)
            {
                if (_consecutiveAIFailureCount > 0)
                {
                    Logger.Log($"AI request succeeded, resetting consecutive failure count (was {_consecutiveAIFailureCount})");
                }
                _consecutiveAIFailureCount = 0;
            }
        }

        public void RecordAIFailure()
        {
            int currentCount;
            lock (_failureCountLock)
            {
                _consecutiveAIFailureCount++;
                currentCount = _consecutiveAIFailureCount;
            }

            Logger.Log($"AI request failed. Consecutive failures: {currentCount}/{MAX_CONSECUTIVE_FAILURES}");

            if (currentCount >= MAX_CONSECUTIVE_FAILURES)
            {
                if (!(Settings?.EnableAutoDiagnostic ?? true))
                {
                    Logger.Log($"Auto diagnostic is disabled. Skipping.");
                    lock (_failureCountLock) { _consecutiveAIFailureCount = 0; }
                    return;
                }

                Logger.Log($"Reached {MAX_CONSECUTIVE_FAILURES} consecutive failures, triggering diagnostic...");
                Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await RunDiagnosticAsync();
                    lock (_failureCountLock)
                    {
                        _consecutiveAIFailureCount = 0;
                    }
                });
            }
        }

        public async Task RunDiagnosticAsync()
        {
            try
            {
                var lang = Settings?.Language ?? "zh-hans";
                var diagService = new Services.DiagnosticService(Settings, lang);

                Logger.Log(lang.StartsWith("zh") ? "正在运行诊断..." : "Running diagnostics...");

                var result = await diagService.RunFullDiagnosticAsync(status =>
                {
                    Logger.Log($"Diagnostic status: {status}");
                });

                var report = diagService.FormatDiagnosticReport(result);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    var title = LanguageHelper.Get("Diagnostic.UiWindowTitle", lang, "运行诊断结果");
                    UI.Windows.winDiagnosticReport? diagWindow = null;
                    diagWindow = new UI.Windows.winDiagnosticReport(
                        title, result, report,
                        LanguageHelper.Get("Diagnostic.UiWindowHint", lang, "可选择测试 LLM 或应用推荐设置"),
                        onTestLLM: async () =>
                        {
                            diagWindow!.ShowProgress(lang.StartsWith("zh") ? "正在测试各渠道 LLM 响应..." : "Testing channel LLM responses...");
                            await TestAllChannelLLMsAsync(diagService, result, diagWindow, lang);
                        },
                        onApplyRecommendations: () =>
                        {
                            var recommendations = diagService.GenerateRecommendedSettings(result);
                            if (recommendations.Count > 0)
                            {
                                diagWindow!.ShowRecommendations(recommendations, accepted =>
                                {
                                    if (accepted)
                                    {
                                        diagService.ApplyRecommendedSettings(recommendations);
                                        diagWindow!.OnRecommendationsApplied();

                                        var appliedMsg = lang.StartsWith("zh")
                                            ? "已应用推荐设置。建议重启对话以使用新设置。"
                                            : "Recommended settings applied. Restart conversation to use new settings.";
                                        diagWindow!.UpdateFromResult(result,
                                            diagService.FormatDiagnosticReport(result),
                                            appliedMsg);
                                    }
                                });
                            }
                            else
                            {
                                var noAdjustTitle = lang.StartsWith("zh") ? "提示" : "Note";
                                var noAdjustMsg = lang.StartsWith("zh")
                                    ? "未发现需要调整的设置。"
                                    : "No settings adjustments needed.";
                                diagWindow!.ShowInfo(noAdjustTitle, noAdjustMsg);
                            }
                        },
                        onOpenSettings: () =>
                        {
                            OpenSettingWindow();
                        });
                    diagWindow.ShowDialog();
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"Diagnostic error: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取本地与 GitHub main 分支 info.lps 中的最新版本信息。
        /// 版本请求只使用插件商店专用代理设置。
        /// </summary>
        public Task<VersionCheckResult> CheckLatestVersionAsync(bool forceRefresh = false)
        {
            lock (_failureCountLock)
            {
                if (_versionCheckTask is null || (forceRefresh && _versionCheckTask.IsCompleted))
                {
                    _versionCheckTask = CheckLatestVersionCoreAsync();
                }

                return _versionCheckTask;
            }
        }

        private async Task<VersionCheckResult> CheckLatestVersionCoreAsync()
        {
            var service = new VersionCheckService(Settings?.PluginStore);
            var result = await service.CheckAsync();

            if (result.Succeeded)
            {
                Logger.Log($"VersionCheck: current={result.CurrentVersion?.DisplayText}, latest={result.LatestVersion?.DisplayText}, update={result.UpdateAvailable}");
            }
            else
            {
                Logger.Log($"VersionCheck: failed: {result.ErrorMessage}");
            }

            return result;
        }

        private async Task RunStartupChecksAsync()
        {
            await RunStartupVersionCheckAsync();
            await RunStartupProxyOptimizationAsync();
        }

        private async Task RunStartupVersionCheckAsync()
        {
            lock (_failureCountLock)
            {
                if (_startupVersionCheckRan)
                    return;
                _startupVersionCheckRan = true;
            }

            var result = await CheckLatestVersionAsync();
            if (!result.Succeeded || !result.UpdateAvailable
                || result.CurrentVersion is null || result.LatestVersion is null)
            {
                return;
            }

            // 更新由 Steam 推送，用户什么都不用做，所以不再弹模态框问「要不要去 GitHub」——
            // 那是个必须点掉才能继续的打断。改用 VPet 自己在用的 NoticeBox（照片解锁提示同款）：
            // 非模态、到点自动消失、不抢焦点，因此每次启动都提示也不烦人。
            var lang = Settings?.Language ?? "zh-hans";
            var title = LanguageHelper.Get("UpdateNotice.Title", lang, "VPetLLM Update Available");
            var message = LanguageHelper.Get(
                    "UpdateNotice.Body", lang,
                    "{Latest} (current {Current})")
                .Replace("{Latest}", result.LatestVersion.DisplayText)
                .Replace("{Current}", result.CurrentVersion.DisplayText);

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                // 正常走不到：启动检查是在插件初始化的 Dispatcher 块之后才起的。
                // 显式记一笔，免得提示悄无声息地丢了还查不出原因。
                Logger.Log($"VersionCheck: 无可用 Dispatcher，跳过新版本提示 {result.LatestVersion.DisplayText}");
                return;
            }

            try
            {
                // NoticeBox 是 WPF 控件，必须回 UI 线程；这里是后台任务，得自己派发
                dispatcher.Invoke(() =>
                    Panuon.WPF.UI.NoticeBox.Show(
                        message,
                        title,
                        Panuon.WPF.UI.MessageBoxIcon.Info,
                        true,
                        UpdateNoticeDurationMs));

                Logger.Log($"VersionCheck: 已提示新版本 {result.LatestVersion.DisplayText}");
            }
            catch (Exception ex)
            {
                Logger.Log($"VersionCheck: 显示更新提示失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 启动时的代理自动优化：
        /// 1) 检测是否处于中国大陆网络环境；
        /// 2) 主动测试各渠道直连/代理连通性、插件商店连通性；
        /// 3) 汇总"仅代理相关"的推荐项（中国大陆→启用插件商店镜像、代理失效→关闭全局代理、
        ///    渠道→强制直连/强制代理）；
        /// 4) 仅在存在可优化项时弹窗，用户确认后才应用。尊重用户手动关闭插件商店代理的选择。
        /// 每次进程仅运行一次。
        /// </summary>
        public async Task RunStartupProxyOptimizationAsync()
        {
            try
            {
                if (!(Settings?.EnableStartupProxyOptimization ?? true))
                    return;

                lock (_failureCountLock)
                {
                    if (_startupProxyOptimizationRan)
                        return;
                    _startupProxyOptimizationRan = true;
                }

                var lang = Settings?.Language ?? "zh-hans";

                // 1) 地区检测（外部 IP 接口 + 本地兜底）
                bool isChina = await Services.GeoService.IsLikelyChinaAsync();

                // 2) 运行完整诊断（内部会对每个渠道分别测试直连与代理）
                var diagService = new Services.DiagnosticService(Settings, lang);
                var result = await diagService.RunFullDiagnosticAsync();

                // 3) 仅生成代理相关推荐项
                var recommendations = diagService.GenerateProxyRecommendations(result, isChina);

                // 用户点过"应用"或"忽略"的条目不再重复打扰。
                // 只靠"应用后设置变了、下次自然不再生成"是不够的：一旦某条应用不下去
                // （例如目标设置项不存在），弹窗就会每次启动原样重来
                var handled = Settings?.HandledProxyRecommendations;
                if (handled is { Count: > 0 })
                {
                    var before = recommendations.Count;
                    recommendations = recommendations
                        .Where(r => !handled.Contains(RecommendationSignature(r)))
                        .ToList();
                    if (before != recommendations.Count)
                        Logger.Log($"StartupProxyOptimization: 跳过 {before - recommendations.Count} 项用户已处理的建议。");
                }

                if (recommendations.Count == 0)
                {
                    Logger.Log("StartupProxyOptimization: 未发现需要调整的代理相关项。");
                    return;
                }

                Logger.Log($"StartupProxyOptimization: 发现 {recommendations.Count} 项代理相关建议，弹窗等待用户确认。");

                // 4) 弹窗确认后应用
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var title = LanguageHelper.Get("Diagnostic.UiProxyWindowTitle", lang, "代理自动优化建议");
                    var status = lang.StartsWith("zh")
                        ? "检测到可优化的代理设置，确认后应用"
                        : "Detected proxy settings that can be optimized";
                    var report = diagService.FormatRecommendationsReport(recommendations);

                    UI.Windows.winDiagnosticReport? diagWindow = null;
                    diagWindow = new UI.Windows.winDiagnosticReport(title, report, status);
                    diagWindow.Loaded += (_, _) =>
                    {
                        diagWindow.ShowRecommendations(recommendations, accepted =>
                        {
                            // 无论应用还是忽略，这批条目都算"用户已经表过态"，下次不再问。
                            // 记录必须先于任何可能提前返回的分支，否则又回到无限弹窗
                            RememberHandledRecommendations(recommendations);

                            if (accepted)
                            {
                                var unapplied = diagService.ApplyRecommendedSettings(recommendations);
                                diagWindow.OnRecommendationsApplied();

                                if (unapplied.Count > 0)
                                {
                                    // 老实告诉用户哪几条没落地，而不是装作已经解决
                                    Logger.Log($"StartupProxyOptimization: {unapplied.Count} 项未能应用: {string.Join(", ", unapplied)}");
                                    diagWindow.ShowInfo(
                                        lang.StartsWith("zh") ? "部分设置未能应用" : "Some settings were not applied",
                                        lang.StartsWith("zh")
                                            ? $"有 {unapplied.Count} 项建议未能写入设置，可在设置中手动调整。"
                                            : $"{unapplied.Count} suggestion(s) could not be written. Please adjust them manually in Settings.");
                                }
                                else
                                {
                                    Logger.Log("StartupProxyOptimization: 已应用代理相关推荐设置。");
                                }
                            }
                            else
                            {
                                Logger.Log("StartupProxyOptimization: 用户忽略本批建议，已记录，不再重复弹窗。");
                                diagWindow.Close();
                            }
                        });
                    };
                    diagWindow.ShowDialog();
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"StartupProxyOptimization error: {ex.Message}");
            }
        }

        /// <summary>
        /// 建议的稳定签名。带上推荐值：同一项设置日后若给出**不同**的推荐，
        /// 仍然应该重新征求用户意见，而不是被旧记录一并压掉。
        /// </summary>
        private static string RecommendationSignature(Services.RecommendedSetting rec)
            => $"{rec.Key}={rec.RecommendedValue}";

        /// <summary>
        /// 记下用户已经表过态的建议并落盘。
        /// </summary>
        private void RememberHandledRecommendations(List<Services.RecommendedSetting> recommendations)
        {
            try
            {
                if (Settings == null) return;

                var handled = Settings.HandledProxyRecommendations ??= new List<string>();
                foreach (var rec in recommendations)
                {
                    var sig = RecommendationSignature(rec);
                    if (!handled.Contains(sig))
                        handled.Add(sig);
                }

                // 渠道可以被反复增删改名，这里加个上限免得它只增不减
                const int maxKept = 200;
                if (handled.Count > maxKept)
                    handled.RemoveRange(0, handled.Count - maxKept);

                Settings.Save();
            }
            catch (Exception ex)
            {
                Logger.Log($"StartupProxyOptimization: 记录已处理建议失败: {ex.Message}");
            }
        }

        private async Task TestAllChannelLLMsAsync(
            Services.DiagnosticService diagService,
            Services.DiagnosticResult result,
            UI.Windows.winDiagnosticReport diagWindow,
            string lang)
        {
            foreach (var cr in result.ChannelResults)
            {
                if (cr.ApiAvailable && !cr.LlmTested)
                {
                    var statusMsg = lang.StartsWith("zh")
                        ? $"正在测试 {cr.ChannelType}: {cr.ChannelName}..."
                        : $"Testing {cr.ChannelType}: {cr.ChannelName}...";
                    Application.Current.Dispatcher.Invoke(() =>
                        diagWindow.ShowProgress(statusMsg));

                    await diagService.CheckChannelLLMAsync(cr);
                }
            }

            var finalReport = diagService.FormatDiagnosticReport(result);

            Application.Current.Dispatcher.Invoke(() =>
            {
                diagWindow.HideProgress();
                diagWindow.UpdateTitle(
                    lang.StartsWith("zh") ? "诊断报告 - 含LLM测试" : "Diagnostic Report - With LLM Tests");
                diagWindow.UpdateFromResult(result,
                    diagService.FormatDiagnosticReport(result),
                    lang.StartsWith("zh") ? "LLM 测试完成" : "LLM test complete");

                var recommendations = diagService.GenerateRecommendedSettings(result);
                if (recommendations.Count > 0)
                {
                    diagWindow.ShowRecommendations(recommendations, accepted =>
                    {
                        if (accepted)
                        {
                            diagService.ApplyRecommendedSettings(recommendations);
                            diagWindow.OnRecommendationsApplied();

                            var appliedMsg = lang.StartsWith("zh")
                                ? "已应用推荐设置。建议重启对话。"
                                : "Recommended settings applied. Restart conversation.";
                            diagWindow.UpdateFromResult(result,
                                diagService.FormatDiagnosticReport(result),
                                appliedMsg);
                        }
                    });
                }
            });
        }

        public void OpenSettingWindow()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (SettingWindow is null || !SettingWindow.IsLoaded)
                {
                    SettingWindow = new UI.Windows.winSettingNew(this);
                }
                SettingWindow.Show();
                SettingWindow.Activate();
                SettingWindow.Focus();
            });
        }

        /// <summary>
        /// 更新聊天核心
        /// </summary>
        public void UpdateChatCore(IChatCore newChatCore)
        {
            ChatCore = newChatCore;
            Logger.Log($"ChatCore updated to: {ChatCore?.GetType().Name}");

            Logger.Log($"Chat history already loaded by HistoryManager (SeparateChatByProvider={Settings.SeparateChatByProvider})");

            // 不能用 InvokeAsync(...).Wait()：从 UI 线程（设置窗口切换提供商）调用时
            // 会触发 PushFrame 重入；UI 线程直接执行，其他线程用同步 Invoke
            void SwapTalkBox()
            {
                if (TalkBox is not null)
                {
                    MW.TalkAPI.Remove(TalkBox);
                    Logger.Log("Old TalkBox removed from TalkAPI");
                }
                TalkBox = new UI.Windows.TalkBox(this);
                MW.TalkAPI.Add(TalkBox);
                Logger.Log("New TalkBox added to TalkAPI");

                Logger.Log($"New TalkBox should use ChatCore: {ChatCore?.GetType().Name}");
            }

            var dispatcher = Application.Current.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                SwapTalkBox();
            }
            else
            {
                dispatcher.Invoke(SwapTalkBox);
            }

            _logger.LogInformation("ChatCore updated");
        }

        /// <summary>
        /// 清除聊天历史
        /// </summary>
        public void ClearChatHistory()
        {
            ChatCore?.ClearContext();
            _logger.LogInformation("Chat history cleared");
        }

        /// <summary>
        /// 获取聊天历史
        /// </summary>
        public List<Message> GetChatHistory()
        {
            return ChatCore is not null ? ChatCore.GetChatHistory() : new List<Message>();
        }

        /// <summary>
        /// 对单条文本取 L2 归一化向量，供插件做向量索引/检索。
        /// 向量化未启用、后端不可用或 ChatCore 未就绪时返回 null。
        /// </summary>
        public Task<float[]?> EmbedTextAsync(string text)
        {
            return ChatCore is not null ? ChatCore.EmbedTextAsync(text) : Task.FromResult<float[]?>(null);
        }

        /// <summary>
        /// 设置聊天历史
        /// </summary>
        public void SetChatHistory(List<Message> history)
        {
            ChatCore?.SetChatHistory(history);
        }

        /// <summary>
        /// 获取当前 ChatCore 信息
        /// </summary>
        public string GetCurrentChatCoreInfo()
        {
            return $"ChatCore Type: {ChatCore?.GetType().Name}, Hash: {ChatCore?.GetHashCode()}";
        }

        // ============================================================================
        // 服务管理方法
        // ============================================================================

        /// <summary>
        /// 显示语音输入窗口
        /// </summary>
        public void ShowVoiceInputWindow()
        {
            (_voiceInputService as Infrastructure.Services.ApplicationServices.VoiceInputService)?.ShowVoiceInputWindowAsync();
        }

        /// <summary>
        /// 语音输入是否正在录音/编辑中。
        /// ListenHandler 用它避免把用户正在进行的录音顶掉。
        /// </summary>
        public bool IsVoiceInputActive
        {
            get
            {
                var service = _voiceInputService as Infrastructure.Services.ApplicationServices.VoiceInputService;
                return service is not null
                    && service.CurrentState != Infrastructure.Services.ApplicationServices.VoiceInputState.Idle;
            }
        }

        /// <summary>
        /// 开始截图捕获
        /// </summary>
        public void StartScreenshotCapture()
        {
            _screenshotService?.StartCapture();
        }

        /// <summary>
        /// AI 主动请求看屏幕：弹出选区窗口让用户圈定范围。用户取消或超时返回 null。
        /// </summary>
        public Task<byte[]?> RequestScreenshotFromUserAsync(string reason, int timeoutSeconds = 60)
        {
            if (_screenshotService is null) return Task.FromResult<byte[]?>(null);
            return _screenshotService.RequestUserCaptureAsync(reason, timeoutSeconds);
        }

        /// <summary>
        /// 更新截图快捷键
        /// </summary>
        public void UpdateScreenshotHotkey()
        {
            _screenshotService?.UpdateHotkey();
        }

        /// <summary>
        /// 更新语音输入快捷键
        /// </summary>
        public void UpdateVoiceInputHotkey()
        {
            (_voiceInputService as Infrastructure.Services.ApplicationServices.VoiceInputService)?.UpdateHotkeyAsync();
        }

        /// <summary>
        /// 播放 TTS
        /// </summary>
        public async Task PlayTTSAsync(string text)
        {
            if (Settings.TTS.IsEnabled && TTSService is not null && !string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    await TTSService.PlayTextAsync(text);
                }
                catch (Exception ex)
                {
                    Logger.Log($"TTS播放失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 更新 TTS 服务
        /// </summary>
        public void UpdateTTSService()
        {
            TTSService?.UpdateSettings(Settings.TTS, Settings.Proxy);
        }

        // ============================================================================
        // 插件管理方法
        // ============================================================================

        /// <summary>
        /// 加载插件
        /// </summary>
        public void LoadPlugins()
        {
            PluginManager.LoadPlugins(this.ChatCore);
        }

        /// <summary>
        /// 更新插件
        /// </summary>
        public async Task<bool> UpdatePlugin(string pluginFilePath)
        {
            return await PluginManager.UpdatePlugin(pluginFilePath, this.ChatCore);
        }

        /// <summary>
        /// 保存插件状态
        /// </summary>
        public void SavePluginStates()
        {
            PluginManager.SavePluginStates();
        }

        /// <summary>
        /// 卸载并尝试删除插件
        /// </summary>
        public async Task<bool> UnloadAndTryDeletePlugin(IVPetLLMPlugin plugin)
        {
            return await PluginManager.UnloadAndTryDeletePlugin(plugin, this.ChatCore);
        }

        /// <summary>
        /// 导入插件
        /// </summary>
        public void ImportPlugin(string filePath)
        {
            PluginManager.ImportPlugin(filePath);
            LoadPlugins();
            RefreshPluginList();
        }

        /// <summary>
        /// 删除插件文件
        /// </summary>
        public async Task<bool> DeletePluginFile(string pluginFilePath)
        {
            return await PluginManager.DeletePluginFile(pluginFilePath);
        }

        /// <summary>
        /// 按名称删除插件
        /// </summary>
        public async Task<bool> DeletePluginByName(string pluginName)
        {
            return await PluginManager.DeletePluginByName(pluginName);
        }

        /// <summary>
        /// 刷新插件列表
        /// </summary>
        public void RefreshPluginList()
        {
            SettingWindow?.RefreshPluginList();
        }

        // ============================================================================
        // 配置管理方法
        // ============================================================================

        /// <summary>
        /// 执行智能配置优化
        /// </summary>
        public void PerformConfigurationOptimization()
        {
            if (_configurationOptimizer is null)
            {
                _configurationOptimizer = new IntelligentConfigurationOptimizer(Settings);
            }
            _configurationOptimizer.PerformIntelligentOptimization();
            Settings.Save();
        }

        /// <summary>
        /// 获取配置健康报告
        /// </summary>
        public string GetConfigurationHealthReport()
        {
            if (_configurationOptimizer is null)
            {
                _configurationOptimizer = new IntelligentConfigurationOptimizer(Settings);
            }
            return _configurationOptimizer.GetIntelligentHealthReport();
        }

        /// <summary>
        /// 重置设置
        /// </summary>
        public void ResetSettings()
        {
            var instanceId = MW?.PrefixSave ?? "";
            Settings = new Setting(ExtensionValue.BaseDirectory, instanceId);
            Settings.Save();
        }

        /// <summary>
        /// 更新动作处理器
        /// </summary>
        public void UpdateActionProcessor()
        {
            ActionProcessor?.RegisterHandlers();
        }

        // ============================================================================
        // 动画方法
        // ============================================================================

        /// <summary>
        /// 获取可用的动画列表
        /// </summary>
        public IEnumerable<string> GetAvailableAnimations()
        {
            return MW.Main.Core.Graph.GraphsList.Keys;
        }

        /// <summary>
        /// 获取可用的说话动画列表
        /// </summary>
        public IEnumerable<string> GetAvailableSayAnimations()
        {
            var animations = new HashSet<string>();

            if (MW.Main.Core.Graph.GraphsName.TryGetValue(VPet_Simulator.Core.GraphInfo.GraphType.Say, out var sayAnimations))
            {
                var modes = new[] { "happy", "nomal", "poorcondition", "ill" };

                foreach (var animName in sayAnimations)
                {
                    foreach (var mode in modes)
                    {
                        VPet_Simulator.Core.IGameSave.ModeType modeType;
                        switch (mode)
                        {
                            case "happy":
                                modeType = VPet_Simulator.Core.IGameSave.ModeType.Happy;
                                break;
                            case "poorcondition":
                                modeType = VPet_Simulator.Core.IGameSave.ModeType.PoorCondition;
                                break;
                            case "ill":
                                modeType = VPet_Simulator.Core.IGameSave.ModeType.Ill;
                                break;
                            default:
                                modeType = VPet_Simulator.Core.IGameSave.ModeType.Nomal;
                                break;
                        }

                        var graph = MW.Main.Core.Graph.FindGraph(animName, VPet_Simulator.Core.GraphInfo.AnimatType.A_Start, modeType);
                        if (graph is not null)
                        {
                            animations.Add($"{mode}_{animName}");
                        }
                    }

                    animations.Add(animName);
                }
            }

            return animations.OrderBy(a => a);
        }


        // ============================================================================
        // 悬浮侧边栏方法
        // ============================================================================

        /// <summary>
        /// 显示悬浮侧边栏
        /// </summary>
        public void ShowFloatingSidebar()
        {
            try
            {
                if (_floatingSidebarManager is null)
                {
                    InitializeFloatingSidebar();
                }
                _floatingSidebarManager?.Show();
                Settings.FloatingSidebar.IsEnabled = true;
                Settings.Save();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error showing sidebar: {ex.Message}");
            }
        }

        /// <summary>
        /// 隐藏悬浮侧边栏
        /// </summary>
        public void HideFloatingSidebar()
        {
            try
            {
                _floatingSidebarManager?.Hide();
                Settings.FloatingSidebar.IsEnabled = false;
                Settings.Save();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error hiding sidebar: {ex.Message}");
            }
        }

        /// <summary>
        /// 切换悬浮侧边栏显示状态
        /// </summary>
        public void ToggleFloatingSidebar()
        {
            try
            {
                if (_floatingSidebarManager?.IsVisible == true)
                {
                    HideFloatingSidebar();
                }
                else
                {
                    ShowFloatingSidebar();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error toggling sidebar: {ex.Message}");
            }
        }

        /// <summary>
        /// 刷新悬浮侧边栏
        /// </summary>
        public void RefreshFloatingSidebar()
        {
            try
            {
                _floatingSidebarManager?.RefreshButtons();
                _floatingSidebarManager?.ApplyConfiguration(Settings.FloatingSidebar);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error refreshing sidebar: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新悬浮侧边栏状态
        /// </summary>
        public void UpdateSidebarStatus(VPetLLMPlugin.UI.Controls.VPetLLMStatus status)
        {
            try
            {
                _floatingSidebarManager?.UpdateStatus(status);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error updating sidebar status: {ex.Message}");
            }
        }

        /// <summary>
        /// 设置侧边栏为处理请求状态
        /// </summary>
        public void SetSidebarProcessingStatus()
        {
            UpdateSidebarStatus(VPetLLMPlugin.UI.Controls.VPetLLMStatus.Processing);
        }

        /// <summary>
        /// 设置侧边栏为输出响应状态
        /// </summary>
        public void SetSidebarOutputtingStatus()
        {
            UpdateSidebarStatus(VPetLLMPlugin.UI.Controls.VPetLLMStatus.Outputting);
        }

        /// <summary>
        /// 设置侧边栏为错误状态
        /// </summary>
        public void SetSidebarErrorStatus()
        {
            UpdateSidebarStatus(VPetLLMPlugin.UI.Controls.VPetLLMStatus.Error);
        }

        /// <summary>
        /// 设置侧边栏为待机状态
        /// </summary>
        public void SetSidebarIdleStatus()
        {
            UpdateSidebarStatus(VPetLLMPlugin.UI.Controls.VPetLLMStatus.Idle);
        }

        /// <summary>
        /// 中断当前这一轮对话：取消在途的 LLM 请求，停掉还没播完的语音和还没输出完的气泡，
        /// 在历史里留下中断标记，并把状态归位到待机。
        ///
        /// 侧边栏状态按钮和输入框的中断按钮共用这一个入口 —— 中断的语义不该跟着入口走。
        /// </summary>
        /// <returns>true 表示确实中断了一轮进行中的对话</returns>
        public bool InterruptCurrentResponse()
        {
            try
            {
                if (!InterruptManager.Interrupt())
                {
                    Logger.Log("VPetLLM: 当前没有可中断的会话");
                    return false;
                }

                // 停止输出侧：思考动画、命令队列、气泡、TTS 播放
                try
                {
                    TalkBox?.AbortCurrentResponse();
                }
                catch (Exception ex)
                {
                    Logger.Log($"VPetLLM: 中断输出失败: {ex.Message}");
                }

                // 在历史里留下"这条回复被打断了"，模型下一轮据此纠正
                try
                {
                    ChatCore?.MarkLastResponseInterrupted();
                }
                catch (Exception ex)
                {
                    Logger.Log($"VPetLLM: 追加中断标记失败: {ex.Message}");
                }

                // 会话计数一并清零：被中断的那些 BeginActiveSession 不会再走到自己的
                // EndActiveSession，计数不清零的话下一轮的状态灯就再也回不到 Idle
                _floatingSidebarManager?.SetIdleStatus();

                Logger.Log("VPetLLM: 本轮对话已被用户中断");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetLLM: 中断会话失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 测试侧边栏状态灯功能
        /// </summary>
        public void TestSidebarStatusLight()
        {
            try
            {
                _floatingSidebarManager?.TestStatusLight();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error testing sidebar: {ex.Message}");
            }
        }

        // ============================================================================
        // 工具方法
        // ============================================================================

        /// <summary>
        /// 获取插件名称
        /// </summary>
        public override string PluginName => "VPetLLM";

        /// <summary>
        /// 日志记录方法（向后兼容）
        /// </summary>
        public void Log(string message)
        {
            Logger.Log(message);
            _logger.LogInformation(message);
        }

        /// <summary>
        /// 获取OCR引擎（向后兼容）
        /// </summary>
        public IOCREngine GetOCREngine()
        {
            // 返回OCR引擎实例
            try
            {
                return new OCREngine(Settings, this);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to create OCR engine: {ex.Message}");
                return null;
            }
        }

        #endregion
    }
}
