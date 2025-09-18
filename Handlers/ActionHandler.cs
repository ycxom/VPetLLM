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

        public async Task Execute(string actionName, IMainWindow mainWindow)
        {
            Utils.Logger.Log($"ActionHandler executed with value: {actionName}");
            var action = string.IsNullOrEmpty(actionName) ? "idel" : actionName;

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                switch (action.ToLower())
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
                await Task.Delay(1000);
            });
        }

        public Task Execute(int value, IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }
        public Task Execute(IMainWindow mainWindow)
        {
            return Execute("idel", mainWindow);
        }
        public int GetAnimationDuration(string animationName) => 1000;
    }
}