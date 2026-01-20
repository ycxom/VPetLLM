using System.Windows;

namespace VPetLLM.Infrastructure.Services.ApplicationServices
{
    /// <summary>
    /// 截图服务 - 使用新架构重构
    /// </summary>
    public class ScreenshotService : ServiceBase<ScreenshotConfiguration>
    {
        private readonly VPetLLM _plugin;
        private readonly IPreprocessingMultimodal _preprocessingMultimodal;
        private GlobalHotkey? _screenshotHotkey;
        private UI.Windows.winScreenshotCapture? _captureWindow;
        private ScreenshotState _currentState = ScreenshotState.Idle;
        private byte[]? _currentImage;
        private const int SCREENSHOT_HOTKEY_ID = 9002;

        public override string ServiceName => "ScreenshotService";
        public override Version Version => new Version(2, 0, 0);

        /// <summary>
        /// 当前截图状态
        /// </summary>
        public ScreenshotState CurrentState => _currentState;

        /// <summary>
        /// 当前图片数据
        /// </summary>
        public byte[]? CurrentImage => _currentImage;

        /// <summary>
        /// 截图捕获完成事件
        /// </summary>
        public event EventHandler<ScreenshotCapturedEventArgs>? ScreenshotCaptured;

        /// <summary>
        /// OCR完成事件
        /// </summary>
        public event EventHandler<string>? OCRCompleted;

        /// <summary>
        /// 状态变更事件
        /// </summary>
        public event EventHandler<ScreenshotState>? StateChanged;

        /// <summary>
        /// 错误发生事件
        /// </summary>
        public event EventHandler<string>? ErrorOccurred;

        /// <summary>
        /// 前置多模态处理完成事件
        /// </summary>
        public event EventHandler<PreprocessingCompletedEventArgs>? PreprocessingCompleted;

        public ScreenshotService(
            VPetLLM plugin,
            ScreenshotConfiguration configuration,
            IStructuredLogger logger,
            IEventBus eventBus)
            : base(configuration, logger, eventBus)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _preprocessingMultimodal = new PreprocessingMultimodal(_plugin.Settings, _plugin);
        }

        protected override async Task OnInitializeAsync(CancellationToken cancellationToken)
        {
            LogInformation("Initializing screenshot service");

            // 订阅配置变更事件
            await _eventBus.SubscribeAsync<ConfigurationChangedEvent<ScreenshotConfiguration>>(OnConfigurationChanged);

            LogInformation("Screenshot service initialized successfully");
        }

        protected override async Task OnStartAsync(CancellationToken cancellationToken)
        {
            LogInformation("Starting screenshot service");

            // 注册截图热键
            if (Configuration.EnableScreenshotHotkey && !string.IsNullOrEmpty(Configuration.ScreenshotHotkey))
            {
                RegisterScreenshotHotkey();
            }

            await Task.CompletedTask;
            LogInformation("Screenshot service started successfully");
        }

