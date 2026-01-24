using VPetLLM.Utils.Audio;
using VPetLLM.Configuration;

namespace VPetLLM.Handlers.TTS;

/// <summary>
/// VPetTTS 集成管理器
/// 统一管理所有与 VPetTTS 插件的交互，封装 VPetTTS 私有功能
/// </summary>
public class VPetTTSIntegrationManager
{
    private readonly VPetLLM _plugin;
    private readonly VPetTTSCoordinator? _coordinator;
    private readonly VPetTTSStateMonitor? _stateMonitor;
    private string? _currentSessionId;

    public VPetTTSIntegrationManager(VPetLLM plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _coordinator = plugin.VPetTTSCoordinator;
        
        // 初始化状态监控器
        if (plugin.IsVPetTTSPluginDetected)
        {
            var vpetTTSPlugin = GetVPetTTSPlugin();
            if (vpetTTSPlugin != null)
            {
                _stateMonitor = new VPetTTSStateMonitor(vpetTTSPlugin);
                Logger.Log("VPetTTSIntegrationManager: 状态监控器已初始化");
            }
        }
    }

    /// <summary>
    /// 检查是否可以使用独占会话模式
    /// </summary>
    public bool CanUseExclusiveMode()
    {
        return TTSCoordinationSettings.Instance.EnableExclusiveMode
            && _coordinator != null
            && _plugin.IsVPetTTSPluginDetected;
    }

    /// <summary>
    /// 检查是否处于独占会话中
    /// </summary>
    public bool IsInExclusiveSession()
    {
        return !string.IsNullOrEmpty(_currentSessionId);
    }

    /// <summary>
    /// 启动独占会话
    /// </summary>
    public async Task<string> StartExclusiveSessionAsync()
    {
        if (_coordinator == null)
        {
            throw new InvalidOperationException("VPetTTS 协调器未初始化");
        }

        _currentSessionId = await _coordinator.StartExclusiveSessionAsync();
        Logger.Log($"VPetTTSIntegrationManager: 启动独占会话，会话 ID: {_currentSessionId}");
        return _currentSessionId;
    }

    /// <summary>
    /// 结束独占会话
    /// </summary>
    public async Task EndExclusiveSessionAsync()
    {
        if (_coordinator == null || string.IsNullOrEmpty(_currentSessionId))
        {
            return;
        }

        try
        {
            await _coordinator.EndExclusiveSessionAsync();
            Logger.Log($"VPetTTSIntegrationManager: 结束独占会话，会话 ID: {_currentSessionId}");
            _currentSessionId = null;
        }
        catch (Exception ex)
        {
            Logger.Log($"VPetTTSIntegrationManager: 结束独占会话失败: {ex.Message}");
            _currentSessionId = null;
            throw;
        }
    }

    /// <summary>
    /// 批量预加载文本
    /// </summary>
    public async Task<int> PreloadTextsAsync(List<string> texts)
    {
        if (_coordinator == null || !IsInExclusiveSession())
        {
            Logger.Log("VPetTTSIntegrationManager: 无法预加载 - 协调器未初始化或不在独占会话中");
            return 0;
        }

        if (!TTSCoordinationSettings.Instance.EnablePreload)
        {
            Logger.Log("VPetTTSIntegrationManager: 预加载功能未启用");
            return 0;
        }

        int successCount = 0;
        foreach (var text in texts)
        {
            try
            {
                var success = await _coordinator.PreloadTextAsync(text);
                if (success)
                {
                    successCount++;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetTTSIntegrationManager: 预加载文本失败: {ex.Message}");
            }
        }

        Logger.Log($"VPetTTSIntegrationManager: 批量预加载完成，成功: {successCount}/{texts.Count}");
        return successCount;
    }

    /// <summary>
    /// 提交 TTS 请求（在独占会话中）
    /// </summary>
    public async Task<string> SubmitTTSRequestAsync(string text)
    {
        if (_coordinator == null || !IsInExclusiveSession())
        {
            throw new InvalidOperationException("无法提交 TTS 请求 - 协调器未初始化或不在独占会话中");
        }

        return await _coordinator.SubmitTTSRequestAsync(text);
    }

    /// <summary>
    /// 等待 TTS 请求完成
    /// </summary>
    public async Task<bool> WaitForRequestCompleteAsync(string requestId, int timeoutSeconds = 60)
    {
        if (_coordinator == null)
        {
            return false;
        }

        return await _coordinator.WaitForRequestCompleteAsync(requestId, timeoutSeconds);
    }

    /// <summary>
    /// 等待播放完成（使用状态监控器）
    /// </summary>
    public async Task<bool> WaitForPlaybackCompleteAsync(int maxWaitMs = 60000)
    {
        if (_stateMonitor == null)
        {
            Logger.Log("VPetTTSIntegrationManager: 状态监控器未初始化，无法等待播放完成");
            return false;
        }

        if (!TTSCoordinationSettings.Instance.EnableStateMonitor)
        {
            Logger.Log("VPetTTSIntegrationManager: 状态监控器未启用");
            return false;
        }

        return await _stateMonitor.WaitForPlaybackCompleteAsync(maxWaitMs);
    }

    /// <summary>
    /// 检查是否正在处理
    /// </summary>
    public bool IsProcessing()
    {
        return _coordinator?.IsProcessing() ?? false;
    }

    /// <summary>
    /// 获取 VPetTTS 插件实例
    /// </summary>
    private object? GetVPetTTSPlugin()
    {
        try
        {
            return _plugin.MW.Plugins.FirstOrDefault(p => p.PluginName == "VPetTTS");
        }
        catch (Exception ex)
        {
            Logger.Log($"VPetTTSIntegrationManager: 获取 VPetTTS 插件失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取协调器（供内部使用）
    /// </summary>
    internal VPetTTSCoordinator? GetCoordinator()
    {
        return _coordinator;
    }

    /// <summary>
    /// 获取状态监控器（供内部使用）
    /// </summary>
    internal VPetTTSStateMonitor? GetStateMonitor()
    {
        return _stateMonitor;
    }
}
