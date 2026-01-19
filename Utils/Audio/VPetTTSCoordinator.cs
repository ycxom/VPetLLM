using VPet_Simulator.Windows.Interface;
using VPetLLMUtils = VPetLLM.Utils.System;

namespace VPetLLM.Utils.Audio
{
    /// <summary>
    /// VPetTTS 状态枚举
    /// </summary>
    public enum VPetTTSOperationStage
    {
        Idle,
        Processing,
        Downloading,
        Playing,
        Completed,
        Error
    }

    /// <summary>
    /// VPetTTS 状态信息
    /// </summary>
    public class VPetTTSStateInfo
    {
        public bool IsProcessing { get; set; }
        public bool IsDownloading { get; set; }
        public bool IsPlaying { get; set; }
        public bool IsEnabled { get; set; }
        public bool CanAcceptNewRequests { get; set; }
        public string CurrentProvider { get; set; } = "";
        public string CurrentText { get; set; } = "";
        public double Progress { get; set; }
        public bool HasError { get; set; }
        public string LastError { get; set; } = "";
        public string PluginVersion { get; set; } = "";
    }

    /// <summary>
    /// VPetTTS 状态变化事件参数
    /// </summary>
    public class VPetTTSStateChangedEventArgs : EventArgs
    {
        public string PropertyName { get; set; }
        public object OldValue { get; set; }
        public object NewValue { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// VPetTTS 可用性变化事件参数
    /// </summary>
    public class VPetTTSAvailabilityChangedEventArgs : EventArgs
    {
        public bool IsAvailable { get; set; }
        public bool IsEnabled { get; set; }
        public string Reason { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// TTS 操作时序记录
    /// </summary>
    public class TTSOperationTiming
    {
        public string RequestId { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime? ProcessingStartTime { get; set; }
        public DateTime? DownloadingStartTime { get; set; }
        public DateTime? PlayingStartTime { get; set; }
        public DateTime? CompletedTime { get; set; }
        public DateTime? ErrorTime { get; set; }
        public string ErrorMessage { get; set; } = "";
        public VPetTTSOperationStage CurrentStage { get; set; } = VPetTTSOperationStage.Idle;
        public VPetTTSOperationStage PreviousStage { get; set; } = VPetTTSOperationStage.Idle;

        /// <summary>
        /// 获取当前阶段的持续时间（毫秒）
        /// </summary>
        public int GetCurrentStageDurationMs()
        {
            var stageStartTime = GetStageStartTime(CurrentStage);
            if (stageStartTime.HasValue)
            {
                return (int)(DateTime.Now - stageStartTime.Value).TotalMilliseconds;
            }
            return 0;
        }

        /// <summary>
        /// 获取总操作时间（毫秒）
        /// </summary>
        public int GetTotalDurationMs()
        {
            var endTime = CompletedTime ?? ErrorTime ?? DateTime.Now;
            return (int)(endTime - StartTime).TotalMilliseconds;
        }

        /// <summary>
        /// 获取指定阶段的开始时间
        /// </summary>
        private DateTime? GetStageStartTime(VPetTTSOperationStage stage)
        {
            return stage switch
            {
                VPetTTSOperationStage.Processing => ProcessingStartTime,
                VPetTTSOperationStage.Downloading => DownloadingStartTime,
                VPetTTSOperationStage.Playing => PlayingStartTime,
                VPetTTSOperationStage.Completed => CompletedTime,
                VPetTTSOperationStage.Error => ErrorTime,
                _ => StartTime
            };
        }
    }

    /// <summary>
    /// TTS 状态转换超时检测器
    /// </summary>
    public class TTSTimeoutDetector
    {
        private readonly Dictionary<VPetTTSOperationStage, int> _stageTimeouts;
        private readonly Dictionary<string, TTSOperationTiming> _activeOperations;
        private readonly object _lockObject = new object();

        public TTSTimeoutDetector()
        {
            _stageTimeouts = new Dictionary<VPetTTSOperationStage, int>
            {
                { VPetTTSOperationStage.Processing, 10000 },   // 10秒
                { VPetTTSOperationStage.Downloading, 30000 },  // 30秒
                { VPetTTSOperationStage.Playing, 60000 },      // 60秒
            };
            _activeOperations = new Dictionary<string, TTSOperationTiming>();
        }

        /// <summary>
        /// 检测超时操作
        /// </summary>
        public List<TTSOperationTiming> DetectTimeouts()
        {
            var timeouts = new List<TTSOperationTiming>();

            lock (_lockObject)
            {
                foreach (var operation in _activeOperations.Values)
                {
                    if (_stageTimeouts.TryGetValue(operation.CurrentStage, out int timeoutMs))
                    {
                        if (operation.GetCurrentStageDurationMs() > timeoutMs)
                        {
                            timeouts.Add(operation);
                        }
                    }
                }
            }

            return timeouts;
        }

        /// <summary>
        /// 更新操作状态
        /// </summary>
        public void UpdateOperation(string requestId, VPetTTSOperationStage newStage, string text = "", string errorMessage = "")
        {
            lock (_lockObject)
            {
                if (!_activeOperations.TryGetValue(requestId, out var operation))
                {
                    operation = new TTSOperationTiming
                    {
                        RequestId = requestId,
                        Text = text,
                        StartTime = DateTime.Now
                    };
                    _activeOperations[requestId] = operation;
                }

                operation.PreviousStage = operation.CurrentStage;
                operation.CurrentStage = newStage;

                // 更新阶段时间戳
                var now = DateTime.Now;
                switch (newStage)
                {
                    case VPetTTSOperationStage.Processing:
                        operation.ProcessingStartTime = now;
                        break;
                    case VPetTTSOperationStage.Downloading:
                        operation.DownloadingStartTime = now;
                        break;
                    case VPetTTSOperationStage.Playing:
                        operation.PlayingStartTime = now;
                        break;
                    case VPetTTSOperationStage.Completed:
                        operation.CompletedTime = now;
                        break;
                    case VPetTTSOperationStage.Error:
                        operation.ErrorTime = now;
                        operation.ErrorMessage = errorMessage;
                        break;
                }

                // 完成或错误时移除活跃操作
                if (newStage == VPetTTSOperationStage.Completed || newStage == VPetTTSOperationStage.Error)
                {
                    _activeOperations.Remove(requestId);
                }
            }
        }

        /// <summary>
        /// 获取活跃操作
        /// </summary>
        public List<TTSOperationTiming> GetActiveOperations()
        {
            lock (_lockObject)
            {
                return new List<TTSOperationTiming>(_activeOperations.Values);
            }
        }

        /// <summary>
        /// 清理操作记录
        /// </summary>
        public void CleanupOperation(string requestId)
        {
            lock (_lockObject)
            {
                _activeOperations.Remove(requestId);
            }
        }
    }

    /// <summary>
    /// VPetTTS 协调器
    /// 用于 VPetLLM 与 VPetTTS 插件之间的状态协调
    /// </summary>
    public class VPetTTSCoordinator : IDisposable
    {
        private readonly IMainWindow _mainWindow;
        private MainPlugin _vpetTTSPlugin;
        private object _ttsState;
        private object _ttsCoordinator;
        private bool _isMonitoring;
        private bool _isDisposed;
        private readonly object _lockObject = new object();

        // 监控和超时检测
        private readonly TTSTimeoutDetector _timeoutDetector;
        private global::System.Threading.Timer _monitoringTimer;
        private VPetTTSStateInfo _lastStateInfo;
        private readonly List<TTSOperationTiming> _recentOperations;

        // 缓存的反射信息
        private global::System.Reflection.PropertyInfo _ttsStateProperty;
        private global::System.Reflection.PropertyInfo _ttsCoordinatorProperty;

        // 状态属性的反射缓存
        private global::System.Reflection.PropertyInfo _isProcessingProperty;
        private global::System.Reflection.PropertyInfo _isDownloadingProperty;
        private global::System.Reflection.PropertyInfo _isPlayingProperty;
        private global::System.Reflection.PropertyInfo _isEnabledProperty;
        private global::System.Reflection.PropertyInfo _canAcceptNewRequestsProperty;
        private global::System.Reflection.PropertyInfo _currentProviderProperty;
        private global::System.Reflection.PropertyInfo _currentTextProperty;
        private global::System.Reflection.PropertyInfo _progressProperty;
        private global::System.Reflection.PropertyInfo _hasErrorProperty;
        private global::System.Reflection.PropertyInfo _lastErrorProperty;
        private global::System.Reflection.PropertyInfo _pluginVersionProperty;

        // 事件
        public event EventHandler<VPetTTSStateChangedEventArgs> StateChanged;
        public event EventHandler<VPetTTSAvailabilityChangedEventArgs> AvailabilityChanged;

        public VPetTTSCoordinator(IMainWindow mainWindow)
        {
            _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
            _timeoutDetector = new TTSTimeoutDetector();
            _recentOperations = new List<TTSOperationTiming>();
        }

        /// <summary>
        /// 初始化协调器，检测并连接到 VPetTTS 插件
        /// </summary>
        public bool Initialize()
        {
            try
            {
                _vpetTTSPlugin = FindVPetTTSPlugin();
                if (_vpetTTSPlugin == null)
                {
                    VPetLLMUtils.Logger.Log("VPetTTSCoordinator: VPetTTS 插件未找到");
                    return false;
                }

                // 获取 TTSState 属性
                _ttsStateProperty = _vpetTTSPlugin.GetType().GetProperty("TTSState");
                if (_ttsStateProperty == null)
                {
                    VPetLLMUtils.Logger.Log("VPetTTSCoordinator: VPetTTS 插件没有 TTSState 属性");
                    return false;
                }

                _ttsState = _ttsStateProperty.GetValue(_vpetTTSPlugin);
                if (_ttsState == null)
                {
                    VPetLLMUtils.Logger.Log("VPetTTSCoordinator: TTSState 为 null");
                    return false;
                }

                // 获取 TTSCoordinator 属性（可选）
                _ttsCoordinatorProperty = _vpetTTSPlugin.GetType().GetProperty("TTSCoordinator");
                if (_ttsCoordinatorProperty != null)
                {
                    _ttsCoordinator = _ttsCoordinatorProperty.GetValue(_vpetTTSPlugin);
                }

                // 缓存状态属性的反射信息
                CacheStateProperties();

                VPetLLMUtils.Logger.Log("VPetTTSCoordinator: 成功连接到 VPetTTS 插件");
                return true;
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: 初始化失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 缓存状态属性的反射信息
        /// </summary>
        private void CacheStateProperties()
        {
            if (_ttsState == null) return;

            var stateType = _ttsState.GetType();
            _isProcessingProperty = stateType.GetProperty("IsProcessing");
            _isDownloadingProperty = stateType.GetProperty("IsDownloading");
            _isPlayingProperty = stateType.GetProperty("IsPlaying");
            _isEnabledProperty = stateType.GetProperty("IsEnabled");
            _canAcceptNewRequestsProperty = stateType.GetProperty("CanAcceptNewRequests");
            _currentProviderProperty = stateType.GetProperty("CurrentProvider");
            _currentTextProperty = stateType.GetProperty("CurrentText");
            _progressProperty = stateType.GetProperty("Progress");
            _hasErrorProperty = stateType.GetProperty("HasError");
            _lastErrorProperty = stateType.GetProperty("LastError");
            _pluginVersionProperty = stateType.GetProperty("PluginVersion");
        }

        /// <summary>
        /// 查找 VPetTTS 插件
        /// </summary>
        private MainPlugin FindVPetTTSPlugin()
        {
            if (_mainWindow?.Plugins == null) return null;

            foreach (var plugin in _mainWindow.Plugins)
            {
                if (string.Equals(plugin.PluginName, "VPetTTS", StringComparison.OrdinalIgnoreCase))
                {
                    return plugin;
                }
            }
            return null;
        }

        /// <summary>
        /// 检查 VPetTTS 是否可用
        /// </summary>
        public bool IsVPetTTSAvailable()
        {
            if (_ttsState == null)
            {
                // 尝试重新初始化
                if (!Initialize())
                    return false;
            }

            try
            {
                var isEnabled = _isEnabledProperty?.GetValue(_ttsState);
                return isEnabled is bool enabled && enabled;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 检查 VPetTTS 是否可以接受新请求
        /// </summary>
        public bool CanAcceptNewRequests()
        {
            if (_ttsState == null) return false;

            try
            {
                var canAccept = _canAcceptNewRequestsProperty?.GetValue(_ttsState);
                return canAccept is bool accept && accept;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取当前 TTS 状态信息
        /// </summary>
        public VPetTTSStateInfo GetStateInfo()
        {
            var info = new VPetTTSStateInfo();

            if (_ttsState == null)
            {
                return info;
            }

            try
            {
                info.IsProcessing = GetBoolProperty(_isProcessingProperty);
                info.IsDownloading = GetBoolProperty(_isDownloadingProperty);
                info.IsPlaying = GetBoolProperty(_isPlayingProperty);
                info.IsEnabled = GetBoolProperty(_isEnabledProperty);
                info.CanAcceptNewRequests = GetBoolProperty(_canAcceptNewRequestsProperty);
                info.CurrentProvider = GetStringProperty(_currentProviderProperty);
                info.CurrentText = GetStringProperty(_currentTextProperty);
                info.Progress = GetDoubleProperty(_progressProperty);
                info.HasError = GetBoolProperty(_hasErrorProperty);
                info.LastError = GetStringProperty(_lastErrorProperty);
                info.PluginVersion = GetStringProperty(_pluginVersionProperty);
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: 获取状态信息失败: {ex.Message}");
            }

            return info;
        }

        private bool GetBoolProperty(global::System.Reflection.PropertyInfo property)
        {
            if (property == null || _ttsState == null) return false;
            var value = property.GetValue(_ttsState);
            return value is bool b && b;
        }

        private string GetStringProperty(global::System.Reflection.PropertyInfo property)
        {
            if (property == null || _ttsState == null) return "";
            var value = property.GetValue(_ttsState);
            return value?.ToString() ?? "";
        }

        private double GetDoubleProperty(global::System.Reflection.PropertyInfo property)
        {
            if (property == null || _ttsState == null) return 0;
            var value = property.GetValue(_ttsState);
            return value is double d ? d : 0;
        }

        /// <summary>
        /// 开始监听 VPetTTS 状态变化
        /// </summary>
        public void StartMonitoring()
        {
            lock (_lockObject)
            {
                if (_isMonitoring || _ttsState == null) return;

                try
                {
                    // 订阅 StateChanged 事件
                    var stateChangedEvent = _ttsState.GetType().GetEvent("StateChanged");
                    if (stateChangedEvent != null)
                    {
                        var handler = new EventHandler<object>(OnTTSStateChanged);
                        // 使用动态方法创建委托
                        var delegateType = stateChangedEvent.EventHandlerType;
                        var methodInfo = GetType().GetMethod(nameof(OnTTSStateChangedDynamic),
                            global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance);
                        var dynamicHandler = Delegate.CreateDelegate(delegateType, this, methodInfo);
                        stateChangedEvent.AddEventHandler(_ttsState, dynamicHandler);
                    }

                    // 订阅 AvailabilityChanged 事件
                    var availabilityChangedEvent = _ttsState.GetType().GetEvent("AvailabilityChanged");
                    if (availabilityChangedEvent != null)
                    {
                        var delegateType = availabilityChangedEvent.EventHandlerType;
                        var methodInfo = GetType().GetMethod(nameof(OnTTSAvailabilityChangedDynamic),
                            global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance);
                        var dynamicHandler = Delegate.CreateDelegate(delegateType, this, methodInfo);
                        availabilityChangedEvent.AddEventHandler(_ttsState, dynamicHandler);
                    }

                    // 启动监控定时器
                    _monitoringTimer = new global::System.Threading.Timer(OnMonitoringTick, null, 1000, 1000); // 每秒检查一次

                    // 初始化状态
                    _lastStateInfo = GetStateInfo();

                    _isMonitoring = true;
                    VPetLLMUtils.Logger.Log("VPetTTSCoordinator: 开始监听 VPetTTS 状态变化和超时检测");
                }
                catch (Exception ex)
                {
                    VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: 启动监听失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 停止监听 VPetTTS 状态变化
        /// </summary>
        public void StopMonitoring()
        {
            lock (_lockObject)
            {
                if (!_isMonitoring) return;

                // 停止监控定时器
                _monitoringTimer?.Dispose();
                _monitoringTimer = null;

                _isMonitoring = false;
                VPetLLMUtils.Logger.Log("VPetTTSCoordinator: 停止监听 VPetTTS 状态变化");
            }
        }

        /// <summary>
        /// 监控定时器回调
        /// </summary>
        private void OnMonitoringTick(object state)
        {
            try
            {
                // 检测超时操作
                var timeouts = _timeoutDetector.DetectTimeouts();
                foreach (var timeout in timeouts)
                {
                    VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: 检测到超时操作 - RequestId: {timeout.RequestId}, " +
                              $"Stage: {timeout.CurrentStage}, Duration: {timeout.GetCurrentStageDurationMs()}ms");

                    // 触发超时恢复
                    HandleOperationTimeout(timeout);
                }

                // 检测状态变化
                var currentState = GetStateInfo();
                if (_lastStateInfo != null)
                {
                    DetectStateTransitions(_lastStateInfo, currentState);
                }
                _lastStateInfo = currentState;

                // 清理旧的操作记录（保留最近100个）
                lock (_lockObject)
                {
                    if (_recentOperations.Count > 100)
                    {
                        _recentOperations.RemoveRange(0, _recentOperations.Count - 100);
                    }
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: 监控定时器异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 检测状态转换
        /// </summary>
        private void DetectStateTransitions(VPetTTSStateInfo oldState, VPetTTSStateInfo newState)
        {
            // 使用旧状态的文本内容作为请求ID，确保一致性
            var operationText = !string.IsNullOrEmpty(oldState.CurrentText) ? oldState.CurrentText : newState.CurrentText;

            // 检测处理状态变化
            if (oldState.IsProcessing != newState.IsProcessing)
            {
                var stage = newState.IsProcessing ? VPetTTSOperationStage.Processing : VPetTTSOperationStage.Idle;
                UpdateCurrentOperationStage(stage, operationText);
                VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: TTS处理状态变化 - {oldState.IsProcessing} -> {newState.IsProcessing}");
            }

            // 检测下载状态变化
            if (oldState.IsDownloading != newState.IsDownloading)
            {
                var stage = newState.IsDownloading ? VPetTTSOperationStage.Downloading : VPetTTSOperationStage.Idle;
                UpdateCurrentOperationStage(stage, operationText);
                VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: TTS下载状态变化 - {oldState.IsDownloading} -> {newState.IsDownloading}");
            }

            // 检测播放状态变化
            if (oldState.IsPlaying != newState.IsPlaying)
            {
                var stage = newState.IsPlaying ? VPetTTSOperationStage.Playing : VPetTTSOperationStage.Completed;
                UpdateCurrentOperationStage(stage, operationText);
                VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: TTS播放状态变化 - {oldState.IsPlaying} -> {newState.IsPlaying}");
            }

            // 检测错误状态变化
            if (oldState.HasError != newState.HasError && newState.HasError)
            {
                UpdateCurrentOperationStage(VPetTTSOperationStage.Error, operationText, newState.LastError);
                VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: TTS错误状态变化 - Error: {newState.LastError}");
            }
        }

        /// <summary>
        /// 更新当前操作阶段
        /// </summary>
        private void UpdateCurrentOperationStage(VPetTTSOperationStage stage, string text = "", string errorMessage = "")
        {
            // 使用文本内容的哈希作为请求ID（同一文本的不同阶段使用相同ID）
            var textHash = text?.GetHashCode() ?? 0;
            var requestId = $"tts_{textHash}";

            // 如果是新的文本，清理之前的操作
            if (!string.IsNullOrEmpty(text) && stage == VPetTTSOperationStage.Processing)
            {
                // 清理可能存在的旧操作
                _timeoutDetector.CleanupOperation(requestId);
            }

            _timeoutDetector.UpdateOperation(requestId, stage, text, errorMessage);
        }

        /// <summary>
        /// 处理操作超时
        /// </summary>
        private void HandleOperationTimeout(TTSOperationTiming timeout)
        {
            try
            {
                VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: 处理超时操作 - RequestId: {timeout.RequestId}, " +
                          $"Stage: {timeout.CurrentStage}, Text: {(timeout.Text?.Length > 50 ? timeout.Text.Substring(0, 50) : timeout.Text ?? "")}...");

                // 记录超时操作
                lock (_lockObject)
                {
                    _recentOperations.Add(timeout);
                }

                // 清理超时操作
                _timeoutDetector.CleanupOperation(timeout.RequestId);

                // 触发超时恢复建议
                var recoveryMessage = GenerateRecoveryMessage(timeout);
                VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: 超时恢复建议 - {recoveryMessage}");

            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: 处理超时操作失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 生成恢复建议消息
        /// </summary>
        private string GenerateRecoveryMessage(TTSOperationTiming timeout)
        {
            return timeout.CurrentStage switch
            {
                VPetTTSOperationStage.Processing => "TTS处理超时，建议检查网络连接或重启VPetTTS插件",
                VPetTTSOperationStage.Downloading => "TTS下载超时，建议检查网络连接或更换TTS提供商",
                VPetTTSOperationStage.Playing => "TTS播放超时，建议检查音频设备或重启VPetTTS插件",
                _ => "TTS操作超时，建议重启VPetTTS插件或检查系统资源"
            };
        }

        /// <summary>
        /// 获取超时检测器
        /// </summary>
        public TTSTimeoutDetector GetTimeoutDetector()
        {
            return _timeoutDetector;
        }

        /// <summary>
        /// 获取最近的操作记录
        /// </summary>
        public List<TTSOperationTiming> GetRecentOperations()
        {
            lock (_lockObject)
            {
                return new List<TTSOperationTiming>(_recentOperations);
            }
        }

        /// <summary>
        /// 获取详细的时序记录
        /// </summary>
        public string GetDetailedTimingLog()
        {
            var log = new global::System.Text.StringBuilder();
            log.AppendLine("=== VPetTTS 操作时序记录 ===");

            var recentOps = GetRecentOperations();
            if (recentOps.Count == 0)
            {
                log.AppendLine("无最近操作记录");
            }
            else
            {
                foreach (var op in recentOps.TakeLast(10)) // 显示最近10个操作
                {
                    log.AppendLine($"RequestId: {op.RequestId}");
                    log.AppendLine($"  文本: {(op.Text?.Length > 30 ? op.Text.Substring(0, 30) : op.Text ?? "")}...");
                    log.AppendLine($"  开始时间: {op.StartTime:HH:mm:ss.fff}");
                    log.AppendLine($"  当前阶段: {op.CurrentStage}");
                    log.AppendLine($"  总耗时: {op.GetTotalDurationMs()}ms");
                    if (!string.IsNullOrEmpty(op.ErrorMessage))
                    {
                        log.AppendLine($"  错误信息: {op.ErrorMessage}");
                    }
                    log.AppendLine();
                }
            }

            var activeOps = _timeoutDetector.GetActiveOperations();
            if (activeOps.Count > 0)
            {
                log.AppendLine("=== 活跃操作 ===");
                foreach (var op in activeOps)
                {
                    log.AppendLine($"RequestId: {op.RequestId}");
                    log.AppendLine($"  当前阶段: {op.CurrentStage}");
                    log.AppendLine($"  阶段耗时: {op.GetCurrentStageDurationMs()}ms");
                    log.AppendLine();
                }
            }

            return log.ToString();
        }

        // 动态事件处理方法（用于反射订阅）
        private void OnTTSStateChangedDynamic(object sender, object e)
        {
            OnTTSStateChanged(sender, e);
        }

        private void OnTTSAvailabilityChangedDynamic(object sender, object e)
        {
            OnTTSAvailabilityChanged(sender, e);
        }

        private void OnTTSStateChanged(object sender, object e)
        {
            try
            {
                var args = new VPetTTSStateChangedEventArgs
                {
                    Timestamp = DateTime.Now
                };

                // 尝试从事件参数中提取信息
                if (e != null)
                {
                    var eType = e.GetType();
                    var propertyNameProp = eType.GetProperty("PropertyName");
                    var oldValueProp = eType.GetProperty("OldValue");
                    var newValueProp = eType.GetProperty("NewValue");

                    args.PropertyName = propertyNameProp?.GetValue(e)?.ToString() ?? "";
                    args.OldValue = oldValueProp?.GetValue(e);
                    args.NewValue = newValueProp?.GetValue(e);
                }

                StateChanged?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: 处理状态变化事件失败: {ex.Message}");
            }
        }

        private void OnTTSAvailabilityChanged(object sender, object e)
        {
            try
            {
                var args = new VPetTTSAvailabilityChangedEventArgs
                {
                    Timestamp = DateTime.Now
                };

                // 尝试从事件参数中提取信息
                if (e != null)
                {
                    var eType = e.GetType();
                    var isAvailableProp = eType.GetProperty("IsAvailable");
                    var isEnabledProp = eType.GetProperty("IsEnabled");
                    var reasonProp = eType.GetProperty("Reason");

                    args.IsAvailable = isAvailableProp?.GetValue(e) is bool available && available;
                    args.IsEnabled = isEnabledProp?.GetValue(e) is bool enabled && enabled;
                    args.Reason = reasonProp?.GetValue(e)?.ToString() ?? "";
                }

                AvailabilityChanged?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: 处理可用性变化事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 请求使用 VPetTTS
        /// </summary>
        public async Task<bool> RequestTTSUsageAsync(string requestId, string text)
        {
            if (!IsVPetTTSAvailable())
            {
                VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: 请求 {requestId} 失败 - VPetTTS 不可用");
                return false;
            }

            if (!CanAcceptNewRequests())
            {
                VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: 请求 {requestId} 失败 - VPetTTS 正忙");
                return false;
            }

            // 等待一小段时间确保状态稳定
            await Task.Delay(10);

            // 再次检查状态
            if (!CanAcceptNewRequests())
            {
                VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: 请求 {requestId} 失败 - VPetTTS 状态已变化");
                return false;
            }

            VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: 请求 {requestId} 成功 - 可以使用 VPetTTS");
            return true;
        }

        /// <summary>
        /// 释放 TTS 使用权
        /// </summary>
        public void ReleaseTTSUsage(string requestId)
        {
            VPetLLMUtils.Logger.Log($"VPetTTSCoordinator: 请求 {requestId} 已释放 TTS 使用权");
        }

        /// <summary>
        /// 获取 VPetTTS 插件实例（供高级用途）
        /// </summary>
        public MainPlugin GetVPetTTSPlugin()
        {
            return _vpetTTSPlugin;
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            StopMonitoring();
            _monitoringTimer?.Dispose();
            _ttsState = null;
            _ttsCoordinator = null;
            _vpetTTSPlugin = null;
            _isDisposed = true;

            VPetLLMUtils.Logger.Log("VPetTTSCoordinator: 已释放资源");
        }
    }
}
