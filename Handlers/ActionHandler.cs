using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers
{
    public class ActionHandler : IActionHandler
    {
        public string Keyword => "action";

        public void Execute(string actionName, IMainWindow mainWindow)
        {
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