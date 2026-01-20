using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VPetLLM.Services
{
    /// <summary>
    /// 媒体播放服务实现
    /// 使用 mpv 播放器播放网络视频和音乐，确保窗口始终可见
    /// </summary>
    public class MediaPlaybackService : IMediaPlaybackService
    {
        #region Windows API

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd); // 检查窗口是否最小化

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;

        #endregion

        private Process? _mpvProcess;
        private readonly object _lock = new object();
        private readonly string _mpvExePath;
        private readonly Setting.MediaPlaybackSetting _settings;
        private CancellationTokenSource? _monitorCts;
        private Task? _monitorTask;

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

        public string? CurrentUrl { get; private set; }

        public event EventHandler<MediaPlaybackEventArgs>? PlaybackStateChanged;

        public MediaPlaybackService(string mpvExePath, Setting.MediaPlaybackSetting settings)
        {
            _mpvExePath = mpvExePath;
            _settings = settings ?? new Setting.MediaPlaybackSetting();

            if (!File.Exists(_mpvExePath))
            {
                Logger.Log($"MediaPlaybackService: mpv.exe 未找到: {_mpvExePath}");
            }
            else
            {
                Logger.Log($"MediaPlaybackService: 初始化成功，mpv 路径: {_mpvExePath}");
            }
        }

        /// <summary>
        /// 钳制音量到有效范围 [0, 100]
        /// </summary>
        public static int ClampVolume(int volume)
        {
            return Math.Clamp(volume, 0, 100);
        }

        public async Task<bool> PlayAsync(string url, int volume = 100)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                Logger.Log("MediaPlaybackService: URL 为空");
                OnPlaybackStateChanged(false, null, "URL 为空");
                return false;
            }

            if (!File.Exists(_mpvExePath))
            {
                var error = $"mpv.exe 未找到: {_mpvExePath}";
                Logger.Log($"MediaPlaybackService: {error}");
                OnPlaybackStateChanged(false, url, error);
                return false;
            }

            // 停止当前播放
            Stop();

            try
            {
                // 钳制音量
                volume = ClampVolume(volume);

                Logger.Log($"MediaPlaybackService: 开始播放 {url}，音量: {volume}");

                // 创建进程启动信息
                // 关键参数：--force-window=yes 确保窗口显示，--keep-open=yes 播放完成后保持窗口
                var startInfo = new ProcessStartInfo
                {
                    FileName = _mpvExePath,
                    Arguments = $"--force-window=yes --keep-open=yes --volume={volume} \"{url}\"",
                    UseShellExecute = false,
                    CreateNoWindow = false, // 必须为 false 才能显示窗口
                };

                lock (_lock)
                {
                    _mpvProcess = new Process { StartInfo = startInfo };
                    _mpvProcess.EnableRaisingEvents = true;
                    _mpvProcess.Exited += OnProcessExited;
                    _mpvProcess.Start();
                    CurrentUrl = url;
                }

                Logger.Log($"MediaPlaybackService: mpv 进程已启动，PID: {_mpvProcess.Id}");
                OnPlaybackStateChanged(true, url, null);

                // 启动窗口可见性监控
                if (_settings.MonitorWindowVisibility)
                {
                    StartWindowMonitor();
                }

                return true;
            }
            catch (Exception ex)
            {
                var error = $"启动 mpv 失败: {ex.Message}";
                Logger.Log($"MediaPlaybackService: {error}");
                OnPlaybackStateChanged(false, url, error);
                return false;
            }
        }

        public void Stop()
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
                            Logger.Log("MediaPlaybackService: mpv 进程已停止");
                        }
                        _mpvProcess.Dispose();
                        _mpvProcess = null;
                    }
                    CurrentUrl = null;
                }

                OnPlaybackStateChanged(false, null, null);
            }
            catch (Exception ex)
            {
                Logger.Log($"MediaPlaybackService: 停止播放错误: {ex.Message}");
            }
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            Logger.Log("MediaPlaybackService: mpv 进程已退出");
            StopWindowMonitor();

            lock (_lock)
            {
                _mpvProcess?.Dispose();
                _mpvProcess = null;
                CurrentUrl = null;
            }

            OnPlaybackStateChanged(false, null, null);
        }

        private void OnPlaybackStateChanged(bool isPlaying, string? url, string? error)
        {
            PlaybackStateChanged?.Invoke(this, new MediaPlaybackEventArgs
            {
                IsPlaying = isPlaying,
                CurrentUrl = url,
                ErrorMessage = error
            });
        }

        #region Window Visibility Monitoring

        private void StartWindowMonitor()
        {
            StopWindowMonitor();

            _monitorCts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorWindowVisibility(_monitorCts.Token));
            Logger.Log("MediaPlaybackService: 窗口可见性监控已启动");
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
                    await Task.Delay(_settings.WindowCheckIntervalMs, ct);

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
                        // 窗口句柄无效，可能还在初始化
                        continue;
                    }

                    // 检查窗口是否可见
                    bool isVisible = IsWindowVisible(mainWindowHandle);
                    bool isMinimized = IsIconic(mainWindowHandle);

                    if (!isVisible || isMinimized)
                    {
                        Logger.Log($"MediaPlaybackService: 检测到 mpv 窗口不可见或最小化，尝试恢复");
                        RestoreWindow(mainWindowHandle);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Log($"MediaPlaybackService: 窗口监控错误: {ex.Message}");
                }
            }

            Logger.Log("MediaPlaybackService: 窗口可见性监控已停止");
        }

        private void RestoreWindow(IntPtr hWnd)
        {
            try
            {
                ShowWindow(hWnd, SW_RESTORE);
                ShowWindow(hWnd, SW_SHOW);
                SetForegroundWindow(hWnd);
                Logger.Log("MediaPlaybackService: 窗口已恢复");
            }
            catch (Exception ex)
            {
                Logger.Log($"MediaPlaybackService: 恢复窗口失败: {ex.Message}");
            }
        }

        #endregion

        public void Dispose()
        {
            Stop();
            Logger.Log("MediaPlaybackService: 资源已释放");
        }
    }
}
