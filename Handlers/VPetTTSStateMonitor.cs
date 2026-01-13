using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VPetLLM.Utils;
using VPetLLM.Configuration;

namespace VPetLLM.Handlers
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
                    if (ttsStateProperty != null)
                    {
                        var ttsState = ttsStateProperty.GetValue(_vpetTTSPlugin);
                        if (ttsState != null)
                        {
                            var isPlayingProperty = ttsState.GetType().GetProperty("IsPlaying");
                            if (isPlayingProperty != null)
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
        /// 优化：完全依赖读取进度，如果3秒内进度没有变化才判断失败
        /// </summary>
        /// <param name="timeoutMs">最大超时时间（毫秒），作为安全保护，默认5分钟</param>
        /// <returns>是否成功完成</returns>
        public async Task<bool> WaitForPlaybackCompleteAsync(int? timeoutMs = null)
        {
            if (_disposed) return true;
            
            // 最大超时作为安全保护（默认5分钟）
            var maxTimeout = timeoutMs ?? 300000;
            var startTime = DateTime.Now;
            var pollingInterval = GetPollingInterval();
            
            // 等待播放开始的超时时间（5秒）
            var startWaitTimeout = 5000;
            // 进度无变化超时时间（3秒内进度没有变化才判断失败）
            var progressStallTimeout = 3000;
            
            Logger.Log($"VPetTTSStateMonitor: 开始等待播放完成（基于进度检测）");
            
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
            // 完全依赖进度检测，如果3秒内进度没有变化才判断失败
            var lastProgress = GetPlaybackProgress();
            var lastProgressChangeTime = DateTime.Now;
            var lastHeartbeat = GetLastHeartbeatTime();
            var lastHeartbeatChangeTime = DateTime.Now;
            
            while ((DateTime.Now - startTime).TotalMilliseconds < maxTimeout)
            {
                // 检查是否播放完成
                if (IsPlaybackComplete())
                {
                    var totalWaitTime = (DateTime.Now - startTime).TotalMilliseconds;
                    Logger.Log($"VPetTTSStateMonitor: 播放完成（IsPlaybackComplete=true），总等待时间: {totalWaitTime}ms");
                    return true;
                }
                
                // 检查 IsPlaying 状态
                if (!IsPlaying)
                {
                    var totalWaitTime = (DateTime.Now - startTime).TotalMilliseconds;
                    Logger.Log($"VPetTTSStateMonitor: 播放完成（IsPlaying=false），总等待时间: {totalWaitTime}ms");
                    return true;
                }
                
                // 检查进度变化
                var currentProgress = GetPlaybackProgress();
                if (currentProgress.Progress != lastProgress.Progress || 
                    currentProgress.PositionMs != lastProgress.PositionMs)
                {
                    lastProgress = currentProgress;
                    lastProgressChangeTime = DateTime.Now;
                }
                
                // 检查心跳变化
                var currentHeartbeat = GetLastHeartbeatTime();
                if (currentHeartbeat != DateTime.MinValue && currentHeartbeat != lastHeartbeat)
                {
                    lastHeartbeat = currentHeartbeat;
                    lastHeartbeatChangeTime = DateTime.Now;
                }
                
                // 判断是否有活动（进度变化或心跳变化）
                var timeSinceLastProgressChange = (DateTime.Now - lastProgressChangeTime).TotalMilliseconds;
                var timeSinceLastHeartbeatChange = (DateTime.Now - lastHeartbeatChangeTime).TotalMilliseconds;
                
                // 如果进度和心跳都超过3秒没有变化，判断播放已完成或异常
                if (timeSinceLastProgressChange > progressStallTimeout && 
                    timeSinceLastHeartbeatChange > progressStallTimeout)
                {
                    // 再次确认 IsPlaying 状态和 IsPlaybackComplete 状态
                    if (!IsPlaying || IsPlaybackComplete())
                    {
                        var totalWaitTime = (DateTime.Now - startTime).TotalMilliseconds;
                        Logger.Log($"VPetTTSStateMonitor: 播放完成（进度停滞后确认状态变化），总等待时间: {totalWaitTime}ms");
                        return true;
                    }
                    
                    // 进度停滞超过3秒且状态仍为播放中，认为播放已完成（可能是状态更新延迟）
                    var totalWaitTime2 = (DateTime.Now - startTime).TotalMilliseconds;
                    Logger.Log($"VPetTTSStateMonitor: 进度停滞超过{progressStallTimeout}ms，判断播放已完成，总等待时间: {totalWaitTime2}ms");
                    return true;
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
                if (ttsStateProperty != null)
                {
                    var ttsState = ttsStateProperty.GetValue(_vpetTTSPlugin);
                    if (ttsState != null)
                    {
                        var isPlaybackCompleteProperty = ttsState.GetType().GetProperty("IsPlaybackComplete");
                        if (isPlaybackCompleteProperty != null)
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
                if (ttsStateProperty != null)
                {
                    var ttsState = ttsStateProperty.GetValue(_vpetTTSPlugin);
                    if (ttsState != null)
                    {
                        var lastHeartbeatProperty = ttsState.GetType().GetProperty("LastHeartbeatTime");
                        if (lastHeartbeatProperty != null)
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
                if (ttsStateProperty != null)
                {
                    var ttsState = ttsStateProperty.GetValue(_vpetTTSPlugin);
                    if (ttsState != null)
                    {
                        var info = new PlaybackProgressInfo();
                        
                        // 获取各个属性
                        var props = new[] { "PlaybackProgress", "PlaybackPositionMs", "AudioDurationMs", 
                                           "PlaybackStartTime", "EstimatedPlaybackEndTime", "IsPlaybackComplete" };
                        
                        foreach (var propName in props)
                        {
                            var prop = ttsState.GetType().GetProperty(propName);
                            if (prop != null)
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
                    if (record.IsPlaying && playStartTime == null)
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