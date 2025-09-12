using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;
using static VPet_Simulator.Core.IGameSave;
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
                    switch (action)
                    {
                        case "touch_head":
                        case "touchhead":
                            mainWindow.Main.DisplayTouchHead();
                            break;
                        case "touch_body":
                        case "touchbody":
                            mainWindow.Main.DisplayTouchBody();
                            break;
                        case "move":
                            mainWindow.Main.DisplayMove();
                            break;
                        case "sleep":
                            mainWindow.Main.DisplaySleep();
                            break;
                        case "idel":
                            mainWindow.Main.DisplayIdel();
                            break;
                        default:
                            mainWindow.Main.Display(action, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                            break;
                    }

                    // 2. Show the speech bubble ONLY by passing a null animation name.
                    mainWindow.Main.Say(text, null, false);
                    await Task.Delay(Math.Max(text.Length * VPetLLM.Instance.Settings.SayTimeMultiplier, VPetLLM.Instance.Settings.SayTimeMin));
                }
                else
                {
                    // No body animation, so just perform the talk animation.
                    var sayAnimation = animation;
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

                    // Wait, then interrupt the loop to return to normal.
                    await Task.Delay(Math.Max(text.Length * VPetLLM.Instance.Settings.SayTimeMultiplier, VPetLLM.Instance.Settings.SayTimeMin));
                    mainWindow.Main.DisplayToNomal();
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
    }
}