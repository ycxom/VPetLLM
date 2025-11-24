using LinePutScript.Localization.WPF;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers
{
   public class HappyHandler : IActionHandler
   {
       public string Keyword => "happy";
       public ActionType ActionType => ActionType.State;
       public ActionCategory Category => ActionCategory.StateBased;
       public string Description => Utils.PromptHelper.Get("Handler_Happy_Description", VPetLLM.Instance.Settings.PromptLanguage);

      public Task Execute(int value, IMainWindow mainWindow)
      {
          // 检查是否为默认插件
          if (VPetLLM.Instance?.IsVPetLLMDefaultPlugin() != true)
          {
              return Task.CompletedTask;
          }

          // 如果启用了状态限制，应用限制逻辑
          if (VPetLLM.Instance.Settings.LimitStateChanges)
          {
              double currentValue = mainWindow.Core.Save.Feeling;
              value = StateChangeLimiter.LimitStateChange(value, currentValue);
          }
          
          mainWindow.Core.Save.FeelingChange(value);
          mainWindow.Main.LabelDisplayShowChangeNumber("心情 ".Translate() + (value > 0 ? "+" : "") + "{0:f0}", value);
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