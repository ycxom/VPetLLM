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
        
        Logger.Log($"TTSProviderFactory: 初始化完成，VPetTTS集成管理器可用: {_vpetTTSIntegration is not null}");
    }

    /// <summary>
    /// 获取当前活动的 TTS 提供者
    /// 优先级: VPetTTS > EdgeTTS > BuiltinTTS
    /// </summary>
    public ITTSProvider GetActiveProvider()
    {
        Logger.Log("TTSProviderFactory: 检测活动提供者...");

        // 首先尝试 VPetTTS
        var vpetTTSProvider = new VPetTTSProvider(_vpetAPI, _unifiedTTSDispatcher, _vpetTTSIntegration);
        if (vpetTTSProvider.IsAvailable())
        {
            Logger.Log("TTSProviderFactory: 使用 VPetTTS 提供者");
            return vpetTTSProvider;
        }

        // 其次尝试 EdgeTTS
        var edgeTTSProvider = new EdgeTTSProvider(_vpetAPI);
        if (edgeTTSProvider.IsAvailable())
        {
            Logger.Log("TTSProviderFactory: 使用 EdgeTTS 提供者");
            return edgeTTSProvider;
        }

        // 回退到内置 TTS
        Logger.Log("TTSProviderFactory: 使用内置 TTS 提供者");
        return new BuiltinTTSProvider(_vpetAPI, _mpvPlayer);
    }

    /// <summary>
    /// 获取所有可用的提供者
    /// </summary>
    public List<ITTSProvider> GetAllAvailableProviders()
    {
        Logger.Log("TTSProviderFactory: 获取所有可用提供者...");
        var providers = new List<ITTSProvider>();

        var vpetTTS = new VPetTTSProvider(_vpetAPI, _unifiedTTSDispatcher, _vpetTTSIntegration);
        if (vpetTTS.IsAvailable())
        {
            providers.Add(vpetTTS);
            Logger.Log("TTSProviderFactory: VPetTTS 可用");
        }

        var edgeTTS = new EdgeTTSProvider(_vpetAPI);
        if (edgeTTS.IsAvailable())
        {
            providers.Add(edgeTTS);
            Logger.Log("TTSProviderFactory: EdgeTTS 可用");
        }

        var builtinTTS = new BuiltinTTSProvider(_vpetAPI, _mpvPlayer);
        if (builtinTTS.IsAvailable())
        {
            providers.Add(builtinTTS);
            Logger.Log("TTSProviderFactory: BuiltinTTS 可用");
        }

        Logger.Log($"TTSProviderFactory: 找到 {providers.Count} 个可用提供者");
        return providers;
    }

    /// <summary>
    /// 按名称获取特定提供者
    /// </summary>
    public ITTSProvider? GetProviderByName(string providerName)
    {
        Logger.Log($"TTSProviderFactory: 按名称获取提供者: {providerName}");

        ITTSProvider? provider = providerName?.ToLower() switch
        {
            "vpettts" => new VPetTTSProvider(_vpetAPI, _unifiedTTSDispatcher, _vpetTTSIntegration),
            "edgetts" => new EdgeTTSProvider(_vpetAPI),
            "builtintts" => new BuiltinTTSProvider(_vpetAPI, _mpvPlayer),
            _ => null
        };

        if (provider is null)
        {
            Logger.Log($"TTSProviderFactory: 未找到提供者: {providerName}");
        }
        else
        {
            Logger.Log($"TTSProviderFactory: 找到提供者: {provider.ProviderName}");
        }

        return provider;
    }
}
