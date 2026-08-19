using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core.Services;
using static VPet_Simulator.Core.GraphInfo;

namespace VPetLLM.Handlers.Animation
{
    /// <summary>
    /// 过渡控制器
    /// 管理动画状态过渡，确保平滑切换
    /// </summary>
    public class TransitionController
    {
        private readonly AnimationSynchronizer _synchronizer;

        public TransitionController(AnimationSynchronizer synchronizer)
        {
            _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
        }

        /// <summary>
        /// 检查是否需要结束动画
        /// </summary>
        public bool NeedsEndAnimation(GraphInfo currentDisplay, GraphType targetType)
        {
            if (currentDisplay is null) return false;

            // 如果当前动画类型与目标类型相同，不需要结束动画
            if (currentDisplay.Type == targetType) return false;

            // 如果当前是循环动画 (B_Loop)，需要播放结束动画
            if (currentDisplay.Animat == AnimatType.B_Loop) return true;

            // 如果当前是开始动画 (A_Start)，需要等待完成
            if (currentDisplay.Animat == AnimatType.A_Start) return true;

            return false;
        }

        /// <summary>
        /// 获取过渡动画
        /// </summary>
        public IGraph GetTransitionAnimation(IMainWindow mainWindow, GraphInfo currentDisplay, GraphType targetType)
        {
            if (mainWindow?.Main?.Core?.Graph is null || currentDisplay is null)
            {
                return null;
            }

            // 尝试获取 C_End 动画
            var graph = mainWindow.Main.Core.Graph;
            var mode = mainWindow.Main.Core.Save.Mode;

            var endGraph = graph.FindGraph(currentDisplay.Name, AnimatType.C_End, mode);
            if (endGraph is not null)
            {
                Logger.Log($"TransitionController: Found C_End animation for {currentDisplay.Name}");
                return endGraph;
            }

            Logger.Log($"TransitionController: No C_End animation found for {currentDisplay.Name}");
            return null;
        }

        /// <summary>
        /// 执行平滑过渡
        /// </summary>
        public async Task<bool> ExecuteTransitionAsync(
            IMainWindow mainWindow,
            AnimationRequest request)
        {
            if (mainWindow?.Main is null)
            {
                Logger.Log("TransitionController: mainWindow is null");
                return false;
            }

            var currentDisplay = mainWindow.Main.DisplayType;
            var targetType = request.TargetGraphType ?? GraphType.Default;
            var animationName = request.AnimationName ?? mainWindow.Main.Core.Graph.FindName(targetType);

            Logger.Log($"TransitionController: Executing transition from {currentDisplay?.Type} to {targetType}");

            // 优化：检查是否可以复用当前动画（避免闪烁）
            if (_synchronizer.CanReuseCurrentAnimation(mainWindow, animationName, request.AnimatType))
            {
                Logger.Log($"TransitionController: Reusing current animation '{animationName}' to avoid flicker");

                // 如果是循环动画，尝试继续
                if (request.AnimatType == AnimatType.B_Loop)
                {
                    if (_synchronizer.TryContinueCurrentAnimation(mainWindow))
                    {
                        Logger.Log("TransitionController: Successfully continued loop animation");
                        return true;
                    }
                }

                // 动画相同，不需要切换
                return true;
            }

            // 检查是否需要结束动画
            if (NeedsEndAnimation(currentDisplay, targetType))
            {
                var transitionGraph = GetTransitionAnimation(mainWindow, currentDisplay, targetType);
                if (transitionGraph is not null)
                {
                    Logger.Log($"TransitionController: Playing C_End animation before transition");

                    // C_End 是过渡中间步，没有自己的回收动作（回收由后面的目标动画负责），
                    // 所以 reclaim 传 null；超时只丢记账，不影响宿主。
                    var endGeneration = _synchronizer.BeginDisplay();
                    using var endCompletion = new AnimationCompletion(
                        reclaim: null,
                        bookkeeping: null,
                        stillOwnsDisplay: () => _synchronizer.OwnsDisplay(endGeneration),
                        describe: $"C_End:{currentDisplay?.Name}");

                    _synchronizer.ExecuteOnUIThread(() =>
                    {
                        try
                        {
                            mainWindow.Main.Display(transitionGraph, endCompletion.Complete);
                        }
                        catch (Exception ex)
                        {
                            endCompletion.Fail(ex);
                        }
                    });

                    // 等待结束动画完成。
                    // 原来是 Task.WhenAny(tcs, Task.Delay(timeout))：tcs 先赢时那个 Delay 定时器
                    // 仍然武装着直到到期，动画高频时会一直堆积。WaitAsync 自己管定时器生命周期。
                    try
                    {
                        await endCompletion.Task.WaitAsync(TimeSpan.FromMilliseconds(request.TimeoutMs));
                    }
                    catch (TimeoutException)
                    {
                        Logger.Log("TransitionController: C_End animation timed out");
                    }
                }
                else
                {
                    // 没有 C_End 动画，等待当前动画自然完成
                    Logger.Log("TransitionController: Waiting for current animation to complete naturally");
                    await _synchronizer.WaitForAnimationCompleteAsync(request.TimeoutMs);
                }
            }

            // 执行目标动画
            return await ExecuteTargetAnimationAsync(mainWindow, request);
        }

