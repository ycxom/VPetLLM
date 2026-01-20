using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers.Animation;
using VPetLLM.Infrastructure.Configuration;
using VPetLLM.Infrastructure.DependencyInjection;
using VPetLLM.Infrastructure.Events;
using VPetLLM.Infrastructure.Logging;
using VPetLLM.Infrastructure.Services;
using VPetLLM.Infrastructure.Services.ApplicationServices;
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
    public class VPetLLM : MainPlugin
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
        /// VPet TTS 插件是否被检测到
        /// </summary>
        public bool IsVPetTTSPluginDetected => _vpetTTSPluginDetected;

        /// <summary>
        /// LLM 调用入口点（供插件和外部应用使用）
        /// </summary>
        public Core.LLMEntryPoint? LLMEntry { get; private set; }

        #endregion

        #region Private Fields

        private System.Timers.Timer _syncTimer;
        private IntelligentConfigurationOptimizer? _configurationOptimizer;
        private Infrastructure.Services.ApplicationServices.VoiceInputService? _voiceInputService;
        private Services.IScreenshotService? _screenshotService;
        private Infrastructure.Services.ApplicationServices.PurchaseService? _purchaseService;
        private Infrastructure.Services.ApplicationServices.MediaPlaybackService? _mediaPlaybackService;
        private DefaultPluginChecker? _defaultPluginChecker;
        private FloatingSidebarManager? _floatingSidebarManager;
        private bool _vpetTTSPluginDetected = false;
        private byte[]? _pendingImageData;

        // 用于追踪最近的 TakeItemHandle 事件
        private DateTime _lastTakeItemHandleTime = DateTime.MinValue;
        private readonly object _takeItemLock = new object();
        private const int TAKE_ITEM_WINDOW_MS = 100;

        // 已注册 Hook 的物品类型列表
        private readonly string[] _hookedItemTypes = { "Food", "Toy", "Tool", "Mail", "Item" };

        #endregion

        #region Constructor

        public VPetLLM(IMainWindow mainwin) : base(mainwin)
        {
            Instance = this;

            // 初始化日志
            Logger.Log("VPetLLM plugin constructor started (refactored version).");

            // 加载设置
            Settings = new Setting(ExtensionValue.BaseDirectory);
            Logger.Log("Settings loaded.");

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
                configTask.Wait();
                Logger.Log($"Free配置初始化完成: {configTask.Result}");

                // 初始化 Free ASR/TTS 认证委托
                InitializeFreeAuthProviders();
            }
            catch (Exception ex)
            {
                Logger.Log($"初始化Free配置失败: {ex.Message}");
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
                switch (Settings.Provider)
                {
                    case SettingClass.LLMType.Ollama:
                        ChatCore = new OllamaChatCore(Settings.Ollama, Settings, MW, ActionProcessor);
                        Logger.Log("Chat core set to Ollama.");
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
                }
                _logger.LogInformation($"ChatCore initialized: {ChatCore?.GetType().Name}");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to initialize ChatCore", ex);
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
            if (Settings.FollowVPetName)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Settings.AiName = MW.Core.Save.Name;
                    Settings.UserName = MW.Core.Save.HostName;
                });
            }
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
                // 初始化语音输入服务
                var asrConfig = new Infrastructure.Configuration.Configurations.ASRConfiguration();
                _voiceInputService = new Infrastructure.Services.ApplicationServices.VoiceInputService(this, asrConfig, _logger, _eventBus);
                _voiceInputService.TranscriptionCompleted += OnVoiceInputTranscriptionCompleted;
                Logger.Log("VoiceInputService initialized");

                // 初始化截图服务
                _screenshotService = new Services.ScreenshotService(this, Settings);
                _screenshotService.ScreenshotCaptured += OnScreenshotCaptured;
                _screenshotService.OCRCompleted += OnOCRCompleted;
                _screenshotService.ErrorOccurred += OnScreenshotError;
                if (_screenshotService is Services.ScreenshotService screenshotService)
                {
                    screenshotService.PreprocessingCompleted += OnPreprocessingCompleted;
                }
                Logger.Log("ScreenshotService initialized");

                // 初始化购买服务
                var purchaseConfig = new Infrastructure.Services.ApplicationServices.PurchaseConfiguration();
                _purchaseService = new Infrastructure.Services.ApplicationServices.PurchaseService(this, purchaseConfig, _logger, _eventBus);
                Logger.Log("PurchaseService initialized");

                // 初始化媒体播放服务
                InitializeMediaPlaybackService();

                _logger.LogInformation("Application services initialized");
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
                // 确定 mpv.exe 路径
                var mpvPath = Settings.MediaPlayback?.MpvPath;
                if (string.IsNullOrWhiteSpace(mpvPath))
                {
                    // 默认使用插件目录下的 mpv/mpv.exe（与 TTS 一致）
                    var dllPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    var mpvDir = Path.Combine(dllPath ?? "", "mpv");
                    mpvPath = Path.Combine(mpvDir, "mpv.exe");
                }

                // 始终创建 MediaPlaybackService（即使 mpv.exe 不存在，执行时会报错）
                var mediaConfig = new Infrastructure.Services.ApplicationServices.MediaPlaybackConfiguration
                {
                    MpvExePath = mpvPath,
                    MpvPath = mpvPath
                };
                _mediaPlaybackService = new Infrastructure.Services.ApplicationServices.MediaPlaybackService(mediaConfig, _logger, _eventBus);
                ActionProcessor?.SetMediaPlaybackService(_mediaPlaybackService as Services.IMediaPlaybackService);

                if (File.Exists(mpvPath))
                {
                    Logger.Log($"MediaPlaybackService initialized with mpv path: {mpvPath}");
                }
                else
                {
                    Logger.Log($"MediaPlaybackService initialized, but mpv.exe not found at {mpvPath}. Playback will fail until mpv.exe is available.");
                }
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

                // 检测 VPet.Plugin.VPetTTS 插件
                DetectAndHandleVPetTTSPlugin();

                // 初始化熔断器配置
                InitializeRateLimiter();

                // 加载聊天历史
                ChatCore?.LoadHistory();

                // 启动所有服务
                _serviceManager.StartAsync().Wait();

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

                Logger.Log("LoadPlugin finished.");
            }
            catch (Exception ex)
            {
                Logger.Log($"LoadPlugin failed: {ex.Message}");
                _logger.LogError("Failed to load plugin", ex);
            }
        }

        /// <summary>
        /// 检测并处理 VPet.Plugin.VPetTTS 插件
        /// </summary>
        private void DetectAndHandleVPetTTSPlugin()
        {
            try
            {
                // 检测所有其他 TTS 插件
                var allPluginsResult = TTSPluginDetector.DetectAllOtherTTSPlugins(MW);

                // 设置实时检测委托，每次调用 TTS 时都会检测插件状态
                TTSService?.SetVPetTTSPluginChecker(() => CheckAnyTTSPluginEnabled());

                // 执行一次初始检测并记录日志
                if (allPluginsResult.HasOtherEnabledTTSPlugin)
                {
                    Logger.Log($"检测到其他已启用的 TTS 插件 ({allPluginsResult.EnabledPluginNames})，内置 TTS 将自动避让");
                    _vpetTTSPluginDetected = true;
                }
                else
                {
                    Logger.Log("未检测到其他已启用的 TTS 插件，保持内置 TTS 功能");
                    _vpetTTSPluginDetected = false;
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
                _vpetTTSPluginDetected = false;
            }
        }

        /// <summary>
        /// 实时检测是否有任何 TTS 插件启用
        /// </summary>
        private bool CheckAnyTTSPluginEnabled()
        {
            try
            {
                var result = TTSPluginDetector.DetectAllOtherTTSPlugins(MW);
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
        /// 初始化动画协调器
        /// </summary>
        private void InitializeAnimationCoordinator()
        {
            try
            {
                Logger.Log("开始初始化AnimationCoordinator...");
                AnimationHelper.Initialize(MW);
                Logger.Log("AnimationCoordinator初始化成功");
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
                Logger.Log("开始初始化FloatingSidebar...");
                _floatingSidebarManager = new FloatingSidebarManager(this);

                // 如果设置中启用了悬浮侧边栏，则显示它
                if (Settings.FloatingSidebar.IsEnabled)
                {
                    _floatingSidebarManager.Show();
                }

                Logger.Log("FloatingSidebar初始化成功");
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
                Logger.Log("开始初始化TouchInteractionHandler...");

                // 检查必要的组件是否已准备好
                if (MW?.Main is null)
                {
                    Logger.Log("错误：MW.Main为null，无法初始化TouchInteractionHandler");
                    return;
                }

                Logger.Log("MW.Main已准备好，创建TouchInteractionHandler...");
                TouchInteractionHandler = new TouchInteractionHandler(this);
                Logger.Log("TouchInteractionHandler初始化成功");
            }
            catch (Exception ex)
            {
                Logger.Log($"初始化TouchInteractionHandler时发生错误: {ex.Message}");
                Logger.Log($"错误堆栈: {ex.StackTrace}");
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

            try
            {
                _configurationManager.SaveAllAsync().Wait();
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

        public void Dispose()
        {
            try
            {
                // 取消事件监听
                if (MW is not null)
                {
                    UnregisterTakeItemHandleEvent();
                    UnregisterItemUseHook();
                }

                // 清理服务
                _voiceInputService?.Dispose();
                _purchaseService?.Dispose();
                _floatingSidebarManager?.Dispose();
                TTSService?.Dispose();
                TouchInteractionHandler?.Dispose();

                // 停止定时器
                _syncTimer?.Stop();
                _syncTimer?.Dispose();

                // 停止所有服务
                _serviceManager.StopAsync().Wait();
                _serviceManager.Dispose();
                _container.Dispose();

                _logger.LogInformation("Plugin disposed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError("Error during disposal", ex);
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

        private void OnVoiceInputTranscriptionCompleted(object? sender, string transcription)
        {
            if (!string.IsNullOrWhiteSpace(transcription))
            {
                Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await SendChat(transcription);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error sending voice input: {ex.Message}");
                    }
                });
            }
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
                else if (processingMode == Configuration.ScreenshotProcessingMode.PreprocessingMultimodal)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ShowScreenshotEditorForPreprocessing(e.ImageData);
                    });
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
                        if (args.ImageData is not null && args.ImageData.Length > 0)
                        {
                            Logger.Log($"Sending screenshot with prompt: {args.Prompt}");

                            if (TalkBox is not null)
                            {
                                await TalkBox.SendChatWithImage(args.Prompt, args.ImageData);
                            }
                            else if (ChatCore is not null)
                            {
                                await ChatCore.ChatWithImage(args.Prompt, args.ImageData);
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
                                    await ChatCore.Chat(args.Prompt);
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
                        if (args.ImageData is not null && args.ImageData.Length > 0)
                        {
                            Logger.Log($"Processing screenshot with preprocessing");

                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                TalkBox?.DisplayThink();
                                TalkBox?.StartThinkingAnimation();
                            });

                            if (_screenshotService is Services.ScreenshotService screenshotService)
                            {
                                _pendingImageData = args.ImageData;
                                await screenshotService.ProcessWithPreprocessingAsync(args.ImageData, args.Prompt);
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
                                    await ChatCore.Chat(args.Prompt);
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
                                await ChatCore.Chat(e.CombinedMessage);
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
                            _ = ChatCore?.Chat(text);
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
                var response = await ChatCore.Chat(prompt);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to send chat message", ex);
                return $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// 更新聊天核心
        /// </summary>
        public void UpdateChatCore(IChatCore newChatCore)
        {
            ChatCore = newChatCore;
            Logger.Log($"ChatCore updated to: {ChatCore?.GetType().Name}");

            Logger.Log($"Chat history already loaded by HistoryManager (SeparateChatByProvider={Settings.SeparateChatByProvider})");

            Application.Current.Dispatcher.InvokeAsync(() =>
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
            }).Wait();

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
        /// 开始截图捕获
        /// </summary>
        public void StartScreenshotCapture()
        {
            _screenshotService?.StartCapture();
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
            Settings = new Setting(ExtensionValue.BaseDirectory);
            Settings.Save();
        }

        /// <summary>
        /// 更新系统消息
        /// </summary>
        public void UpdateSystemMessage()
        {
            // System message is fetched dynamically
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
