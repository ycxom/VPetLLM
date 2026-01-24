namespace VPetLLM.Core.TTS;

/// <summary>
/// 定义所有 TTS 提供者的契约
/// 每个提供者实现自己的音频生成、播放和时长估算逻辑
/// </summary>
public interface ITTSProvider
{
    /// <summary>
    /// 提供者名称，用于识别和日志记录
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// 检查此提供者当前是否可用
    /// </summary>
    /// <returns>如果提供者可以使用则返回 true，否则返回 false</returns>
    bool IsAvailable();

    /// <summary>
    /// 为给定文本生成音频
    /// </summary>
    /// <param name="text">要转换为语音的文本</param>
    /// <param name="options">TTS 选项（语音、速度等）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>包含文件路径和元数据的音频生成结果</returns>
    Task<TTSAudioResult> GenerateAudioAsync(
        string text, 
        TTSOptions options, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 等待音频播放完成
    /// 不同的提供者有不同的播放机制
    /// </summary>
    /// <param name="audioResult">来自 GenerateAudioAsync 的音频结果</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>播放完成时完成的任务</returns>
    Task WaitForPlaybackAsync(
        TTSAudioResult audioResult, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 估算给定文本的音频时长
    /// 用于进度跟踪和时间协调
    /// </summary>
    /// <param name="text">要估算时长的文本</param>
    /// <param name="speed">语速倍数</param>
    /// <returns>估算的时长（毫秒）</returns>
    int EstimateDuration(string text, float speed);
}
