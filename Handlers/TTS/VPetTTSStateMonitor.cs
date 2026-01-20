namespace VPetLLM.Handlers.TTS
{
    /// <summary>
    /// VPetTTS状态监控器，提供实时状态检测和变化通知
    /// 解决VPetLLM与VPetTTS协作时的状态同步问题
    /// </summary>
    public class VPetTTSStateMonitor : IDisposable
    {
        private readonly object _vpetTTSPlugin;
        private readonly Timer _statusCheckTimer;
        private volatile bool _lastKnownPlayingState = false;
        private readonly List<StateChangeRecord> _stateHistory = new List<StateChangeRecord>();
        private readonly object _lockObject = new object();
        private bool _disposed = false;

        /// <summary>
        /// 状态变化记录
        /// </summary>
        public class StateChangeRecord
        {
            public DateTime Timestamp { get; set; }
            public bool IsPlaying { get; set; }
            public string Reason { get; set; }
        }

        /// <summary>
        /// 播放状态变化事件
        /// </summary>
        public event EventHandler<bool> PlayingStateChanged;

        public VPetTTSStateMonitor(object vpetTTSPlugin)
        {
            _vpetTTSPlugin = vpetTTSPlugin ?? throw new ArgumentNullException(nameof(vpetTTSPlugin));

            Logger.Log("VPetTTSStateMonitor: 初始化状态监控器");

            // 启动定时状态检查（可配置间隔，默认200ms）
            var interval = GetPollingInterval();
            _statusCheckTimer = new Timer(CheckPlayingState, null, 0, interval);

            Logger.Log($"VPetTTSStateMonitor: 状态检查定时器已启动，间隔: {interval}ms");
        }

        /// <summary>
        /// 获取当前播放状态
        /// </summary>
        public bool IsPlaying
        {
            get
            {
                if (_disposed) return false;

                try
                {
                    // 直接从VPetTTS获取最新状态，不使用缓存
                    var ttsStateProperty = _vpetTTSPlugin.GetType().GetProperty("TTSState");
                    if (ttsStateProperty is not null)
                    {
                        var ttsState = ttsStateProperty.GetValue(_vpetTTSPlugin);
                        if (ttsState is not null)
                        {
                            var isPlayingProperty = ttsState.GetType().GetProperty("IsPlaying");
                            if (isPlayingProperty is not null)
                            {
                                return (bool)isPlayingProperty.GetValue(ttsState);
                            }
                        }
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    Logger.Log($"VPetTTSStateMonitor: 获取播放状态失败: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// 获取状态变化历史
        /// </summary>
        public List<StateChangeRecord> GetStateHistory()
        {
            lock (_stateHistory)
            {
                return new List<StateChangeRecord>(_stateHistory);
            }
        }

        /// <summary>
        /// 定时检查播放状态
        /// </summary>
        private void CheckPlayingState(object state)
        {
            if (_disposed) return;

            try
            {
                var currentState = IsPlaying;
                if (currentState != _lastKnownPlayingState)
                {
                    var record = new StateChangeRecord
                    {
                        Timestamp = DateTime.Now,
                        IsPlaying = currentState,
                        Reason = currentState ? "播放开始" : "播放结束"
                    };

                    lock (_stateHistory)
                    {
                        _stateHistory.Add(record);
                        // 保留最近100条记录
                        if (_stateHistory.Count > 100)
                        {
                            _stateHistory.RemoveAt(0);
                        }
                    }

                    Logger.Log($"VPetTTSStateMonitor: 状态变化 {_lastKnownPlayingState} -> {currentState}");
                    _lastKnownPlayingState = currentState;

                    // 触发状态变化事件
                    try
                    {
                        PlayingStateChanged?.Invoke(this, currentState);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"VPetTTSStateMonitor: 状态变化事件处理异常: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetTTSStateMonitor: 状态检查异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 等待播放完成
        /// 优化：使用事件驱动方式，订阅VPetTTS的PlaybackCompleted事件
        /// </summary>
        /// <param name="timeoutMs">最大超时时间（毫秒），作为安全保护，默认5分钟</param>
        /// <returns>是否成功完成</returns>
        public async Task<bool> WaitForPlaybackCompleteAsync(int? timeoutMs = null)
        {
            if (_disposed) return true;

            // 最大超时作为安全保护（默认5分钟）
            var maxTimeout = timeoutMs ?? 300000;
            var startTime = DateTime.Now;

            Logger.Log($"VPetTTSStateMonitor: 开始等待播放完成（事件驱动模式）");

            // 尝试使用事件驱动方式
            var eventResult = await WaitForPlaybackCompleteViaEventsAsync(maxTimeout);
            if (eventResult.HasValue)
            {
                var totalWaitTime = (DateTime.Now - startTime).TotalMilliseconds;
                Logger.Log($"VPetTTSStateMonitor: 事件驱动等待完成，结果: {eventResult.Value}, 总等待时间: {totalWaitTime}ms");
                return eventResult.Value;
            }

            // 如果事件方式不可用，回退到轮询方式
            Logger.Log($"VPetTTSStateMonitor: 事件方式不可用，回退到轮询模式");
            return await WaitForPlaybackCompleteViaPollingAsync(maxTimeout);
        }

        /// <summary>
        /// 通过订阅VPetTTS事件等待播放完成（推荐方式）
        /// </summary>
        private async Task<bool?> WaitForPlaybackCompleteViaEventsAsync(int maxTimeout)
        {
            try
            {
                var ttsStateProperty = _vpetTTSPlugin.GetType().GetProperty("TTSState");
                if (ttsStateProperty is null) return null;

                var ttsState = ttsStateProperty.GetValue(_vpetTTSPlugin);
                if (ttsState is null) return null;

                // 获取PlaybackCompleted事件
                var playbackCompletedEvent = ttsState.GetType().GetEvent("PlaybackCompleted");
                if (playbackCompletedEvent is null) return null;

                Logger.Log($"VPetTTSStateMonitor: 使用事件驱动方式，订阅PlaybackCompleted事件");

                var completionSource = new TaskCompletionSource<bool>();
                EventHandler<object> handler = null;

                handler = (sender, args) =>
                {
                    Logger.Log($"VPetTTSStateMonitor: 收到PlaybackCompleted事件");
                    completionSource.TrySetResult(true);
                };

                // 订阅事件
                var delegateType = playbackCompletedEvent.EventHandlerType;
                var convertedHandler = Delegate.CreateDelegate(delegateType, handler.Target, handler.Method);
                playbackCompletedEvent.AddEventHandler(ttsState, convertedHandler);

                try
                {
                    // 检查是否已经完成（避免竞态条件）
                    if (!IsPlaying && IsPlaybackComplete())
                    {
                        Logger.Log($"VPetTTSStateMonitor: 播放已经完成，无需等待");
                        return true;
                    }

                    // 等待事件或超时
                    var timeoutTask = Task.Delay(maxTimeout);
                    var completedTask = await Task.WhenAny(completionSource.Task, timeoutTask);

                    if (completedTask == completionSource.Task)
                    {
                        return await completionSource.Task;
                    }
                    else
                    {
                        Logger.Log($"VPetTTSStateMonitor: 事件等待超时 ({maxTimeout}ms)");
                        return false;
                    }
                }
                finally
                {
                    // 取消订阅事件
                    playbackCompletedEvent.RemoveEventHandler(ttsState, convertedHandler);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetTTSStateMonitor: 事件驱动方式失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 通过轮询方式等待播放完成（回退方案）
        /// </summary>
        private async Task<bool> WaitForPlaybackCompleteViaPollingAsync(int maxTimeout)
        {
            var startTime = DateTime.Now;
            var pollingInterval = GetPollingInterval();

            // 等待播放开始的超时时间（10秒）
            var startWaitTimeout = 10000;

            // 第一阶段：等待播放开始（IsPlaying 变为 True）
            var startWaitBegin = DateTime.Now;
            while (!IsPlaying && (DateTime.Now - startWaitBegin).TotalMilliseconds < startWaitTimeout)
            {
                await Task.Delay(pollingInterval);
            }

            var startWaitTime = (DateTime.Now - startWaitBegin).TotalMilliseconds;

            if (!IsPlaying)
            {
                // 播放未能在超时时间内开始
                Logger.Log($"VPetTTSStateMonitor: 等待播放开始超时 ({startWaitTime}ms)，可能VPetTTS未收到请求或处理失败");
                return true; // 返回 true 允许继续处理下一个请求
            }

            Logger.Log($"VPetTTSStateMonitor: 播放已开始，等待开始耗时: {startWaitTime}ms");

            // 第二阶段：等待播放完成
            // 关键：必须等待 IsPlaying 从 True 变为 False 并保持稳定
            // 不能仅依赖 IsPlaybackComplete，因为它在下载完成时就为 true
            var lastIsPlayingState = true;
            var stableStateStartTime = DateTime.Now;
            var stableStateDuration = 500; // 状态稳定持续时间（毫秒）

            while ((DateTime.Now - startTime).TotalMilliseconds < maxTimeout)
            {
                var currentIsPlaying = IsPlaying;

                // 检测状态变化
                if (currentIsPlaying != lastIsPlayingState)
                {
                    Logger.Log($"VPetTTSStateMonitor: IsPlaying状态变化: {lastIsPlayingState} -> {currentIsPlaying}");
                    lastIsPlayingState = currentIsPlaying;
                    stableStateStartTime = DateTime.Now;
                }

                // 如果 IsPlaying=false 并且状态已经稳定一段时间，认为播放完成
                if (!currentIsPlaying)
                {
                    var stableDuration = (DateTime.Now - stableStateStartTime).TotalMilliseconds;
                    if (stableDuration >= stableStateDuration)
                    {
                        var totalWaitTime = (DateTime.Now - startTime).TotalMilliseconds;
                        Logger.Log($"VPetTTSStateMonitor: 播放完成（IsPlaying=false且稳定{stableDuration}ms），总等待时间: {totalWaitTime}ms");
                        return true;
                    }
                }

                await Task.Delay(pollingInterval);
            }

            var finalWaitTime = (DateTime.Now - startTime).TotalMilliseconds;
            Logger.Log($"VPetTTSStateMonitor: 达到最大等待时间 ({maxTimeout}ms)，总等待时间: {finalWaitTime}ms");

            return false;
        }

        /// <summary>
        /// 检查播放是否完成（通过 VPetTTS 的 IsPlaybackComplete 属性）
        /// </summary>
        private bool IsPlaybackComplete()
        {
            if (_disposed) return true;

            try
            {
                var ttsStateProperty = _vpetTTSPlugin.GetType().GetProperty("TTSState");
                if (ttsStateProperty is not null)
                {
                    var ttsState = ttsStateProperty.GetValue(_vpetTTSPlugin);
                    if (ttsState is not null)
                    {
                        var isPlaybackCompleteProperty = ttsState.GetType().GetProperty("IsPlaybackComplete");
                        if (isPlaybackCompleteProperty is not null)
                        {
                            return (bool)isPlaybackCompleteProperty.GetValue(ttsState);
                        }
                    }
                }
                // 如果没有 IsPlaybackComplete 属性，回退到 !IsPlaying
                return !IsPlaying;
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetTTSStateMonitor: 获取 IsPlaybackComplete 失败: {ex.Message}");
                return !IsPlaying;
            }
        }

        /// <summary>
        /// 获取最后心跳时间
        /// </summary>
        private DateTime GetLastHeartbeatTime()
        {
            if (_disposed) return DateTime.MinValue;

            try
            {
                var ttsStateProperty = _vpetTTSPlugin.GetType().GetProperty("TTSState");
                if (ttsStateProperty is not null)
                {
                    var ttsState = ttsStateProperty.GetValue(_vpetTTSPlugin);
                    if (ttsState is not null)
                    {
                        var lastHeartbeatProperty = ttsState.GetType().GetProperty("LastHeartbeatTime");
                        if (lastHeartbeatProperty is not null)
                        {
                            return (DateTime)lastHeartbeatProperty.GetValue(ttsState);
                        }
                    }
                }
                return DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>
        /// 获取播放进度信息
        /// </summary>
        public PlaybackProgressInfo GetPlaybackProgress()
        {
            if (_disposed) return new PlaybackProgressInfo();

            try
            {
                var ttsStateProperty = _vpetTTSPlugin.GetType().GetProperty("TTSState");
                if (ttsStateProperty is not null)
                {
                    var ttsState = ttsStateProperty.GetValue(_vpetTTSPlugin);
                    if (ttsState is not null)
                    {
                        var info = new PlaybackProgressInfo();

                        // 获取各个属性
                        var props = new[] { "PlaybackProgress", "PlaybackPositionMs", "AudioDurationMs",
                                           "PlaybackStartTime", "EstimatedPlaybackEndTime", "IsPlaybackComplete" };

                        foreach (var propName in props)
                        {
                            var prop = ttsState.GetType().GetProperty(propName);
                            if (prop is not null)
                            {
                                var value = prop.GetValue(ttsState);
                                switch (propName)
                                {
                                    case "PlaybackProgress":
                                        info.Progress = (double)value;
                                        break;
                                    case "PlaybackPositionMs":
                                        info.PositionMs = (long)value;
                                        break;
                                    case "AudioDurationMs":
                                        info.DurationMs = (long)value;
                                        break;
                                    case "PlaybackStartTime":
                                        info.StartTime = (DateTime)value;
                                        break;
                                    case "EstimatedPlaybackEndTime":
                                        info.EstimatedEndTime = (DateTime)value;
                                        break;
                                    case "IsPlaybackComplete":
                                        info.IsComplete = (bool)value;
                                        break;
                                }
                            }
                        }

                        return info;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetTTSStateMonitor: 获取播放进度失败: {ex.Message}");
            }

            return new PlaybackProgressInfo();
        }

        /// <summary>
        /// 播放进度信息
        /// </summary>
        public class PlaybackProgressInfo
        {
            public double Progress { get; set; }
            public long PositionMs { get; set; }
            public long DurationMs { get; set; } = -1;
            public DateTime StartTime { get; set; } = DateTime.MinValue;
            public DateTime EstimatedEndTime { get; set; } = DateTime.MinValue;
            public bool IsComplete { get; set; } = true;

            /// <summary>
            /// 剩余播放时间（毫秒），-1 表示未知
            /// </summary>
            public long RemainingMs => DurationMs > 0 ? Math.Max(0, DurationMs - PositionMs) : -1;
        }

        /// <summary>
        /// 获取轮询间隔
        /// </summary>
        private int GetPollingInterval()
        {
            // 从配置获取轮询间隔，默认200ms，范围50-1000ms
            try
            {
                var configInterval = TTSCoordinationSettings.Instance.PollingIntervalMs;
                return Math.Max(50, Math.Min(1000, configInterval));
            }
            catch
            {
                return 200; // 默认值
            }
        }

        /// <summary>
        /// 清除状态历史
        /// </summary>
        public void ClearHistory()
        {
            lock (_stateHistory)
            {
                _stateHistory.Clear();
            }
            Logger.Log("VPetTTSStateMonitor: 状态历史已清除");
        }

        /// <summary>
        /// 获取状态统计信息
        /// </summary>
        public StateStatistics GetStatistics()
        {
            lock (_stateHistory)
            {
                var stats = new StateStatistics();

                if (_stateHistory.Count == 0)
                {
                    return stats;
                }

                stats.TotalStateChanges = _stateHistory.Count;
                stats.FirstRecordTime = _stateHistory[0].Timestamp;
                stats.LastRecordTime = _stateHistory[_stateHistory.Count - 1].Timestamp;

                // 计算播放次数和总播放时间
                DateTime? playStartTime = null;
                foreach (var record in _stateHistory)
                {
                    if (record.IsPlaying && playStartTime is null)
                    {
                        playStartTime = record.Timestamp;
                        stats.PlaybackCount++;
                    }
                    else if (!record.IsPlaying && playStartTime.HasValue)
                    {
                        var duration = record.Timestamp - playStartTime.Value;
                        stats.TotalPlaybackTime += duration;
                        playStartTime = null;
                    }
                }

                // 如果当前还在播放，计算到现在的时间
                if (playStartTime.HasValue && _lastKnownPlayingState)
                {
                    var duration = DateTime.Now - playStartTime.Value;
                    stats.TotalPlaybackTime += duration;
                }

                return stats;
            }
        }

        /// <summary>
        /// 状态统计信息
        /// </summary>
        public class StateStatistics
        {
            public int TotalStateChanges { get; set; }
            public int PlaybackCount { get; set; }
            public TimeSpan TotalPlaybackTime { get; set; }
            public DateTime FirstRecordTime { get; set; }
            public DateTime LastRecordTime { get; set; }

            public override string ToString()
            {
                return $"状态变化: {TotalStateChanges}, 播放次数: {PlaybackCount}, " +
                       $"总播放时间: {TotalPlaybackTime.TotalSeconds:F1}秒";
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                try
                {
                    _statusCheckTimer?.Dispose();
                    Logger.Log("VPetTTSStateMonitor: 资源已释放");
                }
                catch (Exception ex)
                {
                    Logger.Log($"VPetTTSStateMonitor: 释放资源时发生异常: {ex.Message}");
                }
            }
        }
    }
}