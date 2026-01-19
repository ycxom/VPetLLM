using System.Windows;
using VPetLLM.UI.Windows;
using VPetLLM.Utils.System;

namespace VPetLLM.Services
{
    /// <summary>
    /// 语音输入服务实现
    /// </summary>
    public class VoiceInputService : IVoiceInputService
    {
        private readonly VPetLLM _plugin;
        private readonly Setting _settings;
        private GlobalHotkey? _voiceInputHotkey;
        private winVoiceInput? _currentVoiceInputWindow;
        private VoiceInputState _currentState = VoiceInputState.Idle;
        private const int VOICE_INPUT_HOTKEY_ID = 9001;
        private bool _disposed;

        /// <inheritdoc/>
        public VoiceInputState CurrentState => _currentState;

        /// <inheritdoc/>
        public event EventHandler<string>? TranscriptionCompleted;

        /// <inheritdoc/>
        public event EventHandler<VoiceInputState>? StateChanged;

        public VoiceInputService(VPetLLM plugin, Setting settings)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// 初始化语音输入快捷键
        /// </summary>
        public void InitializeHotkey()
        {
            try
            {
                if (!_settings.ASR.IsEnabled)
                {
                    Logger.Log("ASR is disabled, skipping hotkey registration");
                    return;
                }

                var mainWindow = Application.Current.MainWindow;
                if (mainWindow == null)
                {
                    Logger.Log("Main window not found, cannot register voice input hotkey");
                    return;
                }

                var windowHandle = new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle;
                if (windowHandle == IntPtr.Zero)
                {
                    Logger.Log("Window handle is zero, cannot register voice input hotkey");
                    return;
                }

                _voiceInputHotkey = new GlobalHotkey(windowHandle, VOICE_INPUT_HOTKEY_ID);

                uint modifiers = GlobalHotkey.ParseModifiers(_settings.ASR.HotkeyModifiers);
                uint key = GlobalHotkey.ParseKey(_settings.ASR.HotkeyKey);

                if (key == 0)
                {
                    Logger.Log($"Invalid hotkey key: {_settings.ASR.HotkeyKey}");
                    return;
                }

                bool registered = _voiceInputHotkey.Register(modifiers, key);
                if (registered)
                {
                    _voiceInputHotkey.HotkeyPressed += OnVoiceInputHotkeyPressed;
                    Logger.Log($"Voice input hotkey registered: {_settings.ASR.HotkeyModifiers}+{_settings.ASR.HotkeyKey}");
                }
                else
                {
                    Logger.Log($"Failed to register voice input hotkey: {_settings.ASR.HotkeyModifiers}+{_settings.ASR.HotkeyKey}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error initializing voice input hotkey: {ex.Message}");
            }
        }

        private void OnVoiceInputHotkeyPressed(object? sender, EventArgs e)
        {
            try
            {
                Logger.Log($"Voice input hotkey pressed, current state: {_currentState}");
                Application.Current.Dispatcher.Invoke(() =>
                {
                    HandleHotkeyPressed();
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling voice input hotkey: {ex.Message}");
            }
        }

        /// <inheritdoc/>
        public void HandleHotkeyPressed()
        {
            switch (_currentState)
            {
                case VoiceInputState.Idle:
                    StartRecording();
                    break;

                case VoiceInputState.Recording:
                    StopRecording();
                    break;

                case VoiceInputState.Editing:
                    CancelRecording();
                    StartRecording();
                    break;
            }
        }

        /// <inheritdoc/>
        public void StartRecording()
        {
            try
            {
                Logger.Log("Starting voice input...");

                if (_currentVoiceInputWindow != null)
                {
                    Logger.Log("Previous voice input window still exists, closing it first");
                    try
                    {
                        _currentVoiceInputWindow.Close();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error closing previous window: {ex.Message}");
                    }
                    _currentVoiceInputWindow = null;
                }

                SetState(VoiceInputState.Recording);

                bool quickMode = _settings.ASR.AutoSend;
                _currentVoiceInputWindow = new winVoiceInput(_plugin, quickMode);

                _currentVoiceInputWindow.Closed += (s, e) =>
                {
                    _currentVoiceInputWindow = null;
                    SetState(VoiceInputState.Idle);
                    Logger.Log("Voice input window closed, state reset to Idle");
                };

                _currentVoiceInputWindow.RecordingStopped += (s, e) =>
                {
                    Logger.Log("Recording stopped, waiting for transcription...");
                };

                _currentVoiceInputWindow.TranscriptionCompleted += OnWindowTranscriptionCompleted;

                _currentVoiceInputWindow.Show();
                Logger.Log("Voice input window shown");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error starting voice input: {ex.Message}");
                SetState(VoiceInputState.Idle);
                MessageBox.Show($"启动语音输入失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnWindowTranscriptionCompleted(object? sender, string transcription)
        {
            Logger.Log($"Transcription completed, text length: {transcription?.Length ?? 0}");

            if (!string.IsNullOrWhiteSpace(transcription))
            {
                Logger.Log("Transcription received, raising event");
                SetState(VoiceInputState.Idle);
                TranscriptionCompleted?.Invoke(this, transcription);
            }
            else
            {
                Logger.Log("Empty transcription, resetting to Idle");
                SetState(VoiceInputState.Idle);
            }
        }

        /// <inheritdoc/>
        public void StopRecording()
        {
            try
            {
                Logger.Log("Stopping voice input...");

                if (_currentVoiceInputWindow == null)
                {
                    Logger.Log("No voice input window to stop, resetting state to Idle");
                    SetState(VoiceInputState.Idle);
                    return;
                }

                if (!_currentVoiceInputWindow.IsRecording)
                {
                    Logger.Log("Window is not recording, resetting state to Idle");
                    SetState(VoiceInputState.Idle);
                    return;
                }

                _currentVoiceInputWindow.StopRecording();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error stopping voice input: {ex.Message}");
                SetState(VoiceInputState.Idle);
            }
        }

        /// <inheritdoc/>
        public void CancelRecording()
        {
            try
            {
                Logger.Log("Canceling voice input...");
                if (_currentVoiceInputWindow != null)
                {
                    _currentVoiceInputWindow.Close();
                    _currentVoiceInputWindow = null;
                }
                SetState(VoiceInputState.Idle);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error canceling voice input: {ex.Message}");
                SetState(VoiceInputState.Idle);
            }
        }

        /// <inheritdoc/>
        public void UpdateHotkey()
        {
            _voiceInputHotkey?.Dispose();
            _voiceInputHotkey = null;
            InitializeHotkey();
        }

        /// <inheritdoc/>
        public void ShowVoiceInputWindow()
        {
            HandleHotkeyPressed();
        }

        private void SetState(VoiceInputState newState)
        {
            if (_currentState != newState)
            {
                _currentState = newState;
                StateChanged?.Invoke(this, newState);
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

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
}
