using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils.System;
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
            if (currentDisplay == null) return false;

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
            if (mainWindow?.Main?.Core?.Graph == null || currentDisplay == null)
            {
                return null;
            }

            // 尝试获取 C_End 动画
            var graph = mainWindow.Main.Core.Graph;
            var mode = mainWindow.Main.Core.Save.Mode;

            var endGraph = graph.FindGraph(currentDisplay.Name, AnimatType.C_End, mode);
            if (endGraph != null)
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
            if (mainWindow?.Main == null)
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
                if (transitionGraph != null)
                {
                    Logger.Log($"TransitionController: Playing C_End animation before transition");

                    var tcs = new TaskCompletionSource<bool>();

                    _synchronizer.ExecuteOnUIThread(() =>
                    {
                        mainWindow.Main.Display(transitionGraph, () =>
                        {
                            tcs.TrySetResult(true);
                        });
                    });

                    // 等待结束动画完成
                    var completed = await Task.WhenAny(tcs.Task, Task.Delay(request.TimeoutMs)) == tcs.Task;
                    if (!completed)
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
            var tcs = new TaskCompletionSource<bool>();

            try
            {
                _synchronizer.ExecuteOnUIThread(() =>
                {
                    try
                    {
                        Action endAction = () =>
                        {
                            request.EndAction?.Invoke();
                            _synchronizer.MarkAnimationCompleted();
                            tcs.TrySetResult(true);
                        };

                        if (request.TargetGraphType.HasValue)
                        {
                            mainWindow.Main.Display(request.TargetGraphType.Value, request.AnimatType, endAction);
                        }
                        else if (!string.IsNullOrEmpty(request.AnimationName))
                        {
                            mainWindow.Main.Display(request.AnimationName, request.AnimatType, endAction);
                        }
                        else
                        {
                            mainWindow.Main.DisplayToNomal();
                            tcs.TrySetResult(true);
                        }

                        _synchronizer.UpdateState(mainWindow, request.Source);
                        Logger.Log($"TransitionController: Target animation started - {request.AnimationName ?? request.TargetGraphType?.ToString()}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"TransitionController: Error executing animation: {ex.Message}");
                        tcs.TrySetException(ex);
                    }
                });

                // 等待动画完成或超时
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(request.TimeoutMs)) == tcs.Task;
                if (!completed)
                {
                    Logger.Log($"TransitionController: Animation timed out after {request.TimeoutMs}ms");
                    return false;
                }

                return await tcs.Task;
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
            if (mainWindow?.Main == null) return false;

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

                    if (displayType != null && displayType.Animat != AnimatType.C_End)
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
            if (graph != null)
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
            if (mainWindow?.Main == null || request.TargetState == null)
            {
                Logger.Log("TransitionController: Invalid state change request");
                return false;
            }

            var tcs = new TaskCompletionSource<bool>();

            try
            {
                await _synchronizer.AcquireLockAsync(request.TimeoutMs);

                try
                {
                    _synchronizer.ExecuteOnUIThread(() =>
                    {
                        try
                        {
                            var targetStateName = request.TargetState.ToString();
                            Logger.Log($"TransitionController: Executing state change to {targetStateName}");

                            // 获取状态字段
                            var stateField = mainWindow.Main.GetType().GetField("State");
                            if (stateField == null)
                            {
                                Logger.Log("TransitionController: State field not found");
                                tcs.TrySetResult(false);
                                return;
                            }

                            var workingStateType = stateField.FieldType;
                            var targetState = Enum.Parse(workingStateType, targetStateName, ignoreCase: true);

                            // 原子更新状态和动画
                            switch (targetStateName)
                            {
                                case "Sleep":
                                    mainWindow.Main.DisplaySleep(force: true);
                                    break;
                                case "Nomal":
                                    stateField.SetValue(mainWindow.Main, targetState);
                                    mainWindow.Main.DisplayToNomal();
                                    break;
                                default:
                                    stateField.SetValue(mainWindow.Main, targetState);
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
                    _synchronizer.ReleaseLock();
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
