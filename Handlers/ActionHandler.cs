using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;
using static VPet_Simulator.Core.GraphInfo;

namespace VPetLLM.Handlers
{
    public class ActionHandler : IActionHandler
    {
        public string Keyword => "action";
        public ActionType ActionType => ActionType.Body;
       public string Description => PromptHelper.Get("Handler_Action_Description", VPetLLM.Instance.Settings.PromptLanguage);

        public void Execute(string actionName, IMainWindow mainWindow)
        {
            Utils.Logger.Log($"ActionHandler executed with value: {actionName}");
            var action = string.IsNullOrEmpty(actionName) ? "idel" : actionName.ToLower();
            switch (action)
            {
                case "touchhead":
                    mainWindow.Main.DisplayTouchHead();
                    break;
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
        }

        public void Execute(int value, IMainWindow mainWindow)
        {
            // Not used for this handler
        }
        public void Execute(IMainWindow mainWindow)
        {
            Execute("idel", mainWindow);
        }
    }
}