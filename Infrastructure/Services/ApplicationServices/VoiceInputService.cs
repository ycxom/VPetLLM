using System.Windows;
using VPetLLM.Infrastructure.Configuration.Configurations;
using VPetLLM.Infrastructure.Events;
using VPetLLM.Infrastructure.Logging;
using VPetLLM.UI.Windows;
using VPetLLM.Utils.System;

namespace VPetLLM.Infrastructure.Services.ApplicationServices
{
    /// <summary>
    /// 语音输入服务 - 使用新架构重�?
    /// </summary>
    public class VoiceInputService : ServiceBase<ASRConfiguration>
    {
        private readonly VPetLLM _plugin;
        private GlobalHotkey? _voiceInputHotkey;
        private winVoiceInput? _currentVoiceInputWindow;
        private VoiceInputState _currentState = VoiceInputState.Idle;
        private const int VOICE_INPUT_HOTKEY_ID = 9001;

        public override string ServiceName => "VoiceInputService";
        public override Version Version => new Version(2, 0, 0);

        /// <summary>
        /// 当前语音输入状�?
        /// </summary>
        public VoiceInputState CurrentState => _currentState;

        /// <summary>
        /// 转录完成事件
        /// </summary>
        public event EventHandler<string>? TranscriptionCompleted;

        /// <summary>
        /// 状态变更事�?
        /// </summary>
        public event EventHandler<VoiceInputState>? StateChanged;

        public VoiceInputService(
            VPetLLM plugin,
            ASRConfiguration configuration,
            IStructuredLogger logger,
            IEventBus eventBus)
            : base(configuration, logger, eventBus)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        }

        protected override async Task OnInitializeAsync(CancellationToken cancellationToken)
        {
            LogInformation("Initializing voice input service");

            // 订阅配置变更事件
            await _eventBus.SubscribeAsync<ConfigurationChangedEvent<ASRConfiguration>>(OnConfigurationChanged);

            LogInformation("Voice input service initialized successfully");
        }

        protected override async Task OnStartAsync(CancellationToken cancellationToken)
        {
            LogInformation("Starting voice input service");

            await InitializeHotkeyAsync();

            // 发布服务启动事件
            await PublishEventAsync(new VoiceInputServiceStartedEvent
            {
                ServiceName = ServiceName,
                Configuration = _configuration
            });

            LogInformation("Voice input service started successfully");
        }

        protected override async Task OnStopAsync(CancellationToken cancellationToken)
        {
            LogInformation("Stopping voice input service");

            await CleanupHotkeyAsync();
            await CancelRecordingAsync();

            // 发布服务停止事件
            await PublishEventAsync(new VoiceInputServiceStoppedEvent
            {
                ServiceName = ServiceName
            });

            LogInformation("Voice input service stopped successfully");
        }

        protected override async Task<ServiceHealthStatus> OnCheckHealthAsync()
        {
            var healthStatus = new ServiceHealthStatus
            {
                Status = HealthStatus.Healthy,
                Description = "Voice input service is running normally"
            };

            // 检查配置状�?
            if (!_configuration.IsEnabled)
            {
                healthStatus.Status = HealthStatus.Degraded;
                healthStatus.Description = "ASR is disabled in configuration";
            }

            // 检查快捷键状�?
            if (_configuration.IsEnabled && _voiceInputHotkey == null)
            {
                healthStatus.Status = HealthStatus.Degraded;
                healthStatus.Description = "Hotkey is not registered";
            }

            // 添加健康指标
            healthStatus.Metrics["CurrentState"] = _currentState.ToString();
            healthStatus.Metrics["HotkeyRegistered"] = _voiceInputHotkey != null;
            healthStatus.Metrics["ASREnabled"] = _configuration.IsEnabled;
            healthStatus.Metrics["WindowActive"] = _currentVoiceInputWindow != null;

            return healthStatus;
        }

        private async Task OnConfigurationChangedAsync(ASRConfiguration oldConfiguration, ASRConfiguration newConfiguration)
        {
            LogInformation("Configuration changed, updating voice input service");

            // 重新初始化快捷键
            await CleanupHotkeyAsync();
            await InitializeHotkeyAsync();

            LogInformation("Voice input service configuration updated successfully");
        }

