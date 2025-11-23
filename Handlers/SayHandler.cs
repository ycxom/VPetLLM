using System.Text.RegularExpressions;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;
using VPet_Simulator.Core;
using static VPet_Simulator.Core.GraphInfo;

namespace VPetLLM.Handlers
{
    public class SayHandler : IActionHandler
    {
        public string Keyword => "say";
        public ActionType ActionType => ActionType.Talk;
        public string Description => PromptHelper.Get("Handler_Say_Description", VPetLLM.Instance.Settings.PromptLanguage);

        public async Task Execute(string value, IMainWindow mainWindow)
        {
            // 检查是否为默认插件
            if (VPetLLM.Instance?.IsVPetLLMDefaultPlugin() != true)
            {
                Utils.Logger.Log("SayHandler: VPetLLM不是默认插件，忽略Say请求");
                return;
            }

            Utils.Logger.Log($"SayHandler executed with value: {value}");
            
            // Say动作只是显示气泡，不影响动画，所以在所有情况下都允许执行
            // 不需要检查动画状态
            
            try
            {
                string text;
                string animation = null;
                string bodyAnimation = null;

                var match = new Regex("\"(.*?)\"(?:,\\s*([^,]*))?(?:,\\s*(.*))?").Match(value);
                if (match.Success)
                {
                    text = match.Groups[1].Value;
                    animation = match.Groups[2].Success && !string.IsNullOrEmpty(match.Groups[2].Value) ? match.Groups[2].Value.Trim() : null;
                    bodyAnimation = match.Groups[3].Success && !string.IsNullOrEmpty(match.Groups[3].Value) ? match.Groups[3].Value.Trim() : null;
                }
                else
                {
                    text = value;
                }

                if (!string.IsNullOrEmpty(bodyAnimation))
                {
                    // Play body animation AND show the speech bubble without a conflicting talk animation.
                    var action = bodyAnimation.ToLower();
                    Utils.Logger.Log($"SayHandler performing body animation: {action} while talking.");

                    // 先结束上一个动画（如果不是重要动画）
                    bool shouldBlockAnimation = AnimationStateChecker.IsPlayingImportantAnimation(mainWindow);
                    if (!shouldBlockAnimation)
                    {
                        Logger.Log($"SayHandler: 准备播放Body动画，先尝试结束上一个动画");
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                mainWindow.Main.DisplayStopForce(() =>
                                {
                                    Logger.Log("SayHandler: 上一个动画已结束（Body模式）");
                                });
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"SayHandler: 结束上一个动画失败（Body模式）: {ex.Message}");
                            }
                        });
                        
                        // 短暂延迟，确保动画结束完成
                        await Task.Delay(50);
                    }
                    else
                    {
                        Logger.Log($"SayHandler: VPet正在执行重要动画，跳过Body动画");
                        // 只显示气泡，不执行Body动画
                        mainWindow.Main.Say(text, null, false);
                        
                        if (!VPetLLM.Instance.Settings.TTS.IsEnabled)
                        {
                            await WaitForBubbleCloseByVisibility(mainWindow, text, "重要动画阻止Body模式");
                        }
                        return;
                    }

