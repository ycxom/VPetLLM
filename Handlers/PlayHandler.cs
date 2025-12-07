using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Services;
using VPetLLM.Utils;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// 处理 LLM 发出的媒体播放命令
    /// 命令格式：<|play_begin|> url, volume <|play_end|> 或 <|play_begin|> stop <|play_end|>
    /// </summary>
    public class PlayHandler : IActionHandler
    {
        private readonly IMediaPlaybackService _mediaPlaybackService;

        public string Keyword => "play";
        public ActionType ActionType => ActionType.Tool;
        public ActionCategory Category => ActionCategory.Interactive;
        public string Description => PromptHelper.Get("Handler_Play_Description", VPetLLM.Instance?.Settings?.PromptLanguage ?? "zh");

        public PlayHandler(IMediaPlaybackService mediaPlaybackService)
        {
            _mediaPlaybackService = mediaPlaybackService ?? throw new ArgumentNullException(nameof(mediaPlaybackService));
        }

        public async Task Execute(string value, IMainWindow mainWindow)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Logger.Log("PlayHandler: 命令参数为空");
                return;
            }

            value = value.Trim();
            Logger.Log($"PlayHandler: 收到命令: {value}");

            // 检查是否为停止命令
            if (value.Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log("PlayHandler: 执行停止播放");
                _mediaPlaybackService.Stop();
                return;
            }

            // 解析 URL 和音量
            var (url, volume) = ParseCommand(value);

            if (string.IsNullOrWhiteSpace(url))
            {
                Logger.Log("PlayHandler: 无法解析 URL");
                return;
            }

            // 获取默认音量
            if (volume == null)
            {
                volume = VPetLLM.Instance?.Settings?.MediaPlayback?.DefaultVolume ?? 100;
            }

            Logger.Log($"PlayHandler: 播放 URL: {url}, 音量: {volume}");
            var success = await _mediaPlaybackService.PlayAsync(url, volume.Value);
            
            if (success)
            {
                Logger.Log("PlayHandler: 播放启动成功");
            }
            else
            {
                Logger.Log("PlayHandler: 播放启动失败");
            }
        }

        /// <summary>
        /// 解析命令参数，提取 URL 和音量
        /// 支持格式：
        /// - url
        /// - url, volume
        /// - "url", volume
        /// </summary>
        private (string? url, int? volume) ParseCommand(string value)
        {
            // 尝试匹配带引号的 URL
            var quotedMatch = Regex.Match(value, @"""([^""]+)""(?:\s*,\s*(\d+))?");
            if (quotedMatch.Success)
            {
                var url = quotedMatch.Groups[1].Value;
                int? volume = null;
                if (quotedMatch.Groups[2].Success && int.TryParse(quotedMatch.Groups[2].Value, out var v))
                {
                    volume = v;
                }
                return (url, volume);
            }

            // 尝试匹配不带引号的格式：url, volume 或 url
            var parts = value.Split(',');
            if (parts.Length >= 1)
            {
                var url = parts[0].Trim();
                int? volume = null;
                
                if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out var v))
                {
                    volume = v;
                }
                
                return (url, volume);
            }

            return (null, null);
        }

        public Task Execute(int value, IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }

        public Task Execute(IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }

        public int GetAnimationDuration(string animationName) => 0;
    }
}
