using System;
using System.Threading.Tasks;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Utils
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

        // 缓存的反射信息
        private System.Reflection.PropertyInfo _ttsStateProperty;
        private System.Reflection.PropertyInfo _ttsCoordinatorProperty;

        // 状态属性的反射缓存
        private System.Reflection.PropertyInfo _isProcessingProperty;
        private System.Reflection.PropertyInfo _isDownloadingProperty;
        private System.Reflection.PropertyInfo _isPlayingProperty;
        private System.Reflection.PropertyInfo _isEnabledProperty;
        private System.Reflection.PropertyInfo _canAcceptNewRequestsProperty;
        private System.Reflection.PropertyInfo _currentProviderProperty;
        private System.Reflection.PropertyInfo _currentTextProperty;
        private System.Reflection.PropertyInfo _progressProperty;
        private System.Reflection.PropertyInfo _hasErrorProperty;
        private System.Reflection.PropertyInfo _lastErrorProperty;
        private System.Reflection.PropertyInfo _pluginVersionProperty;

        // 事件
        public event EventHandler<VPetTTSStateChangedEventArgs> StateChanged;
        public event EventHandler<VPetTTSAvailabilityChangedEventArgs> AvailabilityChanged;

        public VPetTTSCoordinator(IMainWindow mainWindow)
        {
            _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
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
                    Logger.Log("VPetTTSCoordinator: VPetTTS 插件未找到");
                    return false;
                }

                // 获取 TTSState 属性
                _ttsStateProperty = _vpetTTSPlugin.GetType().GetProperty("TTSState");
                if (_ttsStateProperty == null)
                {
                    Logger.Log("VPetTTSCoordinator: VPetTTS 插件没有 TTSState 属性");
                    return false;
                }

                _ttsState = _ttsStateProperty.GetValue(_vpetTTSPlugin);
                if (_ttsState == null)
                {
                    Logger.Log("VPetTTSCoordinator: TTSState 为 null");
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

                Logger.Log("VPetTTSCoordinator: 成功连接到 VPetTTS 插件");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetTTSCoordinator: 初始化失败: {ex.Message}");
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
                Logger.Log($"VPetTTSCoordinator: 获取状态信息失败: {ex.Message}");
            }

            return info;
        }

        private bool GetBoolProperty(System.Reflection.PropertyInfo property)
        {
            if (property == null || _ttsState == null) return false;
            var value = property.GetValue(_ttsState);
            return value is bool b && b;
        }

        private string GetStringProperty(System.Reflection.PropertyInfo property)
        {
            if (property == null || _ttsState == null) return "";
            var value = property.GetValue(_ttsState);
            return value?.ToString() ?? "";
        }

        private double GetDoubleProperty(System.Reflection.PropertyInfo property)
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
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        var dynamicHandler = Delegate.CreateDelegate(delegateType, this, methodInfo);
                        stateChangedEvent.AddEventHandler(_ttsState, dynamicHandler);
                    }

                    // 订阅 AvailabilityChanged 事件
                    var availabilityChangedEvent = _ttsState.GetType().GetEvent("AvailabilityChanged");
                    if (availabilityChangedEvent != null)
                    {
                        var delegateType = availabilityChangedEvent.EventHandlerType;
                        var methodInfo = GetType().GetMethod(nameof(OnTTSAvailabilityChangedDynamic),
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        var dynamicHandler = Delegate.CreateDelegate(delegateType, this, methodInfo);
                        availabilityChangedEvent.AddEventHandler(_ttsState, dynamicHandler);
                    }

                    _isMonitoring = true;
                    Logger.Log("VPetTTSCoordinator: 开始监听 VPetTTS 状态变化");
                }
                catch (Exception ex)
                {
                    Logger.Log($"VPetTTSCoordinator: 启动监听失败: {ex.Message}");
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

                _isMonitoring = false;
                Logger.Log("VPetTTSCoordinator: 停止监听 VPetTTS 状态变化");
            }
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
                Logger.Log($"VPetTTSCoordinator: 处理状态变化事件失败: {ex.Message}");
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
                Logger.Log($"VPetTTSCoordinator: 处理可用性变化事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 请求使用 VPetTTS
        /// </summary>
        public async Task<bool> RequestTTSUsageAsync(string requestId, string text)
        {
            if (!IsVPetTTSAvailable())
            {
                Logger.Log($"VPetTTSCoordinator: 请求 {requestId} 失败 - VPetTTS 不可用");
                return false;
            }

            if (!CanAcceptNewRequests())
            {
                Logger.Log($"VPetTTSCoordinator: 请求 {requestId} 失败 - VPetTTS 正忙");
                return false;
            }

            // 等待一小段时间确保状态稳定
            await Task.Delay(10);

            // 再次检查状态
            if (!CanAcceptNewRequests())
            {
                Logger.Log($"VPetTTSCoordinator: 请求 {requestId} 失败 - VPetTTS 状态已变化");
                return false;
            }

            Logger.Log($"VPetTTSCoordinator: 请求 {requestId} 成功 - 可以使用 VPetTTS");
            return true;
        }

        /// <summary>
        /// 释放 TTS 使用权
        /// </summary>
        public void ReleaseTTSUsage(string requestId)
        {
            Logger.Log($"VPetTTSCoordinator: 请求 {requestId} 已释放 TTS 使用权");
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
            _ttsState = null;
            _ttsCoordinator = null;
            _vpetTTSPlugin = null;
            _isDisposed = true;

            Logger.Log("VPetTTSCoordinator: 已释放资源");
        }
    }
}