                    // 1. Start the body animation. It will manage its own lifecycle.
                    bool actionTriggered = false;
                    switch (action)
                    {
                        case "touch_head":
                        case "touchhead":
                            mainWindow.Main.DisplayTouchHead();
                            actionTriggered = true;
                            break;
                        case "touch_body":
                        case "touchbody":
                            mainWindow.Main.DisplayTouchBody();
                            actionTriggered = true;
                            break;
                        case "pinch":
                        case "pinch_face":
                        case "touchpinch":
                            // 调用VPet的DisplayPinch方法（如果可用）
                            try
                            {
                                var displayPinchMethod = mainWindow.GetType().GetMethod("DisplayPinch");
                                if (displayPinchMethod != null)
                                {
                                    displayPinchMethod.Invoke(mainWindow, null);
                                    actionTriggered = true;
                                }
                                else
                                {
                                    Logger.Log("SayHandler: DisplayPinch method not found, pinch animation not available");
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Logger.Log($"SayHandler: Failed to execute pinch action: {ex.Message}");
                            }
                            break;
                        case "move":
                            // 直接调用Display方法显示移动动画，绕过可能失效的委托属性
                            mainWindow.Main.Display(GraphType.Move, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                            actionTriggered = true;
                            break;
                        case "sleep":
                            mainWindow.Main.DisplaySleep();
                            actionTriggered = true;
                            break;
                        case "idel":
                            // 使用DisplayToNomal()作为待机状态的替代方法
                            mainWindow.Main.DisplayToNomal();
                            actionTriggered = true;
                            break;
                        case "sideleft":
                            // 贴墙状态（左边）- VPet 11057+ 通过设置 State 实现
                            try
                            {
                                var stateProperty = mainWindow.Main.GetType().GetProperty("State");
                                if (stateProperty != null)
                                {
                                    var workingStateType = stateProperty.PropertyType;
                                    var sideLeftValue = System.Enum.Parse(workingStateType, "SideLeft");
                                    stateProperty.SetValue(mainWindow.Main, sideLeftValue);
                                    Logger.Log("SayHandler: Set state to SideLeft");
                                    actionTriggered = true;
                                }
                                else
                                {
                                    Logger.Log("SayHandler: State property not found, falling back to idel");
                                    mainWindow.Main.DisplayToNomal();
                                    actionTriggered = true;
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Logger.Log($"SayHandler: Failed to set SideLeft state: {ex.Message}, falling back to idel");
                                mainWindow.Main.DisplayToNomal();
                                actionTriggered = true;
                            }
                            break;
                        case "sideright":
                            // 贴墙状态（右边）- VPet 11057+ 通过设置 State 实现
                            try
                            {
                                var stateProperty = mainWindow.Main.GetType().GetProperty("State");
                                if (stateProperty != null)
                                {
                                    var workingStateType = stateProperty.PropertyType;
                                    var sideRightValue = System.Enum.Parse(workingStateType, "SideRight");
                                    stateProperty.SetValue(mainWindow.Main, sideRightValue);
                                    Logger.Log("SayHandler: Set state to SideRight");
                                    actionTriggered = true;
                                }
                                else
                                {
                                    Logger.Log("SayHandler: State property not found, falling back to idel");
                                    mainWindow.Main.DisplayToNomal();
                                    actionTriggered = true;
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Logger.Log($"SayHandler: Failed to set SideRight state: {ex.Message}, falling back to idel");
                                mainWindow.Main.DisplayToNomal();
                                actionTriggered = true;
                            }
                            break;
                        default:
                            mainWindow.Main.Display(action, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                            actionTriggered = true;
                            break;
                    }
                    
                    if (!actionTriggered)
                    {
                        Logger.Log($"SayHandler: Body animation '{action}' failed to trigger, falling back to default");
                    }

                    // 2. Show the speech bubble ONLY by passing a null animation name.
                    // 显示气泡
                    mainWindow.Main.Say(text, null, false);
                    
                    // TTS启用时不需要等待气泡消失，TTS关闭时才等待
                    if (!VPetLLM.Instance.Settings.TTS.IsEnabled)
                    {
                        await WaitForBubbleCloseByVisibility(mainWindow, text, "body animation模式");
                    }
                    // TTS启用时不等待气泡，让下一个动作可以立即执行
                }
                else
                {
                    // No body animation, so just perform the talk animation.
                    var sayAnimation = animation;
                    
                    // 检查VPet是否正在执行重要动画
                    // 如果是，则屏蔽Say动画，只显示气泡
                    bool shouldBlockAnimation = AnimationStateChecker.IsPlayingImportantAnimation(mainWindow);
                    
                    if (shouldBlockAnimation)
                    {
                        Logger.Log($"SayHandler: VPet正在执行重要动画，屏蔽Say动画，仅显示气泡");
                        
                        // 只显示气泡，不执行Say动画
                        mainWindow.Main.Say(text, null, false);
                        Utils.Logger.Log($"SayHandler called Say with text: \"{text}\", animation: none (blocked)");
                        
                        // TTS启用时不需要等待气泡消失，TTS关闭时才等待
                        if (!VPetLLM.Instance.Settings.TTS.IsEnabled)
                        {
                            await WaitForBubbleCloseByVisibility(mainWindow, text, "无动画模式");
                        }
                        // TTS启用时不等待气泡，让下一个动作可以立即执行
                    }
                    else
                    {
                        // VPet不在重要状态，先结束上一个动画（如果有）
                        Logger.Log($"SayHandler: 准备播放Say动画，先尝试结束上一个动画");
                        
                        // 使用DisplayStopForce确保上一个动画结束
                        bool animationStopped = false;
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                // DisplayStopForce会尝试播放C_End动画，如果没有则直接执行回调
                                mainWindow.Main.DisplayStopForce(() =>
                                {
                                    animationStopped = true;
                                    Logger.Log("SayHandler: 上一个动画已结束");
                                });
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"SayHandler: 结束上一个动画失败: {ex.Message}");
                                animationStopped = true;
                            }
                        });
                        
                        // 短暂延迟，确保动画结束完成
                        await Task.Delay(50);
                        
                        // 解析动画参数（支持"状态_动画"格式）
                        var (animName, modeType) = ParseAnimationParameter(sayAnimation);
                        
                        // 如果指定了状态模式，临时切换到该状态
                        VPet_Simulator.Core.IGameSave.ModeType? originalMode = null;
                        if (modeType.HasValue)
                        {
                            originalMode = mainWindow.Main.Core.Save.Mode;
                            mainWindow.Main.Core.Save.Mode = modeType.Value;
                            Logger.Log($"SayHandler: 临时切换到状态模式 {modeType.Value}");
                        }
                        
                        // 验证动画是否可用
                        var currentMode = mainWindow.Main.Core.Save.Mode;
                        var graph = mainWindow.Main.Core.Graph.FindGraph(animName, VPet_Simulator.Core.GraphInfo.AnimatType.A_Start, currentMode);
                        
                        if (graph == null)
                        {
                            Logger.Log($"Say animation '{animName}' not found in mode '{currentMode}'. Using default say animation.");
                            animName = "say";
                        }

                        // 播放说话动画
                        mainWindow.Main.Say(text, animName, true);
                        Utils.Logger.Log($"SayHandler called Say with text: \"{text}\", animation: {animName}, mode: {currentMode}");
                        
                        // 恢复原始状态模式
                        if (originalMode.HasValue)
                        {
                            mainWindow.Main.Core.Save.Mode = originalMode.Value;
                            Logger.Log($"SayHandler: 恢复到原始状态模式 {originalMode.Value}");
                        }

                        // TTS启用时不需要等待气泡消失，TTS关闭时才等待
                        if (!VPetLLM.Instance.Settings.TTS.IsEnabled)
                        {
                            await WaitForBubbleCloseByVisibility(mainWindow, text, "正常Say动画模式");
                        }
                        // TTS启用时不等待气泡，让下一个动作可以立即执行
                        
                        // 说话完成后恢复默认状态
                        mainWindow.Main.DisplayToNomal();
                    }
                }
            }
            catch (Exception e)
            {
                Utils.Logger.Log($"Error in SayHandler: {e.Message}");
            }
        }

        public Task Execute(int value, IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }
        public Task Execute(IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }
        public int GetAnimationDuration(string animationName) => 0;

        /// <summary>
        /// 解析动画参数，支持"状态_动画"格式
        /// 例如：happy_shy -> 在happy状态下播放shy动画
        ///       shy -> 在当前状态下播放shy动画
        ///       happy -> 在happy状态下播放默认say动画
        /// </summary>
        private (string animationName, VPet_Simulator.Core.IGameSave.ModeType? modeType) ParseAnimationParameter(string animation)
        {
            if (string.IsNullOrEmpty(animation))
                return ("say", null);
            
            var animLower = animation.ToLower().Trim();
            
            // 检查是否为"状态_动画"格式
            var parts = animLower.Split('_');
            if (parts.Length >= 2)
            {
                var potentialMode = parts[0];
                var animName = string.Join("_", parts.Skip(1));
                
                // 检查第一部分是否为有效的状态模式
                VPet_Simulator.Core.IGameSave.ModeType? mode = potentialMode switch
                {
                    "happy" => VPet_Simulator.Core.IGameSave.ModeType.Happy,
                    "nomal" => VPet_Simulator.Core.IGameSave.ModeType.Nomal,
                    "poorcondition" => VPet_Simulator.Core.IGameSave.ModeType.PoorCondition,
                    "ill" => VPet_Simulator.Core.IGameSave.ModeType.Ill,
                    _ => null
                };
                
                if (mode.HasValue)
                {
                    Logger.Log($"SayHandler: 解析为状态模式动画 - 状态: {potentialMode}, 动画: {animName}");
                    return (animName, mode.Value);
                }
            }
            
            // 检查是否为纯状态名（如happy, nomal等），映射到该状态的默认say动画
            var stateOnlyMode = animLower switch
            {
                "happy" => VPet_Simulator.Core.IGameSave.ModeType.Happy,
                "nomal" => VPet_Simulator.Core.IGameSave.ModeType.Nomal,
                "poorcondition" => VPet_Simulator.Core.IGameSave.ModeType.PoorCondition,
                "ill" => VPet_Simulator.Core.IGameSave.ModeType.Ill,
                _ => (VPet_Simulator.Core.IGameSave.ModeType?)null
            };
            
            if (stateOnlyMode.HasValue)
            {
                Logger.Log($"SayHandler: 解析为纯状态名 '{animLower}'，使用该状态的say动画");
                return ("say", stateOnlyMode.Value);
            }
            
            // 不是状态_动画格式，直接返回动画名（使用当前状态）
            Logger.Log($"SayHandler: 使用动画名称 '{animLower}'（当前状态）");
            return (animLower, null);
        }

        /// <summary>
        /// 等待气泡显示足够的时间（仅在TTS关闭时使用）
        /// 优化：结合VPet新的SayInfo架构，提供更智能的等待策略
        /// 增加：为外置TTS额外添加0.1秒等待时间
        /// </summary>
        private async Task WaitForBubbleCloseByVisibility(IMainWindow mainWindow, string text, string mode)
        {
            Logger.Log($"SayHandler: 开始智能等待气泡显示（{mode}）");
            
            // 优化策略：结合VPet内置语音状态和新的SayInfo架构
            int displayTime = Math.Max(text.Length * VPetLLM.Instance.Settings.SayTimeMultiplier, VPetLLM.Instance.Settings.SayTimeMin);
            
            // 如果VPet正在播放语音，等待语音播放完成
            if (mainWindow.Main.PlayingVoice)
            {
                Logger.Log($"SayHandler: 检测到VPet语音播放，等待语音完成（{mode}）");
                await WaitForVPetVoiceComplete(mainWindow, mode);
                
                // 语音播放完成后，再等待基本的显示时间
                int remainingTime = Math.Max(displayTime - 500, 500); // 至少显示500ms
                if (remainingTime > 0)
                {
                    Logger.Log($"SayHandler: 语音完成，继续显示气泡 {remainingTime}ms（{mode}）");
                    await Task.Delay(remainingTime);
                }
                
                // 为外置TTS添加额外等待时间，确保播放完全
                await Task.Delay(100);
            }
            else
            {
                // 没有语音时，直接按计算时间显示
                Logger.Log($"SayHandler: 无语音，直接显示气泡 {displayTime}ms（{mode}）");
                await Task.Delay(displayTime);
                
                // 即使没有检测到语音，也为外置TTS添加额外等待
                Logger.Log($"SayHandler: 为外置TTS添加额外1秒缓冲时间（{mode}）");
                await Task.Delay(1000);
            }
            
            Logger.Log($"SayHandler: 气泡显示完成（{mode}）");
        }

        /// <summary>
        /// 等待VPet语音播放完成（优化版，减少轮询频率）
        /// 利用VPet新的SayInfo架构提供的状态信息
        /// </summary>
        private async Task WaitForVPetVoiceComplete(IMainWindow mainWindow, string mode)
        {
            try
            {
                // 优化：减少轮询频率，增加检查间隔到200ms，减少CPU占用
                int maxWaitTime = 30000; // 最多等待 30 秒
                int checkInterval = 200; // 优化：从100ms增加到200ms
                int elapsedTime = 0;

                Logger.Log($"SayHandler: 开始等待VPet语音播放完成（{mode}）");
                
                while (mainWindow.Main.PlayingVoice && elapsedTime < maxWaitTime)
                {
                    await Task.Delay(checkInterval);
                    elapsedTime += checkInterval;
                }

                if (elapsedTime >= maxWaitTime)
                {
                    Logger.Log($"SayHandler: 等待VPet语音播放超时（{mode}）");
                }
                else if (elapsedTime > 0)
                {
                    Logger.Log($"SayHandler: VPet语音播放完成，等待时间: {elapsedTime}ms（{mode}）");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SayHandler: 等待VPet语音播放失败: {ex.Message}（{mode}）");
            }
        }
    }
}