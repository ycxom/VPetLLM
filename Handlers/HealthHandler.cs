using LinePutScript.Localization.WPF;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers
{
    public class HealthHandler : IActionHandler
    {
        public string Keyword => "health";

        public void Execute(int value, IMainWindow mainWindow)
        {
            mainWindow.Core.Save.Health += value;
            mainWindow.Main.LabelDisplayShowChangeNumber("健康 ".Translate() + (value > 0 ? "+" : "") + "{0:f0}", value);
        }
        public void Execute(string value, IMainWindow mainWindow) { }
    }
}