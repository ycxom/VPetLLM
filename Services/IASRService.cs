namespace VPetLLM.Services
{
    /// <summary>
    /// ASR (Automatic Speech Recognition) 服务接口
    /// </summary>
    public interface IASRService : IDisposable
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
        /// 是否正在录音
        /// </summary>
        bool IsRecording { get; }

        /// <summary>
        /// 将音频数据转录为文本
        /// </summary>
        /// <param name="audioData">音频数据字节数组</param>
        /// <returns>转录的文本</returns>
        Task<string?> TranscribeAsync(byte[] audioData);

        /// <summary>
        /// 将音频流转录为文本
        /// </summary>
        /// <param name="audioStream">音频流</param>
        /// <returns>转录的文本</returns>
        Task<string?> TranscribeAsync(Stream audioStream);

        /// <summary>
        /// 开始录音
        /// </summary>
        void StartRecording();

        /// <summary>
        /// 停止录音并返回转录结果
        /// </summary>
        /// <returns>转录的文本</returns>
        Task<string?> StopRecordingAsync();

        /// <summary>
        /// 取消录音
        /// </summary>
        void CancelRecording();

        /// <summary>
        /// 转录完成事件
        /// </summary>
        event EventHandler<string>? TranscriptionCompleted;

        /// <summary>
        /// 转录错误事件
        /// </summary>
        event EventHandler<Exception>? TranscriptionError;
    }
}
