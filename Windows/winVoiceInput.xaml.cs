using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VPetLLM.Utils;

namespace VPetLLM.Windows
{
    public partial class winVoiceInput : Window
    {
        private readonly VPetLLM _plugin;
        private readonly ASRService _asrService;
        private bool _isRecording;

        public event EventHandler<string>? TranscriptionCompleted;

        public winVoiceInput(VPetLLM plugin)
        {
            InitializeComponent();
            _plugin = plugin;
            _asrService = new ASRService(_plugin.Settings);

            // 订阅 ASR 服务事件
            _asrService.RecordingStarted += OnRecordingStarted;
            _asrService.RecordingStopped += OnRecordingStopped;
            _asrService.TranscriptionCompleted += OnTranscriptionCompleted;
            _asrService.TranscriptionError += OnTranscriptionError;

            Loaded += (s, e) =>
            {
                // 窗口加载后自动开始录音
                if (_plugin.Settings.ASR.IsEnabled)
                {
                    StartRecording();
                }
            };
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecording)
            {
                StopRecording();
            }
            else
            {
                StartRecording();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecording)
            {
                _asrService.StopRecording();
            }
            Close();
        }

        private void StartRecording()
        {
            try
            {
                _asrService.StartRecording();
            }
            catch (Exception ex)
            {
                Logger.Log($"VoiceInput: Error starting recording: {ex.Message}");
                MessageBox.Show($"启动录音失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StopRecording()
        {
            try
            {
                _asrService.StopRecording();
            }
            catch (Exception ex)
            {
                Logger.Log($"VoiceInput: Error stopping recording: {ex.Message}");
                MessageBox.Show($"停止录音失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnRecordingStarted(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _isRecording = true;
                RecordButton.Content = LanguageHelper.Get("VoiceInput.StopRecording", _plugin.Settings.Language) ?? "停止录音";
                RecordButton.Style = (Style)FindResource("RecordingButton");
                StatusText.Text = LanguageHelper.Get("VoiceInput.Recording", _plugin.Settings.Language) ?? "正在录音...";
                
                // 显示录音动画
                RecordingPulse.Visibility = Visibility.Visible;
                StatusIndicator.Visibility = Visibility.Collapsed;
                StartPulseAnimation();
            });
        }

        private void OnRecordingStopped(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _isRecording = false;
                RecordButton.Content = LanguageHelper.Get("VoiceInput.StartRecording", _plugin.Settings.Language) ?? "开始录音";
                RecordButton.Style = (Style)FindResource("ModernButton");
                StatusText.Text = LanguageHelper.Get("VoiceInput.Processing", _plugin.Settings.Language) ?? "正在处理...";
                
                // 停止录音动画
                RecordingPulse.Visibility = Visibility.Collapsed;
                StatusIndicator.Visibility = Visibility.Visible;
                StopPulseAnimation();
            });
        }

        private void OnTranscriptionCompleted(object? sender, string transcription)
        {
            Dispatcher.Invoke(() =>
            {
                Logger.Log($"VoiceInput: Transcription completed: {transcription}");
                
                if (_plugin.Settings.ASR.ShowTranscriptionWindow)
                {
                    TranscriptionPanel.Visibility = Visibility.Visible;
                    TranscriptionText.Text = transcription;
                    StatusText.Text = LanguageHelper.Get("VoiceInput.Completed", _plugin.Settings.Language) ?? "识别完成";
                }

                // 触发事件
                TranscriptionCompleted?.Invoke(this, transcription);

                // 如果设置了自动发送，则关闭窗口
                if (_plugin.Settings.ASR.AutoSend)
                {
                    // 延迟关闭，让用户看到结果
                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(1)
                    };
                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        Close();
                    };
                    timer.Start();
                }
            });
        }

        private void OnTranscriptionError(object? sender, string error)
        {
            Dispatcher.Invoke(() =>
            {
                Logger.Log($"VoiceInput: Transcription error: {error}");
                StatusText.Text = $"错误: {error}";
                MessageBox.Show(error, "语音识别错误", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }

        private void StartPulseAnimation()
        {
            var scaleAnimation = new DoubleAnimation
            {
                From = 1.0,
                To = 1.3,
                Duration = TimeSpan.FromSeconds(0.8),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };

            var opacityAnimation = new DoubleAnimation
            {
                From = 1.0,
                To = 0.3,
                Duration = TimeSpan.FromSeconds(0.8),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };

            PulseTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
            PulseTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
            RecordingPulse.BeginAnimation(OpacityProperty, opacityAnimation);
        }

        private void StopPulseAnimation()
        {
            PulseTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PulseTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            RecordingPulse.BeginAnimation(OpacityProperty, null);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _asrService?.Dispose();
        }
    }
}
