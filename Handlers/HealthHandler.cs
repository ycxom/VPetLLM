using LinePutScript.Localization.WPF;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers
{
   public class HealthHandler : IActionHandler
   {
       public string Keyword => "health";
       public ActionType ActionType => ActionType.State;
       public string Description => Utils.PromptHelper.Get("Handler_Health_Description", VPetLLM.Instance.Settings.PromptLanguage);

      public Task Execute(int value, IMainWindow mainWindow)
      {
          mainWindow.Core.Save.Health += value;
          mainWindow.Main.LabelDisplayShowChangeNumber("健康 ".Translate() + (value > 0 ? "+" : "") + "{0:f0}", value);
          return Task.CompletedTask;
      }
      public Task Execute(string value, IMainWindow mainWindow)
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