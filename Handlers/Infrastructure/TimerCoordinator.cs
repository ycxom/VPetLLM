namespace VPetLLM.Handlers.Infrastructure
{
    /// <summary>
    /// 定时器协调器
    /// 统一管理 VPet MessageBar 定时器和 VPetLLM 思考动画定时器
    /// 避免定时器冲突，确保状态一致性
    /// </summary>
    public class TimerCoordinator
    {
        private readonly VPetLLM _plugin;
        private readonly object _lock = new object();

        // 定时器状态
        private bool _showTimerWasActive;
        private bool _endTimerWasActive;
        private bool _closeTimerWasActive;
        private bool _isPaused;

        // 显示时间配置
        private int _currentDisplayDuration;
        private bool _ttsEnabled;
        private int _audioDuration;

        public TimerCoordinator(VPetLLM plugin)
        {
            _plugin = plugin;
            _isPaused = false;
            _currentDisplayDuration = 2000; // 默认2秒
        }

        /// <summary>
        /// 暂停所有定时器（准备显示新气泡）
        /// </summary>
        public void PauseAllTimers()
        {
            lock (_lock)
            {
                if (_isPaused) return;

                try
                {
                    var msgBar = _plugin?.MW?.Main?.MsgBar;
                    if (msgBar is null) return;

                    // 保存当前状态
                    var showTimer = GetTimer(msgBar, "ShowTimer");
                    var endTimer = GetTimer(msgBar, "EndTimer");
                    var closeTimer = GetTimer(msgBar, "CloseTimer");

                    _showTimerWasActive = showTimer?.Enabled ?? false;
                    _endTimerWasActive = endTimer?.Enabled ?? false;
                    _closeTimerWasActive = closeTimer?.Enabled ?? false;

                    // 停止所有定时器
                    showTimer?.Stop();
                    endTimer?.Stop();
                    closeTimer?.Stop();

                    _isPaused = true;
                    Logger.Log("TimerCoordinator: 所有定时器已暂停");
                }
                catch (Exception ex)
                {
                    Logger.Log($"TimerCoordinator.PauseAllTimers: 暂停失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 恢复定时器（气泡显示完成）
        /// </summary>
        public void ResumeTimers()
        {
            lock (_lock)
            {
                if (!_isPaused) return;

                try
                {
                    var msgBar = _plugin?.MW?.Main?.MsgBar;
                    if (msgBar is null) return;

                    // 根据当前配置决定启动哪个定时器
                    var endTimer = GetTimer(msgBar, "EndTimer");

                    // 通常在显示完成后启动 EndTimer
                    if (endTimer is not null && _currentDisplayDuration > 0)
                    {
                        endTimer.Interval = 200; // VPet 默认间隔
                        endTimer.Start();
                    }

                    _isPaused = false;
                    Logger.Log("TimerCoordinator: 定时器已恢复");
                }
                catch (Exception ex)
                {
                    Logger.Log($"TimerCoordinator.ResumeTimers: 恢复失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 设置气泡显示时间
        /// </summary>
        /// <param name="milliseconds">显示时间（毫秒）</param>
        public void SetDisplayDuration(int milliseconds)
        {
            lock (_lock)
            {
                _currentDisplayDuration = Math.Max(500, milliseconds); // 最少500ms
                Logger.Log($"TimerCoordinator: 显示时间设置为 {_currentDisplayDuration}ms");
            }
        }

        /// <summary>
        /// 根据文本长度计算显示时间
        /// </summary>
        /// <param name="text">文本内容</param>
        /// <param name="minTime">最小显示时间</param>
        /// <param name="timePerChar">每字符时间</param>
        /// <returns>计算的显示时间</returns>
        public int CalculateDisplayDuration(string text, int minTime = 1000, int timePerChar = 50)
        {
            if (string.IsNullOrEmpty(text))
                return minTime;

            return Math.Max(minTime, text.Length * timePerChar);
        }

        /// <summary>
        /// 与 TTS 同步
        /// </summary>
        /// <param name="ttsEnabled">TTS 是否启用</param>
        /// <param name="audioDuration">音频时长（毫秒）</param>
        public void SyncWithTTS(bool ttsEnabled, int audioDuration)
        {
            lock (_lock)
            {
                _ttsEnabled = ttsEnabled;
                _audioDuration = audioDuration;

                if (ttsEnabled && audioDuration > 0)
                {
                    // TTS 启用时，显示时间至少等于音频时长
                    _currentDisplayDuration = Math.Max(_currentDisplayDuration, audioDuration + 500);
                    Logger.Log($"TimerCoordinator: TTS同步，显示时间调整为 {_currentDisplayDuration}ms");
                }
            }
        }

        /// <summary>
        /// 强制停止所有定时器（用于错误恢复）
        /// </summary>
        public void ForceStopAll()
        {
            lock (_lock)
            {
                try
                {
                    var msgBar = _plugin?.MW?.Main?.MsgBar;
                    if (msgBar is null) return;

                    GetTimer(msgBar, "ShowTimer")?.Stop();
                    GetTimer(msgBar, "EndTimer")?.Stop();
                    GetTimer(msgBar, "CloseTimer")?.Stop();

                    _isPaused = false;
                    _showTimerWasActive = false;
                    _endTimerWasActive = false;
                    _closeTimerWasActive = false;

                    Logger.Log("TimerCoordinator: 所有定时器已强制停止");
                }
                catch (Exception ex)
                {
                    Logger.Log($"TimerCoordinator.ForceStopAll: 强制停止失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 检查定时器状态是否一致
        /// </summary>
        /// <returns>状态是否一致</returns>
        public bool CheckTimerConsistency()
        {
            lock (_lock)
            {
                try
                {
                    var msgBar = _plugin?.MW?.Main?.MsgBar;
                    if (msgBar is null) return true;

                    var showTimer = GetTimer(msgBar, "ShowTimer");
                    var endTimer = GetTimer(msgBar, "EndTimer");
                    var closeTimer = GetTimer(msgBar, "CloseTimer");

                    int activeCount = 0;
                    if (showTimer?.Enabled == true) activeCount++;
                    if (endTimer?.Enabled == true) activeCount++;
                    if (closeTimer?.Enabled == true) activeCount++;

                    // 正常情况：最多一个定时器活动，或全部停止
                    bool isConsistent = activeCount <= 1;

                    if (!isConsistent)
                    {
                        Logger.Log($"TimerCoordinator: 检测到定时器冲突，活动数: {activeCount}");
                    }

                    return isConsistent;
                }
                catch (Exception ex)
                {
                    Logger.Log($"TimerCoordinator.CheckTimerConsistency: 检查失败: {ex.Message}");
                    return true;
                }
            }
        }

        /// <summary>
        /// 解决定时器冲突
        /// </summary>
        public void ResolveConflict()
        {
            lock (_lock)
            {
                if (CheckTimerConsistency())
                    return;

                Logger.Log("TimerCoordinator: 正在解决定时器冲突...");
                ForceStopAll();
            }
        }

        /// <summary>
        /// 获取定时器实例
        /// </summary>
        private System.Timers.Timer GetTimer(object msgBar, string timerName)
        {
            try
            {
                var field = msgBar.GetType().GetField(timerName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                return field?.GetValue(msgBar) as System.Timers.Timer;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 获取当前状态快照
        /// </summary>
        public TimerStateSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                try
                {
                    var msgBar = _plugin?.MW?.Main?.MsgBar;

                    return new TimerStateSnapshot
                    {
                        IsPaused = _isPaused,
                        ShowTimerActive = GetTimer(msgBar, "ShowTimer")?.Enabled ?? false,
                        EndTimerActive = GetTimer(msgBar, "EndTimer")?.Enabled ?? false,
                        CloseTimerActive = GetTimer(msgBar, "CloseTimer")?.Enabled ?? false,
                        CurrentDisplayDuration = _currentDisplayDuration,
                        TTSEnabled = _ttsEnabled,
                        AudioDuration = _audioDuration
                    };
                }
                catch
                {
                    return new TimerStateSnapshot { IsPaused = _isPaused };
                }
            }
        }
    }

    /// <summary>
    /// 定时器状态快照
    /// </summary>
    public class TimerStateSnapshot
    {
        public bool IsPaused { get; init; }
        public bool ShowTimerActive { get; init; }
        public bool EndTimerActive { get; init; }
        public bool CloseTimerActive { get; init; }
        public int CurrentDisplayDuration { get; init; }
        public bool TTSEnabled { get; init; }
        public int AudioDuration { get; init; }
    }
}
