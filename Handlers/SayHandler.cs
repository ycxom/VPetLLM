using System;
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

        public void Execute(string value, IMainWindow mainWindow)
        {
            Utils.Logger.Log($"SayHandler executed with value: {value}");
            try
            {
                string text;
                string animation = null;

                int firstQuote = value.IndexOf('"');
                int lastQuote = value.LastIndexOf('"');

                if (firstQuote != -1 && lastQuote > firstQuote)
                {
                    text = value.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                    int commaIndex = value.IndexOf(',', lastQuote);
                    if (commaIndex != -1)
                    {
                        animation = value.Substring(commaIndex + 1).Trim();
                    }
                }
                else
                {
                    // Fallback if quotes are missing
                    text = value;
                }

               var availableSayAnimations = VPetLLM.Instance.GetAvailableSayAnimations().Select(a => a.ToLower());
               if (string.IsNullOrEmpty(animation) || !availableSayAnimations.Contains(animation.ToLower()))
               {
                   if (!string.IsNullOrEmpty(animation))
                   {
                       Logger.Log($"Say animation '{animation}' not found. Using random say animation.");
                   }
                   mainWindow.Main.SayRnd(text, true);
               }
               else
               {
                   mainWindow.Main.Say(text, animation, true);
               }
                Utils.Logger.Log($"SayHandler called Say with text: \"{text}\" and animation: {animation ?? "none"}");
            }
            catch (Exception e)
            {
                Utils.Logger.Log($"Error in SayHandler: {e.Message}");
            }
        }

        public void Execute(int value, IMainWindow mainWindow) { }
        public void Execute(IMainWindow mainWindow) { }
    }
}