using LinePutScript.Localization.WPF;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils.Common;

namespace VPetLLM.Handlers
{
    public class HealthHandler : IActionHandler
    {
        public string Keyword => "health";
        public ActionType ActionType => ActionType.State;
        public ActionCategory Category => ActionCategory.StateBased;
        public string Description => PromptHelper.Get("Handler_Health_Description", VPetLLM.Instance.Settings.PromptLanguage);

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
                double currentValue = mainWindow.Core.Save.Health;
                value = StateChangeLimiter.LimitStateChange(value, currentValue);
            }

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