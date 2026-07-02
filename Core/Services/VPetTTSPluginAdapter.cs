using System.Reflection;

namespace VPetLLM.Core.Services
{
    /// <summary>
    /// VPetTTS 插件（外部程序集）非公开成员的统一访问适配层。
    /// 约束：对 VPetTTS 内部（TTSCoordinator 方法、TTSState 属性/事件）的所有反射
    /// 必须收拢到这里——成员按运行时类型解析并缓存一次，缺失时只在首次记日志，
    /// 调用方拿到 null/false 后走各自的降级路径。
    /// 不直接引用 VPetTTS 程序集，避免硬依赖。
    /// </summary>
    public static class VPetTTSPluginAdapter
    {
        private static readonly object _lock = new object();

        // ---- 插件对象 → TTSCoordinator / TTSState ----
        private static volatile Type _pluginType;
        private static PropertyInfo _coordinatorProperty;
        private static PropertyInfo _ttsStateProperty;

        private static void EnsurePluginAccessors(object plugin)
        {
            var type = plugin.GetType();
            if (_pluginType == type) return;

            lock (_lock)
            {
                if (_pluginType == type) return;

                _coordinatorProperty = type.GetProperty("TTSCoordinator");
                _ttsStateProperty = type.GetProperty("TTSState");

                var missing = new List<string>();
                if (_coordinatorProperty is null) missing.Add("TTSCoordinator");
                if (_ttsStateProperty is null) missing.Add("TTSState");
                if (missing.Count > 0)
                    Logger.Log($"VPetTTSPluginAdapter: VPetTTS({type.Name}) 缺少成员: {string.Join(", ", missing)}，相关协作功能降级");

                _pluginType = type;
            }
        }

        /// <summary>
        /// 获取 VPetTTS 的协调器实例（插件未初始化完成时可能为 null）。
        /// </summary>
        public static object GetCoordinator(object plugin)
        {
            if (plugin is null) return null;
            EnsurePluginAccessors(plugin);
            return _coordinatorProperty?.GetValue(plugin);
        }

        /// <summary>
        /// 获取 VPetTTS 的状态对象。
        /// </summary>
        public static object GetTTSState(object plugin)
        {
            if (plugin is null) return null;
            EnsurePluginAccessors(plugin);
            return _ttsStateProperty?.GetValue(plugin);
        }

        // ---- 协调器方法 ----
        private static volatile Type _coordinatorType;
        private static MethodInfo _miStartExclusiveSession;
        private static MethodInfo _miEndExclusiveSession;
        private static MethodInfo _miPreload;
        private static MethodInfo _miSubmitTTS;
        private static MethodInfo _miIsRequestComplete;
        private static MethodInfo _miIsProcessing;

        private static void EnsureCoordinatorAccessors(object coordinator)
        {
            var type = coordinator.GetType();
            if (_coordinatorType == type) return;

            lock (_lock)
            {
                if (_coordinatorType == type) return;

                _miStartExclusiveSession = type.GetMethod("StartExclusiveSessionAsync");
                _miEndExclusiveSession = type.GetMethod("EndExclusiveSessionAsync");
                _miPreload = type.GetMethod("PreloadAsync");
                _miSubmitTTS = type.GetMethod("SubmitTTSAsync");
                _miIsRequestComplete = type.GetMethod("IsRequestCompleteAsync");
                _miIsProcessing = type.GetMethod("IsProcessing");

                var missing = new List<string>();
                if (_miStartExclusiveSession is null) missing.Add("StartExclusiveSessionAsync");
                if (_miEndExclusiveSession is null) missing.Add("EndExclusiveSessionAsync");
                if (_miPreload is null) missing.Add("PreloadAsync");
                if (_miSubmitTTS is null) missing.Add("SubmitTTSAsync");
                if (_miIsRequestComplete is null) missing.Add("IsRequestCompleteAsync");
                if (_miIsProcessing is null) missing.Add("IsProcessing");
                if (missing.Count > 0)
                    Logger.Log($"VPetTTSPluginAdapter: 协调器({type.Name}) 缺少方法: {string.Join(", ", missing)}");

                _coordinatorType = type;
            }
        }