        protected override async Task OnStopAsync(CancellationToken cancellationToken)
        {
            LogInformation("Stopping screenshot service");

            // 注销热键
            UnregisterScreenshotHotkey();

            // 清理窗口
            if (_captureWindow is not null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _captureWindow.Close();
                    _captureWindow = null;
                });
            }

            // 清理状态
            _currentImage = null;
            ChangeState(ScreenshotState.Idle);

            await Task.CompletedTask;
            LogInformation("Screenshot service stopped successfully");
        }

        protected override async Task OnHealthCheckAsync(CancellationToken cancellationToken)
        {
            // 检查热键是否正常注册
            if (Configuration.EnableScreenshotHotkey && _screenshotHotkey is null)
            {
                LogWarning("Screenshot hotkey is not registered");
            }

            await Task.CompletedTask;
        }

        private async Task OnConfigurationChanged(ConfigurationChangedEvent<ScreenshotConfiguration> evt)
        {
            LogInformation("Screenshot configuration changed, reloading");

            // 重新注册热键
            UnregisterScreenshotHotkey();
            if (evt.NewConfiguration.EnableScreenshotHotkey && !string.IsNullOrEmpty(evt.NewConfiguration.ScreenshotHotkey))
            {
                RegisterScreenshotHotkey();
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// 开始截图捕获
        /// </summary>
        public async Task<bool> StartCaptureAsync()
        {
            try
            {
                if (_currentState != ScreenshotState.Idle)
                {
                    LogWarning("Cannot start capture: service is not idle");
                    return false;
                }

                ChangeState(ScreenshotState.Capturing);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _captureWindow = new UI.Windows.winScreenshotCapture();
                    _captureWindow.ScreenshotCaptured += OnScreenshotCapturedInternal;
                    _captureWindow.CaptureCancelled += OnCaptureCancelled;
                    _captureWindow.Show();
                });

                return true;
            }
            catch (Exception ex)
            {
                LogError("Failed to start screenshot capture", ex);
                ChangeState(ScreenshotState.Error);
                ErrorOccurred?.Invoke(this, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 执行OCR识别
        /// </summary>
        public async Task<string?> PerformOCRAsync(byte[] imageData)
        {
            try
            {
                if (_currentState != ScreenshotState.Captured)
                {
                    LogWarning("Cannot perform OCR: no screenshot captured");
                    return null;
                }

                ChangeState(ScreenshotState.Processing);

                var ocrEngine = _plugin.GetOCREngine();
                if (ocrEngine is null)
                {
                    LogError("OCR engine not available");
                    ChangeState(ScreenshotState.Error);
                    ErrorOccurred?.Invoke(this, "OCR引擎未配置");
                    return null;
                }

                var result = await ocrEngine.RecognizeText(imageData);

                ChangeState(ScreenshotState.Completed);
                OCRCompleted?.Invoke(this, result);

                await _eventBus.PublishAsync(new ScreenshotOCRCompletedEvent
                {
                    Text = result,
                    ImageData = imageData,
                    Timestamp = DateTime.Now
                });

                return result;
            }
            catch (Exception ex)
            {
                LogError("OCR processing failed", ex);
                ChangeState(ScreenshotState.Error);
                ErrorOccurred?.Invoke(this, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 执行前置多模态处理
        /// </summary>
        public async Task<bool> PerformPreprocessingAsync(byte[] imageData)
        {
            try
            {
                if (_currentState != ScreenshotState.Captured)
                {
                    LogWarning("Cannot perform preprocessing: no screenshot captured");
                    return false;
                }

                ChangeState(ScreenshotState.Processing);

                var result = await _preprocessingMultimodal.AnalyzeImageAsync(imageData);

                ChangeState(ScreenshotState.Completed);
                PreprocessingCompleted?.Invoke(this, new PreprocessingCompletedEventArgs
                {
                    Success = result.Success,
                    ImageData = imageData
                });

                await _eventBus.PublishAsync(new ScreenshotPreprocessingCompletedEvent
                {
                    Success = result.Success,
                    ImageData = imageData,
                    Timestamp = DateTime.Now
                });

                return result.Success;
            }
            catch (Exception ex)
            {
                LogError("Preprocessing failed", ex);
                ChangeState(ScreenshotState.Error);
                ErrorOccurred?.Invoke(this, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 清除当前截图
        /// </summary>
        public void ClearCurrentScreenshot()
        {
            _currentImage = null;
            ChangeState(ScreenshotState.Idle);
            LogInformation("Current screenshot cleared");
        }

        private void RegisterScreenshotHotkey()
        {
            try
            {
                UnregisterScreenshotHotkey();

                _screenshotHotkey = new GlobalHotkey(
                    IntPtr.Zero, // 使用默认窗口句柄
                    SCREENSHOT_HOTKEY_ID);

                // 解析热键字符串并注册
                var parts = Configuration.ScreenshotHotkey.Split('+');
                uint modifiers = 0;
                uint key = 0;

                foreach (var part in parts)
                {
                    var trimmed = part.Trim().ToLower();
                    switch (trimmed)
                    {
                        case "ctrl":
                        case "control":
                            modifiers |= GlobalHotkey.MOD_CONTROL;
                            break;
                        case "shift":
                            modifiers |= GlobalHotkey.MOD_SHIFT;
                            break;
                        case "alt":
                            modifiers |= GlobalHotkey.MOD_ALT;
                            break;
                        case "win":
                        case "windows":
                            modifiers |= GlobalHotkey.MOD_WIN;
                            break;
                        default:
                            key = GlobalHotkey.ParseKey(trimmed);
                            break;
                    }
                }

                if (key != 0)
                {
                    _screenshotHotkey.HotkeyPressed += (s, e) => OnScreenshotHotkeyPressed();
                    _screenshotHotkey.Register(modifiers, key);
                }

                LogInformation($"Screenshot hotkey registered: {Configuration.ScreenshotHotkey}");
            }
            catch (Exception ex)
            {
                LogError("Failed to register screenshot hotkey", ex);
            }
        }

        private void UnregisterScreenshotHotkey()
        {
            if (_screenshotHotkey is not null)
            {
                _screenshotHotkey.Dispose();
                _screenshotHotkey = null;
                LogInformation("Screenshot hotkey unregistered");
            }
        }

        private async void OnScreenshotHotkeyPressed()
        {
            LogInformation("Screenshot hotkey pressed");
            await StartCaptureAsync();
        }

        private async void OnScreenshotCapturedInternal(object? sender, byte[] imageData)
        {
            try
            {
                _currentImage = imageData;
                ChangeState(ScreenshotState.Captured);

                ScreenshotCaptured?.Invoke(this, new ScreenshotCapturedEventArgs
                {
                    ImageData = imageData,
                    Timestamp = DateTime.Now
                });

                await _eventBus.PublishAsync(new Infrastructure.Events.ScreenshotCapturedEvent
                {
                    Screenshot = ConvertBytesToBitmap(imageData),
                    CapturedAt = DateTime.Now
                });

                // 关闭捕获窗口
                if (_captureWindow is not null)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _captureWindow.Close();
                        _captureWindow = null;
                    });
                }
            }
            catch (Exception ex)
            {
                LogError("Failed to process captured screenshot", ex);
                ChangeState(ScreenshotState.Error);
                ErrorOccurred?.Invoke(this, ex.Message);
            }
        }

        private void OnCaptureCancelled(object? sender, EventArgs e)
        {
            LogInformation("Screenshot capture cancelled");
            ChangeState(ScreenshotState.Idle);

            if (_captureWindow is not null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _captureWindow.Close();
                    _captureWindow = null;
                });
            }
        }

        private void ChangeState(ScreenshotState newState)
        {
            if (_currentState != newState)
            {
                var oldState = _currentState;
                _currentState = newState;

                LogInformation($"Screenshot state changed: {oldState} -> {newState}");
                StateChanged?.Invoke(this, newState);
            }
        }

        private System.Drawing.Bitmap ConvertBytesToBitmap(byte[] imageData)
        {
            using (var ms = new System.IO.MemoryStream(imageData))
            {
                return new System.Drawing.Bitmap(ms);
            }
        }

        protected override void OnDispose()
        {
            UnregisterScreenshotHotkey();

            if (_captureWindow is not null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _captureWindow.Close();
                    _captureWindow = null;
                });
            }

            base.OnDispose();
        }
    }

    /// <summary>
    /// 截图状态枚举
    /// </summary>
    public enum ScreenshotState
    {
        Idle,
        Capturing,
        Captured,
        Processing,
        Completed,
        Error
    }

    /// <summary>
    /// 截图捕获事件参数
    /// </summary>
    public class ScreenshotCapturedEventArgs : EventArgs
    {
        public byte[] ImageData { get; set; } = Array.Empty<byte>();
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// 前置处理完成事件参数
    /// </summary>
    public class PreprocessingCompletedEventArgs : EventArgs
    {
        public bool Success { get; set; }
        public byte[] ImageData { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// 截图捕获事件
    /// </summary>
    public class ScreenshotCapturedEvent
    {
        public byte[] ImageData { get; set; } = Array.Empty<byte>();
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// OCR完成事件
    /// </summary>
    public class ScreenshotOCRCompletedEvent
    {
        public string Text { get; set; } = string.Empty;
        public byte[] ImageData { get; set; } = Array.Empty<byte>();
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// 前置处理完成事件
    /// </summary>
    public class ScreenshotPreprocessingCompletedEvent
    {
        public bool Success { get; set; }
        public byte[] ImageData { get; set; } = Array.Empty<byte>();
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// 截图配置
    /// </summary>
    public class ScreenshotConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "ScreenshotConfiguration";

        public bool EnableScreenshotHotkey { get; set; } = true;
        public string ScreenshotHotkey { get; set; } = "Ctrl+Shift+S";
        public bool AutoOCR { get; set; } = false;
        public bool AutoPreprocessing { get; set; } = false;

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult();

            if (string.IsNullOrWhiteSpace(ScreenshotHotkey))
            {
                result.AddError("Screenshot hotkey cannot be empty");
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new ScreenshotConfiguration
            {
                EnableScreenshotHotkey = this.EnableScreenshotHotkey,
                ScreenshotHotkey = this.ScreenshotHotkey,
                AutoOCR = this.AutoOCR,
                AutoPreprocessing = this.AutoPreprocessing,
                LastModified = this.LastModified,
                IsModified = this.IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is ScreenshotConfiguration otherConfig)
            {
                EnableScreenshotHotkey = otherConfig.EnableScreenshotHotkey;
                ScreenshotHotkey = otherConfig.ScreenshotHotkey;
                AutoOCR = otherConfig.AutoOCR;
                AutoPreprocessing = otherConfig.AutoPreprocessing;
                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            EnableScreenshotHotkey = true;
            ScreenshotHotkey = "Ctrl+Shift+S";
            AutoOCR = false;
            AutoPreprocessing = false;
            MarkAsModified();
        }
    }
}
