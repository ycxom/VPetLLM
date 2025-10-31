using System.Text.RegularExpressions;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;
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
                            // TODO: 贴墙状态（左边）- 需要 VPet >= 11057 (提交 8acf02c0)
                            // 当前版本暂不支持，等待 VPet 更新后取消注释以下代码：
                            // mainWindow.Main.Display(VPet_Simulator.Core.GraphInfo.GraphType.SideLeft, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                            // actionTriggered = true;
                            Logger.Log("SayHandler: 'sideleft' action requires VPet >= 11057, falling back to idel");
                            mainWindow.Main.DisplayToNomal();
                            actionTriggered = true;
                            break;
                        case "sideright":
                            // TODO: 贴墙状态（右边）- 需要 VPet >= 11057 (提交 8acf02c0)
                            // 当前版本暂不支持，等待 VPet 更新后取消注释以下代码：
                            // mainWindow.Main.Display(VPet_Simulator.Core.GraphInfo.GraphType.SideRight, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                            // actionTriggered = true;
                            Logger.Log("SayHandler: 'sideright' action requires VPet >= 11057, falling back to idel");
                            mainWindow.Main.DisplayToNomal();
                            actionTriggered = true;
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
                        // VPet不在重要状态，正常执行Say动画
                        var availableSayAnimations = VPetLLM.Instance.GetAvailableSayAnimations().Select(a => a.ToLower());
                        if (string.IsNullOrEmpty(sayAnimation) || !availableSayAnimations.Contains(sayAnimation.ToLower()))
                        {
                            if (!string.IsNullOrEmpty(animation))
                            {
                                Logger.Log($"Say animation '{animation}' not found. Using default say animation.");
                            }
                            sayAnimation = "say";
                        }

                        // Force the talk animation to loop.
                        mainWindow.Main.Say(text, sayAnimation, true);
                        Utils.Logger.Log($"SayHandler called Say with text: \"{text}\", animation: {sayAnimation}");

                        // TTS启用时不需要等待气泡消失，TTS关闭时才等待
                        if (!VPetLLM.Instance.Settings.TTS.IsEnabled)
                        {
                            await WaitForBubbleCloseByVisibility(mainWindow, text, "正常Say动画模式");
                        }
                        // TTS启用时不等待气泡，让下一个动作可以立即执行
                        
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
        /// 等待气泡显示足够的时间（仅在TTS关闭时使用）
        /// 使用简单的延迟策略，确保用户能看清气泡内容
        /// </summary>
        private async Task WaitForBubbleCloseByVisibility(IMainWindow mainWindow, string text, string mode)
        {
            Logger.Log($"SayHandler: 开始等待气泡显示（{mode}）");
            
            // 计算显示时间：基于文本长度
            int displayTime = Math.Max(text.Length * VPetLLM.Instance.Settings.SayTimeMultiplier, VPetLLM.Instance.Settings.SayTimeMin);
            
            Logger.Log($"SayHandler: 气泡将显示 {displayTime}ms（{mode}）");
            await Task.Delay(displayTime);
            Logger.Log($"SayHandler: 气泡显示完成（{mode}）");
        }
    }
}