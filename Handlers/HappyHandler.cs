using LinePutScript.Localization.WPF;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers
{
   public class HappyHandler : IActionHandler
   {
       public string Keyword => "happy";
       public ActionType ActionType => ActionType.State;
       public string Description => Utils.PromptHelper.Get("Handler_Happy_Description", VPetLLM.Instance.Settings.PromptLanguage);

       public void Execute(int value, IMainWindow mainWindow)
       {
           mainWindow.Core.Save.FeelingChange(value);
           mainWindow.Main.LabelDisplayShowChangeNumber("心情 ".Translate() + (value > 0 ? "+" : "") + "{0:f0}", value);
       }
       public void Execute(string value, IMainWindow mainWindow) { }
       public void Execute(IMainWindow mainWindow) { }
   }
}