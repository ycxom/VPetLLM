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
        public string Description => "通过 'say' 指令让宠物说话，并可以指定情绪。格式: '[:talk(say(\"文本\",情绪))]'。文本必须用英文双引号包裹。";

        public void Execute(string value, IMainWindow mainWindow)
        {
            Utils.Logger.Log($"SayHandler executed with value: {value}");
            try
            {
                string text;
                string moodStr;

                int firstQuote = value.IndexOf('"');
                int lastQuote = value.LastIndexOf('"');

                if (firstQuote != -1 && lastQuote > firstQuote)
                {
                    text = value.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                    int commaIndex = value.IndexOf(',', lastQuote);
                    moodStr = (commaIndex != -1) ? value.Substring(commaIndex + 1).Trim() : "Nomal";
                }
                else
                {
                    // Fallback if quotes are missing
                    text = value;
                    moodStr = "Nomal";
                }

                if (Enum.TryParse<ModeType>(moodStr, true, out var parsedMode))
                {
                    mainWindow.Core.Save.Mode = parsedMode;
                    Utils.Logger.Log($"SayHandler set mode to: {parsedMode}");
                }
                
                mainWindow.Main.Say(text);
                Utils.Logger.Log($"SayHandler called Say with text: \"{text}\" and mode {mainWindow.Core.Save.Mode}");
            }
            catch(Exception e)
            {
                Utils.Logger.Log($"Error in SayHandler: {e.Message}");
            }
        }

        public void Execute(int value, IMainWindow mainWindow) { }
        public void Execute(IMainWindow mainWindow) { }
    }
}