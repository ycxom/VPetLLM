using System.Text;
using System.Windows;
using VPetLLM.Core.Services;

namespace VPetLLM.Utils.Audio
{
    /// <summary>
    /// 静音占位动画保持器：mpv 播放真实音频的同时，把一段静音 WAV 交给宿主的
    /// Main.PlayVoice 占位。宿主的 MessageBar 依据 PlayingVoice + VoicePlayer.Clock
    /// 剩余时长保持说话动画/气泡（与 EdgeTTS 插件同机制）——mpv 高音质与动画保持兼得。
    ///
    /// 占位文件是固定 10 分钟静音（8kHz/8bit/单声道，一次性生成复用），
    /// 不需要匹配真实音频时长：真实播放结束时 End() 主动复位宿主状态即可。
    /// </summary>
    public static class SilentVoiceAnimationHold
    {
        private const int SilenceDurationSeconds = 600; // 与播放安全上限一致（10 分钟）
        private const int SampleRate = 8000;

        private static readonly object _fileLock = new object();
        private static string _silentWavPath;

        /// <summary>
        /// 开始占位：让宿主进入"语音播放中"状态。宿主不可用或生成失败返回 false。
        /// </summary>
        public static bool Begin()
        {
            var main = VPetLLM.Instance?.MW?.Main;
            if (main is null) return false;

            try
            {
                var path = EnsureSilentWav();
                if (path is null) return false;

                var uri = new Uri(path);
                Application.Current.Dispatcher.Invoke(() => main.PlayVoice(uri));
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"SilentVoiceAnimationHold: 启动占位失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 结束占位：停掉宿主的静音 Clock 并复位 PlayingVoice，
        /// MessageBar 下一个 tick 即执行正常收尾（C_End 动画 + 气泡倒计时）。
        /// </summary>
        public static void End()
        {
            var mainWindow = VPetLLM.Instance?.MW;
            var main = mainWindow?.Main;
            if (main is null) return;

            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (VPetHostAdapter.GetVoicePlayer(mainWindow) is global::System.Windows.Controls.MediaElement voicePlayer)
                    {
                        voicePlayer.Clock?.Controller?.Stop();
                        voicePlayer.Clock = null;
                    }
                    main.PlayingVoice = false;
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"SilentVoiceAnimationHold: 结束占位失败: {ex.Message}");
            }
        }

        private static string EnsureSilentWav()
        {
            lock (_fileLock)
            {
                if (_silentWavPath is not null && File.Exists(_silentWavPath))
                    return _silentWavPath;

                var path = Path.Combine(Path.GetTempPath(), $"VPetLLM_silence_{SilenceDurationSeconds}s.wav");
                if (!File.Exists(path))
                {
                    WriteSilentWav(path);
                    Logger.Log($"SilentVoiceAnimationHold: 已生成静音占位文件: {path}");
                }

                _silentWavPath = path;
                return path;
            }
        }

        private static void WriteSilentWav(string path)
        {
            // 8kHz 8-bit 单声道 PCM：无符号 8 位的静音电平是 0x80
            var dataSize = SampleRate * SilenceDurationSeconds;

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(fs);

            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);              // fmt 块大小
            writer.Write((short)1);        // PCM
            writer.Write((short)1);        // 单声道
            writer.Write(SampleRate);      // 采样率
            writer.Write(SampleRate);      // 字节率（8bit 单声道 = 采样率）
            writer.Write((short)1);        // 块对齐
            writer.Write((short)8);        // 位深
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);

            var silence = new byte[64 * 1024];
            Array.Fill(silence, (byte)0x80);
            var remaining = dataSize;
            while (remaining > 0)
            {
                var count = Math.Min(remaining, silence.Length);
                writer.Write(silence, 0, count);
                remaining -= count;
            }
        }
    }
}
