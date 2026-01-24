namespace VPetLLM.Core.TTS;

/// <summary>
/// 音频生成操作的结果
/// 包含音频文件路径、时长和元数据
/// </summary>
public class TTSAudioResult
{
    /// <summary>
    /// 生成的音频文件路径（对于流式提供者可能为 null）
    /// </summary>
    public string? AudioFilePath { get; set; }

    /// <summary>
    /// 实际音频时长（毫秒）（如果未知则为 0）
    /// </summary>
    public int DurationMs { get; set; }

    /// <summary>
    /// 音频生成是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 如果生成失败的错误消息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 提供者特定的元数据
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// TTS 请求 ID（用于 VPetTTS 协调器等待请求完成）
    /// </summary>
    public string? RequestId { get; set; }
}
