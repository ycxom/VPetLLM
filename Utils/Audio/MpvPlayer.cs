using System.Collections.Concurrent;
using System.Diagnostics;

namespace VPetLLM.Utils.Audio
{
    /// <summary>
    /// 基于 mpv.exe 的媒体播放器
    /// 支持音频和视频播放
    /// </summary>
    public class MpvPlayer : IMediaPlayer
    {
        private Process? _process;
        private readonly object _lock = new object();
        private bool _isPlaying = false;
        private readonly string _mpvExePath;
        private double _volume = 100.0;
        private double _gain = 0.0;

        /// <summary>
        /// 本次使用的 mpv 是否认识 --media-controls。按 exe 路径缓存，
        /// 一个进程生命周期内每个 mpv 只探测一次。
        /// </summary>
        private readonly bool _supportsMediaControls;

        private static readonly ConcurrentDictionary<string, bool> _mediaControlsSupportCache =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public string Name => "mpv";

        public bool IsPlaying
        {
            get
            {
                lock (_lock)
                {
                    return _isPlaying;
                }
            }
        }

        public MpvPlayer(string mpvExePath)
        {
            _mpvExePath = mpvExePath;

            if (!File.Exists(_mpvExePath))
            {
                throw new FileNotFoundException($"mpv.exe 未找到: {_mpvExePath}");
            }

            _supportsMediaControls = SupportsMediaControls(_mpvExePath);
            Logger.Log($"mpv 播放器初始化成功: {_mpvExePath}"
                + (_supportsMediaControls
                    ? "（已关闭 SMTC 集成）"
                    : "（该 mpv 不支持 --media-controls，语音仍会出现在系统媒体控制中，建议升级到 mpv 0.38+）"));
        }

        /// <summary>
        /// 关闭 mpv 与 Windows 系统媒体传输控件（SMTC）的集成。
        ///
        /// mpv 默认会把正在播放的内容注册成一个系统媒体会话：桌宠每说一句话，
        /// 系统就多出一条"正在播放"，媒体键被抢走，依赖 SMTC 的第三方程序
        /// （歌词、手表联动等）也会被这几秒的语音顶掉当前曲目，而且 mpv 进程退出后
        /// 这条会话信息常常还残留一会儿不消失。TTS 是几秒钟的语音而非媒体内容，
        /// 压根不该出现在系统媒体控制里，所以直接从源头关掉，不去注册就没有释放问题。
        ///
        /// 注意这只针对语音播放。MediaPlaybackService 那边是用户主动点播的网络视频/音乐，
        /// 有窗口、可暂停，本来就该出现在系统媒体控制里，不要一起关掉。
        /// </summary>
        private const string DisableMediaControlsArg = "--media-controls=no";

        /// <summary>探测 mpv 能力的超时</summary>
        private const int CapabilityProbeMs = 3000;

        /// <summary>
        /// 探测 mpv 是否支持 <see cref="DisableMediaControlsArg"/>。
        ///
        /// 该选项 mpv 0.38 才引入，而 mpv.exe 由用户自行放到插件目录，版本不可控。
        /// mpv 碰到不认识的选项会直接以退出码 1 失败 —— 无脑加上去的话，老版本用户
        /// 会变成每句话都播放失败。所以先花一次进程启动的代价问清楚，结果按路径缓存。
        /// </summary>
        private static bool SupportsMediaControls(string mpvExePath)
        {
            return _mediaControlsSupportCache.GetOrAdd(mpvExePath, ProbeMediaControlsSupport);
        }

        private static bool ProbeMediaControlsSupport(string mpvExePath)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = mpvExePath,
                    Arguments = $"{DisableMediaControlsArg} --version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(mpvExePath)
                };

                using var probe = Process.Start(startInfo);
                if (probe is null)
                    return false;

                // 先挂上异步读取再等待：不排空管道的话，输出填满缓冲区会让 mpv 卡在写入上
                _ = probe.StandardOutput.ReadToEndAsync();
                _ = probe.StandardError.ReadToEndAsync();

                if (!probe.WaitForExit(CapabilityProbeMs))
                {
                    try { probe.Kill(); } catch { }
                    return false;
                }

                return probe.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Logger.Log($"mpv: 探测 --media-controls 支持失败，按不支持处理: {ex.Message}");
                return false;
            }
        }

        public async Task PlayAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                Logger.Log($"mpv: 文件不存在: {filePath}");
                return;
            }

            try
            {
                lock (_lock)
                {
                    _isPlaying = true;
                }

                Logger.Log($"mpv: 开始播放: {filePath}");

                // 构建命令行参数。
                // 命令行上的 --media-controls 会覆盖用户 mpv.conf 里的同名设置，
                // 所以只要 mpv 认这个选项，SMTC 集成就一定是关的。
                var args = $"--no-video --volume={_volume}";

                if (_supportsMediaControls)
                {
                    args += $" {DisableMediaControlsArg}";
                }

                // 当增益不为0时，添加音频滤镜参数
                if (Math.Abs(_gain) > 0.01)
                {
                    args += $" --af=volume={_gain}dB";
                }

                args += $" \"{filePath}\"";

                // 创建进程启动信息
                var startInfo = new ProcessStartInfo
                {
                    FileName = _mpvExePath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _process = new Process { StartInfo = startInfo };
                _process.Start();

                // 挂进 Job：宿主进程无论怎么没的（VPet 退出走的是 Environment.Exit，
                // 插件清理根本跑不到），系统都会把 mpv 一起收走
                global::VPetLLM.Utils.System.ChildProcessTracker.Track(_process);

                // 等待进程结束
                await _process.WaitForExitAsync();

                Logger.Log($"mpv: 播放完成: {filePath}");

                lock (_lock)
                {
                    _isPlaying = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"mpv 播放错误: {ex.Message}");
                lock (_lock)
                {
                    _isPlaying = false;
                }
            }
        }

        public void Stop()
        {
            try
            {
                if (_process is not null && !_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(1000);
                }

                lock (_lock)
                {
                    _isPlaying = false;
                }

                Logger.Log("mpv: 已停止播放");
            }
            catch (Exception ex)
            {
                Logger.Log($"mpv 停止播放错误: {ex.Message}");
            }
        }

        public void SetVolume(double volume)
        {
            // mpv 音量范围是 0-100
            _volume = Math.Clamp(volume, 0.0, 100.0);
        }

        public void SetGain(double gainDb)
        {
            // mpv 增益范围是 -200dB 到 +60dB
            _gain = Math.Clamp(gainDb, -200.0, 60.0);
        }

        public void Dispose()
        {
            try
            {
                Stop();
                _process?.Dispose();
                _process = null;

                Logger.Log("mpv: 资源已释放");
            }
            catch (Exception ex)
            {
                Logger.Log($"mpv 释放资源错误: {ex.Message}");
            }
        }
    }
}