        public async Task InitializeHotkeyAsync()
        {
            try
            {
                if (!_configuration.IsEnabled)
                {
                    LogInformation("ASR is disabled, skipping hotkey registration");
                    return;
                }

                var mainWindow = Application.Current.MainWindow;
                if (mainWindow == null)
                {
                    LogWarning("Main window not found, cannot register voice input hotkey");
                    return;
                }

                var windowHandle = new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle;
                if (windowHandle == IntPtr.Zero)
                {
                    LogWarning("Window handle is zero, cannot register voice input hotkey");
                    return;
                }

                _voiceInputHotkey = new GlobalHotkey(windowHandle, VOICE_INPUT_HOTKEY_ID);

                uint modifiers = GlobalHotkey.ParseModifiers(_configuration.HotkeyModifiers);
                uint key = GlobalHotkey.ParseKey(_configuration.HotkeyKey);

                if (key == 0)
                {
                    LogWarning($"Invalid hotkey key: {_configuration.HotkeyKey}");
                    return;
                }

                bool registered = _voiceInputHotkey.Register(modifiers, key);
                if (registered)
                {
                    _voiceInputHotkey.HotkeyPressed += OnVoiceInputHotkeyPressed;
                    LogInformation($"Voice input hotkey registered: {_configuration.HotkeyModifiers}+{_configuration.HotkeyKey}");

                    // 发布快捷键注册成功事�?
                    await PublishEventAsync(new VoiceInputHotkeyRegisteredEvent
                    {
                        Modifiers = _configuration.HotkeyModifiers,
                        Key = _configuration.HotkeyKey
                    });
                }
                else
                {
                    LogWarning($"Failed to register voice input hotkey: {_configuration.HotkeyModifiers}+{_configuration.HotkeyKey}");
                }
            }
            catch (Exception ex)
            {
                LogError("Error initializing voice input hotkey", ex);
            }
        }

        private async Task CleanupHotkeyAsync()
        {
            if (_voiceInputHotkey != null)
            {
                try
                {
                    _voiceInputHotkey.Dispose();
                    _voiceInputHotkey = null;
                    LogInformation("Voice input hotkey cleaned up");
                }
                catch (Exception ex)
                {
                    LogError("Error cleaning up voice input hotkey", ex);
                }
            }
        }

