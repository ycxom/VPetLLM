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
    private readonly VPetTTSStateMonitor? _stateMonitor;
    private string? _currentSessionId;

    public VPetTTSIntegrationManager(VPetLLM plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        
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
    /// 获取协调器（动态获取，确保获取最新状态）
    /// </summary>
    private VPetTTSCoordinator? GetCoordinator()
    {
        return _plugin.VPetTTSCoordinator;
    }

    /// <summary>
    /// 检查是否可以使用独占会话模式
    /// </summary>
    public bool CanUseExclusiveMode()
    {
        var coordinator = GetCoordinator();
        return TTSCoordinationSettings.Instance.EnableExclusiveMode
            && coordinator != null
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
        var coordinator = GetCoordinator();
        if (coordinator == null)
        {
            throw new InvalidOperationException("VPetTTS 协调器未初始化");
        }

        _currentSessionId = await coordinator.StartExclusiveSessionAsync();
        Logger.Log($"VPetTTSIntegrationManager: 启动独占会话，会话 ID: {_currentSessionId}");
        return _currentSessionId;
    }

    /// <summary>
    /// 结束独占会话
    /// </summary>
    public async Task EndExclusiveSessionAsync()
    {
        var coordinator = GetCoordinator();
        if (coordinator == null || string.IsNullOrEmpty(_currentSessionId))
        {
            return;
        }

        try
        {
            await coordinator.EndExclusiveSessionAsync();
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
        var coordinator = GetCoordinator();
        if (coordinator == null || !IsInExclusiveSession())
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
                var success = await coordinator.PreloadTextAsync(text);
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
        var coordinator = GetCoordinator();
        if (coordinator == null || !IsInExclusiveSession())
        {
            throw new InvalidOperationException("无法提交 TTS 请求 - 协调器未初始化或不在独占会话中");
        }

        return await coordinator.SubmitTTSRequestAsync(text);
    }

    /// <summary>
    /// 等待 TTS 请求完成
    /// </summary>
    public async Task<bool> WaitForRequestCompleteAsync(string requestId, int timeoutSeconds = 60)
    {
        var coordinator = GetCoordinator();
        if (coordinator == null)
        {
            return false;
        }

        return await coordinator.WaitForRequestCompleteAsync(requestId, timeoutSeconds);
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
        var coordinator = GetCoordinator();
        return coordinator?.IsProcessing() ?? false;
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
    /// 获取状态监控器（供内部使用）
    /// </summary>
    internal VPetTTSStateMonitor? GetStateMonitor()
    {
        return _stateMonitor;
    }

    /// <summary>
    /// 处理完整消息（带独占会话管理）
    /// 此方法负责：
    /// 1. 启动独占会话
    /// 2. 批量预加载所有 talk 文本
    /// 3. 调用回调函数处理每个命令
    /// 4. 结束独占会话
    /// </summary>
    /// <param name="message">完整消息</param>
    /// <param name="commandProcessor">命令处理回调（接收命令和会话ID）</param>
    /// <returns>处理的命令任务列表</returns>
    public async Task<List<Task>> ProcessCompleteMessageWithExclusiveSessionAsync(
        string message,
        Func<string, string, Task> commandProcessor)
    {
        if (!CanUseExclusiveMode())
        {
            throw new InvalidOperationException("无法使用独占会话模式");
        }

        string sessionId = null;
        var commandTasks = new List<Task>();

        try
        {
            // 启动独占会话
            sessionId = await StartExclusiveSessionAsync();
            Logger.Log($"VPetTTSIntegrationManager: 启动独占会话（覆盖所有命令），会话 ID: {sessionId}");

            // 提取所有 talk 文本进行批量预加载
            var talkTexts = ExtractAllTalkTexts(message);
            if (talkTexts.Count > 0)
            {
                Logger.Log($"VPetTTSIntegrationManager: 批量预加载 {talkTexts.Count} 个文本");
                var preloadCount = await PreloadTextsAsync(talkTexts);
                Logger.Log($"VPetTTSIntegrationManager: 批量预加载完成，成功: {preloadCount}/{talkTexts.Count}");
            }

            // 返回会话 ID 和任务列表，让调用方处理命令
            // 注意：不在这里等待任务完成，由调用方决定何时等待
            return commandTasks;
        }
        catch (Exception ex)
        {
            Logger.Log($"VPetTTSIntegrationManager: 处理完整消息失败: {ex.Message}");
            
            // 发生异常时立即结束会话
            if (!string.IsNullOrEmpty(sessionId))
            {
                try
                {
                    await EndExclusiveSessionAsync();
                }
                catch { }
            }
            
            throw;
        }
    }

    /// <summary>
    /// 从完整消息中提取所有 talk 文本（用于批量预加载）
    /// </summary>
    public List<string> ExtractAllTalkTexts(string message)
    {
        var talkTexts = new List<string>();
        
        try
        {
            // 使用正则表达式提取所有 say/talk 命令中的文本
            var sayPattern = @"<\|(?:say|talk)_begin\|>\s*""([^""]+)""\s*(?:,\s*\w+)?(?:\s*,\s*\w+)?\s*<\|(?:say|talk)_end\|>";
            var matches = System.Text.RegularExpressions.Regex.Matches(message, sayPattern);
            
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    var text = match.Groups[1].Value;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        talkTexts.Add(text);
                    }
                }
            }
            
            Logger.Log($"VPetTTSIntegrationManager: 从消息中提取了 {talkTexts.Count} 个 talk 文本");
        }
        catch (Exception ex)
        {
            Logger.Log($"VPetTTSIntegrationManager: 提取 talk 文本失败: {ex.Message}");
        }
        
        return talkTexts;
    }
}
