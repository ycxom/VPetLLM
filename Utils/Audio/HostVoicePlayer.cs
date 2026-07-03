using System.Windows;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core.Services;

namespace VPetLLM.Utils.Audio
{
    /// <summary>
    /// VPet 宿主播放器：把音频交给 Main.PlayVoice 播放。
    ///
    /// 收益：宿主的说话动画/气泡保持闭环自动生效——MessageBar 打字机在
    /// PlayingVoice 且剩余时长 &gt; 2 秒时拒绝收尾（与 EdgeTTS 插件同机制），
    /// 无需插件侧任何时序估算。
    /// 限制：单通道（PlayVoice 全局一个 VoicePlayer）；不支持音量增益。
    /// </summary>
    public class HostVoicePlayer : IMediaPlayer
    {
        private const int PollIntervalMs = 200;
        private const int MaxPlaybackMs = 10 * 60 * 1000; // 安全上限 10 分钟
        private const int StartupGraceMs = 500;           // 给 Clock 建立留时间

        private readonly IMainWindow _mainWindow;
        private volatile bool _stopRequested;

        public HostVoicePlayer(IMainWindow mainWindow)
        {
            _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        }

        public string Name => "VPet 内置播放器";

        public bool IsPlaying => _mainWindow?.Main?.PlayingVoice == true;

        /// <summary>
        /// 播放音频文件并等待播放完成（阻塞语义与 MpvPlayer 一致，保证分段串行）。
        /// </summary>
        public async Task PlayAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                throw new FileNotFoundException($"音频文件不存在: {filePath}");
            }

            var main = _mainWindow?.Main;
            if (main is null)
            {
                throw new InvalidOperationException("VPet 宿主不可用，无法使用宿主播放器");
            }

            _stopRequested = false;
            var uri = new Uri(Path.GetFullPath(filePath));

            await Application.Current.Dispatcher.InvokeAsync(() => main.PlayVoice(uri));

            // PlayVoice 首行同步置 PlayingVoice=true，Clock_Completed 置回 false。
            // 轮询等待播放结束；MessageBar 在此期间自动保持说话动画。
            await Task.Delay(StartupGraceMs);

            var elapsed = StartupGraceMs;
            while (main.PlayingVoice && !_stopRequested && elapsed < MaxPlaybackMs)
            {
                await Task.Delay(PollIntervalMs);
                elapsed += PollIntervalMs;
            }

            if (elapsed >= MaxPlaybackMs)
            {
                Logger.Log($"HostVoicePlayer: 播放等待达到安全上限 {MaxPlaybackMs}ms，停止等待");
            }
        }

        /// <summary>
        /// 停止播放：停掉宿主 VoicePlayer 的 Clock 并复位 PlayingVoice。
        /// </summary>
        public void Stop()
        {
            _stopRequested = true;

            var main = _mainWindow?.Main;
            if (main is null) return;

            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (VPetHostAdapter.GetVoicePlayer(_mainWindow) is global::System.Windows.Controls.MediaElement voicePlayer)
                    {
                        voicePlayer.Clock?.Controller?.Stop();
                        voicePlayer.Clock = null;
                    }
                    main.PlayingVoice = false;
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"HostVoicePlayer: 停止播放失败: {ex.Message}");
            }
        }

        public void SetVolume(double volume)
        {
            try
            {
                // 宿主音量属性内部已做 Dispatcher 调度
                _mainWindow.Main.PlayVoiceVolume = Math.Clamp(volume, 0, 100) / 100.0;
            }
            catch (Exception ex)
            {
                Logger.Log($"HostVoicePlayer: 设置音量失败: {ex.Message}");
            }
        }

        public void SetGain(double gainDb)
        {
            // 宿主播放器不支持增益，静默忽略（设置界面已提示该限制）
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
