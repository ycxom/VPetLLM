using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core;
using VPetLLM.Core.ChatCore;
using VPetLLM.Handlers;
using VPetLLM.Handlers.Animation;
using VPetLLM.Services;
using VPetLLM.UI.Windows;
using VPetLLM.Utils;
using TalkBox = VPetLLM.UI.Windows.TalkBox;

namespace VPetLLM
{
    public class VPetLLM : MainPlugin
    {
        public static VPetLLM? Instance { get; private set; }
        public Setting Settings;
        public IChatCore? ChatCore;
        public TalkBox? TalkBox;
        public ActionProcessor? ActionProcessor;
        public TouchInteractionHandler? TouchInteractionHandler;
        private System.Timers.Timer _syncTimer;
        public List<IVPetLLMPlugin> Plugins => PluginManager.Plugins;
        public List<FailedPlugin> FailedPlugins => PluginManager.FailedPlugins;
        public string PluginPath => PluginManager.PluginPath;
        public winSettingNew? SettingWindow;
        public TTSService? TTSService;
        private IntelligentConfigurationOptimizer? _configurationOptimizer;
        
        // 服务实例
        private IVoiceInputService? _voiceInputService;
        private Services.IScreenshotService? _screenshotService;
        private IPurchaseService? _purchaseService;
        
        private DefaultPluginChecker? _defaultPluginChecker;
        private FloatingSidebarManager? _floatingSidebarManager;
        
        // VPet TTS 插件检测
        private bool _vpetTTSPluginDetected = false;
        
        /// <summary>
        /// 获取 VPet TTS 插件是否被检测到
        /// </summary>
        public bool IsVPetTTSPluginDetected => _vpetTTSPluginDetected;

        public VPetLLM(IMainWindow mainwin) : base(mainwin)
        {
            Instance = this;
            Utils.Logger.Log("VPetLLM plugin constructor started.");
            Settings = new Setting(ExtensionValue.BaseDirectory);
            Utils.Logger.Log("Settings loaded.");
            var dllPath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var langPath = Path.Combine(dllPath, "VPetLLM_lang", "Language.json");
            LanguageHelper.LoadLanguages(langPath);
            PromptHelper.LoadPrompts(langPath);
            if (string.IsNullOrEmpty(Settings.Language))
            {
                var culture = System.Globalization.CultureInfo.CurrentUICulture.Name.ToLower();
                if (LanguageHelper.LanguageDisplayMap.ContainsKey(culture))
                {
                    Settings.Language = culture;
                }
                else
                {
                    Settings.Language = "en";
                }
            }
            ActionProcessor = new ActionProcessor(mainwin);
            ActionProcessor.SetSettings(Settings);
            
            // 初始化Free服务配置（默认执行）
            try
            {
                Utils.Logger.Log("开始初始化Free配置...");
                // 清理未加密的配置文件
                Utils.FreeConfigCleaner.CleanUnencryptedConfigs();
                // 同步等待配置初始化完成
                var configTask = Utils.FreeConfigManager.InitializeConfigsAsync();
                configTask.Wait(); // 同步等待
                Utils.Logger.Log($"Free配置初始化完成: {configTask.Result}");
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"初始化Free配置失败: {ex.Message}");
            }
            
            // 初始化 Free ASR/TTS 认证委托
            InitializeFreeAuthProviders(mainwin);
            
            switch (Settings.Provider)
            {
                case global::VPetLLM.Setting.LLMType.Ollama:
                    ChatCore = new OllamaChatCore(Settings.Ollama, Settings, mainwin, ActionProcessor);
                    Utils.Logger.Log("Chat core set to Ollama.");
                    break;
                case global::VPetLLM.Setting.LLMType.OpenAI:
                    ChatCore = new OpenAIChatCore(Settings.OpenAI, Settings, mainwin, ActionProcessor);
                    Utils.Logger.Log("Chat core set to OpenAI.");
                    break;
                case global::VPetLLM.Setting.LLMType.Gemini:
                    ChatCore = new GeminiChatCore(Settings.Gemini, Settings, mainwin, ActionProcessor);
                    Utils.Logger.Log("Chat core set to Gemini.");
                    break;
                case global::VPetLLM.Setting.LLMType.Free:
                    ChatCore = new FreeChatCore(Settings.Free, Settings, mainwin, ActionProcessor);
                    Utils.Logger.Log("Chat core set to Free.");
                    break;
            }
            // 加载聊天历史记录
            Utils.Logger.Log("VPetLLM plugin constructor finished.");

            _syncTimer = new System.Timers.Timer(5000); // 5 seconds
            _syncTimer.Elapsed += SyncNames;
            _syncTimer.AutoReset = true;
            _syncTimer.Enabled = true;

