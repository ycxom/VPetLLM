using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;
using static VPet_Simulator.Core.GraphInfo;

namespace VPetLLM.Handlers.Animation
{
    /// <summary>
    /// 动画同步器
    /// 确保与 VPet 动画系统的线程安全交互
    /// </summary>
    public class AnimationSynchronizer : IDisposable
    {
        private readonly SemaphoreSlim _animationLock = new SemaphoreSlim(1, 1);
        private AnimationState _currentState = new AnimationState();
        private Action _completionCallback;
        private bool _disposed = false;

        /// <summary>当前动画状态</summary>
        public AnimationState CurrentState => _currentState;

        /// <summary>
        /// 等待当前动画完成
        /// </summary>
        /// <param name="timeoutMs">超时时间 (毫秒)</param>
        /// <returns>true 如果动画完成，false 如果超时</returns>
        public async Task<bool> WaitForAnimationCompleteAsync(int timeoutMs = 5000)
        {
            var startTime = DateTime.Now;
            while (_currentState.IsAnimating)
            {
                if ((DateTime.Now - startTime).TotalMilliseconds > timeoutMs)
                {
                    Logger.Log($"AnimationSynchronizer: Wait timed out after {timeoutMs}ms");
                    return false;
                }
                await Task.Delay(50);
            }
            return true;
        }

        /// <summary>
        /// 等待当前动画完成 (带取消令牌)
        /// </summary>
        public async Task WaitForAnimationCompleteAsync(CancellationToken cancellationToken)
        {
            while (_currentState.IsAnimating && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(50, cancellationToken);
            }
        }

        /// <summary>
        /// 安全执行 UI 线程操作
        /// </summary>
        public T ExecuteOnUIThread<T>(Func<T> action)
        {
            if (Application.Current?.Dispatcher == null)
            {
                Logger.Log("AnimationSynchronizer: Dispatcher is null, executing directly");
                return action();
            }

            if (Application.Current.Dispatcher.CheckAccess())
            {
                return action();
            }

            return Application.Current.Dispatcher.Invoke(action);
        }

        /// <summary>
        /// 安全执行 UI 线程操作 (无返回值)
        /// </summary>
        public void ExecuteOnUIThread(Action action)
        {
            if (Application.Current?.Dispatcher == null)
            {
                Logger.Log("AnimationSynchronizer: Dispatcher is null, executing directly");
                action();
                return;
            }

            if (Application.Current.Dispatcher.CheckAccess())
            {
                action();
                return;
            }

            Application.Current.Dispatcher.Invoke(action);
        }

        /// <summary>
        /// 异步安全执行 UI 线程操作
        /// </summary>
        public async Task<T> ExecuteOnUIThreadAsync<T>(Func<T> action)
        {
            if (Application.Current?.Dispatcher == null)
            {
                Logger.Log("AnimationSynchronizer: Dispatcher is null, executing directly");
                return action();
            }

            if (Application.Current.Dispatcher.CheckAccess())
            {
                return action();
            }

            return await Application.Current.Dispatcher.InvokeAsync(action);
        }

