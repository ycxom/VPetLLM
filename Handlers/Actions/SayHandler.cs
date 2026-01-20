using System.Text.RegularExpressions;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers.Animation;
using VPetLLM.Utils.Common;
using VPetLLM.Utils.System;
using VPetLLM.Utils.UI;
using static VPet_Simulator.Core.GraphInfo;

namespace VPetLLM.Handlers.Actions
{
    public class SayHandler : IActionHandler
    {
        public string Keyword => "say";
        public ActionType ActionType => ActionType.Talk;
        public ActionCategory Category => ActionCategory.Interactive;
        public string Description => PromptHelper.Get("Handler_Say_Description", VPetLLM.Instance.Settings.PromptLanguage);

        public async Task Execute(string value, IMainWindow mainWindow)
        {
            // 检查是否为默认插件
            if (VPetLLM.Instance?.IsVPetLLMDefaultPlugin() != true)
            {
                Logger.Log("SayHandler: VPetLLM不是默认插件，忽略Say请求");
                return;
            }

            Logger.Log($"SayHandler executed with value: {value}");

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

                // 使用动画协调器检查闪烁风险
                if (AnimationHelper.IsInitialized && AnimationHelper.IsFlickerRisk())
                {
                    var delay = AnimationHelper.GetRecommendedDelay();
                    Logger.Log($"SayHandler: 检测到闪烁风险，延迟 {delay}ms");
                    await Task.Delay(delay);
                }

                if (!string.IsNullOrEmpty(bodyAnimation))
                {
                    // Play body animation AND show the speech bubble without a conflicting talk animation.
                    var action = bodyAnimation.ToLower();
                    Logger.Log($"SayHandler performing body animation: {action} while talking.");

                    // 使用动画协调器检查是否可以执行动画
                    bool shouldBlockAnimation = AnimationStateChecker.IsPlayingImportantAnimation(mainWindow);
                    if (!shouldBlockAnimation)
                    {
                        Logger.Log($"SayHandler: 准备播放Body动画");
                        // 不需要停止当前动画，直接播放Body动画会自动处理过渡
                    }
                    else
                    {
                        Logger.Log($"SayHandler: VPet正在执行重要动画，跳过Body动画");
                        // 只显示气泡，不执行Body动画
                        await ShowBubbleOnlyAsync(mainWindow, text);

                        // SayHandler 不再负责等待，由 SmartMessageProcessor 统一处理
                        Logger.Log($"SayHandler: 仅气泡已启动（重要动画阻止），等待由调用方处理");
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
                    await ShowBubbleOnlyAsync(mainWindow, text);

                    // SayHandler 不再负责等待，由 SmartMessageProcessor 统一处理
                    Logger.Log($"SayHandler: Body动画+气泡已启动，等待由调用方处理");
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
                        await ShowBubbleOnlyAsync(mainWindow, text);
                        Logger.Log($"SayHandler called Say with text: \"{text}\", animation: none (blocked)");

                        // SayHandler 不再负责等待，由 SmartMessageProcessor 统一处理
                        Logger.Log($"SayHandler: 仅气泡已启动（无动画模式），等待由调用方处理");
                    }
                    else
                    {
                        // VPet不在重要状态，直接播放Say动画
                        // 注意：不要使用 AnimationHelper.RequestStopAsync()，因为它会调用 DisplayToNomal() 覆盖 Say 动画
                        Logger.Log($"SayHandler: 准备播放Say动画");

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

                        // 播放说话动画 - 使用标准的Say方法
                        mainWindow.Main.Say(text, animName, true);
                        Logger.Log($"SayHandler: 显示完成 - 文本: \"{text}\", 动画: {animName}, 模式: {currentMode}");

                        // 恢复原始状态模式
                        if (originalMode.HasValue)
                        {
                            mainWindow.Main.Core.Save.Mode = originalMode.Value;
                            Logger.Log($"SayHandler: 恢复到原始状态模式 {originalMode.Value}");
                        }

                        // SayHandler 不再负责等待，由 SmartMessageProcessor 统一处理
                        // 这样可以避免重复等待的问题
                        Logger.Log($"SayHandler: Say动画已启动，等待由调用方处理");
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Log($"Error in SayHandler: {e.Message}");
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
        /// 仅显示气泡（不触发动画）
        /// 使用DirectBubbleManager实现直接覆盖
        /// 修复：确保在所有情况下都能正确显示气泡内容
        /// </summary>
        private async Task ShowBubbleOnlyAsync(IMainWindow mainWindow, string text)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var plugin = VPetLLM.Instance;
                    if (plugin != null)
                    {
                        Logger.Log($"SayHandler: 显示气泡（仅气泡模式）- 文本: \"{text}\"");
                        
                        // 无论是否有VPetTTS插件，都使用VPet原生Say API
                        // 这样可以确保气泡内容正确显示，同时让VPetTTS插件处理TTS协调
                        mainWindow.Main.Say(text, null, false);
                        
                        Logger.Log($"SayHandler: 气泡显示完成（使用VPet原生API）");
                    }
                    else
                    {
                        Logger.Log("SayHandler: VPetLLM实例不可用，回退到API调用");
                        // 回退到标准API调用
                        mainWindow.Main.Say(text, null, false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"SayHandler: 显示气泡失败: {ex.Message}");
                }
            });
        }

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
    }
}