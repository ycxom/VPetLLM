using System.Windows;

namespace VPetLLM.Services
{
    /// <summary>
    /// 截图服务实现
    /// </summary>
    public class ScreenshotService : IScreenshotService
    {
        private readonly VPetLLM _plugin;
        private readonly Setting _settings;
        private readonly IPreprocessingMultimodal _preprocessingMultimodal;
        // 处理类型（原生/前置多模态/OCR）的分发统一走它，与 SeeScreenHandler 同一份实现
        private readonly ScreenshotAnalyzer _analyzer;
        private GlobalHotkey? _screenshotHotkey;
        private UI.Windows.winScreenshotCapture? _captureWindow;
        private ScreenshotState _currentState = ScreenshotState.Idle;
        private byte[]? _currentImage;
        private const int SCREENSHOT_HOTKEY_ID = 9002;
        private bool _disposed;

        /// <summary>
        /// 前置多模态处理完成事件
        /// </summary>
        public event EventHandler<PreprocessingCompletedEventArgs>? PreprocessingCompleted;

        /// <inheritdoc/>
        public ScreenshotState CurrentState => _currentState;

        /// <inheritdoc/>
        public byte[]? CurrentImage => _currentImage;

        /// <inheritdoc/>
        public event EventHandler<ScreenshotCapturedEventArgs>? ScreenshotCaptured;

        /// <inheritdoc/>
        public event EventHandler<string>? OCRCompleted;

        /// <inheritdoc/>
        public event EventHandler<ScreenshotState>? StateChanged;

        /// <inheritdoc/>
        public event EventHandler<string>? ErrorOccurred;

        public ScreenshotService(VPetLLM plugin, Setting settings)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _preprocessingMultimodal = new PreprocessingMultimodal(settings, plugin);
            _analyzer = new ScreenshotAnalyzer(settings, plugin, _preprocessingMultimodal);
        }

