namespace VPetLLM.Services
{
    /// <summary>
    /// TTS (Text-to-Speech) 服务接口
    /// </summary>
    public interface ITTSService : IDisposable
    {
        /// <summary>
        /// 服务提供商名称
        /// </summary>
        string ProviderName { get; }

        /// <summary>
        /// 服务是否启用
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// 是否正在播放
        /// </summary>
        bool IsPlaying { get; }

        /// <summary>
        /// 将文本合成为音频数据
        /// </summary>
        /// <param name="text">要合成的文本</param>
        /// <returns>音频数据字节数组</returns>
        Task<byte[]?> SynthesizeAsync(string text);

        /// <summary>
        /// 将文本合成并播放
        /// </summary>
        /// <param name="text">要播放的文本</param>
        Task PlayAsync(string text);

        /// <summary>
        /// 停止当前播放
        /// </summary>
        void Stop();

        /// <summary>
        /// 设置音量
        /// </summary>
        /// <param name="volume">音量值 (0.0 - 1.0)</param>
        void SetVolume(double volume);

        /// <summary>
        /// 设置语速
        /// </summary>
        /// <param name="speed">语速值</param>
        void SetSpeed(double speed);
    }
}
