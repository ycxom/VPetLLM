using LinePutScript.Localization.WPF;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers
{
   public class ExpHandler : IActionHandler
   {
       public string Keyword => "exp";
       public ActionType ActionType => ActionType.State;
       public string Description => Utils.PromptHelper.Get("Handler_Exp_Description", VPetLLM.Instance.Settings.PromptLanguage);

       public void Execute(int value, IMainWindow mainWindow)
       {
           mainWindow.Core.Save.Exp += value;
           mainWindow.Main.LabelDisplayShowChangeNumber("经验 ".Translate() + (value > 0 ? "+" : "") + "{0:f0}", value);
       }
       public void Execute(string value, IMainWindow mainWindow) { }
       public void Execute(IMainWindow mainWindow) { }
   }
}