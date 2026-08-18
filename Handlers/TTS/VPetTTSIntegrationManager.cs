using System.Threading;
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
    // 非 readonly：VPetTTS 插件可能晚于本类构造才出现在 MW.Plugins 中，支持延迟获取
    private VPetTTSStateMonitor? _stateMonitor;
    private string? _currentSessionId;

    /// <summary>
    /// 会话的开合闸门。"看一眼有没有会话，没有就开一个"必须是原子的：
    /// 流式回复是每检测到一条完整命令就派发一次，两条命令并发进来时都会看到"还没有会话"，
    /// 于是双双去开 —— 后到的那个在 VPetTTS 侧撞上"会话已存在"异常，被吞掉后整条消息
    /// 退回无会话路径：气泡不再等起播，语音改由事后捕获文本再合成，差一整个合成往返。
    /// </summary>
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    /// <summary>
    /// 当前会话的参与者数量（引用计数）。
    ///
    /// 会话不能谁先说完谁就关：后到的消息会把自己的 TTS 请求挂到已有会话上，
    /// 先到的那条一收尾就 EndSession，VPetTTS 侧 ClearRequests 把还挂着的起播等待
    /// 全部以"没播出来"放掉 —— 后一条的气泡当场弹出，音频还在合成队列里。
    /// 计数归零（最后一个参与者离开）才真正结束会话。
    /// </summary>
    private int _sessionParticipants;

    public VPetTTSIntegrationManager(VPetLLM plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        EnsureStateMonitor();
    }

    /// <summary>
    /// 延迟初始化状态监控器。
    /// 注意：IsVPetTTSPluginDetected 检测的是"任意其它 TTS 插件"，
    /// 而监控器需要名字恰为 "VPetTTS" 的插件实例——两者可能不一致，
    /// 找不到时监控器保持 null，调用方必须回退到传统等待而非跳过等待。
    /// </summary>
    private VPetTTSStateMonitor? EnsureStateMonitor()
    {
        if (_stateMonitor == null && _plugin.IsVPetTTSPluginDetected)
        {
            var vpetTTSPlugin = GetVPetTTSPlugin();
            if (vpetTTSPlugin != null)
            {
                _stateMonitor = new VPetTTSStateMonitor(vpetTTSPlugin);
                Logger.Log("VPetTTSIntegrationManager: 状态监控器已初始化");
            }
        }
        return _stateMonitor;
    }

    /// <summary>
    /// 状态监控器是否可用（供调用方决定走精确等待还是传统等待）
    /// </summary>
    public bool HasStateMonitor => EnsureStateMonitor() != null;

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
    /// 加入独占会话；当前没有会话就开一个。返回会话 ID。
    ///
    /// 每次调用都算一个参与者，必须与 <see cref="EndExclusiveSessionAsync"/> 成对使用 ——
    /// 会话要等所有参与者都收尾才真正结束。
    /// </summary>
    public async Task<string> StartExclusiveSessionAsync()
    {
        var coordinator = GetCoordinator();
        if (coordinator == null)
        {
            throw new InvalidOperationException("VPetTTS 协调器未初始化");
        }

        await _sessionGate.WaitAsync();
        try
        {
            // 已有会话：加入而不是另起。抢着开会在 VPetTTS 侧是直接抛异常的，
            // 而且就算不抛，两个会话也会互相把对方的请求表清掉
            if (!string.IsNullOrEmpty(_currentSessionId))
            {
                _sessionParticipants++;
                Logger.Log($"VPetTTSIntegrationManager: 加入已有独占会话，会话 ID: {_currentSessionId}，" +
                           $"当前参与者: {_sessionParticipants}");
                return _currentSessionId;
            }

            _currentSessionId = await coordinator.StartExclusiveSessionAsync();
            _sessionParticipants = 1;
            Logger.Log($"VPetTTSIntegrationManager: 启动独占会话，会话 ID: {_currentSessionId}");
            return _currentSessionId;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// 退出独占会话。最后一个参与者离开时才真正结束会话。
    /// </summary>
    public async Task EndExclusiveSessionAsync()
    {
        var coordinator = GetCoordinator();
        if (coordinator == null)
        {
            return;
        }

        await _sessionGate.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(_currentSessionId))
            {
                return;
            }

            if (_sessionParticipants > 1)
            {
                _sessionParticipants--;
                Logger.Log($"VPetTTSIntegrationManager: 退出独占会话（仍有 {_sessionParticipants} 个参与者在用），" +
                           $"会话 ID: {_currentSessionId} 保持开启");
                return;
            }

            var endingSessionId = _currentSessionId;
            try
            {
                await coordinator.EndExclusiveSessionAsync();
                Logger.Log($"VPetTTSIntegrationManager: 结束独占会话，会话 ID: {endingSessionId}");
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetTTSIntegrationManager: 结束独占会话失败: {ex.Message}");
                throw;
            }
            finally
            {
                // 成功与否都要清干净：留着一个已经关掉（或状态不明）的会话 ID，
                // 后续消息会误以为自己在会话里，请求提交进去只会被 VPetTTS 判定为无效
                _currentSessionId = null;
                _sessionParticipants = 0;
            }
        }
        finally
        {
            _sessionGate.Release();
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
            // 预加载 = 真去合成一遍音频，中断后再合成剩下的纯属烧算力和额度
            if (InterruptManager.IsInterrupted)
            {
                Logger.Log($"VPetTTSIntegrationManager: 已中断，停止预加载剩余文本（已完成 {successCount}/{texts.Count}）");
                break;
            }

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
    /// 等待某个 TTS 请求的音频真正起播，返回音频时长（毫秒，未知为 0）；
    /// 未起播或对方不支持起播回报时返回 -1，调用方应当照常显示气泡并按文本估算时长。
    /// </summary>
    public async Task<long> WaitForPlaybackStartAsync(string requestId, int timeoutMs)
    {
        var coordinator = GetCoordinator();
        if (coordinator == null)
        {
            return VPetTTSCoordinator.PlaybackNeverStarted;
        }

        return await coordinator.WaitForPlaybackStartAsync(requestId, timeoutMs);
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
        var monitor = EnsureStateMonitor();
        if (monitor == null)
        {
            Logger.Log("VPetTTSIntegrationManager: 状态监控器未初始化，无法等待播放完成");
            return false;
        }

        if (!TTSCoordinationSettings.Instance.EnableStateMonitor)
        {
            Logger.Log("VPetTTSIntegrationManager: 状态监控器未启用");
            return false;
        }

        return await monitor.WaitForPlaybackCompleteAsync(maxWaitMs);
    }

    /// <summary>
    /// 通知 VPetTTS 中断当前语音。用户点中断时调用，独立于独占会话是否存在 ——
    /// 语音可能是会话外（宿主 Say 捕获）触发的，一样要停。
    /// </summary>
    /// <returns>true 表示通知已送达 VPetTTS</returns>
    public async Task<bool> InterruptAsync()
    {
        var coordinator = GetCoordinator();
        if (coordinator == null)
        {
            return false;
        }

        return await coordinator.InterruptAsync();
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
    /// 从完整消息中提取所有 talk 文本（用于批量预加载）。
    ///
    /// 走 <see cref="CommandFormatParser.ExtractAllSayTexts"/> —— 和真正显示气泡那条路
    /// 同一套解析。这里曾经另写过一条正则（要求文本必须带引号、参数必须是 \w+），
    /// 漏掉的那些消息就不会开独占会话，语音退回事后捕获，气泡因此早于语音好几秒。
    /// </summary>
    public List<string> ExtractAllTalkTexts(string message)
    {
        try
        {
            var talkTexts = CommandFormatParser.ExtractAllSayTexts(message);
            Logger.Log($"VPetTTSIntegrationManager: 从消息中提取了 {talkTexts.Count} 个 talk 文本");
            return talkTexts;
        }
        catch (Exception ex)
        {
            Logger.Log($"VPetTTSIntegrationManager: 提取 talk 文本失败: {ex.Message}");
            return new List<string>();
        }
    }
}
