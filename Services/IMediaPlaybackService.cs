using System;
using System.Threading.Tasks;

namespace VPetLLM.Services
{
    /// <summary>
    /// 媒体播放状态变更事件参数
    /// </summary>
    public class MediaPlaybackEventArgs : EventArgs
    {
        public bool IsPlaying { get; set; }
        public string? CurrentUrl { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// 媒体播放服务接口
    /// 负责管理 mpv 媒体播放进程和窗口状态监控
    /// </summary>
    public interface IMediaPlaybackService : IDisposable
    {
        /// <summary>
        /// 是否正在播放
        /// </summary>
        bool IsPlaying { get; }

        /// <summary>
        /// 当前播放的 URL
        /// </summary>
        string? CurrentUrl { get; }

        /// <summary>
        /// 播放媒体
        /// </summary>
        /// <param name="url">媒体 URL</param>
        /// <param name="volume">音量 (0-100)，默认 100</param>
        /// <returns>是否成功启动播放</returns>
        Task<bool> PlayAsync(string url, int volume = 100);

        /// <summary>
        /// 停止播放
        /// </summary>
        void Stop();

        /// <summary>
        /// 播放状态变更事件
        /// </summary>
        event EventHandler<MediaPlaybackEventArgs>? PlaybackStateChanged;
    }
}
