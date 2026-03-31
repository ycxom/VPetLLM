using VPetLLM.Core.TTS.Providers;
using VPetLLM.Interfaces;
using VPetLLM.Utils.Audio;

namespace VPetLLM.Core.TTS;

/// <summary>
/// 创建和管理 TTS 提供者实例的工厂
/// 处理提供者检测和选择
/// 支持独占会话和批量预加载（通过 VPetTTSIntegrationManager）
/// </summary>
public class TTSProviderFactory
{
    private readonly IVPetAPI _vpetAPI;
    private readonly ITTSDispatcher? _unifiedTTSDispatcher;
    private readonly MpvPlayer? _mpvPlayer;
    private readonly VPetTTSIntegrationManager? _vpetTTSIntegration;

    public TTSProviderFactory(
        IVPetAPI vpetAPI, 
        ITTSDispatcher? unifiedTTSDispatcher, 
        MpvPlayer? mpvPlayer,
        VPetTTSIntegrationManager? vpetTTSIntegration = null)
    {
        _vpetAPI = vpetAPI ?? throw new ArgumentNullException(nameof(vpetAPI));
        _unifiedTTSDispatcher = unifiedTTSDispatcher;
        _mpvPlayer = mpvPlayer;
        _vpetTTSIntegration = vpetTTSIntegration;
    }

    /// <summary>
    /// 获取当前活动的 TTS 提供者
    /// 优先级: VPetTTS > EdgeTTS > BuiltinTTS
    /// </summary>
    public ITTSProvider GetActiveProvider()
    {
        var vpetTTSProvider = new VPetTTSProvider(_vpetAPI, _unifiedTTSDispatcher, _vpetTTSIntegration);
        if (vpetTTSProvider.IsAvailable())
        {
            return vpetTTSProvider;
        }

        var edgeTTSProvider = new EdgeTTSProvider(_vpetAPI);
        if (edgeTTSProvider.IsAvailable())
        {
            return edgeTTSProvider;
        }

        return new BuiltinTTSProvider(_vpetAPI, _mpvPlayer);
    }

    /// <summary>
    /// 获取所有可用的提供者
    /// </summary>
    public List<ITTSProvider> GetAllAvailableProviders()
    {
        var providers = new List<ITTSProvider>();

        var vpetTTS = new VPetTTSProvider(_vpetAPI, _unifiedTTSDispatcher, _vpetTTSIntegration);
        if (vpetTTS.IsAvailable())
        {
            providers.Add(vpetTTS);
        }

        var edgeTTS = new EdgeTTSProvider(_vpetAPI);
        if (edgeTTS.IsAvailable())
        {
            providers.Add(edgeTTS);
        }

        var builtinTTS = new BuiltinTTSProvider(_vpetAPI, _mpvPlayer);
        if (builtinTTS.IsAvailable())
        {
            providers.Add(builtinTTS);
        }

        return providers;
    }

    /// <summary>
    /// 按名称获取特定提供者
    /// </summary>
    public ITTSProvider? GetProviderByName(string providerName)
    {
        ITTSProvider? provider = providerName?.ToLower() switch
        {
            "vpettts" => new VPetTTSProvider(_vpetAPI, _unifiedTTSDispatcher, _vpetTTSIntegration),
            "edgetts" => new EdgeTTSProvider(_vpetAPI),
            "builtintts" => new BuiltinTTSProvider(_vpetAPI, _mpvPlayer),
            _ => null
        };

        return provider;
    }
}