        private async void OnVoiceInputHotkeyPressed(object? sender, EventArgs e)
        {
            try
            {
                LogDebug($"Voice input hotkey pressed, current state: {_currentState}");

                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await HandleHotkeyPressedAsync();
                });
            }
            catch (Exception ex)
            {
                LogError("Error handling voice input hotkey", ex);
            }
        }

        /// <summary>
        /// 处理快捷键按下事�?
        /// </summary>
        public async Task HandleHotkeyPressedAsync()
        {
            switch (_currentState)
            {
                case VoiceInputState.Idle:
                    await StartRecordingAsync();
                    break;

                case VoiceInputState.Recording:
                    await StopRecordingAsync();
                    break;

                case VoiceInputState.Editing:
                    await CancelRecordingAsync();
                    await StartRecordingAsync();
                    break;
            }
        }

        /// <summary>
        /// 开始录�?
        /// </summary>
        public async Task StartRecordingAsync()
        {
            try
            {
                LogInformation("Starting voice input recording");

                if (_currentVoiceInputWindow != null)
                {
                    LogInformation("Previous voice input window still exists, closing it first");
                    try
                    {
                        _currentVoiceInputWindow.Close();
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"Error closing previous window: {ex.Message}");
                    }
                    _currentVoiceInputWindow = null;
                }

                await SetStateAsync(VoiceInputState.Recording);

                bool quickMode = _configuration.AutoSend;
                _currentVoiceInputWindow = new winVoiceInput(_plugin, quickMode);

                _currentVoiceInputWindow.Closed += async (s, e) =>
                {
                    _currentVoiceInputWindow = null;
                    await SetStateAsync(VoiceInputState.Idle);
                    LogInformation("Voice input window closed, state reset to Idle");
                };

                _currentVoiceInputWindow.RecordingStopped += (s, e) =>
                {
                    LogInformation("Recording stopped, waiting for transcription...");
                };

                _currentVoiceInputWindow.TranscriptionCompleted += OnWindowTranscriptionCompleted;

                _currentVoiceInputWindow.Show();
                LogInformation("Voice input window shown");

                // 发布录音开始事�?
                await PublishEventAsync(new VoiceInputRecordingStartedEvent
                {
                    QuickMode = quickMode
                });
            }
            catch (Exception ex)
            {
                LogError("Error starting voice input", ex);
                await SetStateAsync(VoiceInputState.Idle);

                // 发布错误事件
                await PublishEventAsync(new VoiceInputErrorEvent
                {
                    ErrorMessage = $"启动语音输入失败: {ex.Message}",
                    Exception = ex
                });
            }
        }

        /// <summary>
        /// 停止录音
        /// </summary>
        public async Task StopRecordingAsync()
        {
            try
            {
                LogInformation("Stopping voice input recording");

                if (_currentVoiceInputWindow == null)
                {
                    LogInformation("No voice input window to stop, resetting state to Idle");
                    await SetStateAsync(VoiceInputState.Idle);
                    return;
                }

                if (!_currentVoiceInputWindow.IsRecording)
                {
                    LogInformation("Window is not recording, resetting state to Idle");
                    await SetStateAsync(VoiceInputState.Idle);
                    return;
                }

                _currentVoiceInputWindow.StopRecording();

                // 发布录音停止事件
                await PublishEventAsync(new VoiceInputRecordingStoppedEvent());
            }
            catch (Exception ex)
            {
                LogError("Error stopping voice input", ex);
                await SetStateAsync(VoiceInputState.Idle);
            }
        }

        /// <summary>
        /// 取消录音
        /// </summary>
        public async Task CancelRecordingAsync()
        {
            try
            {
                LogInformation("Canceling voice input recording");

                if (_currentVoiceInputWindow != null)
                {
                    _currentVoiceInputWindow.Close();
                    _currentVoiceInputWindow = null;
                }

                await SetStateAsync(VoiceInputState.Idle);

                // 发布录音取消事件
                await PublishEventAsync(new VoiceInputRecordingCancelledEvent());
            }
            catch (Exception ex)
            {
                LogError("Error canceling voice input", ex);
                await SetStateAsync(VoiceInputState.Idle);
            }
        }

        /// <summary>
        /// 更新快捷键
        /// </summary>
        public async Task UpdateHotkeyAsync()
        {
            await CleanupHotkeyAsync();
            await InitializeHotkeyAsync();
        }

        /// <summary>
        /// 显示语音输入窗口
        /// </summary>
        public async Task ShowVoiceInputWindowAsync()
        {
            await HandleHotkeyPressedAsync();
        }

        private async void OnWindowTranscriptionCompleted(object? sender, string transcription)
        {
            LogInformation($"Transcription completed, text length: {transcription?.Length ?? 0}");

            if (!string.IsNullOrWhiteSpace(transcription))
            {
                LogInformation("Transcription received, raising events");
                await SetStateAsync(VoiceInputState.Idle);

                // 发布转录完成事件
                await PublishEventAsync(new VoiceInputTranscriptionCompletedEvent
                {
                    Transcription = transcription
                });

                // 触发传统事件（向后兼容）
                TranscriptionCompleted?.Invoke(this, transcription);
            }
            else
            {
                LogInformation("Empty transcription, resetting to Idle");
                await SetStateAsync(VoiceInputState.Idle);
            }
        }

        private async Task SetStateAsync(VoiceInputState newState)
        {
            if (_currentState != newState)
            {
                var oldState = _currentState;
                _currentState = newState;

                // 发布状态变更事�?
                await PublishEventAsync(new VoiceInputStateChangedEvent
                {
                    OldState = oldState,
                    NewState = newState
                });

                // 触发传统事件（向后兼容）
                StateChanged?.Invoke(this, newState);

                LogDebug($"Voice input state changed: {oldState} -> {newState}");
            }
        }

        private async Task OnConfigurationChanged(ConfigurationChangedEvent<ASRConfiguration> configEvent)
        {
            try
            {
                await UpdateConfigurationAsync(configEvent.NewConfiguration);
            }
            catch (Exception ex)
            {
                LogError("Failed to handle configuration change", ex);
            }
        }

        protected override void OnDispose()
        {
            base.OnDispose();

            _voiceInputHotkey?.Dispose();
            _voiceInputHotkey = null;

            if (_currentVoiceInputWindow != null)
            {
                try
                {
                    _currentVoiceInputWindow.Close();
                }
                catch { }
                _currentVoiceInputWindow = null;
            }
        }
    }

    #region Event Classes

    /// <summary>
    /// 语音输入状态枚�?
    /// </summary>
    public enum VoiceInputState
    {
        Idle,
        Recording,
        Editing
    }

    /// <summary>
    /// 语音输入服务启动事件
    /// </summary>
    public class VoiceInputServiceStartedEvent
    {
        public string ServiceName { get; set; }
        public ASRConfiguration Configuration { get; set; }
    }

    /// <summary>
    /// 语音输入服务停止事件
    /// </summary>
    public class VoiceInputServiceStoppedEvent
    {
        public string ServiceName { get; set; }
    }

    /// <summary>
    /// 语音输入快捷键注册事�?
    /// </summary>
    public class VoiceInputHotkeyRegisteredEvent
    {
        public string Modifiers { get; set; }
        public string Key { get; set; }
    }

    /// <summary>
    /// 语音输入录音开始事�?
    /// </summary>
    public class VoiceInputRecordingStartedEvent
    {
        public bool QuickMode { get; set; }
    }

    /// <summary>
    /// 语音输入录音停止事件
    /// </summary>
    public class VoiceInputRecordingStoppedEvent
    {
    }

    /// <summary>
    /// 语音输入录音取消事件
    /// </summary>
    public class VoiceInputRecordingCancelledEvent
    {
    }

    /// <summary>
    /// 语音输入转录完成事件
    /// </summary>
    public class VoiceInputTranscriptionCompletedEvent
    {
        public string Transcription { get; set; }
    }

    /// <summary>
    /// 语音输入状态变更事�?
    /// </summary>
    public class VoiceInputStateChangedEvent
    {
        public VoiceInputState OldState { get; set; }
        public VoiceInputState NewState { get; set; }
    }

    /// <summary>
    /// 语音输入错误事件
    /// </summary>
    public class VoiceInputErrorEvent
    {
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
    }

    #endregion
}