        /// <summary>
        /// 检查是否可以执行动画
        /// </summary>
        public bool CanExecuteAnimation(IMainWindow mainWindow)
        {
            if (mainWindow?.Main == null)
            {
                Logger.Log("AnimationSynchronizer: mainWindow is null");
                return false;
            }

            // 检查用户交互
            if (_currentState.IsUserInteracting)
            {
                Logger.Log("AnimationSynchronizer: User is interacting, cannot execute animation");
                return false;
            }

            // 检查重要状态
            var workingState = mainWindow.Main.State;
            if (workingState == Main.WorkingState.Work ||
                workingState == Main.WorkingState.Sleep ||
                workingState == Main.WorkingState.Travel)
            {
                Logger.Log($"AnimationSynchronizer: VPet is in {workingState} state, cannot execute animation");
                return false;
            }

            // 检查是否正在播放语音（参考 VPet MessageBar 的逻辑）
            if (mainWindow.Main.PlayingVoice)
            {
                Logger.Log("AnimationSynchronizer: VPet is playing voice, should not interrupt Say animation");
                // 注意：这里不直接返回 false，因为某些动画可能需要在语音播放时执行
                // 但我们需要记录这个状态
            }

            // 检查触摸动画
            var displayType = mainWindow.Main.DisplayType;
            if (displayType != null)
            {
                if (displayType.Type == GraphType.Touch_Head ||
                    displayType.Type == GraphType.Touch_Body)
                {
                    Logger.Log($"AnimationSynchronizer: VPet is in touch animation ({displayType.Type}), cannot execute animation");
                    return false;
                }

                // 检查过渡动画
                if (displayType.Type == GraphType.Switch_Up ||
                    displayType.Type == GraphType.Switch_Down ||
                    displayType.Type == GraphType.Switch_Thirsty ||
                    displayType.Type == GraphType.Switch_Hunger)
                {
                    Logger.Log($"AnimationSynchronizer: VPet is in transition animation ({displayType.Type}), cannot execute animation");
                    return false;
                }

                // 检查提起动画
                if (displayType.Type == GraphType.Raised_Dynamic ||
                    displayType.Type == GraphType.Raised_Static)
                {
                    Logger.Log($"AnimationSynchronizer: VPet is being raised ({displayType.Type}), cannot execute animation");
                    return false;
                }

                // 检查说话动画 - 如果正在播放语音，不应该中断说话动画
                if (displayType.Type == GraphType.Say && mainWindow.Main.PlayingVoice)
                {
                    Logger.Log("AnimationSynchronizer: VPet is in Say animation with voice playing, cannot interrupt");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 检查是否正在播放语音
        /// </summary>
        public bool IsPlayingVoice(IMainWindow mainWindow)
        {
            if (mainWindow?.Main == null) return false;
            return mainWindow.Main.PlayingVoice;
        }

        /// <summary>
        /// 获取语音剩余播放时间（毫秒）
        /// 参考 VPet MessageBar 的实现
        /// </summary>
        public int GetVoiceRemainingTime(IMainWindow mainWindow)
        {
            if (mainWindow?.Main == null || !mainWindow.Main.PlayingVoice)
                return 0;

            return ExecuteOnUIThread(() =>
            {
                try
                {
                    // 获取 VoicePlayer 字段
                    var voicePlayerField = mainWindow.Main.GetType().GetField("VoicePlayer",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (voicePlayerField == null) return 0;

                    var voicePlayer = voicePlayerField.GetValue(mainWindow.Main) as System.Windows.Media.MediaPlayer;
                    if (voicePlayer?.Clock?.NaturalDuration.HasTimeSpan == true)
                    {
                        var remaining = voicePlayer.Clock.NaturalDuration.TimeSpan - (voicePlayer.Clock.CurrentTime ?? TimeSpan.Zero);
                        return (int)remaining.TotalMilliseconds;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"AnimationSynchronizer: Failed to get voice remaining time: {ex.Message}");
                }
                return 0;
            });
        }

        /// <summary>
        /// 等待语音播放完成（参考 VPet MessageBar 的逻辑）
        /// </summary>
        public async Task WaitForVoiceCompleteAsync(IMainWindow mainWindow, int maxWaitMs = 30000)
        {
            if (mainWindow?.Main == null || !mainWindow.Main.PlayingVoice)
                return;

            var startTime = DateTime.Now;
            while (mainWindow.Main.PlayingVoice)
            {
                if ((DateTime.Now - startTime).TotalMilliseconds > maxWaitMs)
                {
                    Logger.Log($"AnimationSynchronizer: Voice wait timed out after {maxWaitMs}ms");
                    break;
                }

                var remaining = GetVoiceRemainingTime(mainWindow);
                if (remaining <= 2000) // 参考 VPet 的 2 秒阈值
                {
                    Logger.Log($"AnimationSynchronizer: Voice remaining time {remaining}ms, proceeding");
                    break;
                }

                await Task.Delay(100);
            }
        }

        /// <summary>
        /// 获取阻塞原因
        /// </summary>
        public string GetBlockingReason(IMainWindow mainWindow)
        {
            if (mainWindow?.Main == null)
                return "mainWindow is null";

            if (_currentState.IsUserInteracting)
                return "User is interacting";

            var workingState = mainWindow.Main.State;
            if (workingState == Main.WorkingState.Work)
                return "VPet is working";
            if (workingState == Main.WorkingState.Sleep)
                return "VPet is sleeping";
            if (workingState == Main.WorkingState.Travel)
                return "VPet is traveling";

            var displayType = mainWindow.Main.DisplayType;
            if (displayType != null)
            {
                if (displayType.Type == GraphType.Touch_Head || displayType.Type == GraphType.Touch_Body)
                    return $"Touch animation in progress ({displayType.Type})";
                if (displayType.Type == GraphType.Switch_Up || displayType.Type == GraphType.Switch_Down)
                    return $"Transition animation in progress ({displayType.Type})";
                if (displayType.Type == GraphType.Raised_Dynamic || displayType.Type == GraphType.Raised_Static)
                    return $"Raised animation in progress ({displayType.Type})";
            }

            return null;
        }

        /// <summary>
        /// 注册动画完成回调
        /// </summary>
        public void RegisterCompletionCallback(Action callback)
        {
            _completionCallback = callback;
        }

        /// <summary>
        /// 获取动画锁
        /// </summary>
        public async Task<bool> AcquireLockAsync(int timeoutMs = 5000)
        {
            return await _animationLock.WaitAsync(timeoutMs);
        }

        /// <summary>
        /// 释放动画锁
        /// </summary>
        public void ReleaseLock()
        {
            try
            {
                _animationLock.Release();
            }
            catch (SemaphoreFullException)
            {
                // 忽略重复释放
            }
        }

        /// <summary>
        /// 更新动画状态
        /// </summary>
        public void UpdateState(IMainWindow mainWindow, string source)
        {
            if (mainWindow?.Main == null) return;

            ExecuteOnUIThread(() =>
            {
                _currentState.Update(
                    mainWindow.Main.DisplayType,
                    mainWindow.Main.State,
                    source);
            });
        }

        /// <summary>
        /// 标记动画完成
        /// </summary>
        public void MarkAnimationCompleted()
        {
            _currentState.MarkCompleted();
            _completionCallback?.Invoke();
        }

        /// <summary>
        /// 设置用户交互状态
        /// </summary>
        public void SetUserInteracting(bool isInteracting)
        {
            _currentState.IsUserInteracting = isInteracting;
            if (isInteracting)
            {
                Logger.Log("AnimationSynchronizer: User interaction started");
            }
            else
            {
                Logger.Log("AnimationSynchronizer: User interaction ended");
            }
        }

        /// <summary>
        /// 跟踪双缓冲状态
        /// </summary>
        public bool GetPetGridCrlf(IMainWindow mainWindow)
        {
            if (mainWindow?.Main == null) return true;

            return ExecuteOnUIThread(() =>
            {
                var field = mainWindow.Main.GetType().GetField("petgridcrlf",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null)
                {
                    return (bool)field.GetValue(mainWindow.Main);
                }
                return true;
            });
        }

        /// <summary>
        /// 检查当前动画是否可以复用（避免闪烁）
        /// 如果请求的动画与当前正在播放的动画相同，可以调用 SetContinue() 而不是重新开始
        /// </summary>
        /// <param name="mainWindow">主窗口</param>
        /// <param name="animationName">请求的动画名称</param>
        /// <param name="animatType">请求的动画类型</param>
        /// <returns>true 如果可以复用当前动画</returns>
        public bool CanReuseCurrentAnimation(IMainWindow mainWindow, string animationName, AnimatType animatType)
        {
            if (mainWindow?.Main == null) return false;

            return ExecuteOnUIThread(() =>
            {
                var displayType = mainWindow.Main.DisplayType;
                if (displayType == null) return false;

                // 检查动画名称和类型是否匹配
                if (displayType.Name == animationName && displayType.Animat == animatType)
                {
                    Logger.Log($"AnimationSynchronizer: Animation '{animationName}' ({animatType}) can be reused");
                    return true;
                }

                // 对于循环动画，检查是否在 B_Loop 状态
                if (animatType == AnimatType.B_Loop && displayType.Animat == AnimatType.B_Loop)
                {
                    if (displayType.Name == animationName)
                    {
                        Logger.Log($"AnimationSynchronizer: Loop animation '{animationName}' can be continued");
                        return true;
                    }
                }

                return false;
            });
        }

        /// <summary>
        /// 尝试继续当前动画（调用 SetContinue）
        /// </summary>
        /// <param name="mainWindow">主窗口</param>
        /// <returns>true 如果成功继续动画</returns>
        public bool TryContinueCurrentAnimation(IMainWindow mainWindow)
        {
            if (mainWindow?.Main == null) return false;

            return ExecuteOnUIThread(() =>
            {
                try
                {
                    // 获取 PetGrid 和 PetGrid2 的 Tag（当前动画）
                    var petGridField = mainWindow.Main.GetType().GetField("PetGrid",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var petGrid2Field = mainWindow.Main.GetType().GetField("PetGrid2",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    if (petGridField == null || petGrid2Field == null) return false;

                    var petGrid = petGridField.GetValue(mainWindow.Main) as System.Windows.Controls.Decorator;
                    var petGrid2 = petGrid2Field.GetValue(mainWindow.Main) as System.Windows.Controls.Decorator;

                    if (petGrid == null || petGrid2 == null) return false;

                    // 检查哪个 Grid 当前可见并尝试继续其动画
                    if (petGrid.Visibility == System.Windows.Visibility.Visible && petGrid.Tag is IGraph ig1)
                    {
                        ig1.SetContinue();
                        Logger.Log("AnimationSynchronizer: Continued animation on PetGrid");
                        return true;
                    }
                    else if (petGrid2.Visibility == System.Windows.Visibility.Visible && petGrid2.Tag is IGraph ig2)
                    {
                        ig2.SetContinue();
                        Logger.Log("AnimationSynchronizer: Continued animation on PetGrid2");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"AnimationSynchronizer: Failed to continue animation: {ex.Message}");
                }

                return false;
            });
        }

        /// <summary>
        /// 检查当前动画的 TaskControl 状态
        /// </summary>
        /// <param name="mainWindow">主窗口</param>
        /// <returns>当前动画是否正在播放</returns>
        public bool IsCurrentAnimationPlaying(IMainWindow mainWindow)
        {
            if (mainWindow?.Main == null) return false;

            return ExecuteOnUIThread(() =>
            {
                try
                {
                    var petGridField = mainWindow.Main.GetType().GetField("PetGrid",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var petGrid2Field = mainWindow.Main.GetType().GetField("PetGrid2",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    if (petGridField == null || petGrid2Field == null) return false;

                    var petGrid = petGridField.GetValue(mainWindow.Main) as System.Windows.Controls.Decorator;
                    var petGrid2 = petGrid2Field.GetValue(mainWindow.Main) as System.Windows.Controls.Decorator;

                    // 检查当前可见的 Grid 的动画状态
                    if (petGrid?.Visibility == System.Windows.Visibility.Visible && petGrid.Tag is IGraph ig1)
                    {
                        return ig1.Control?.PlayState ?? false;
                    }
                    else if (petGrid2?.Visibility == System.Windows.Visibility.Visible && petGrid2.Tag is IGraph ig2)
                    {
                        return ig2.Control?.PlayState ?? false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"AnimationSynchronizer: Failed to check animation state: {ex.Message}");
                }

                return false;
            });
        }

        /// <summary>
        /// 获取当前动画的剩余循环次数（用于判断是否应该等待）
        /// </summary>
        public int GetCurrentLoopCount(IMainWindow mainWindow)
        {
            if (mainWindow?.Main == null) return 0;

            return ExecuteOnUIThread(() =>
            {
                try
                {
                    var looptimesField = mainWindow.Main.GetType().GetField("looptimes",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (looptimesField != null)
                    {
                        return (int)looptimesField.GetValue(mainWindow.Main);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"AnimationSynchronizer: Failed to get loop count: {ex.Message}");
                }
                return 0;
            });
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _animationLock?.Dispose();
                _disposed = true;
            }
        }
    }
}
