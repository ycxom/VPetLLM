using System.Diagnostics;
using System.IO;

namespace VPetLLM.Utils
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

            Logger.Log($"mpv 播放器初始化成功: {_mpvExePath}");
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

                // 创建进程启动信息
                var startInfo = new ProcessStartInfo
                {
                    FileName = _mpvExePath,
                    Arguments = $"--no-video --volume={_volume} \"{filePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _process = new Process { StartInfo = startInfo };
                _process.Start();

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
                if (_process != null && !_process.HasExited)
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
