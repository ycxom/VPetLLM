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

        /// <inheritdoc/>
        public void StartCapture()
        {
            try
            {
                Logger.Log("Starting screenshot capture...");

                if (_captureWindow is not null)
                {
                    Logger.Log("Previous capture window still exists, closing it first");
                    try
                    {
                        _captureWindow.Close();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error closing previous window: {ex.Message}");
                    }
                    _captureWindow = null;
                }

                SetState(ScreenshotState.Capturing);

                _captureWindow = new UI.Windows.winScreenshotCapture();

                _captureWindow.Closed += (s, e) =>
                {
                    _captureWindow = null;
                    if (_currentState == ScreenshotState.Capturing)
                    {
                        SetState(ScreenshotState.Idle);
                    }
                    Logger.Log("Screenshot capture window closed");
                };

                _captureWindow.ScreenshotCaptured += OnCaptureCompleted;
                _captureWindow.CaptureCancelled += OnCaptureCancelled;

                _captureWindow.Show();
                Logger.Log("Screenshot capture window shown");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error starting screenshot capture: {ex.Message}");
                SetState(ScreenshotState.Idle);
                ErrorOccurred?.Invoke(this, $"启动截图失败: {ex.Message}");
            }
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

        private void OnCaptureCancelled(object? sender, EventArgs e)
        {
            Logger.Log("Screenshot capture cancelled");
            SetState(ScreenshotState.Idle);
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

                if (processingMode == ScreenshotProcessingMode.OCRApi)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var text = await PerformOCR(imageData);
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                OCRCompleted?.Invoke(this, text);
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
        public async Task<PreprocessingResult> ProcessWithPreprocessingAsync(byte[] imageData, string userQuestion)
        {
            try
            {
                Logger.Log($"Starting preprocessing multimodal analysis, image size: {imageData.Length} bytes");
                SetState(ScreenshotState.Processing);

                var result = await _preprocessingMultimodal.AnalyzeImageAsync(imageData);

                if (result.Success)
                {
                    Logger.Log($"Preprocessing completed successfully, provider: {result.UsedProvider}");

                    // 组合图片描述和用户问题
                    var combinedMessage = MessageCombiner.Combine(result.ImageDescription, userQuestion);

                    // 触发完成事件
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        PreprocessingCompleted?.Invoke(this, new PreprocessingCompletedEventArgs
                        {
                            Success = true,
                            CombinedMessage = combinedMessage,
                            ImageDescription = result.ImageDescription,
                            UsedProvider = result.UsedProvider
                        });
                        SetState(ScreenshotState.Idle);
                    });
                }
                else
                {
                    Logger.Log($"Preprocessing failed: {result.ErrorMessage}");

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        PreprocessingCompleted?.Invoke(this, new PreprocessingCompletedEventArgs
                        {
                            Success = false,
                            ErrorMessage = result.ErrorMessage
                        });
                        ErrorOccurred?.Invoke(this, result.ErrorMessage);
                        SetState(ScreenshotState.Idle);
                    });
                }

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
