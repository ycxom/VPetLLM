using System.Windows;
using VPetLLM.Handlers.Animation;
using VPetLLM.Utils.System;
using VPetLLM.Utils.UI;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// 气泡管理器
    /// 统一管理气泡显示，减少调用层级，优化性能
    /// 集成动画协调器以避免闪烁
    /// </summary>
    public class BubbleManager
    {
        private readonly VPetLLM _plugin;
        private readonly BubbleState _state;
        private readonly TimerCoordinator _timerCoordinator;
        private readonly Queue<BubbleUpdateRequest> _updateQueue;
        private readonly object _queueLock = new object();

        // 配置
        private readonly BubbleDisplayConfig _config;

        // 批量更新控制
        private DateTime _lastFlushTime = DateTime.MinValue;
        private bool _isFlushing = false;

        // 动画协调 - 避免气泡显示触发不必要的动画
        private DateTime _lastAnimationCheck = DateTime.MinValue;
        private const int AnimationCheckIntervalMs = 100;

        public BubbleManager(VPetLLM plugin)
        {
            _plugin = plugin;
            _state = new BubbleState();
            _timerCoordinator = new TimerCoordinator(plugin);
            _updateQueue = new Queue<BubbleUpdateRequest>();
            _config = new BubbleDisplayConfig();
        }

        /// <summary>
        /// 获取气泡状态
        /// </summary>
        public BubbleState State => _state;

        /// <summary>
        /// 获取定时器协调器
        /// </summary>
        public TimerCoordinator TimerCoordinator => _timerCoordinator;

        /// <summary>
        /// 直接显示气泡（快速路径）
        /// 集成动画协调器以避免闪烁
        /// </summary>
        public async Task ShowBubbleAsync(string text, string animation = null)
        {
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                // 检查是否需要更新
                if (!_state.NeedsUpdate(text))
                {
                    Logger.Log("BubbleManager: 文本相同，跳过更新");
                    return;
                }

                // 检查闪烁风险（使用动画协调器）
                if (AnimationHelper.IsInitialized && ShouldCheckFlickerRisk())
                {
                    if (AnimationHelper.IsFlickerRisk())
                    {
                        var delay = AnimationHelper.GetRecommendedDelay();
                        Logger.Log($"BubbleManager: 检测到闪烁风险，延迟 {delay}ms");
                        await Task.Delay(delay);
                    }
                    _lastAnimationCheck = DateTime.Now;
                }

                // 暂停定时器
                _timerCoordinator.PauseAllTimers();

                // 更新状态
                _state.Update(text, true);

                // 计算显示时间
                int displayDuration = _config.CalculateDisplayTime(text);
                _timerCoordinator.SetDisplayDuration(displayDuration);

                // 在 UI 线程显示气泡
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (string.IsNullOrEmpty(animation))
                        {
                            // 快速显示（无动画）- 不触发动画切换
                            _plugin.MW.Main.Say(text, null, false);
                        }
                        else
                        {
                            // 带动画显示 - 检查动画兼容性
                            if (IsAnimationCompatible(animation))
                            {
                                _plugin.MW.Main.Say(text, animation, true);
                            }
                            else
                            {
                                // 动画不兼容，只显示气泡
                                Logger.Log($"BubbleManager: 动画 '{animation}' 不兼容当前状态，仅显示气泡");
                                _plugin.MW.Main.Say(text, null, false);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"BubbleManager.ShowBubbleAsync: UI显示失败: {ex.Message}");
                        // 回退：使用 MessageBarHelper
                        FallbackShow(text);
                    }
                });

                Logger.Log($"BubbleManager: 气泡已显示，文本长度: {text.Length}");
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleManager.ShowBubbleAsync: 显示失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查是否应该检查闪烁风险（节流）
        /// </summary>
        private bool ShouldCheckFlickerRisk()
        {
            return (DateTime.Now - _lastAnimationCheck).TotalMilliseconds >= AnimationCheckIntervalMs;
        }

        /// <summary>
        /// 检查动画是否与当前状态兼容
        /// </summary>
        private bool IsAnimationCompatible(string animation)
        {
            if (string.IsNullOrEmpty(animation)) return true;
            if (_plugin?.MW?.Main == null) return true;

            try
            {
                // 检查当前是否在重要动画中
                if (AnimationStateChecker.IsPlayingImportantAnimation(_plugin.MW))
                {
                    Logger.Log($"BubbleManager: 当前正在播放重要动画，跳过动画 '{animation}'");
                    return false;
                }

                // 检查动画是否存在
                var graph = _plugin.MW.Main.Core.Graph;
                var mode = _plugin.MW.Main.Core.Save.Mode;
                var foundGraph = graph.FindGraph(animation, VPet_Simulator.Core.GraphInfo.AnimatType.A_Start, mode);

                if (foundGraph == null)
                {
                    Logger.Log($"BubbleManager: 动画 '{animation}' 不存在");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleManager.IsAnimationCompatible: 检查失败: {ex.Message}");
                return true; // 出错时允许尝试
            }
        }

        /// <summary>
        /// 显示思考气泡
        /// </summary>
        public void ShowThinkingBubble(string thinkingText)
        {
            try
            {
                _state.SetThinking(thinkingText);
                _timerCoordinator.PauseAllTimers();

                Application.Current.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Input,
                    new Action(() =>
                    {
                        try
                        {
                            var msgBar = _plugin.MW.Main.MsgBar;
                            if (msgBar != null)
                            {
                                MessageBarHelper.ShowBubbleQuick(msgBar, thinkingText,
                                    _plugin.MW.Core.Save.Name);
                            }
                        }
                        catch { }
                    }));
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleManager.ShowThinkingBubble: 显示失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从思考状态过渡到响应
        /// 使用动画协调器确保平滑过渡
        /// </summary>
        public async Task TransitionToResponseAsync(string responseText, string animation = null)
        {
            try
            {
                // 平滑过渡，保持可见性
                _state.TransitionToResponse(responseText);

                // 使用动画协调器检查是否需要延迟
                if (AnimationHelper.IsInitialized && AnimationHelper.IsFlickerRisk())
                {
                    var delay = AnimationHelper.GetRecommendedDelay();
                    Logger.Log($"BubbleManager: 过渡时检测到闪烁风险，延迟 {delay}ms");
                    await Task.Delay(delay);
                }

                // 显示响应气泡
                await ShowBubbleAsync(responseText, animation);

                Logger.Log("BubbleManager: 已从思考过渡到响应");
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleManager.TransitionToResponseAsync: 过渡失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 队列气泡更新（用于流式传输）
        /// </summary>
        public void QueueBubbleUpdate(string text, string animation = null)
        {
            if (string.IsNullOrEmpty(text)) return;

            lock (_queueLock)
            {
                // 检查队列大小
                if (_updateQueue.Count >= _config.MaxBatchSize)
                {
                    // 队列满，立即刷新
                    _ = FlushBubbleUpdatesAsync();
                }

                _updateQueue.Enqueue(new BubbleUpdateRequest
                {
                    Text = text,
                    Animation = animation,
                    Timestamp = DateTime.Now
                });
            }
        }

        /// <summary>
        /// 刷新批量更新
        /// </summary>
        public async Task FlushBubbleUpdatesAsync()
        {
            if (_isFlushing) return;

            BubbleUpdateRequest lastRequest = null;

            lock (_queueLock)
            {
                if (_updateQueue.Count == 0) return;

                _isFlushing = true;

                // 只处理最后一个请求（合并更新）
                while (_updateQueue.Count > 0)
                {
                    lastRequest = _updateQueue.Dequeue();
                }
            }

            try
            {
                if (lastRequest != null)
                {
                    await ShowBubbleAsync(lastRequest.Text, lastRequest.Animation);
                }

                _lastFlushTime = DateTime.Now;
            }
            finally
            {
                _isFlushing = false;
            }
        }

        /// <summary>
        /// 隐藏气泡
        /// </summary>
        public void HideBubble()
        {
            try
            {
                _state.Hide();

                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var msgBar = _plugin.MW.Main.MsgBar;
                        if (msgBar != null)
                        {
                            MessageBarHelper.SetVisibility(msgBar, false);
                        }
                    }
                    catch { }
                }));
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleManager.HideBubble: 隐藏失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理状态（幂等）
        /// </summary>
        public void Clear()
        {
            if (!_state.Clear())
            {
                // 已经清理过，跳过
                return;
            }

            _timerCoordinator.ForceStopAll();

            lock (_queueLock)
            {
                _updateQueue.Clear();
            }

            Logger.Log("BubbleManager: 状态已清理");
        }

        /// <summary>
        /// 与 TTS 同步
        /// </summary>
        public void SyncWithTTS(bool enabled, int audioDuration)
        {
            _timerCoordinator.SyncWithTTS(enabled, audioDuration);
        }

        /// <summary>
        /// 回退显示方法
        /// </summary>
        private void FallbackShow(string text)
        {
            try
            {
                var msgBar = _plugin.MW.Main.MsgBar;
                if (msgBar != null)
                {
                    MessageBarHelper.ShowBubbleQuick(msgBar, text, _plugin.MW.Core.Save.Name);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleManager.FallbackShow: 回退显示也失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 气泡更新请求
    /// </summary>
    public class BubbleUpdateRequest
    {
        public string Text { get; set; }
        public string Animation { get; set; }
        public bool Force { get; set; }
        public int? DisplayDuration { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