        /// <summary>
        /// 执行目标动画
        /// </summary>
        private async Task<bool> ExecuteTargetAnimationAsync(IMainWindow mainWindow, AnimationRequest request)
        {
            var label = request.AnimationName ?? request.TargetGraphType?.ToString() ?? "(normal)";

            // 两条通道必须分开走（详见 AnimationCompletion 的注释）：
            //   · reclaim = request.EndAction，全项目一律是 Main.DisplayToNomal —— 这是 VPet 的
            //     回收契约，不调宠物就卡在最后一帧。即使我们等超时了，它也得留着，
            //     由代际检查负责在显示权易主时自动失效。
            //   · bookkeeping = 协调器记账 —— 超时就必须丢，否则会给下一个动画错误记账。
            var generation = _synchronizer.BeginDisplay();
            using var completion = new AnimationCompletion(
                reclaim: request.EndAction,
                bookkeeping: () => _synchronizer.MarkAnimationCompleted(),
                stillOwnsDisplay: () => _synchronizer.OwnsDisplay(generation),
                describe: $"{request.Source}:{label}");

            try
            {
                _synchronizer.ExecuteOnUIThread(() =>
                {
                    try
                    {
                        if (request.TargetGraphType.HasValue)
                        {
                            mainWindow.Main.Display(request.TargetGraphType.Value, request.AnimatType, completion.Complete);
                        }
                        else if (!string.IsNullOrEmpty(request.AnimationName))
                        {
                            mainWindow.Main.Display(request.AnimationName, request.AnimatType, completion.Complete);
                        }
                        else
                        {
                            // 这一支自己就已经回到待机了，回收动作没必要再跑一遍。
                            mainWindow.Main.DisplayToNomal();
                            completion.HandOff();
                        }

                        _synchronizer.UpdateState(mainWindow, request.Source);
                        Logger.Log($"TransitionController: Target animation started - {label}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"TransitionController: Error executing animation: {ex.Message}");
                        completion.Fail(ex);
                    }
                });

                // 超时用 WaitAsync：不像 Task.WhenAny(t, Task.Delay(..)) 那样留下武装着的定时器。
                return await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(request.TimeoutMs));
            }
            catch (TimeoutException)
            {
                Logger.Log($"TransitionController: Animation timed out after {request.TimeoutMs}ms - {label}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"TransitionController: Exception during animation execution: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 安全结束当前动画（参考 VPet MessageBar 的 DisplayCEndtoNomal 逻辑）
        /// 只有在满足条件时才结束动画，避免闪烁
        /// </summary>
        public async Task<bool> SafeEndCurrentAnimationAsync(IMainWindow mainWindow, string expectedGraphName = null)
        {
            if (mainWindow?.Main is null) return false;

            // 检查是否正在播放语音 - 如果是，等待语音完成
            if (_synchronizer.IsPlayingVoice(mainWindow))
            {
                Logger.Log("TransitionController: Voice is playing, waiting before ending animation");
                await _synchronizer.WaitForVoiceCompleteAsync(mainWindow);
            }

            var tcs = new TaskCompletionSource<bool>();

            _synchronizer.ExecuteOnUIThread(() =>
            {
                try
                {
                    var displayType = mainWindow.Main.DisplayType;

                    // 参考 VPet MessageBar 的逻辑：
                    // 只有当 displayType.Name == graphName 或 displayType.Type == GraphType.Say 时才结束动画
                    // 并且 displayType.Animat != AnimatType.C_End（不是已经在播放结束动画）
                    bool shouldEnd = false;

                    if (displayType is not null && displayType.Animat != AnimatType.C_End)
                    {
                        if (!string.IsNullOrEmpty(expectedGraphName) && displayType.Name == expectedGraphName)
                        {
                            shouldEnd = true;
                        }
                        else if (displayType.Type == GraphType.Say)
                        {
                            shouldEnd = true;
                        }
                    }

                    if (shouldEnd)
                    {
                        Logger.Log($"TransitionController: Safely ending animation '{displayType.Name}' with C_End");
                        mainWindow.Main.DisplayCEndtoNomal(displayType.Name);
                        tcs.TrySetResult(true);
                    }
                    else
                    {
                        Logger.Log($"TransitionController: Skipping animation end - conditions not met (current: {displayType?.Name}, expected: {expectedGraphName})");
                        tcs.TrySetResult(false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"TransitionController: Error in SafeEndCurrentAnimationAsync: {ex.Message}");
                    tcs.TrySetResult(false);
                }
            });

            return await tcs.Task;
        }

        /// <summary>
        /// 设置循环属性
        /// </summary>
        public void SetLoopProperty(IGraph graph, bool isLoop)
        {
            if (graph is not null)
            {
                graph.IsLoop = isLoop;
                Logger.Log($"TransitionController: Set IsLoop={isLoop} for animation");
            }
        }

        /// <summary>
        /// 执行状态变更
        /// </summary>
        public async Task<bool> ExecuteStateChangeAsync(IMainWindow mainWindow, AnimationRequest request)
        {
            if (mainWindow?.Main is null || request.TargetState is null)
            {
                Logger.Log("TransitionController: Invalid state change request");
                return false;
            }

            var tcs = new TaskCompletionSource<bool>();

            var lockAcquired = false;
            try
            {
                // 原来这里丢弃了返回值：拿不到锁照样往下跑（等于无锁改状态），
                // 而 finally 还会去 Release 一把根本没拿到的锁。
                lockAcquired = await _synchronizer.AcquireLockAsync(request.TimeoutMs);
                if (!lockAcquired)
                {
                    Logger.Log($"TransitionController: 获取动画锁超时 ({request.TimeoutMs}ms)，放弃本次状态变更");
                    return false;
                }

                try
                {
                    _synchronizer.ExecuteOnUIThread(() =>
                    {
                        try
                        {
                            var targetStateName = request.TargetState.ToString();
                            Logger.Log($"TransitionController: Executing state change to {targetStateName}");

                            // State 访问经适配层（缓存反射）
                            if (!VPetHostAdapter.CanAccessState(mainWindow))
                            {
                                Logger.Log("TransitionController: State member not found");
                                tcs.TrySetResult(false);
                                return;
                            }

                            // 原子更新状态和动画
                            switch (targetStateName)
                            {
                                case "Sleep":
                                    mainWindow.Main.DisplaySleep(force: true);
                                    break;
                                default:
                                    VPetHostAdapter.TrySetStateByName(mainWindow, targetStateName);
                                    mainWindow.Main.DisplayToNomal();
                                    break;
                            }

                            _synchronizer.UpdateState(mainWindow, request.Source);
                            tcs.TrySetResult(true);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"TransitionController: Error during state change: {ex.Message}");
                            tcs.TrySetException(ex);
                        }
                    });

                    return await tcs.Task;
                }
                finally
                {
                    if (lockAcquired) _synchronizer.ReleaseLock();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TransitionController: Exception during state change: {ex.Message}");
                return false;
            }
        }
    }
}
