using System.Diagnostics;
using System.Runtime.InteropServices;
using VPetLLM.Infrastructure.Configuration;
using VPetLLM.Infrastructure.Events;
using VPetLLM.Infrastructure.Logging;

namespace VPetLLM.Infrastructure.Services.ApplicationServices
{
    /// <summary>
    /// 媒体播放服务 - 使用新架构重�?
    /// 使用 mpv 播放器播放网络视频和音乐
    /// </summary>
    public class MediaPlaybackService : ServiceBase<MediaPlaybackConfiguration>
    {
        #region Windows API

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;

        #endregion

        private Process? _mpvProcess;
        private readonly object _lock = new object();
        private CancellationTokenSource? _monitorCts;
        private Task? _monitorTask;
        private string? _currentUrl;

        public override string ServiceName => "MediaPlaybackService";
        public override Version Version => new Version(2, 0, 0);

        /// <summary>
        /// 是否正在播放
        /// </summary>
        public bool IsPlaying
        {
            get
            {
                lock (_lock)
                {
                    return _mpvProcess is not null && !_mpvProcess.HasExited;
                }
            }
        }

        /// <summary>
        /// 当前播放�?URL
        /// </summary>
        public string? CurrentUrl => _currentUrl;

        /// <summary>
        /// 播放状态变更事�?
        /// </summary>
        public event EventHandler<MediaPlaybackEventArgs>? PlaybackStateChanged;

        public MediaPlaybackService(
            MediaPlaybackConfiguration configuration,
            IStructuredLogger logger,
            IEventBus eventBus)
            : base(configuration, logger, eventBus)
        {
        }

        protected override async Task OnInitializeAsync(CancellationToken cancellationToken)
        {
            LogInformation("Initializing media playback service");

            // 验证 mpv 路径
            if (!File.Exists(Configuration.MpvExePath))
            {
                LogWarning($"mpv.exe not found at: {Configuration.MpvExePath}");
            }
            else
            {
                LogInformation($"mpv.exe found at: {Configuration.MpvExePath}");
            }

            // 订阅配置变更事件
            await _eventBus.SubscribeAsync<ConfigurationChangedEvent<MediaPlaybackConfiguration>>(OnConfigurationChanged);

            LogInformation("Media playback service initialized successfully");
        }

        protected override async Task OnStartAsync(CancellationToken cancellationToken)
        {
            LogInformation("Starting media playback service");
            await Task.CompletedTask;
            LogInformation("Media playback service started successfully");
        }

        protected override async Task OnStopAsync(CancellationToken cancellationToken)
        {
            LogInformation("Stopping media playback service");

            // 停止当前播放
            StopPlayback();

            LogInformation("Media playback service stopped successfully");
        }

        protected override async Task OnHealthCheckAsync(CancellationToken cancellationToken)
        {
            // 检�?mpv 进程状�?
            if (IsPlaying)
            {
                lock (_lock)
                {
                    if (_mpvProcess is not null && _mpvProcess.HasExited)
                    {
                        LogWarning("mpv process has exited unexpectedly");
                    }
                }
            }

            await Task.CompletedTask;
        }

