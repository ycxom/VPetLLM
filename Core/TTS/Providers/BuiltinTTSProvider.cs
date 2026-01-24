using VPetLLM.Interfaces;
using VPetLLM.Utils.Audio;

namespace VPetLLM.Core.TTS.Providers;

/// <summary>
/// 使用 mpv 播放器的内置 TTS 提供者
/// 处理基于 mpv 的音频播放
/// </summary>
public class BuiltinTTSProvider : ITTSProvider
{
    private readonly IVPetAPI _vpetAPI;
    private readonly MpvPlayer? _mpvPlayer;

    public string ProviderName => "BuiltinTTS";

    public BuiltinTTSProvider(IVPetAPI vpetAPI, MpvPlayer? mpvPlayer)
    {
        _vpetAPI = vpetAPI ?? throw new ArgumentNullException(nameof(vpetAPI));
        _mpvPlayer = mpvPlayer;
        Logger.Log($"BuiltinTTSProvider: 初始化完成，mpv 播放器可用: {_mpvPlayer is not null}");
    }

    public bool IsAvailable()
    {
        // 如果 VPetAPI 可用，内置 TTS 就可用
        // mpv 播放器是可选的（如果可用则用于音频播放）
        var available = _vpetAPI.IsAvailable;
        Logger.Log($"BuiltinTTSProvider: IsAvailable = {available}");
        return available;
    }

    public async Task<TTSAudioResult> GenerateAudioAsync(
        string text, 
        TTSOptions options, 
        CancellationToken cancellationToken)
    {
        try
        {
            Logger.Log($"BuiltinTTSProvider: 生成音频，文本长度: {text.Length}, 流式: {options.UseStreaming}");

            // 内置 TTS 使用 VPetAPI 显示气泡
            // 音频文件路径来自选项（如果提供）
            if (options.UseStreaming)
            {
                await _vpetAPI.SayInfoWithStreamAsync(text, options.Voice);
            }
            else
            {
                await _vpetAPI.SayInfoWithOutStreamAsync(text, options.Voice);
            }

            var estimatedDuration = EstimateDuration(text, options.Speed);

            Logger.Log($"BuiltinTTSProvider: 音频生成成功，估算时长: {estimatedDuration}ms");

            return new TTSAudioResult
            {
                Success = true,
                AudioFilePath = options.AudioFilePath,
                DurationMs = estimatedDuration,
                ErrorMessage = null
            };
        }
        catch (Exception ex)
        {
            Logger.Log($"BuiltinTTSProvider: 音频生成失败: {ex.Message}");
            return new TTSAudioResult
            {
                Success = false,
                AudioFilePath = null,
                DurationMs = 0,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task WaitForPlaybackAsync(
        TTSAudioResult audioResult, 
        CancellationToken cancellationToken)
    {
        try
        {
            // 如果有音频文件和 mpv 播放器，播放它
            if (!string.IsNullOrEmpty(audioResult.AudioFilePath) && 
                File.Exists(audioResult.AudioFilePath) && 
                _mpvPlayer is not null)
            {
                Logger.Log($"BuiltinTTSProvider: 使用 mpv 播放音频: {audioResult.AudioFilePath}");
                
                var startTime = DateTime.Now;
                await _mpvPlayer.PlayAsync(audioResult.AudioFilePath);
                var actualDuration = (int)(DateTime.Now - startTime).TotalMilliseconds;
                
                Logger.Log($"BuiltinTTSProvider: mpv 播放完成，实际时长: {actualDuration}ms");
            }
            else
            {
                // 回退：等待估算的时长
                // 注意：这里使用估算时长而不是气泡打印时间
                // 因为 GenerateAudioAsync 已经调用了 VPetAPI 显示气泡
                // 我们只需要等待足够的时间让用户阅读完气泡内容
                Logger.Log($"BuiltinTTSProvider: 等待估算时长: {audioResult.DurationMs}ms");
                await Task.Delay(audioResult.DurationMs, cancellationToken);
                Logger.Log("BuiltinTTSProvider: 估算时长等待完成");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"BuiltinTTSProvider: 播放等待失败: {ex.Message}");
            throw;
        }
    }

    public int EstimateDuration(string text, float speed)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        try
        {
            // 内置 TTS 时长估算
            // 参考 ChatVPet 和 BubbleDisplayConfig 的做法：
            // VPet MessageBar 每 150ms 显示 2-3 个字符（平均 2.5 个）
            // 公式: (text.Length / 2.5) * 150 + 300 毫秒缓冲
            var charactersPerInterval = 2.5 * speed;
            var intervalMs = 150;
            var estimatedMs = (int)((text.Length / charactersPerInterval) * intervalMs) + 300;
            var result = Math.Max(500, estimatedMs); // 最小 500ms

            Logger.Log($"BuiltinTTSProvider: 估算时长: {result}ms (文本长度: {text.Length}, 速度: {speed})");
            return result;
        }
        catch (Exception ex)
        {
            Logger.Log($"BuiltinTTSProvider: 估算时长失败: {ex.Message}");
            return 2000; // 默认 2 秒
        }
    }
}
