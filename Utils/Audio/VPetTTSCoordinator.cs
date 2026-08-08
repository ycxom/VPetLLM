using VPet_Simulator.Windows.Interface;
using VPetLLM.Core.Services;

namespace VPetLLM.Utils.Audio;

/// <summary>
/// VPetTTS 协调器（VPetLLM 侧）
/// 用于与 VPetTTS 插件的协调器接口交互。
/// 对 VPetTTS 内部成员的访问统一经 VPetTTSPluginAdapter（缓存反射、能力探测），
/// 避免直接引用 VPetTTS 程序集。
/// </summary>
public class VPetTTSCoordinator : IDisposable
{
    private readonly IMainWindow _mainWindow;
    private object? _ttsCoordinator;
    private bool _isInitialized;
    private string? _currentSessionId;
    private readonly string _callerId = "VPetLLM";

    public VPetTTSCoordinator(IMainWindow mainWindow)
    {
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
    }

    /// <summary>
    /// 初始化协调器
    /// </summary>
    public bool Initialize()
    {
        try
        {
            // 查找 VPetTTS 插件
            var vpetTTSPlugin = _mainWindow.Plugins.FirstOrDefault(p => p.PluginName == "VPetTTS");
            if (vpetTTSPlugin == null)
            {
                Logger.Log("VPetTTSCoordinator.Initialize: 未找到 VPetTTS 插件");
                return false;
            }

            _ttsCoordinator = VPetTTSPluginAdapter.GetCoordinator(vpetTTSPlugin);
            if (_ttsCoordinator == null)
            {
                Logger.Log("VPetTTSCoordinator.Initialize: 无法获取 VPetTTS 协调器接口（属性缺失或插件尚未完成初始化）");
                return false;
            }

            _isInitialized = true;
            Logger.Log($"VPetTTSCoordinator.Initialize: 协调器初始化成功，类型: {_ttsCoordinator.GetType().FullName}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"VPetTTSCoordinator.Initialize: 初始化失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 检查是否处于独占会话
    /// </summary>
    public bool IsInExclusiveSession()
    {
        return !string.IsNullOrEmpty(_currentSessionId);
    }

    /// <summary>
    /// 预加载文本
    /// </summary>
    public async Task<bool> PreloadTextAsync(string text)
    {
        if (!_isInitialized || _ttsCoordinator == null)
        {
            return false;
        }

        if (string.IsNullOrEmpty(_currentSessionId))
        {
            return false;
        }

        try
        {
            var task = VPetTTSPluginAdapter.Preload(_ttsCoordinator, text, _currentSessionId);
            if (task == null)
            {
                return false;
            }

            return await task;
        }
        catch (Exception ex)
        {
            Logger.Log($"VPetTTSCoordinator: 预加载失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 提交 TTS 请求（别名方法，与 SubmitTTSAsync 相同）
    /// </summary>
    public async Task<string> SubmitTTSRequestAsync(string text)
    {
        return await SubmitTTSAsync(text);
    }

    /// <summary>
    /// 启动独占会话
    /// </summary>
    public async Task<string> StartExclusiveSessionAsync()
    {
        if (!_isInitialized || _ttsCoordinator == null)
        {
            throw new InvalidOperationException("VPetTTS 协调器未初始化");
        }

        try
        {
            var task = VPetTTSPluginAdapter.StartExclusiveSession(_ttsCoordinator, _callerId);
            if (task == null)
            {
                throw new InvalidOperationException("VPetTTS 协调器不支持 StartExclusiveSessionAsync");
            }

            _currentSessionId = await task;
            Logger.Log($"VPetTTSCoordinator: 启动独占会话成功，会话 ID: {_currentSessionId}");
            return _currentSessionId;
        }
        catch (Exception ex)
        {
            Logger.Log($"VPetTTSCoordinator: 启动独占会话失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 结束独占会话
    /// </summary>
    public async Task EndExclusiveSessionAsync()
    {
        if (!_isInitialized || _ttsCoordinator == null)
        {
            throw new InvalidOperationException("VPetTTS 协调器未初始化");
        }

        if (string.IsNullOrEmpty(_currentSessionId))
        {
            Logger.Log("VPetTTSCoordinator: 没有活跃的会话");
            return;
        }

        try
        {
            var task = VPetTTSPluginAdapter.EndExclusiveSession(_ttsCoordinator, _callerId, _currentSessionId);
            if (task == null)
            {
                throw new InvalidOperationException("VPetTTS 协调器不支持 EndExclusiveSessionAsync");
            }

            await task;
            Logger.Log($"VPetTTSCoordinator: 结束独占会话成功，会话 ID: {_currentSessionId}");
            _currentSessionId = null;
        }
        catch (Exception ex)
        {
            Logger.Log($"VPetTTSCoordinator: 结束独占会话失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 提交 TTS 请求
    /// </summary>
    public async Task<string> SubmitTTSAsync(string text)
    {
        if (!_isInitialized || _ttsCoordinator == null)
        {
            throw new InvalidOperationException("VPetTTS 协调器未初始化");
        }

        if (string.IsNullOrEmpty(_currentSessionId))
        {
            throw new InvalidOperationException("没有活跃的独占会话");
        }

        try
        {
            // 预加载
            var preloadTask = VPetTTSPluginAdapter.Preload(_ttsCoordinator, text, _currentSessionId);
            if (preloadTask != null)
            {
                await preloadTask;
                Logger.Log("VPetTTSCoordinator: 预加载成功");
            }

            // 提交请求
            var submitTask = VPetTTSPluginAdapter.SubmitTTS(_ttsCoordinator, text, _currentSessionId);
            if (submitTask == null)
            {
                throw new InvalidOperationException("VPetTTS 协调器不支持 SubmitTTSAsync");
            }

            var requestId = await submitTask;
            Logger.Log($"VPetTTSCoordinator: 提交 TTS 请求成功，请求 ID: {requestId}");
            return requestId;
        }
        catch (Exception ex)
        {
            Logger.Log($"VPetTTSCoordinator: 提交 TTS 请求失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 等待某个 TTS 请求的音频真正起播。
    ///
    /// SubmitTTSAsync 返回时音频还没出声（可能在合成，也可能在排队等前一句播完），
    /// 此刻显示气泡会比语音早出来。等到本方法返回再显示，字和声音就对齐了。
    /// </summary>
    /// <returns>
    /// ≥0：已起播，值为音频时长（毫秒，未知为 0）；
    /// -1：超时、合成失败、被中断，或当前 VPetTTS 版本不支持起播回报。
    /// 调用方拿到 -1 时应当按文本长度估算，照常把气泡放出来。
    /// </returns>
    public async Task<long> WaitForPlaybackStartAsync(string requestId, int timeoutMs)
    {
        if (!_isInitialized || _ttsCoordinator == null || string.IsNullOrEmpty(requestId))
        {
            return PlaybackNeverStarted;
        }

        try
        {
            var task = VPetTTSPluginAdapter.WaitForPlaybackStart(_ttsCoordinator, requestId, timeoutMs);
            if (task == null)
            {
                // 旧版插件：没有这个能力，让调用方立刻走回退路径，别白等一个超时
                return PlaybackNeverStarted;
            }

            return await task;
        }
        catch (Exception ex)
        {
            Logger.Log($"VPetTTSCoordinator: 等待起播失败: {ex.Message}");
            return PlaybackNeverStarted;
        }
    }

    /// <summary>
    /// <see cref="WaitForPlaybackStartAsync"/> 的哨兵值：音频没能播出来（或对方不支持回报）。
    /// </summary>
    public const long PlaybackNeverStarted = -1;

    /// <summary>
    /// 等待请求完成
    /// </summary>
    public async Task<bool> WaitForRequestCompleteAsync(string requestId, int timeoutSeconds = 60)
    {
        if (!_isInitialized || _ttsCoordinator == null)
        {
            throw new InvalidOperationException("VPetTTS 协调器未初始化");
        }

        try
        {
            var startTime = DateTime.Now;
            while ((DateTime.Now - startTime).TotalSeconds < timeoutSeconds)
            {
                // 中断时新版 VPetTTS 会把在途请求全部标记完成，这里自然退出；
                // 旧版没有中断接口，靠这一条别把等待拖满整个超时
                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log($"VPetTTSCoordinator: 等待请求 {requestId} 时被中断");
                    return false;
                }

                var task = VPetTTSPluginAdapter.IsRequestComplete(_ttsCoordinator, requestId);
                if (task == null)
                {
                    throw new InvalidOperationException("VPetTTS 协调器不支持 IsRequestCompleteAsync");
                }

                if (await task)
                {
                    return true;
                }

                await Task.Delay(100);
            }

            Logger.Log($"VPetTTSCoordinator: 等待请求 {requestId} 完成超时");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Log($"VPetTTSCoordinator: 等待请求完成失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 通知 VPetTTS 中断当前语音：停止播放并作废已提交但未播出的请求。
    /// 对方不支持（旧版插件）或未初始化时返回 false，调用方据此决定是否提示。
    /// </summary>
    public async Task<bool> InterruptAsync()
    {
        if (!_isInitialized || _ttsCoordinator == null)
        {
            return false;
        }

        try
        {
            var task = VPetTTSPluginAdapter.Interrupt(_ttsCoordinator, _callerId);
            if (task == null)
            {
                Logger.Log("VPetTTSCoordinator: 当前 VPetTTS 版本不支持中断通知");
                return false;
            }

            await task;
            Logger.Log("VPetTTSCoordinator: 已通知 VPetTTS 中断语音");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"VPetTTSCoordinator: 通知 VPetTTS 中断失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 检查是否正在处理
    /// </summary>
    public bool IsProcessing()
    {
        if (!_isInitialized || _ttsCoordinator == null)
        {
            return false;
        }

        try
        {
            return VPetTTSPluginAdapter.IsProcessing(_ttsCoordinator);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (!string.IsNullOrEmpty(_currentSessionId))
        {
            try
            {
                // 有界等待：Dispose 在插件卸载线程上执行，不能无限阻塞卸载流程
                EndExclusiveSessionAsync().Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // 忽略清理错误
            }
        }
    }
}