        private async Task OnConfigurationChanged(ConfigurationChangedEvent<MediaPlaybackConfiguration> evt)
        {
            LogInformation("Media playback configuration changed");

            // 如果 mpv 路径改变，验证新路径
            if (evt.OldConfiguration.MpvExePath != evt.NewConfiguration.MpvExePath)
            {
                if (!File.Exists(evt.NewConfiguration.MpvExePath))
                {
                    LogWarning($"New mpv path not found: {evt.NewConfiguration.MpvExePath}");
                }
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// 播放媒体
        /// </summary>
        public async Task<bool> PlayAsync(string url, int volume = 100)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                LogWarning("Play requested with empty URL");
                OnPlaybackStateChangedInternal(false, null, "URL is empty");
                return false;
            }

            if (!File.Exists(Configuration.MpvExePath))
            {
                var error = $"mpv.exe not found: {Configuration.MpvExePath}";
                LogError(error);
                OnPlaybackStateChangedInternal(false, url, error);
                return false;
            }

            // 停止当前播放
            StopPlayback();

            try
            {
                // 钳制音量
                volume = Math.Clamp(volume, 0, 100);

                LogInformation($"Starting playback: {url}, volume: {volume}");

                // 创建进程启动信息
                var startInfo = new ProcessStartInfo
                {
                    FileName = Configuration.MpvExePath,
                    Arguments = $"--force-window=yes --keep-open=yes --volume={volume} \"{url}\"",
                    UseShellExecute = false,
                    CreateNoWindow = false,
                };

                lock (_lock)
                {
                    _mpvProcess = new Process { StartInfo = startInfo };
                    _mpvProcess.EnableRaisingEvents = true;
                    _mpvProcess.Exited += OnProcessExited;
                    _mpvProcess.Start();
                    _currentUrl = url;
                }

                LogInformation($"mpv process started, PID: {_mpvProcess.Id}");
                OnPlaybackStateChangedInternal(true, url, null);

                // 发布播放开始事�?
                await _eventBus.PublishAsync(new MediaPlaybackStartedEvent
                {
                    Url = url,
                    Volume = volume,
                    ProcessId = _mpvProcess.Id
                });

                // 启动窗口可见性监�?
                if (Configuration.MonitorWindowVisibility)
                {
                    StartWindowMonitor();
                }

                return true;
            }
            catch (Exception ex)
            {
                var error = $"Failed to start mpv: {ex.Message}";
                LogError("Failed to start playback", ex);
                OnPlaybackStateChangedInternal(false, url, error);
                return false;
            }
        }

        /// <summary>
        /// 停止播放
        /// </summary>
        public void StopPlayback()
        {
            try
            {
                // 停止窗口监控
                StopWindowMonitor();

                lock (_lock)
                {
                    if (_mpvProcess is not null)
                    {
                        if (!_mpvProcess.HasExited)
                        {
                            _mpvProcess.Kill();
                            _mpvProcess.WaitForExit(1000);
                            LogInformation("mpv process stopped");
                        }
                        _mpvProcess.Dispose();
                        _mpvProcess = null;
                    }
                    _currentUrl = null;
                }

                OnPlaybackStateChangedInternal(false, null, null);

                // 发布播放停止事件
                _ = _eventBus.PublishAsync(new MediaPlaybackStoppedEvent
                {
                    StopTime = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                LogError("Error stopping playback", ex);
            }
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            LogInformation("mpv process exited");
            StopWindowMonitor();

            lock (_lock)
            {
                _mpvProcess?.Dispose();
                _mpvProcess = null;
                _currentUrl = null;
            }

            OnPlaybackStateChangedInternal(false, null, null);

            // 发布播放结束事件
            _ = _eventBus.PublishAsync(new MediaPlaybackEndedEvent
            {
                EndTime = DateTime.Now
            });
        }

        private void OnPlaybackStateChangedInternal(bool isPlaying, string? url, string? error)
        {
            PlaybackStateChanged?.Invoke(this, new MediaPlaybackEventArgs
            {
                IsPlaying = isPlaying,
                CurrentUrl = url,
                ErrorMessage = error
            });

            // 发布状态变更事�?
            _ = _eventBus.PublishAsync(new MediaPlaybackStateChangedEvent
            {
                IsPlaying = isPlaying,
                Url = url,
                ErrorMessage = error,
                Timestamp = DateTime.Now
            });
        }

        #region Window Visibility Monitoring

        private void StartWindowMonitor()
        {
            StopWindowMonitor();

            _monitorCts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorWindowVisibility(_monitorCts.Token));
            LogInformation("Window visibility monitoring started");
        }

        private void StopWindowMonitor()
        {
            if (_monitorCts is not null)
            {
                _monitorCts.Cancel();
                _monitorCts.Dispose();
                _monitorCts = null;
            }
            _monitorTask = null;
        }

        private async Task MonitorWindowVisibility(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(Configuration.WindowCheckIntervalMs, ct);

                    Process? process;
                    lock (_lock)
                    {
                        process = _mpvProcess;
                    }

                    if (process is null || process.HasExited)
                        break;

                    var mainWindowHandle = process.MainWindowHandle;
                    if (mainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    // 检查窗口是否可�?
                    bool isVisible = IsWindowVisible(mainWindowHandle);
                    bool isMinimized = IsIconic(mainWindowHandle);

                    if (!isVisible || isMinimized)
                    {
                        LogDebug("mpv window not visible or minimized, attempting to restore");
                        RestoreWindow(mainWindowHandle);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogWarning($"Window monitoring error: {ex.Message}");
                }
            }

            LogInformation("Window visibility monitoring stopped");
        }

        private void RestoreWindow(IntPtr hWnd)
        {
            try
            {
                ShowWindow(hWnd, SW_RESTORE);
                ShowWindow(hWnd, SW_SHOW);
                SetForegroundWindow(hWnd);
                LogDebug("Window restored");
            }
            catch (Exception ex)
            {
                LogWarning($"Failed to restore window: {ex.Message}");
            }
        }

        #endregion

        protected override void OnDispose()
        {
            {
                StopPlayback();
            }

            // Disposed by base class
        }
    }

