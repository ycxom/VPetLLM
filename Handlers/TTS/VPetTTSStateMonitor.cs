using VPetLLM.Core.Services;

namespace VPetLLM.Handlers.TTS
{
    /// <summary>
    /// VPetTTS状态监控器，提供按需状态检测
    /// 解决VPetLLM与VPetTTS协作时的状态同步问题
    /// 注意：按需检查模式，不启动后台定时器或监控线程。
    /// 对 VPetTTS 内部成员的访问统一经 VPetTTSPluginAdapter（缓存反射引用，
    /// 本类的 IsPlaying 处于 50-200ms 轮询热路径上）。
    /// </summary>
    public class VPetTTSStateMonitor : IDisposable
    {
        private readonly object _vpetTTSPlugin;
        private bool _disposed = false;

        public VPetTTSStateMonitor(object vpetTTSPlugin)
        {
            _vpetTTSPlugin = vpetTTSPlugin ?? throw new ArgumentNullException(nameof(vpetTTSPlugin));
            Logger.Log("VPetTTSStateMonitor: 初始化状态监控器（按需检查模式）");
        }

        /// <summary>
        /// 获取当前播放状态（按需检查，不缓存）
        /// </summary>
        public bool IsPlaying
        {
            get
            {
                if (_disposed) return false;

                try
                {
                    var ttsState = VPetTTSPluginAdapter.GetTTSState(_vpetTTSPlugin);
                    return VPetTTSPluginAdapter.GetStateValue(ttsState, "IsPlaying") is true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"VPetTTSStateMonitor: 获取播放状态失败: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// 等待播放完成（按需轮询模式）
        /// </summary>
        /// <param name="timeoutMs">最大超时时间（毫秒），作为安全保护，默认5分钟</param>
        /// <returns>是否成功完成</returns>
        public async Task<bool> WaitForPlaybackCompleteAsync(int? timeoutMs = null)
        {
            if (_disposed) return true;

            // 最大超时作为安全保护（默认5分钟）
            var maxTimeout = timeoutMs ?? 300000;
            var startTime = DateTime.Now;

            Logger.Log($"VPetTTSStateMonitor: 开始等待播放完成（按需轮询模式）");

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
                var ttsState = VPetTTSPluginAdapter.GetTTSState(_vpetTTSPlugin);
                if (ttsState is null) return null;

                // 获取PlaybackCompleted事件
                var playbackCompletedEvent = VPetTTSPluginAdapter.GetPlaybackCompletedEvent(ttsState);
                if (playbackCompletedEvent is null) return null;

                Logger.Log($"VPetTTSStateMonitor: 使用事件驱动方式，订阅PlaybackCompleted事件");

                // 关键修复：在订阅事件之前，先等待播放开始
                // 这样可以避免收到上一次播放的完成事件
                var waitStartTime = DateTime.Now;
                var maxWaitForPlaybackStart = 10000; // 最多等待10秒让播放开始
                var playbackStarted = false;
                
                Logger.Log($"VPetTTSStateMonitor: 等待播放开始...");
                while ((DateTime.Now - waitStartTime).TotalMilliseconds < maxWaitForPlaybackStart)
                {
                    if (IsPlaying)
                    {
                        playbackStarted = true;
                        var waitTime = (DateTime.Now - waitStartTime).TotalMilliseconds;
                        Logger.Log($"VPetTTSStateMonitor: 检测到播放已开始，等待时间: {waitTime}ms");
                        break;
                    }
                    await Task.Delay(50);
                }
                
                if (!playbackStarted)
                {
                    var waitTime = (DateTime.Now - waitStartTime).TotalMilliseconds;
                    Logger.Log($"VPetTTSStateMonitor: 播放未在超时时间内开始 ({waitTime}ms)，可能已完成或失败");
                    // 检查是否已经完成
                    if (IsPlaybackComplete())
                    {
                        Logger.Log($"VPetTTSStateMonitor: 播放已经完成");
                        return true;
                    }
                    return false;
                }

                // 现在订阅事件，等待播放完成
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
                    // 再次检查当前状态（可能在订阅事件之前就已经完成了）
                    if (!IsPlaying && IsPlaybackComplete())
                    {
                        Logger.Log($"VPetTTSStateMonitor: 播放已经完成，无需等待事件");
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
        /// 通过轮询方式等待播放完成（回退方案，按需检查）
        /// </summary>
        private async Task<bool> WaitForPlaybackCompleteViaPollingAsync(int maxTimeout)
        {
            var startTime = DateTime.Now;
            var pollingInterval = 200; // 固定200ms轮询间隔

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
                var ttsState = VPetTTSPluginAdapter.GetTTSState(_vpetTTSPlugin);
                var value = VPetTTSPluginAdapter.GetStateValue(ttsState, "IsPlaybackComplete");
                if (value is bool complete)
                {
                    return complete;
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
        /// 获取播放进度信息（按需查询）
        /// </summary>
        public PlaybackProgressInfo GetPlaybackProgress()
        {
            if (_disposed) return new PlaybackProgressInfo();

            try
            {
                var ttsState = VPetTTSPluginAdapter.GetTTSState(_vpetTTSPlugin);
                if (ttsState is not null)
                {
                    var info = new PlaybackProgressInfo();

                    if (VPetTTSPluginAdapter.GetStateValue(ttsState, "PlaybackProgress") is double progress)
                        info.Progress = progress;
                    if (VPetTTSPluginAdapter.GetStateValue(ttsState, "PlaybackPositionMs") is long positionMs)
                        info.PositionMs = positionMs;
                    if (VPetTTSPluginAdapter.GetStateValue(ttsState, "AudioDurationMs") is long durationMs)
                        info.DurationMs = durationMs;
                    if (VPetTTSPluginAdapter.GetStateValue(ttsState, "PlaybackStartTime") is DateTime startTime)
                        info.StartTime = startTime;
                    if (VPetTTSPluginAdapter.GetStateValue(ttsState, "EstimatedPlaybackEndTime") is DateTime endTime)
                        info.EstimatedEndTime = endTime;
                    if (VPetTTSPluginAdapter.GetStateValue(ttsState, "IsPlaybackComplete") is bool isComplete)
                        info.IsComplete = isComplete;

                    return info;
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
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Logger.Log("VPetTTSStateMonitor: 资源已释放");
            }
        }
    }
}
