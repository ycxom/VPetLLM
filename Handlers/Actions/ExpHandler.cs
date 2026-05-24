using LinePutScript.Localization.WPF;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers.State;

namespace VPetLLM.Handlers.Actions
{
    public class ExpHandler : IActionHandler
    {
        public string Keyword => "exp";
        public ActionType ActionType => ActionType.State;
        public ActionCategory Category => ActionCategory.StateBased;
        public string Description => PromptHelper.Get("Handler_Exp_Description", VPetLLM.Instance.Settings.PromptLanguage);

        public Task Execute(int value, IMainWindow mainWindow)
        {
            if (value > 0)
            {
                if (VPetLLM.Instance.Settings.LimitStateChanges)
                {
                    double currentValue = mainWindow.Core.Save.Exp;
                    value = StateChangeLimiter.LimitStateChange(value, currentValue);
                }

                mainWindow.Core.Save.Exp += value;
            }
            else if (value < 0)
            {
                double newExp = mainWindow.Core.Save.Exp + value;
                ExpSetHandler.SetExpDirect(mainWindow, newExp);
            }

            mainWindow.Main.LabelDisplayShowChangeNumber("经验 ".Translate() + (value > 0 ? "+" : "") + "{0:f0}", value);
            return Task.CompletedTask;
        }
        public Task Execute(string value, IMainWindow mainWindow)
        {
            if (!double.TryParse(value, out double doubleValue))
                return Task.CompletedTask;

            if (doubleValue > 0)
            {
                if (VPetLLM.Instance.Settings.LimitStateChanges)
                {
                    double currentValue = mainWindow.Core.Save.Exp;
                    int safeInput = doubleValue > int.MaxValue ? int.MaxValue : (int)Math.Round(doubleValue);
                    int limitedValue = StateChangeLimiter.LimitStateChange(safeInput, currentValue);
                    mainWindow.Core.Save.Exp += limitedValue;
                }
                else
                {
                    mainWindow.Core.Save.Exp += doubleValue;
                }
            }
            else if (doubleValue < 0)
            {
                double newExp = mainWindow.Core.Save.Exp + doubleValue;
                ExpSetHandler.SetExpDirect(mainWindow, newExp);
            }

            mainWindow.Main.LabelDisplayShowChangeNumber("经验 ".Translate() + (doubleValue > 0 ? "+" : "") + "{0:f0}", doubleValue);
            return Task.CompletedTask;
        }
        public Task Execute(IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }
        public int GetAnimationDuration(string animationName) => 0;
    }
}