    /// <summary>
    /// 媒体播放事件参数
    /// </summary>
    public class MediaPlaybackEventArgs : EventArgs
    {
        public bool IsPlaying { get; set; }
        public string? CurrentUrl { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// 媒体播放开始事�?
    /// </summary>
    public class MediaPlaybackStartedEvent
    {
        public string Url { get; set; } = "";
        public int Volume { get; set; }
        public int ProcessId { get; set; }
    }

    /// <summary>
    /// 媒体播放停止事件
    /// </summary>
    public class MediaPlaybackStoppedEvent
    {
        public DateTime StopTime { get; set; }
    }

    /// <summary>
    /// 媒体播放结束事件
    /// </summary>
    public class MediaPlaybackEndedEvent
    {
        public DateTime EndTime { get; set; }
    }

    /// <summary>
    /// 媒体播放状态变更事�?
    /// </summary>
    public class MediaPlaybackStateChangedEvent
    {
        public bool IsPlaying { get; set; }
        public string? Url { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// 媒体播放配置
    /// </summary>
    public class MediaPlaybackConfiguration : IConfiguration
    {
        public string ConfigurationName => "MediaPlaybackConfiguration";
        public Version Version => new Version(1, 0, 0);
        public DateTime LastModified { get; set; } = DateTime.Now;
        public bool IsModified { get; set; }

        public string MpvExePath { get; set; } = "mpv.exe";
        public string MpvPath { get; set; } = "mpv.exe"; // 向后兼容属性
        public bool MonitorWindowVisibility { get; set; } = true;
        public int WindowCheckIntervalMs { get; set; } = 1000;

        public IConfiguration Clone()
        {
            return new MediaPlaybackConfiguration
            {
                MpvExePath = this.MpvExePath,
                MpvPath = this.MpvPath,
                MonitorWindowVisibility = this.MonitorWindowVisibility,
                WindowCheckIntervalMs = this.WindowCheckIntervalMs,
                LastModified = this.LastModified,
                IsModified = this.IsModified
            };
        }

        public void Merge(IConfiguration other)
        {
            if (other is MediaPlaybackConfiguration config)
            {
                MpvExePath = config.MpvExePath;
                MpvPath = config.MpvPath;
                MonitorWindowVisibility = config.MonitorWindowVisibility;
                WindowCheckIntervalMs = config.WindowCheckIntervalMs;
                IsModified = true;
                LastModified = DateTime.Now;
            }
        }

        public void ResetToDefaults()
        {
            MpvExePath = "mpv.exe";
            MpvPath = "mpv.exe";
            MonitorWindowVisibility = true;
            WindowCheckIntervalMs = 1000;
            IsModified = true;
            LastModified = DateTime.Now;
        }

        public SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(MpvExePath))
            {
                result.IsValid = false;
                result.Errors.Add("MpvExePath cannot be empty");
            }

            if (WindowCheckIntervalMs < 0)
            {
                result.IsValid = false;
                result.Errors.Add("WindowCheckIntervalMs must be non-negative");
            }

            return result;
        }
    }
}
