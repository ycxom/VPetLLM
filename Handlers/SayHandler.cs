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