            // 初始化TTS服务
            TTSService = new TTSService(Settings.TTS, Settings.Proxy);

            // 初始化配置优化管理器
            _configurationOptimizer = new IntelligentConfigurationOptimizer(Settings);
            
            // 执行配置优化
            _configurationOptimizer.PerformIntelligentOptimization();

            // 初始化服务
            InitializeServices();

            LoadPlugins();
            
            // 初始化默认插件检查器
            _defaultPluginChecker = new DefaultPluginChecker(this);
        }

        /// <summary>
        /// 初始化 Free ASR/TTS 认证委托
        /// </summary>
        private void InitializeFreeAuthProviders(IMainWindow mainwin)
        {
            try
            {
                // 设置获取 SteamID 的委托
                Func<ulong> getSteamId = () =>
                {
                    try { return mainwin?.SteamID ?? 0; } catch { return 0; }
                };

                // 设置获取 AuthKey 的委托
                Func<Task<int>> getAuthKey = async () =>
                {
                    try { return mainwin != null ? await mainwin.GenerateAuthKey() : 0; } catch { return 0; }
                };

                // 设置获取 ModId 的委托（从 VPet MOD 系统动态获取）
                Func<string> getModId = () =>
                {
                    try
                    {
                        var dllPath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                        if (string.IsNullOrEmpty(dllPath)) return "";
                        
                        foreach (var mod in mainwin.OnModInfo)
                        {
                            if (mod.Path != null && dllPath.StartsWith(mod.Path.FullName, StringComparison.OrdinalIgnoreCase))
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
                Utils.RequestSignatureHelper.Init(getSteamId, getAuthKey, getModId);

                Utils.Logger.Log("Free ASR/TTS 认证委托初始化完成");
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"初始化 Free ASR/TTS 认证委托失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 初始化服务
        /// </summary>
        private void InitializeServices()
        {
            try
            {
                // 初始化语音输入服务
                _voiceInputService = new VoiceInputService(this, Settings);
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
                _purchaseService = new PurchaseService(this, Settings);
                Logger.Log("PurchaseService initialized");
                
                // 初始化媒体播放服务
                InitializeMediaPlaybackService();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error initializing services: {ex.Message}");
            }
        }
        
        private IMediaPlaybackService? _mediaPlaybackService;
        
        /// <summary>
        /// 初始化媒体播放服务
        /// </summary>
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
                _mediaPlaybackService = new MediaPlaybackService(mpvPath, Settings.MediaPlayback ?? new Setting.MediaPlaybackSetting());
                ActionProcessor?.SetMediaPlaybackService(_mediaPlaybackService);
                
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

        /// <summary>
        /// 处理语音输入转录完成事件
        /// </summary>
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
                        Logger.Log($"Error sending voice input to chat: {ex.Message}");
                    }
                });
            }
        }

        private void InitializeVoiceInputHotkey()
        {
            // 委托给 VoiceInputService
            (_voiceInputService as VoiceInputService)?.InitializeHotkey();
        }

        private void InitializeScreenshotHotkey()
        {
            // 委托给 ScreenshotService
            (_screenshotService as Services.ScreenshotService)?.InitializeHotkey();
        }

