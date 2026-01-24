using VPet_Simulator.Windows.Interface;
using System.Reflection;

namespace VPetLLM.Utils.Audio;

/// <summary>
/// VPetTTS 协调器（VPetLLM 侧）
/// 用于与 VPetTTS 插件的协调器接口交互
/// 使用反射调用，避免直接引用 VPetTTS 程序集
/// </summary>
public class VPetTTSCoordinator : IDisposable
{
    private readonly IMainWindow _mainWindow;
    private object? _ttsCoordinator; // 使用 object 而不是接口类型
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

            Logger.Log($"VPetTTSCoordinator.Initialize: 找到 VPetTTS 插件，类型: {vpetTTSPlugin.GetType().FullName}");

            // 获取协调器接口
            var coordinatorProperty = vpetTTSPlugin.GetType().GetProperty("TTSCoordinator");
            if (coordinatorProperty == null)
            {
                Logger.Log("VPetTTSCoordinator.Initialize: VPetTTS 插件未提供 TTSCoordinator 属性");
                
                // 列出所有可用属性
                var properties = vpetTTSPlugin.GetType().GetProperties();
                Logger.Log($"VPetTTSCoordinator.Initialize: 可用属性列表:");
                foreach (var prop in properties)
                {
                    Logger.Log($"  - {prop.Name} ({prop.PropertyType.Name})");
                }
                
                return false;
            }

            Logger.Log($"VPetTTSCoordinator.Initialize: 找到 TTSCoordinator 属性，类型: {coordinatorProperty.PropertyType.FullName}");

            _ttsCoordinator = coordinatorProperty.GetValue(vpetTTSPlugin);
            if (_ttsCoordinator == null)
            {
                Logger.Log("VPetTTSCoordinator.Initialize: 无法获取 VPetTTS 协调器接口（返回值为 null）");
                Logger.Log("VPetTTSCoordinator.Initialize: 这可能是因为 VPetTTS 插件还未完成初始化");
                return false;
            }

            Logger.Log($"VPetTTSCoordinator.Initialize: 成功获取协调器实例，类型: {_ttsCoordinator.GetType().FullName}");

            _isInitialized = true;
            Logger.Log("VPetTTSCoordinator.Initialize: 协调器初始化成功");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"VPetTTSCoordinator.Initialize: 初始化失败: {ex.Message}");
            Logger.Log($"VPetTTSCoordinator.Initialize: 堆栈跟踪: {ex.StackTrace}");
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
            var method = _ttsCoordinator.GetType().GetMethod("PreloadAsync");
            if (method == null)
            {
                return false;
            }

            var task = method.Invoke(_ttsCoordinator, new object[] { text, _currentSessionId }) as Task<bool>;
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
            // 使用反射调用 StartExclusiveSessionAsync
            var method = _ttsCoordinator.GetType().GetMethod("StartExclusiveSessionAsync");
            if (method == null)
            {
                throw new InvalidOperationException("未找到 StartExclusiveSessionAsync 方法");
            }

            var task = method.Invoke(_ttsCoordinator, new object[] { _callerId }) as Task<string>;
            if (task == null)
            {
                throw new InvalidOperationException("StartExclusiveSessionAsync 返回值无效");
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
            // 使用反射调用 EndExclusiveSessionAsync
            var method = _ttsCoordinator.GetType().GetMethod("EndExclusiveSessionAsync");
            if (method == null)
            {
                throw new InvalidOperationException("未找到 EndExclusiveSessionAsync 方法");
            }

            var task = method.Invoke(_ttsCoordinator, new object[] { _callerId, _currentSessionId }) as Task;
            if (task == null)
            {
                throw new InvalidOperationException("EndExclusiveSessionAsync 返回值无效");
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
            var preloadMethod = _ttsCoordinator.GetType().GetMethod("PreloadAsync");
            if (preloadMethod != null)
            {
                var preloadTask = preloadMethod.Invoke(_ttsCoordinator, new object[] { text, _currentSessionId }) as Task<bool>;
                if (preloadTask != null)
                {
                    await preloadTask;
                    Logger.Log("VPetTTSCoordinator: 预加载成功");
                }
            }

            // 提交请求
            var submitMethod = _ttsCoordinator.GetType().GetMethod("SubmitTTSAsync");
            if (submitMethod == null)
            {
                throw new InvalidOperationException("未找到 SubmitTTSAsync 方法");
            }

            var submitTask = submitMethod.Invoke(_ttsCoordinator, new object[] { text, _currentSessionId }) as Task<string>;
            if (submitTask == null)
            {
                throw new InvalidOperationException("SubmitTTSAsync 返回值无效");
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
            var method = _ttsCoordinator.GetType().GetMethod("IsRequestCompleteAsync");
            if (method == null)
            {
                throw new InvalidOperationException("未找到 IsRequestCompleteAsync 方法");
            }

            var startTime = DateTime.Now;
            while ((DateTime.Now - startTime).TotalSeconds < timeoutSeconds)
            {
                var task = method.Invoke(_ttsCoordinator, new object[] { requestId }) as Task<bool>;
                if (task != null && await task)
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
            var method = _ttsCoordinator.GetType().GetMethod("IsProcessing");
            if (method == null)
            {
                return false;
            }

            var result = method.Invoke(_ttsCoordinator, null);
            return result is bool isProcessing && isProcessing;
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
                EndExclusiveSessionAsync().Wait();
            }
            catch
            {
                // 忽略清理错误
            }
        }
    }
}
