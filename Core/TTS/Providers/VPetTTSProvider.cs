using VPetLLM.Interfaces;
using VPetLLM.Handlers.TTS;

namespace VPetLLM.Core.TTS.Providers;

/// <summary>
/// VPetTTS 插件的 TTS 提供者
/// 处理 VPetTTS 特定的音频生成和播放
/// 支持独占会话和预加载功能（通过 VPetTTSIntegrationManager）
/// </summary>
public class VPetTTSProvider : ITTSProvider
{
    private readonly IVPetAPI _vpetAPI;
    private readonly ITTSDispatcher? _unifiedTTSDispatcher;
    private readonly VPetTTSIntegrationManager? _vpetTTSIntegration;

    public string ProviderName => "VPetTTS";

    public VPetTTSProvider(
        IVPetAPI vpetAPI, 
        ITTSDispatcher? unifiedTTSDispatcher,
        VPetTTSIntegrationManager? vpetTTSIntegration = null)
    {
        _vpetAPI = vpetAPI ?? throw new ArgumentNullException(nameof(vpetAPI));
        _unifiedTTSDispatcher = unifiedTTSDispatcher;
        _vpetTTSIntegration = vpetTTSIntegration;
        Logger.Log($"VPetTTSProvider: 初始化完成，统一 TTS 调度器可用: {_unifiedTTSDispatcher is not null}, VPetTTS集成管理器可用: {_vpetTTSIntegration is not null}");
    }

    public bool IsAvailable()
    {
        // 检查 VPetTTS 插件是否已加载并可用
        var available = _unifiedTTSDispatcher is not null;
        Logger.Log($"VPetTTSProvider: IsAvailable = {available}");
        return available;
    }

    public async Task<TTSAudioResult> GenerateAudioAsync(
        string text, 
        TTSOptions options, 
        CancellationToken cancellationToken)
    {
        try
        {
            Logger.Log($"VPetTTSProvider: 生成音频，文本长度: {text.Length}");

            // 优先使用集成管理器（支持独占会话和预加载）
            if (_vpetTTSIntegration is not null && _vpetTTSIntegration.IsInExclusiveSession())
            {
                Logger.Log("VPetTTSProvider: 使用集成管理器提交 TTS 请求");

                // 提交 TTS 请求（集成管理器会自动处理预加载）
                var requestId = await _vpetTTSIntegration.SubmitTTSRequestAsync(text);
                Logger.Log($"VPetTTSProvider: TTS 请求已提交，请求 ID: {requestId}");

                // 估算时长
                var estimatedDuration = EstimateDuration(text, options.Speed);

                return new TTSAudioResult
                {
                    Success = true,
                    AudioFilePath = null, // VPetTTS 内部处理播放
                    DurationMs = estimatedDuration,
                    ErrorMessage = null,
                    RequestId = requestId // 保存请求 ID 用于后续等待
                };
            }

            // 回退到统一 TTS 调度器
            if (_unifiedTTSDispatcher is null)
            {
                throw new InvalidOperationException("统一 TTS 调度器不可用");
            }

            Logger.Log("VPetTTSProvider: 使用统一 TTS 调度器");

            // VPetTTS 特定的音频生成逻辑
            // 使用统一 TTS 调度器处理请求
            var request = new ModelsTTSRequest
            {
                RequestId = Guid.NewGuid().ToString(),
                Text = text,
                Settings = new TTSRequestSettings
                {
                    Voice = options.Voice ?? "default",
                    Speed = options.Speed,
                    Volume = options.Volume,
                    UseStreaming = options.UseStreaming,
                    TimeoutMs = options.TimeoutMs
                }
            };

            var response = await _unifiedTTSDispatcher.ProcessRequestAsync(request);

            if (!response.Success)
            {
                Logger.Log($"VPetTTSProvider: 音频生成失败: {response.ErrorMessage}");
            }
            else
            {
                Logger.Log($"VPetTTSProvider: 音频生成成功，时长: {response.AudioDurationMs}ms");
            }

            return new TTSAudioResult
            {
                Success = response.Success,
                AudioFilePath = response.AudioFilePath,
                DurationMs = response.AudioDurationMs > 0 ? response.AudioDurationMs : EstimateDuration(text, options.Speed),
                ErrorMessage = response.ErrorMessage
            };
        }
        catch (Exception ex)
        {
            Logger.Log($"VPetTTSProvider: 音频生成异常: {ex.Message}");
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
            // 如果有请求 ID 且集成管理器可用，等待请求完成
            if (!string.IsNullOrEmpty(audioResult.RequestId) && _vpetTTSIntegration is not null)
            {
                Logger.Log($"VPetTTSProvider: 等待请求完成，请求 ID: {audioResult.RequestId}");
                var completed = await _vpetTTSIntegration.WaitForRequestCompleteAsync(audioResult.RequestId, 60);
                
                if (completed)
                {
                    Logger.Log("VPetTTSProvider: 请求完成");
                }
                else
                {
                    Logger.Log("VPetTTSProvider: 请求等待超时");
                }
                return;
            }

            // 回退到估算时长等待
            if (audioResult.DurationMs > 0)
            {
                Logger.Log($"VPetTTSProvider: 等待播放完成（估算时长）: {audioResult.DurationMs}ms");
                await Task.Delay(audioResult.DurationMs, cancellationToken);
                Logger.Log("VPetTTSProvider: 播放等待完成");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"VPetTTSProvider: 播放等待失败: {ex.Message}");
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
            // VPetTTS 特定的时长估算
            // 基于中文 TTS 特性（204 字符/分钟）
            var charactersPerMinute = 204 * speed;
            var charactersPerSecond = charactersPerMinute / 60.0;
            var estimatedSeconds = text.Length / charactersPerSecond;
            var estimatedMs = (int)(estimatedSeconds * 1000 * 1.15); // 15% 缓冲
            var result = Math.Max(2000, estimatedMs); // 最小 2 秒

            Logger.Log($"VPetTTSProvider: 估算时长: {result}ms (文本长度: {text.Length}, 速度: {speed})");
            return result;
        }
        catch (Exception ex)
        {
            Logger.Log($"VPetTTSProvider: 估算时长失败: {ex.Message}");
            return 5000; // 默认 5 秒
        }
    }
}