        public void ShowVoiceInputWindow()
        {
            // 委托给 VoiceInputService
            _voiceInputService?.ShowVoiceInputWindow();
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

        private void OnScreenshotCaptured(object? sender, Services.ScreenshotCapturedEventArgs e)
        {
            try
            {
                Logger.Log($"Screenshot captured, size: {e.ImageData.Length} bytes");
                
                var processingMode = Settings.Screenshot.ProcessingMode;
                
                if (processingMode == Configuration.ScreenshotProcessingMode.NativeMultimodal)
                {
                    // 原生多模态模式：打开编辑窗口让用户预览和编辑，直接发送图片给主 LLM
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ShowScreenshotEditor(e.ImageData);
                    });
                }
                else if (processingMode == Configuration.ScreenshotProcessingMode.PreprocessingMultimodal)
                {
                    // 前置多模态模式：打开编辑窗口，使用前置多模态处理
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ShowScreenshotEditorForPreprocessing(e.ImageData);
                    });
                }
                // OCR 模式在 ScreenshotService 中处理
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling screenshot captured: {ex.Message}");
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
                        if (args.ImageData != null && args.ImageData.Length > 0)
                        {
                            Logger.Log($"Sending screenshot with prompt: {args.Prompt}");
                            
                            // 使用 TalkBox.SendChat 统一处理动画和消息发送
                            // TalkBox.SendChat 内部会自动管理思考动画
                            if (TalkBox != null)
                            {
                                await TalkBox.SendChatWithImage(args.Prompt, args.ImageData);
                            }
                            else if (ChatCore != null)
                            {
                                // 降级：直接调用 ChatCore
                                await ChatCore.ChatWithImage(args.Prompt, args.ImageData);
                            }
                            else
                            {
                                Logger.Log("ChatCore is null, cannot send multimodal message");
                            }
                        }
                        else
                        {
                            // 没有图片，只发送文本
                            if (!string.IsNullOrWhiteSpace(args.Prompt))
                            {
                                if (TalkBox != null)
                                {
                                    await TalkBox.SendChat(args.Prompt);
                                }
                                else if (ChatCore != null)
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
                Logger.Log("Screenshot editor window opened");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error showing screenshot editor: {ex.Message}");
            }
        }

        private byte[]? _pendingImageData;

        /// <summary>
        /// 显示截图编辑器（前置多模态模式）
        /// </summary>
        private void ShowScreenshotEditorForPreprocessing(byte[] imageData)
        {
            try
            {
                var editor = new UI.Windows.winScreenshotEditor(this, imageData);
                
                editor.SendRequested += async (s, args) =>
                {
                    try
                    {
                        if (args.ImageData != null && args.ImageData.Length > 0)
                        {
                            Logger.Log($"Processing screenshot with preprocessing multimodal, prompt: {args.Prompt}");
                            
                            // 启动思考动画（前置多模态需要等待两次 API 调用）
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                TalkBox?.DisplayThink();
                                TalkBox?.StartThinkingAnimation();
                            });
                            
                            // 使用前置多模态处理：先分析图片，再发送描述给主 LLM
                            if (_screenshotService is Services.ScreenshotService screenshotService)
                            {
                                _pendingImageData = args.ImageData;
                                await screenshotService.ProcessWithPreprocessingAsync(args.ImageData, args.Prompt);
                            }
                            else
                            {
                                Logger.Log("ScreenshotService is null, cannot process with preprocessing");
                                // 服务不可用时停止思考动画
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    TalkBox?.StopThinkingAnimationWithoutHide();
                                });
                            }
                        }
                        else
                        {
                            // 没有图片，只发送文本，使用 TalkBox.SendChat 统一处理
                            if (!string.IsNullOrWhiteSpace(args.Prompt))
                            {
                                if (TalkBox != null)
                                {
                                    await TalkBox.SendChat(args.Prompt);
                                }
                                else if (ChatCore != null)
                                {
                                    await ChatCore.Chat(args.Prompt);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error processing screenshot with preprocessing: {ex.Message}");
                        // 出错时停止思考动画
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            TalkBox?.StopThinkingAnimationWithoutHide();
                        });
                    }
                };

                editor.Cancelled += (s, args) =>
                {
                    Logger.Log("Screenshot editor cancelled (preprocessing mode)");
                };

                editor.Show();
                Logger.Log("Screenshot editor window opened (preprocessing mode)");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error showing screenshot editor for preprocessing: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理前置多模态处理完成事件
        /// </summary>
        private void OnPreprocessingCompleted(object? sender, Services.PreprocessingCompletedEventArgs e)
        {
            try
            {
                if (e.Success)
                {
                    Logger.Log($"Preprocessing completed successfully, provider: {e.UsedProvider}");
                    
                    // 将组合后的消息发送给主 LLM
                    // 注意：思考动画已在 ShowScreenshotEditorForPreprocessing 中启动
                    // ChatCore.Chat() 内部会通过 SmartMessageProcessor 处理响应，
                    // SmartMessageProcessor 会在处理完成后自动管理动画状态，
                    // 所以这里不需要手动停止思考动画
                    Application.Current.Dispatcher.Invoke(async () =>
                    {
                        try
                        {
                            if (ChatCore != null && !string.IsNullOrWhiteSpace(e.CombinedMessage))
                            {
                                await ChatCore.Chat(e.CombinedMessage);
                            }
                            // 注意：不在这里停止思考动画，由 SmartMessageProcessor 处理
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Error sending preprocessed message: {ex.Message}");
                            // 只有在发送失败时才停止思考动画
                            TalkBox?.StopThinkingAnimationWithoutHide();
                        }
                    });
                }
                else
                {
                    Logger.Log($"Preprocessing failed: {e.ErrorMessage}");
                    // 前置处理失败时，停止思考动画
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        TalkBox?.StopThinkingAnimationWithoutHide();
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling preprocessing completed: {ex.Message}");
                // 异常时也要停止思考动画
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
                Logger.Log($"OCR completed, text length: {text.Length}");
                
                // 将OCR文本插入到聊天输入
                Application.Current.Dispatcher.Invoke(() =>
                {
                    // 这里可以触发一个事件或直接发送消息
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        // 如果启用了自动发送，直接发送
                        if (Settings.Screenshot.AutoSend)
                        {
                            _ = ChatCore?.Chat(text);
                        }
                        else
                        {
                            // 否则显示文本供用户编辑
                            Logger.Log($"OCR text ready for user: {text.Substring(0, Math.Min(50, text.Length))}...");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling OCR completed: {ex.Message}");
            }
        }

        private void OnScreenshotError(object? sender, string error)
        {
            Logger.Log($"Screenshot error: {error}");
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

        public override void LoadPlugin()
        {
            Utils.Logger.Log("LoadPlugin started.");
            
            // 检测 VPet.Plugin.VPetTTS 插件
            DetectAndHandleVPetTTSPlugin();
            
            // 初始化熔断器配置
            InitializeRateLimiter();
            
            ChatCore?.LoadHistory();
            Application.Current.Dispatcher.Invoke(() =>
            {
                Utils.Logger.Log("Dispatcher.Invoke started.");
                if (TalkBox != null)
                {
                    MW.TalkAPI.Remove(TalkBox);
                }
                TalkBox = new TalkBox(this);
                MW.TalkAPI.Add(TalkBox);
                var menuItem = new MenuItem()
                {
                    Header = "VPetLLM",
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                menuItem.Click += (s, e) => Setting();
                MW.Main.ToolBar.MenuMODConfig.Items.Add(menuItem);
                
                // 在LoadPlugin阶段初始化TouchInteractionHandler，确保Main窗口已经完全加载
                InitializeTouchInteractionHandler();
                
                // 监听购买事件 - 使用TakeItemHandle以获取购买来源信息
                // 注意：Event_TakeItemHandle在MainWindow类中，不在IMainWindow接口中，需要使用反射
                RegisterTakeItemHandleEvent();
                Utils.Logger.Log("Purchase event listener registered.");
                
                // 注册物品使用监听 - 通过 Hook Item.UseAction 静态字典
                RegisterItemUseHook();
                Utils.Logger.Log("Item use hook registered.");
                
                // 初始化语音输入快捷键
                InitializeVoiceInputHotkey();
                
                // 初始化截图快捷键
                InitializeScreenshotHotkey();
                
                // 初始化默认插件状态检查器
                if (_defaultPluginChecker != null)
                {
                    _defaultPluginChecker.IsVPetLLMDefaultPlugin();
                    // 如果设置窗口已经打开，刷新窗口标题
                    _defaultPluginChecker.RefreshWindowTitle();
                }
                
                // 初始化动画协调器
                InitializeAnimationCoordinator();
                
                // 初始化悬浮侧边栏
                InitializeFloatingSidebar();
                
                Utils.Logger.Log("Dispatcher.Invoke finished.");
            });
            Utils.Logger.Log("LoadPlugin finished.");
        }

        /// <summary>
        /// 检测并处理 VPet.Plugin.VPetTTS 插件
        /// 恢复原始的避让机制：VPetLLM内置TTS自动避让VPetTTS插件
        /// </summary>
        private void DetectAndHandleVPetTTSPlugin()
        {
            try
            {
                // 检测所有其他 TTS 插件
                var allPluginsResult = TTSPluginDetector.DetectAllOtherTTSPlugins(MW);

                // 设置实时检测委托，每次调用 TTS 时都会检测插件状态
                // 这是原始的避让机制：VPetLLM内置TTS检测到VPetTTS时自动跳过
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
        /// 这是原始的避让机制：VPetLLM内置TTS检测到其他TTS插件时自动跳过
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
        /// 禁用内置 TTS 功能（保留用于兼容性）
        /// </summary>
        private void DisableBuiltInTTS()
        {
            // 现在使用实时检测，此方法保留用于兼容性
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

        public override void Save()
        {
            Settings.Save();
            ChatCore?.SaveHistory();
            SavePluginStates();
        }

        public void Dispose()
        {
            // 取消事件监听
            if (MW != null)
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
            _syncTimer?.Stop();
            _syncTimer?.Dispose();
        }

        public void UpdateVoiceInputHotkey()
        {
            // 委托给 VoiceInputService
            _voiceInputService?.UpdateHotkey();
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
                    Utils.Logger.Log("Purchase event: VPetLLM不是默认插件，忽略购买事件");
                    return;
                }

                Utils.Logger.Log($"Purchase event detected: {food?.Name ?? "Unknown"}, count: {count}, from: {from}");
                
                if (MW == null)
                {
                    Utils.Logger.Log("Purchase event: MW is null, skipping");
                    return;
                }

                // 委托给 PurchaseService 处理
                _purchaseService?.HandlePurchase(food, count, from);
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Error handling purchase event: {ex.Message}");
                Utils.Logger.Log($"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 使用反射注册Event_TakeItemHandle事件
        /// </summary>
        private void RegisterTakeItemHandleEvent()
        {
            try
            {
                // Event_TakeItemHandle在MainWindow类中，不在IMainWindow接口中
                var eventInfo = MW.GetType().GetEvent("Event_TakeItemHandle");
                if (eventInfo != null)
                {
                    var handler = new Action<Food, int, string>(OnTakeItemHandle);
                    eventInfo.AddEventHandler(MW, handler);
                    Utils.Logger.Log("Successfully registered Event_TakeItemHandle using reflection");
                }
                else
                {
                    Utils.Logger.Log("Warning: Event_TakeItemHandle not found in MainWindow, falling back to Event_TakeItem");
                    // 降级方案：使用Event_TakeItem（不包含来源信息）
                    MW.Event_TakeItem += OnTakeItemFallback;
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Error registering Event_TakeItemHandle: {ex.Message}");
                Utils.Logger.Log("Falling back to Event_TakeItem");
                MW.Event_TakeItem += OnTakeItemFallback;
            }
        }

        /// <summary>
        /// 使用反射注销Event_TakeItemHandle事件
        /// </summary>
        private void UnregisterTakeItemHandleEvent()
        {
            try
            {
                var eventInfo = MW.GetType().GetEvent("Event_TakeItemHandle");
                if (eventInfo != null)
                {
                    var handler = new Action<Food, int, string>(OnTakeItemHandle);
                    eventInfo.RemoveEventHandler(MW, handler);
                    Utils.Logger.Log("Successfully unregistered Event_TakeItemHandle");
                }
                else
                {
                    MW.Event_TakeItem -= OnTakeItemFallback;
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Error unregistering Event_TakeItemHandle: {ex.Message}");
                MW.Event_TakeItem -= OnTakeItemFallback;
            }
        }

        /// <summary>
        /// 降级方案：处理Event_TakeItem事件（不包含来源信息）
        /// </summary>
        private void OnTakeItemFallback(Food food)
        {
            // 没有来源信息，假设为用户手动购买
            Utils.Logger.Log("Using fallback Event_TakeItem (no source information available)");
            OnTakeItemHandle(food, 1, "unknown");
        }

        /// <summary>
        /// 注册物品使用监听处理器
        /// 通过 Item.UseAction 注册自定义处理器来监听物品使用事件
        /// </summary>
        private void RegisterItemUseHandler()
        {
            // 此方法已弃用，使用 RegisterItemUseHook 代替
        }

        /// <summary>
        /// 注销物品使用监听处理器（已弃用）
        /// </summary>
        private void UnregisterItemUseHandler()
        {
            // 此方法已弃用
        }

        // 用于追踪最近的 TakeItemHandle 事件
        private DateTime _lastTakeItemHandleTime = DateTime.MinValue;
        private string _lastTakeItemHandleSource = null;
        private string _lastTakeItemHandleName = null;
        private readonly object _takeItemLock = new object();
        private const int TAKE_ITEM_WINDOW_MS = 100; // 100ms 时间窗口

        // 已注册 Hook 的物品类型列表
        private readonly string[] _hookedItemTypes = { "Food", "Toy", "Tool", "Mail", "Item" };

        /// <summary>
        /// 注册物品使用 Hook - 通过 Hook Item.UseAction 静态字典
        /// 用于监听用户从物品栏使用物品的事件
        /// 
        /// 注意：Item.UseAction 在 NuGet 包 1.1.0.58 中不存在，此功能暂时禁用
        /// 
        /// 技术背景：
        /// - Item.UseAction 是 VPet 源码中的静态字典 (Dictionary&lt;string, List&lt;Func&lt;Item, bool&gt;&gt;&gt;)
        /// - 当用户在物品栏点击使用物品时，Item.Use() 方法会遍历 UseAction 中的处理器
        /// - 通过向 UseAction 注册自定义处理器，可以监听所有物品使用事件
        /// - 但此 API 在当前 NuGet 包版本中不可用
        /// 
        /// 等待条件：VPet 发布包含 Item.UseAction 的新 NuGet 包版本
        /// 相关源码位置：VPet/VPet-Simulator.Windows.Interface/Mod/Item.cs
        /// </summary>
        private void RegisterItemUseHook()
        {
            // TODO: 等待 VPet 更新 NuGet 包后启用此功能
            // Item.UseAction 在 NuGet 包 1.1.0.58 中不存在
            // 此功能需要等待 VPet 发布包含 UseAction 的新版本
            Utils.Logger.Log("Item use hook is disabled: Item.UseAction not available in NuGet package 1.1.0.58");
            Utils.Logger.Log("To enable this feature, wait for VPet to release a new NuGet package with UseAction support");
            
            // 启用后的实现方案：
            // foreach (var itemType in _hookedItemTypes)
            // {
            //     if (!Item.UseAction.ContainsKey(itemType))
            //         Item.UseAction[itemType] = new List<Func<Item, bool>>();
            //     Item.UseAction[itemType].Insert(0, OnItemUseHookFunc);
            // }
        }

        /// <summary>
        /// 注销物品使用 Hook
        /// </summary>
        private void UnregisterItemUseHook()
        {
            // 功能已禁用，无需注销
        }

        /// <summary>
        /// 物品使用 Hook 处理器（Action 类型，无返回值）
        /// </summary>
        private void OnItemUseHookAction(Item item)
        {
            OnItemUseHookInternal(item);
        }
        
        /// <summary>
        /// 物品使用 Hook 处理器（Func 类型，返回 bool）
        /// </summary>
        private bool OnItemUseHookFunc(Item item)
        {
            OnItemUseHookInternal(item);
            return false; // 返回 false 让其他处理器继续执行
        }
        
        /// <summary>
        /// 物品使用 Hook 内部处理逻辑
        /// </summary>
        private void OnItemUseHookInternal(Item item)
        {
            try
            {
                // 检查是否为默认插件
                if (!IsVPetLLMDefaultPlugin())
                {
                    return; // 不是默认插件，直接返回
                }
                
                Utils.Logger.Log($"Item use detected via hook: {item.Name} (type: {item.ItemType}, count: {item.Count})");
                
                // 检查是否在时间窗口内有 TakeItemHandle 事件（区分购买和使用）
                bool isFromPurchase = false;
                lock (_takeItemLock)
                {
                    var timeSinceLastHandle = (DateTime.Now - _lastTakeItemHandleTime).TotalMilliseconds;
                    
                    // 如果在时间窗口内有 TakeItemHandle 事件，且物品名称匹配
                    if (timeSinceLastHandle < TAKE_ITEM_WINDOW_MS && 
                        _lastTakeItemHandleName == item.Name)
                    {
                        // 这是商店购买，不是物品栏使用
                        isFromPurchase = true;
                        Utils.Logger.Log($"OnItemUseHook: {item.Name} - matched TakeItemHandle (source: {_lastTakeItemHandleSource}), treating as purchase");
                    }
                }
                
                // 只处理物品栏使用，不处理购买
                if (!isFromPurchase)
                {
                    // 通知 AI 物品被使用
                    // 使用 Task.Run 在后台线程处理，避免阻塞 UI 线程
                    var itemName = item.Name;
                    var food = item as Food;
                    
                    Task.Run(() =>
                    {
                        try
                        {
                            if (_purchaseService != null && food != null)
                            {
                                // 使用 "use" 来源标记物品使用
                                _purchaseService.HandlePurchase(food, 1, "use");
                            }
                            else if (food == null)
                            {
                                Utils.Logger.Log($"OnItemUseHook: Item {itemName} is not a Food, skipping purchase service");
                            }
                        }
                        catch (Exception ex)
                        {
                            Utils.Logger.Log($"Error in OnItemUseHook background task: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Error in OnItemUseHook: {ex.Message}");
            }
        }

        /// <summary>
        /// 注册物品使用事件监听器（已弃用，保留兼容）
        /// 通过 Event_TakeItem 监听物品使用，并使用时间窗口区分来源
        /// </summary>
        private void RegisterItemUseEventHandler()
        {
            // 此方法已弃用，使用 RegisterItemUseHook 代替
            Utils.Logger.Log("RegisterItemUseEventHandler is deprecated, use RegisterItemUseHook instead");
        }

        /// <summary>
        /// 注销物品使用事件监听器（已弃用，保留兼容）
        /// </summary>
        private void UnregisterItemUseEventHandler()
        {
            // 此方法已弃用，使用 UnregisterItemUseHook 代替
            Utils.Logger.Log("UnregisterItemUseEventHandler is deprecated, use UnregisterItemUseHook instead");
        }

        /// <summary>
        /// Event_TakeItem 事件处理器（已弃用）
        /// 检测物品使用来源（物品栏 vs 商店购买）
        /// </summary>
        private void OnEventTakeItem(Food food)
        {
            // 此方法已弃用，使用 OnItemUseHook 代替
        }

        /// <summary>
        /// 物品使用事件处理器（Func 类型，返回 bool）- 已弃用
        /// </summary>
        private bool OnItemUseFuncHandler(Item item)
        {
            return false;
        }

        /// <summary>
        /// 物品使用事件处理器（Action 类型，无返回值）- 已弃用
        /// </summary>
        private void OnItemUseActionHandler(Item item)
        {
        }

        /// <summary>
        /// 物品使用事件内部处理逻辑 - 已弃用
        /// </summary>
        private void OnItemUseInternal(Item item)
        {
        }

        public void UpdateChatCore(IChatCore newChatCore)
        {
            // 首先更新ChatCore字段
            ChatCore = newChatCore;
            Utils.Logger.Log($"ChatCore updated to: {ChatCore?.GetType().Name}");

            // 注意：不需要手动调用LoadHistory()，因为HistoryManager在构造时已经自动加载了历史
            // 历史记录会根据SeparateChatByProvider设置自动加载：
            // - false: 加载所有提供商的历史（聚合模式）
            // - true: 只加载当前提供商的历史（分离模式）
            Utils.Logger.Log($"Chat history already loaded by HistoryManager (SeparateChatByProvider={Settings.SeparateChatByProvider})");

            // 重新创建TalkBox以确保使用新的ChatCore实例
            // 使用InvokeAsync并等待完成，确保热重载生效
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (TalkBox != null)
                {
                    MW.TalkAPI.Remove(TalkBox);
                    Utils.Logger.Log("Old TalkBox removed from TalkAPI");
                }
                TalkBox = new TalkBox(this);
                MW.TalkAPI.Add(TalkBox);
                Utils.Logger.Log("New TalkBox added to TalkAPI");

                // 验证新的TalkBox是否使用正确的ChatCore实例
                Utils.Logger.Log($"New TalkBox should use ChatCore: {ChatCore?.GetType().Name}");
            }).Wait(); // 等待操作完成
        }

        public override void Setting()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var settingWindow = new winSettingNew(this);
                settingWindow.Show();
            });
        }

        public void RefreshPluginList()
        {
            SettingWindow?.RefreshPluginList();
        }

        /// <summary>
        /// 执行智能配置优化
        /// </summary>
        public void PerformConfigurationOptimization()
        {
            if (_configurationOptimizer == null)
            {
                _configurationOptimizer = new IntelligentConfigurationOptimizer(Settings);
            }

            _configurationOptimizer.PerformIntelligentOptimization();
            Settings.Save(); // 确保优化结果被保存
        }

        /// <summary>
        /// 获取配置健康报告
        /// </summary>
        public string GetConfigurationHealthReport()
        {
            if (_configurationOptimizer == null)
            {
                _configurationOptimizer = new IntelligentConfigurationOptimizer(Settings);
            }

            return _configurationOptimizer.GetIntelligentHealthReport();
        }

        public void ResetSettings()
        {
            Settings = new Setting(ExtensionValue.BaseDirectory);
            Settings.Save();
        }


        public override string PluginName => "VPetLLM";


        public async Task<string> SendChat(string prompt)
        {
            if (ChatCore == null)
            {
                return "错误：聊天核心未初始化。";
            }
            PromptHelper.ReloadPrompts();
            var response = await ChatCore.Chat(prompt);

            // 注意：TTS和动作处理现在由SmartMessageProcessor在HandleResponse中统一处理
            // 这里只返回原始回复，不再单独处理TTS

            return response;
        }

        public List<Message> GetChatHistory()
        {
            return ChatCore?.GetChatHistory() ?? new List<Message>();
        }

        public void SetChatHistory(List<Message> history)
        {
            ChatCore?.SetChatHistory(history);
        }

        public void ClearChatHistory()
        {
            ChatCore?.ClearContext();
        }
        // 测试方法：验证当前ChatCore实例类型
        public string GetCurrentChatCoreInfo()
        {
            return $"ChatCore Type: {ChatCore?.GetType().Name}, Hash: {ChatCore?.GetHashCode()}";
        }

        public void LoadPlugins()
        {
            PluginManager.LoadPlugins(this.ChatCore);
        }

        public async Task<bool> UpdatePlugin(string pluginFilePath)
        {
            return await PluginManager.UpdatePlugin(pluginFilePath, this.ChatCore);
        }

        public void SavePluginStates()
        {
            PluginManager.SavePluginStates();
        }

        public async Task<bool> UnloadAndTryDeletePlugin(IVPetLLMPlugin plugin)
        {
            return await PluginManager.UnloadAndTryDeletePlugin(plugin, this.ChatCore);
        }

        public void ImportPlugin(string filePath)
        {
            PluginManager.ImportPlugin(filePath);
            LoadPlugins();
            RefreshPluginList();
        }

        public async Task<bool> DeletePluginFile(string pluginFilePath)
        {
            return await PluginManager.DeletePluginFile(pluginFilePath);
        }

        public async Task<bool> DeletePluginByName(string pluginName)
        {
            return await PluginManager.DeletePluginByName(pluginName);
        }

        public void Log(string message)
        {
            Logger.Log(message);
        }

        public void UpdateSystemMessage()
        {
            if (ChatCore != null)
            {
                // The system message is fetched dynamically, so we just need to ensure the chat core is aware of changes.
                // This can be a no-op if the system message is always fetched fresh,
                // but it's good practice to have a method for explicit updates.
            }
        }

        public void UpdateActionProcessor()
        {
            ActionProcessor?.RegisterHandlers();
        }

        public void UpdateTTSService()
        {
            TTSService?.UpdateSettings(Settings.TTS, Settings.Proxy);
            // Logger.Log("TTS服务设置已更新");
        }

        /// <summary>
        /// 初始化熔断器配置
        /// </summary>
        private void InitializeRateLimiter()
        {
            try
            {
                Logger.Log("开始初始化RateLimiter配置...");
                
                // 配置Tool限流
                Utils.RateLimiter.SetConfig("tool", new Utils.RateLimiter.RateLimitConfig
                {
                    MaxCount = Settings.RateLimiter.ToolMaxCount,
                    Window = TimeSpan.FromMinutes(Settings.RateLimiter.ToolWindowMinutes),
                    Enabled = Settings.RateLimiter.EnableToolRateLimit,
                    Description = "Tool调用限流"
                });
                
                // 配置Plugin限流
                Utils.RateLimiter.SetConfig("ai-plugin", new Utils.RateLimiter.RateLimitConfig
                {
                    MaxCount = Settings.RateLimiter.PluginMaxCount,
                    Window = TimeSpan.FromMinutes(Settings.RateLimiter.PluginWindowMinutes),
                    Enabled = Settings.RateLimiter.EnablePluginRateLimit,
                    Description = "Plugin调用限流"
                });
                
                Logger.Log($"RateLimiter配置完成 - Tool: {(Settings.RateLimiter.EnableToolRateLimit ? "启用" : "禁用")} ({Settings.RateLimiter.ToolMaxCount}次/{Settings.RateLimiter.ToolWindowMinutes}分钟), Plugin: {(Settings.RateLimiter.EnablePluginRateLimit ? "启用" : "禁用")} ({Settings.RateLimiter.PluginMaxCount}次/{Settings.RateLimiter.PluginWindowMinutes}分钟)");
            }
            catch (Exception ex)
            {
                Logger.Log($"初始化RateLimiter时发生错误: {ex.Message}");
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
                if (MW?.Main == null)
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

        public async Task PlayTTSAsync(string text)
        {
            if (Settings.TTS.IsEnabled && TTSService != null && !string.IsNullOrWhiteSpace(text))
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

        public IEnumerable<string> GetAvailableAnimations()
        {
            return MW.Main.Core.Graph.GraphsList.Keys;
        }
        
        /// <summary>
        /// 获取可用的说话动画列表（状态模式+动画名称组合）
        /// 例如：happy_say, nomal_say, ill_shy等
        /// </summary>
        public IEnumerable<string> GetAvailableSayAnimations()
        {
            var animations = new HashSet<string>();
            
            if (MW.Main.Core.Graph.GraphsName.TryGetValue(VPet_Simulator.Core.GraphInfo.GraphType.Say, out var sayAnimations))
            {
                // 获取所有状态模式
                var modes = new[] { "happy", "nomal", "poorcondition", "ill" };
                
                foreach (var animName in sayAnimations)
                {
                    foreach (var mode in modes)
                    {
                        // 检查该状态模式下是否存在该动画
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
                        
                        // 检查该动画在该状态下是否存在
                        var graph = MW.Main.Core.Graph.FindGraph(animName, VPet_Simulator.Core.GraphInfo.AnimatType.A_Start, modeType);
                        if (graph != null)
                        {
                            // 添加"状态_动画"组合
                            animations.Add($"{mode}_{animName}");
                        }
                    }
                    
                    // 同时也添加不带状态前缀的动画名（使用当前状态）
                    animations.Add(animName);
                }
            }
            
            return animations.OrderBy(a => a);
        }

        /// <summary>
        /// 检查VPetLLM是否为默认插件
        /// </summary>
        /// <returns>如果VPetLLM是默认插件返回true，否则返回false</returns>
        public bool IsVPetLLMDefaultPlugin()
        {
            return _defaultPluginChecker?.IsVPetLLMDefaultPlugin() ?? false;
        }

        /// <summary>
        /// 显示悬浮侧边栏
        /// </summary>
        public void ShowFloatingSidebar()
        {
            try
            {
                if (_floatingSidebarManager == null)
                {
                    InitializeFloatingSidebar();
                }
                _floatingSidebarManager?.Show();
                Settings.FloatingSidebar.IsEnabled = true;
                Settings.Save();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error showing floating sidebar: {ex.Message}");
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
                Logger.Log($"Error hiding floating sidebar: {ex.Message}");
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
                Logger.Log($"Error toggling floating sidebar: {ex.Message}");
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
                Logger.Log($"Error refreshing floating sidebar: {ex.Message}");
            }
        }
    }
}