        /// <inheritdoc/>
        public void InitializeHotkey()
        {
            try
            {
                if (!_settings.Screenshot.IsEnabled)
                {
                    Logger.Log("Screenshot is disabled, skipping hotkey registration");
                    return;
                }

                var mainWindow = Application.Current.MainWindow;
                if (mainWindow is null)
                {
                    Logger.Log("Main window not found, cannot register screenshot hotkey");
                    return;
                }

                var windowHandle = new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle;
                if (windowHandle == IntPtr.Zero)
                {
                    Logger.Log("Window handle is zero, cannot register screenshot hotkey");
                    return;
                }

                _screenshotHotkey = new GlobalHotkey(windowHandle, SCREENSHOT_HOTKEY_ID);

                uint modifiers = GlobalHotkey.ParseModifiers(_settings.Screenshot.HotkeyModifiers);
                uint key = GlobalHotkey.ParseKey(_settings.Screenshot.HotkeyKey);

                if (key == 0)
                {
                    Logger.Log($"Invalid hotkey key: {_settings.Screenshot.HotkeyKey}");
                    return;
                }

                bool registered = _screenshotHotkey.Register(modifiers, key);
                if (registered)
                {
                    _screenshotHotkey.HotkeyPressed += OnScreenshotHotkeyPressed;
                    Logger.Log($"Screenshot hotkey registered: {_settings.Screenshot.HotkeyModifiers}+{_settings.Screenshot.HotkeyKey}");
                }
                else
                {
                    Logger.Log($"Failed to register screenshot hotkey: {_settings.Screenshot.HotkeyModifiers}+{_settings.Screenshot.HotkeyKey}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error initializing screenshot hotkey: {ex.Message}");
            }
        }

        private void OnScreenshotHotkeyPressed(object? sender, EventArgs e)
        {
            try
            {
                Logger.Log($"Screenshot hotkey pressed, current state: {_currentState}");
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (_currentState == ScreenshotState.Idle)
                    {
                        StartCapture();
                    }
                    else if (_currentState == ScreenshotState.Capturing)
                    {
                        CancelCapture();
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling screenshot hotkey: {ex.Message}");
            }
        }

        /// <inheritdoc/>
        public void UpdateHotkey()
        {
            _screenshotHotkey?.Dispose();
            _screenshotHotkey = null;
            InitializeHotkey();
        }

        /// <summary>
        /// 唯一的抓图内核。两个公开入口（用户手动 / AI 请求）都从这里走，
        /// 窗口生命周期、状态机、取消与超时兜底只有这一份实现。
        ///
        /// 分开写的时候两边能力是不对等的：AI 那条有超时和「窗口被外部关掉」的兜底，
        /// 手动那条没有；反过来手动那条把窗口记进 _captureWindow（所以 CancelCapture /
        /// Dispose 关得掉），AI 那条用的是局部变量 —— 插件卸载时那个选区窗口会留在屏幕上。
        /// </summary>
        /// <param name="reason">非空表示这是 AI 发起的请求，会显示在选区窗口上。</param>
        /// <param name="timeoutSeconds">等待用户操作的上限；null 表示不设上限（手动截图）。</param>
        private async Task<byte[]?> CaptureCoreAsync(string? reason, int? timeoutSeconds)
        {
            var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    // 上一个窗口还开着就先关掉，别叠出两层选区遮罩
                    if (_captureWindow is not null)
                    {
                        Logger.Log("Previous capture window still exists, closing it first");
                        try { _captureWindow.Close(); }
                        catch (Exception ex) { Logger.Log($"Error closing previous window: {ex.Message}"); }
                        _captureWindow = null;
                    }

                    SetState(ScreenshotState.Capturing);

                    var window = reason is null
                        ? new UI.Windows.winScreenshotCapture()
                        : new UI.Windows.winScreenshotCapture(reason);

                    window.ScreenshotCaptured += (s, data) => tcs.TrySetResult(data);
                    window.CaptureCancelled += (s, e) => tcs.TrySetResult(null);
                    // 兜底：窗口被外部关闭（宿主退出、Dispose 等）时也要让等待方解除阻塞
                    window.Closed += (s, e) =>
                    {
                        if (ReferenceEquals(_captureWindow, window)) _captureWindow = null;
                        tcs.TrySetResult(null);
                        Logger.Log("Screenshot capture window closed");
                    };

                    _captureWindow = window;
                    window.Show();
                    Logger.Log($"Screenshot capture window shown (reason={reason ?? "manual"})");
                });

                if (timeoutSeconds is null)
                {
                    return await tcs.Task;
                }

                try
                {
                    // WaitAsync 自己管定时器；旧的 Task.WhenAny(tcs, Task.Delay(..)) 在 tcs 先赢时
                    // 会把那个 Delay 定时器一直武装到到期。
                    return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds.Value)));
                }
                catch (TimeoutException)
                {
                    Logger.Log($"ScreenshotService: 截图请求等待超时（{timeoutSeconds}s），自动取消");
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        try { _captureWindow?.Close(); } catch { }
                    });
                    return null;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"ScreenshotService: 抓图失败: {ex.Message}");
                ErrorOccurred?.Invoke(this, $"启动截图失败: {ex.Message}");
                return null;
            }
            finally
            {
                if (_currentState == ScreenshotState.Capturing)
                {
                    SetState(ScreenshotState.Idle);
                }
            }
        }

        /// <inheritdoc/>
        public void StartCapture()
        {
            // 用户手动截图：不设超时（他可能盯着屏幕慢慢挑区域），结果走常规事件链。
            _ = Task.Run(async () =>
            {
                var data = await CaptureCoreAsync(reason: null, timeoutSeconds: null);
                if (data is null || data.Length == 0)
                {
                    Logger.Log("Screenshot capture cancelled");
                    return;
                }

                await Application.Current.Dispatcher.InvokeAsync(() => OnCaptureCompleted(this, data));
            });
        }

        private void OnCaptureCompleted(object? sender, byte[] imageData)
        {
            try
            {
                Logger.Log($"Screenshot captured, size: {imageData.Length} bytes");
                _currentImage = imageData;
                SetState(ScreenshotState.Processing);

                var args = new ScreenshotCapturedEventArgs
                {
                    ImageData = imageData,
                    Width = 0,
                    Height = 0
                };

                ScreenshotCaptured?.Invoke(this, args);
                ProcessScreenshot(imageData);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error processing captured screenshot: {ex.Message}");
                SetState(ScreenshotState.Idle);
                ErrorOccurred?.Invoke(this, $"处理截图失败: {ex.Message}");
            }
        }

        /// <inheritdoc/>
        public async Task<byte[]?> RequestUserCaptureAsync(string reason, int timeoutSeconds = 60)
        {
            if (_currentState != ScreenshotState.Idle)
            {
                Logger.Log($"ScreenshotService: 当前状态为 {_currentState}，拒绝 AI 的截图请求");
                return null;
            }

            // 与 StartCapture 的唯一区别：带上请求原因、设超时，且结果只回给调用方，
            // 不触发 ScreenshotCaptured / 前置多模态那套常规事件链。
            var result = await CaptureCoreAsync(reason, timeoutSeconds);
            Logger.Log(result is null
                ? "ScreenshotService: 用户取消了 AI 的截图请求"
                : $"ScreenshotService: 用户已授权截图，{result.Length} 字节");
            return result;
        }

        /// <inheritdoc/>
        public void CancelCapture()
        {
            try
            {
                Logger.Log("Canceling screenshot capture...");
                if (_captureWindow is not null)
                {
                    _captureWindow.Close();
                    _captureWindow = null;
                }
                SetState(ScreenshotState.Idle);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error canceling screenshot capture: {ex.Message}");
                SetState(ScreenshotState.Idle);
            }
        }

        /// <inheritdoc/>
        public void ProcessScreenshot(byte[] imageData)
        {
            try
            {
                var processingMode = _settings.Screenshot.ProcessingMode;
                Logger.Log($"Processing screenshot with mode: {processingMode}");

                // 只有 AutoSend 打开时才在这里直接识别并发送；
                // 否则图片交给编辑器流程，由用户补充提问后再识别，
                // 避免同一张图被识别两次（编辑器发送时还会再走一次）。
                if (processingMode == ScreenshotProcessingMode.OCRApi && _settings.Screenshot.AutoSend)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // 走统一分发器而不是直接 PerformOCR：这样这条路也能享受到
                            // 「OCR 未配独立端点时不做无谓重试」之类的判断，与其它入口一致。
                            var analysis = await _analyzer.AnalyzeAsync(imageData);
                            var text = analysis.Success ? analysis.Text : "";

                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                if (analysis.Success)
                                {
                                    OCRCompleted?.Invoke(this, text);
                                }
                                else
                                {
                                    Logger.Log($"ScreenshotService: 自动发送的 OCR 未取到文字: {analysis.ErrorMessage}");
                                    ErrorOccurred?.Invoke(this, analysis.ErrorMessage);
                                }
                                SetState(ScreenshotState.Idle);
                            });
                        }
                        catch (Exception ex)
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                ErrorOccurred?.Invoke(this, $"OCR 处理失败: {ex.Message}");
                                SetState(ScreenshotState.Idle);
                            });
                        }
                    });
                }
                else if (processingMode == ScreenshotProcessingMode.PreprocessingMultimodal)
                {
                    // PreprocessingMultimodal mode - 前置多模态处理
                    // 实际处理在 ProcessWithPreprocessingAsync 中进行
                    SetState(ScreenshotState.Idle);
                }
                else
                {
                    // NativeMultimodal mode - 原生多模态，直接发送图片给视觉 LLM
                    // 图片数据将通过 ChatCore 直接发送
                    SetState(ScreenshotState.Idle);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error processing screenshot: {ex.Message}");
                SetState(ScreenshotState.Idle);
                ErrorOccurred?.Invoke(this, $"处理截图失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 使用前置多模态处理图片
        /// </summary>
        /// <param name="imageData">图片数据</param>
        /// <param name="userQuestion">用户问题</param>
        public Task<PreprocessingResult> ProcessWithPreprocessingAsync(byte[] imageData, string userQuestion)
            => ProcessWithPreprocessingAsync(new[] { imageData }, userQuestion);

        /// <summary>
        /// 把图变成文字。模式分流、失败回落、多图编号全在 ScreenshotAnalyzer 里，
        /// 这里只负责把结果转回旧的 PreprocessingResult 形状给上层事件用。
        ///
        /// 以前这里是自己判 OCRApi 再分流的，识图不带关注点、视觉失败也没有任何回落——
        /// 同样的能力 SeeScreenHandler 那边却有一整套。现在两边同一份实现。
        /// </summary>
        private async Task<PreprocessingResult> RecognizeImagesAsync(IReadOnlyList<byte[]> images, string? focus)
        {
            var analysis = await _analyzer.AnalyzeAsync(images, focus);
            return analysis.ToPreprocessingResult();
        }

        /// <summary>
        /// 前置处理入口：把图交给统一分发器变成文字，再拼上用户提问，最后播事件。
        ///
        /// 单图/多图不再分两条路 —— 编号、失败回落、模式分流全在 ScreenshotAnalyzer 里，
        /// 这里只剩「拼消息 + 播事件 + 管状态」这点本职工作。
        /// </summary>
        public async Task<PreprocessingResult> ProcessWithPreprocessingAsync(IReadOnlyList<byte[]> images, string userQuestion)
        {
            var valid = (images ?? Array.Empty<byte[]>())
                .Where(i => i is not null && i.Length > 0)
                .ToList();

            if (valid.Count == 0)
            {
                return PreprocessingResult.CreateFailure("没有可分析的图片");
            }

            try
            {
                Logger.Log($"Starting preprocessing for {valid.Count} image(s)");
                SetState(ScreenshotState.Processing);

                // 用户的提问同时也是识图的关注点：以前手动截图这条路是不传的，
                // 视觉模型只能泛泛描述整屏，用户问什么它并不知道。
                var result = await RecognizeImagesAsync(valid, userQuestion);

                if (result.Success)
                {
                    Logger.Log($"Preprocessing completed successfully, provider: {result.UsedProvider}");
                }
                else
                {
                    Logger.Log($"Preprocessing failed: {result.ErrorMessage}");
                }

                var combinedMessage = result.Success
                    ? MessageCombiner.Combine(result.ImageDescription, userQuestion)
                    : "";

                Application.Current.Dispatcher.Invoke(() =>
                {
                    PreprocessingCompleted?.Invoke(this, new PreprocessingCompletedEventArgs
                    {
                        Success = result.Success,
                        CombinedMessage = combinedMessage,
                        ImageDescription = result.ImageDescription,
                        UsedProvider = result.UsedProvider,
                        ErrorMessage = result.ErrorMessage
                    });

                    if (!result.Success)
                    {
                        ErrorOccurred?.Invoke(this, result.ErrorMessage);
                    }

                    SetState(ScreenshotState.Idle);
                });

                return result;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error in preprocessing: {ex.Message}");
                SetState(ScreenshotState.Idle);

                var errorResult = PreprocessingResult.CreateFailure($"前置处理异常: {ex.Message}");

                Application.Current.Dispatcher.Invoke(() =>
                {
                    PreprocessingCompleted?.Invoke(this, new PreprocessingCompletedEventArgs
                    {
                        Success = false,
                        ErrorMessage = errorResult.ErrorMessage
                    });
                    ErrorOccurred?.Invoke(this, errorResult.ErrorMessage);
                });

                return errorResult;
            }
        }

        /// <summary>
        /// 获取可用的视觉节点列表
        /// </summary>
        public System.Collections.Generic.List<VisionNodeIdentifier> GetAvailableVisionNodes()
        {
            return _preprocessingMultimodal.GetAvailableVisionNodes();
        }

        /// <summary>
        /// 检查是否有可用的多模态提供商
        /// </summary>
        public bool HasAvailableProvider()
        {
            return _preprocessingMultimodal.HasAvailableProvider();
        }

        /// <inheritdoc/>
        public void ClearCurrentImage()
        {
            _currentImage = null;
        }

        /// <inheritdoc/>
        public async Task<string> PerformOCR(byte[] imageData)
        {
            try
            {
                var ocrEngine = new OCREngine(_settings, _plugin);
                return await ocrEngine.RecognizeText(imageData);
            }
            catch (Exception ex)
            {
                Logger.Log($"OCR error: {ex.Message}");
                throw;
            }
        }

        private void SetState(ScreenshotState newState)
        {
            if (_currentState != newState)
            {
                _currentState = newState;
                StateChanged?.Invoke(this, newState);
                Logger.Log($"Screenshot state changed to: {newState}");
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _screenshotHotkey?.Dispose();
            _screenshotHotkey = null;

            if (_captureWindow is not null)
            {
                try
                {
                    _captureWindow.Close();
                }
                catch { }
                _captureWindow = null;
            }

            _currentImage = null;
            Logger.Log("ScreenshotService disposed");
        }
    }
}
