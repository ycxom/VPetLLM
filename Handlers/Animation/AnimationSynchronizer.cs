using System.Windows;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core.Services;
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

        // 显示权代际号。每下发一个动画就 +1。
        // 用途：一次动画超时之后，它交给 VPet 的回收动作（DisplayToNomal）可能很久才被回调；
        // 那时候如果已经有新动画接管了显示，这个迟到的回收就必须放弃，否则会把新动画掐掉。
        // 单调递增，永不复位 —— 复位会让过期的收尾器误以为自己又持有显示权。
        private long _displayGeneration;
        private AnimationState _currentState = new AnimationState();
        private Action _completionCallback;
        private bool _disposed = false;

        /// <summary>当前动画状态</summary>
        public AnimationState CurrentState => _currentState;

        /// <summary>
        /// 认领显示权。真正下发动画之前调用，返回本次的代际号。
        /// </summary>
        public long BeginDisplay() => Interlocked.Increment(ref _displayGeneration);

        /// <summary>
        /// 指定代际是否仍然持有显示权（即之后没有别的动画接管过）。
        /// </summary>
        public bool OwnsDisplay(long generation) => Interlocked.Read(ref _displayGeneration) == generation;

        /// <summary>
        /// 等待当前动画完成
        /// </summary>
        /// <param name="timeoutMs">超时时间 (毫秒)</param>
        /// <returns>true 如果动画完成，false 如果超时</returns>
        public async Task<bool> WaitForAnimationCompleteAsync(int timeoutMs = 5000)
        {
            // Stopwatch 而非 DateTime.Now：墙钟被校时/夏令时拨动会让超时判断失准。
            var sw = global::System.Diagnostics.Stopwatch.StartNew();
            while (_currentState.IsAnimating)
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
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
            if (Application.Current?.Dispatcher is null)
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
            if (Application.Current?.Dispatcher is null)
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
            if (Application.Current?.Dispatcher is null)
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
        /// <summary>
        /// 检查是否可以执行动画。
        /// 判定逻辑只有 <see cref="GetBlockingReason"/> 一份 —— 这两个方法结构上不可能再漂移。
        /// （以前是各写各的：CanExecuteAnimation 认 Say+语音，GetBlockingReason 不认，
        /// 于是协调器打出来的日志会是 "Request blocked - " 后面一片空白。）
        /// </summary>
        public bool CanExecuteAnimation(IMainWindow mainWindow)
        {
            var reason = GetBlockingReason(mainWindow);
            if (reason is null) return true;

            Logger.Log($"AnimationSynchronizer: cannot execute animation - {reason}");
            return false;
        }

        /// <summary>
        /// 检查是否正在播放语音
        /// </summary>
        public bool IsPlayingVoice(IMainWindow mainWindow)
        {
            if (mainWindow?.Main is null) return false;
            return mainWindow.Main.PlayingVoice;
        }

        /// <summary>
        /// 获取语音剩余播放时间（毫秒）
        /// 参考 VPet MessageBar 的实现
        /// </summary>
        public int GetVoiceRemainingTime(IMainWindow mainWindow)
        {
            if (mainWindow?.Main is null || !mainWindow.Main.PlayingVoice)
                return 0;

            return ExecuteOnUIThread(() =>
            {
                try
                {
                    var voicePlayer = VPetHostAdapter.GetVoicePlayer(mainWindow) as System.Windows.Media.MediaPlayer;
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
            if (mainWindow?.Main is null || !mainWindow.Main.PlayingVoice)
                return;

            var sw = global::System.Diagnostics.Stopwatch.StartNew();
            while (mainWindow.Main.PlayingVoice)
            {
                if (sw.ElapsedMilliseconds > maxWaitMs)
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
        /// <summary>
        /// 获取阻塞原因。null = 当前可以执行动画。
        /// 这是「能不能动画」的唯一实现：宿主动画那部分统一走
        /// <see cref="VPetMovementPolicy.GetAnimationOverrideBlockReason"/>，本方法只额外加上
        /// VPetLLM 自己的两层门（用户交互中 / 宠物处于工作·睡眠·旅行会话）。
        /// </summary>
        public string GetBlockingReason(IMainWindow mainWindow)
        {
            if (mainWindow?.Main is null)
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

            // 宿主动画的保护清单只有 VPetMovementPolicy 一份，别在这里再抄。
            return VPetMovementPolicy.GetAnimationOverrideBlockReason(mainWindow);
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
            if (mainWindow?.Main is null) return;

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
            if (mainWindow?.Main is null) return true;

            return ExecuteOnUIThread(() => VPetHostAdapter.GetPetGridCrlf(mainWindow) ?? true);
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
            if (mainWindow?.Main is null) return false;

            return ExecuteOnUIThread(() =>
            {
                var displayType = mainWindow.Main.DisplayType;
                if (displayType is null) return false;

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
            if (mainWindow?.Main is null) return false;

            return ExecuteOnUIThread(() =>
            {
                try
                {
                    // 获取 PetGrid 和 PetGrid2 的 Tag（当前动画）
                    if (!VPetHostAdapter.TryGetPetGrids(mainWindow, out var petGridObj, out var petGrid2Obj))
                        return false;

                    var petGrid = petGridObj as System.Windows.Controls.Decorator;
                    var petGrid2 = petGrid2Obj as System.Windows.Controls.Decorator;

                    if (petGrid is null || petGrid2 is null) return false;

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
            if (mainWindow?.Main is null) return false;

            return ExecuteOnUIThread(() =>
            {
                try
                {
                    if (!VPetHostAdapter.TryGetPetGrids(mainWindow, out var petGridObj, out var petGrid2Obj))
                        return false;

                    var petGrid = petGridObj as System.Windows.Controls.Decorator;
                    var petGrid2 = petGrid2Obj as System.Windows.Controls.Decorator;

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
            if (mainWindow?.Main is null) return 0;

            return ExecuteOnUIThread(() =>
            {
                try
                {
                    return VPetHostAdapter.GetLoopTimes(mainWindow) ?? 0;
                }
                catch (Exception ex)
                {
                    Logger.Log($"AnimationSynchronizer: Failed to get loop count: {ex.Message}");
                }
                return 0;
            });
        }

        /// <summary>
        /// 复位瞬时状态，但保留信号量 —— 供协调器关停后重新初始化时使用。
        /// 重点是把 _completionCallback 断掉：那个委托可能捕获了已经卸载的插件对象，
        /// 留着它既是内存泄漏，也可能在下一轮动画里被误触发。
        /// </summary>
        public void ResetTransientState()
        {
            _completionCallback = null;
            _currentState = new AnimationState();
            Logger.Log("AnimationSynchronizer: 瞬时状态已复位");
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _completionCallback = null;
                _animationLock?.Dispose();
                _disposed = true;
            }
        }
    }
}
