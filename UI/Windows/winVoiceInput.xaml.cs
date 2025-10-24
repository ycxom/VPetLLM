using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VPetLLM.Utils;

namespace VPetLLM.UI.Windows
{
    public partial class winVoiceInput : Window
    {
        private readonly VPetLLM _plugin;
        private readonly ASRService _asrService;
        private bool _isRecording;
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _isQuickDisplayMode;
        private string? _recognizedText;

        public event EventHandler<string>? TranscriptionCompleted;

        public winVoiceInput(VPetLLM plugin, bool quickDisplayMode = false)
        {
            InitializeComponent();
            _plugin = plugin;
            _asrService = new ASRService(_plugin.Settings);
            _isQuickDisplayMode = quickDisplayMode;
            _cancellationTokenSource = new CancellationTokenSource();

            // 订阅 ASR 服务事件
            _asrService.RecordingStarted += OnRecordingStarted;
            _asrService.RecordingStopped += OnRecordingStopped;
            _asrService.TranscriptionCompleted += OnTranscriptionCompleted;
            _asrService.TranscriptionError += OnTranscriptionError;

            Loaded += (s, e) =>
            {
                // 窗口加载后定位到屏幕底部中央
                PositionWindowAtBottomCenter();

                // 自动开始录音
                if (_plugin.Settings.ASR.IsEnabled)
                {
                    StartRecording();
                }
            };

            // 监听窗口关闭事件，立即熔断任务
            Closing += (s, e) =>
            {
                CancelAllOperations();
            };
        }

