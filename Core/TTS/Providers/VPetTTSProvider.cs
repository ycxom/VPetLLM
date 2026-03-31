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
    }

    public bool IsAvailable()
    {
        return _unifiedTTSDispatcher is not null;
    }

    public async Task<TTSAudioResult> GenerateAudioAsync(
        string text, 
        TTSOptions options, 
        CancellationToken cancellationToken)
    {
        try
        {
            if (_vpetTTSIntegration is not null && _vpetTTSIntegration.IsInExclusiveSession())
            {
                var requestId = await _vpetTTSIntegration.SubmitTTSRequestAsync(text);
                var estimatedDuration = EstimateDuration(text, options.Speed);

                return new TTSAudioResult
                {
                    Success = true,
                    AudioFilePath = null,
                    DurationMs = estimatedDuration,
                    ErrorMessage = null,
                    RequestId = requestId
                };
            }

            if (_unifiedTTSDispatcher is null)
            {
                throw new InvalidOperationException("统一 TTS 调度器不可用");
            }

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
            if (!string.IsNullOrEmpty(audioResult.RequestId) && _vpetTTSIntegration is not null)
            {
                await _vpetTTSIntegration.WaitForRequestCompleteAsync(audioResult.RequestId, 60);
                return;
            }

            if (audioResult.DurationMs > 0)
            {
                await Task.Delay(audioResult.DurationMs, cancellationToken);
            }
        }
        catch (Exception)
        {
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
            var charactersPerMinute = 204 * speed;
            var charactersPerSecond = charactersPerMinute / 60.0;
            var estimatedSeconds = text.Length / charactersPerSecond;
            var estimatedMs = (int)(estimatedSeconds * 1000 * 1.15);
            return Math.Max(2000, estimatedMs);
        }
        catch (Exception)
        {
            return 5000;
        }
    }
}