        public static Task<string> StartExclusiveSession(object coordinator, string callerId)
        {
            if (coordinator is null) return null;
            EnsureCoordinatorAccessors(coordinator);
            return _miStartExclusiveSession?.Invoke(coordinator, new object[] { callerId }) as Task<string>;
        }

        public static Task EndExclusiveSession(object coordinator, string callerId, string sessionId)
        {
            if (coordinator is null) return null;
            EnsureCoordinatorAccessors(coordinator);
            return _miEndExclusiveSession?.Invoke(coordinator, new object[] { callerId, sessionId }) as Task;
        }

        public static Task<bool> Preload(object coordinator, string text, string sessionId)
        {
            if (coordinator is null) return null;
            EnsureCoordinatorAccessors(coordinator);
            return _miPreload?.Invoke(coordinator, new object[] { text, sessionId }) as Task<bool>;
        }

        public static Task<string> SubmitTTS(object coordinator, string text, string sessionId)
        {
            if (coordinator is null) return null;
            EnsureCoordinatorAccessors(coordinator);
            return _miSubmitTTS?.Invoke(coordinator, new object[] { text, sessionId }) as Task<string>;
        }

        public static Task<bool> IsRequestComplete(object coordinator, string requestId)
        {
            if (coordinator is null) return null;
            EnsureCoordinatorAccessors(coordinator);
            return _miIsRequestComplete?.Invoke(coordinator, new object[] { requestId }) as Task<bool>;
        }

        public static bool IsProcessing(object coordinator)
        {
            if (coordinator is null) return false;
            EnsureCoordinatorAccessors(coordinator);
            return _miIsProcessing?.Invoke(coordinator, null) is true;
        }

        // ---- TTSState 属性/事件 ----
        private static volatile Type _stateType;
        // 整体替换引用而非就地修改，避免类型切换瞬间的读写竞态
        private static volatile Dictionary<string, PropertyInfo> _stateProperties = new();
        private static EventInfo _playbackCompletedEvent;

        private static void EnsureStateAccessors(object ttsState)
        {
            var type = ttsState.GetType();
            if (_stateType == type) return;

            lock (_lock)
            {
                if (_stateType == type) return;

                var props = new Dictionary<string, PropertyInfo>();
                foreach (var name in new[]
                {
                    "IsPlaying", "IsPlaybackComplete", "LastHeartbeatTime",
                    "PlaybackProgress", "PlaybackPositionMs", "AudioDurationMs",
                    "PlaybackStartTime", "EstimatedPlaybackEndTime"
                })
                {
                    var prop = type.GetProperty(name);
                    if (prop is not null) props[name] = prop;
                }
                _playbackCompletedEvent = type.GetEvent("PlaybackCompleted");

                if (!props.ContainsKey("IsPlaying"))
                    Logger.Log($"VPetTTSPluginAdapter: TTSState({type.Name}) 缺少 IsPlaying 属性，播放状态检测降级");

                _stateProperties = props;
                _stateType = type;
            }
        }

        /// <summary>
        /// 读取 TTSState 的属性值（成员缺失返回 null）。
        /// </summary>
        public static object GetStateValue(object ttsState, string propertyName)
        {
            if (ttsState is null) return null;
            EnsureStateAccessors(ttsState);
            return _stateProperties.TryGetValue(propertyName, out var prop)
                ? prop.GetValue(ttsState)
                : null;
        }

        /// <summary>
        /// TTSState.PlaybackCompleted 事件（用于事件驱动等待播放完成）。
        /// </summary>
        public static EventInfo GetPlaybackCompletedEvent(object ttsState)
        {
            if (ttsState is null) return null;
            EnsureStateAccessors(ttsState);
            return _playbackCompletedEvent;
        }
    }
}