        /// <summary>
        /// 将窗口定位到屏幕底部中央
        /// </summary>
        private void PositionWindowAtBottomCenter()
        {
            try
            {
                // 获取工作区域（排除任务栏）
                var workArea = SystemParameters.WorkArea;
                
                // 计算窗口位置
                // X: 屏幕中央
                Left = workArea.Left + (workArea.Width - ActualWidth) / 2;
                
                // Y: 屏幕底部，留出一些边距
                Top = workArea.Bottom - ActualHeight - 20;

                Logger.Log($"VoiceInput: Positioned at ({Left:F0}, {Top:F0})");
            }
            catch (Exception ex)
            {
                Logger.Log($"VoiceInput: Error positioning window: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理气泡框拖动
        /// </summary>
        private void BubbleContainer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try
                {
                    DragMove();
                }
                catch (Exception ex)
                {
                    // DragMove 可能在某些情况下抛出异常，忽略即可
                    Logger.Log($"VoiceInput: DragMove error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 立即熔断所有正在进行的操作
        /// </summary>
        private void CancelAllOperations()
        {
            try
            {
                // 取消所有异步操作
                _cancellationTokenSource?.Cancel();
                
                // 停止录音
                if (_isRecording)
                {
                    _asrService?.StopRecording();
                    _isRecording = false;
                }

                Logger.Log("VoiceInput: All operations cancelled");
            }
            catch (Exception ex)
            {
                Logger.Log($"VoiceInput: Error cancelling operations: {ex.Message}");
            }
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
            // 立即熔断所有操作
            CancelAllOperations();
            Close();
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            // 获取用户编辑后的文本
            var editedText = TranscriptionTextBox.Text?.Trim();
            
            if (!string.IsNullOrEmpty(editedText))
            {
                // 触发事件
                TranscriptionCompleted?.Invoke(this, editedText);
            }
            
            Close();
        }

        private void StartRecording()
        {
            try
            {
                // 检查是否已取消
                if (_cancellationTokenSource?.IsCancellationRequested == true)
                {
                    return;
                }

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
                // 检查是否已取消
                if (_cancellationTokenSource?.IsCancellationRequested == true)
                {
                    return;
                }

                _isRecording = true;
                RecordButton.Content = LanguageHelper.Get("VoiceInput.StopRecording", _plugin.Settings.Language) ?? "停止录音";
                StatusText.Text = LanguageHelper.Get("VoiceInput.Recording", _plugin.Settings.Language) ?? "正在录音...";
                StatusHint.Text = LanguageHelper.Get("VoiceInput.RecordingHint", _plugin.Settings.Language) ?? "再次点击停止录音";
                
                // 显示录音动画
                RecordingPulse.Visibility = Visibility.Visible;
                StartPulseAnimation();
            });
        }

        private void OnRecordingStopped(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // 检查是否已取消
                if (_cancellationTokenSource?.IsCancellationRequested == true)
                {
                    return;
                }

                _isRecording = false;
                RecordButton.Content = LanguageHelper.Get("VoiceInput.StartRecording", _plugin.Settings.Language) ?? "开始录音";
                StatusText.Text = LanguageHelper.Get("VoiceInput.Processing", _plugin.Settings.Language) ?? "正在处理...";
                StatusHint.Text = "";
                
                // 停止录音动画
                RecordingPulse.Visibility = Visibility.Collapsed;
                StopPulseAnimation();
            });
        }

        private void OnTranscriptionCompleted(object? sender, string transcription)
        {
            Dispatcher.Invoke(async () =>
            {
                // 检查是否已取消
                if (_cancellationTokenSource?.IsCancellationRequested == true)
                {
                    return;
                }

                Logger.Log($"VoiceInput: Transcription completed: {transcription}");
                _recognizedText = transcription;

                // 立即返回模式（快速显示）
                if (_isQuickDisplayMode || _plugin.Settings.ASR.AutoSend)
                {
                    await ShowQuickDisplay(transcription);
                }
                else
                {
                    // 可编辑模式
                    ShowEditableTranscription(transcription);
                }
            });
        }

        /// <summary>
        /// 显示可编辑的转录结果
        /// </summary>
        private void ShowEditableTranscription(string transcription)
        {
            RecordingPanel.Visibility = Visibility.Collapsed;
            TranscriptionPanel.Visibility = Visibility.Visible;
            TranscriptionTextBox.Text = transcription;
            TranscriptionTextBox.Focus();
            TranscriptionTextBox.SelectAll();
        }

        /// <summary>
        /// 快速显示模式：显示气泡框并自动消失
        /// </summary>
        private async Task ShowQuickDisplay(string transcription)
        {
            RecordingPanel.Visibility = Visibility.Collapsed;
            QuickDisplayPanel.Visibility = Visibility.Visible;
            QuickDisplayText.Text = transcription;

            // 触发事件
            TranscriptionCompleted?.Invoke(this, transcription);

            // 根据文字长度计算阅读时间（每个字约 0.3 秒，最少 2 秒，最多 8 秒）
            int charCount = transcription.Length;
            double readingTime = Math.Max(2, Math.Min(8, charCount * 0.3));

            Logger.Log($"VoiceInput: Quick display for {readingTime:F1} seconds");

            try
            {
                // 等待阅读时间
                await Task.Delay(TimeSpan.FromSeconds(readingTime), _cancellationTokenSource.Token);

                // 渐变消失动画
                var fadeOut = new DoubleAnimation
                {
                    From = 1.0,
                    To = 0.0,
                    Duration = TimeSpan.FromSeconds(0.3),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };

                fadeOut.Completed += (s, e) =>
                {
                    Close();
                };

                BubbleContainer.BeginAnimation(OpacityProperty, fadeOut);
            }
            catch (TaskCanceledException)
            {
                // 任务被取消，直接关闭
                Logger.Log("VoiceInput: Quick display cancelled");
            }
        }

        private void OnTranscriptionError(object? sender, string error)
        {
            Dispatcher.Invoke(() =>
            {
                // 检查是否已取消
                if (_cancellationTokenSource?.IsCancellationRequested == true)
                {
                    return;
                }

                Logger.Log($"VoiceInput: Transcription error: {error}");
                StatusText.Text = $"错误: {error}";
                StatusHint.Text = "";
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
            
            // 清理资源
            _cancellationTokenSource?.Dispose();
            _asrService?.Dispose();
        }
    }
}
