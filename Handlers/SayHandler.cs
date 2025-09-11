using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;
using static VPet_Simulator.Core.IGameSave;

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
                    var actionHandler = new ActionHandler();
                    await actionHandler.Execute(bodyAnimation, mainWindow);
                }

                // If a body animation is present, disable the talk animation to avoid conflicts.
                if (!string.IsNullOrEmpty(bodyAnimation))
                {
                    mainWindow.Main.Say(text, "say", true);
                }
                else
                {
                    var availableSayAnimations = VPetLLM.Instance.GetAvailableSayAnimations().Select(a => a.ToLower());
                    if (string.IsNullOrEmpty(animation) || !availableSayAnimations.Contains(animation.ToLower()))
                    {
                        if (!string.IsNullOrEmpty(animation))
                        {
                            Logger.Log($"Say animation '{animation}' not found. Using random say animation.");
                        }
                        mainWindow.Main.Say(text, "say", true);
                    }
                    else
                    {
                        mainWindow.Main.Say(text, animation, true);
                    }
                }
                Utils.Logger.Log($"SayHandler called Say with text: \"{text}\", animation: {animation ?? "none"}, bodyAnimation: {bodyAnimation ?? "none"}");
                await Task.Delay(Math.Max(text.Length * 200, 2000)); // Wait for the text to be displayed and animation to play
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