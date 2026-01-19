using LinePutScript.Localization.WPF;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils.Common;

namespace VPetLLM.Handlers
{
    public class ExpHandler : IActionHandler
    {
        public string Keyword => "exp";
        public ActionType ActionType => ActionType.State;
        public ActionCategory Category => ActionCategory.StateBased;
        public string Description => PromptHelper.Get("Handler_Exp_Description", VPetLLM.Instance.Settings.PromptLanguage);

        public Task Execute(int value, IMainWindow mainWindow)
        {
            // 如果启用了状态限制，应用限制逻辑
            if (VPetLLM.Instance.Settings.LimitStateChanges)
            {
                double currentValue = mainWindow.Core.Save.Exp;
                value = StateChangeLimiter.LimitStateChange(value, currentValue);
            }

            mainWindow.Core.Save.Exp += value;
            mainWindow.Main.LabelDisplayShowChangeNumber("经验 ".Translate() + (value > 0 ? "+" : "") + "{0:f0}", value);
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