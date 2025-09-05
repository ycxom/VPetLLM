using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers
{
    public class ActionHandler : IActionHandler
    {
        public string Keyword => "action";
        public ActionType ActionType => ActionType.Body;
        public string Description => "通过 'action' 指令播放动画。可用动作: 'touchhead', 'touchbody', 'sleep', 'idel'。例如 '[:body(action(touchhead))]'。";

        public void Execute(string actionName, IMainWindow mainWindow)
        {
            Logger.Log($"ActionHandler executed with value: {actionName}");
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