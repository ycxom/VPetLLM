namespace VPetLLM.Utils.Audio
{
    /// <summary>
    /// 音频/视频播放器接口
    /// </summary>
    public interface IMediaPlayer : IDisposable
    {
        /// <summary>
        /// 播放音频文件
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <returns>播放任务</returns>
        Task PlayAsync(string filePath);

        /// <summary>
        /// 停止播放
        /// </summary>
        void Stop();

        /// <summary>
        /// 设置音量 (0.0 - 100.0)
        /// </summary>
        /// <param name="volume">音量值</param>
        void SetVolume(double volume);

        /// <summary>
        /// 设置音量增益 (dB)
        /// </summary>
        /// <param name="gainDb">增益值，单位dB</param>
        void SetGain(double gainDb);

        /// <summary>
        /// 是否正在播放
        /// </summary>
        bool IsPlaying { get; }

        /// <summary>
        /// 播放器名称
        /// </summary>
        string Name { get; }
    }
}
