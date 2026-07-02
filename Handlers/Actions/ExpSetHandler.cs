using LinePutScript.Localization.WPF;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core.Services;

namespace VPetLLM.Handlers.Actions
{
    public class ExpSetHandler : IActionHandler
    {
        public string Keyword => "exp_set";
        public ActionType ActionType => ActionType.State;
        public ActionCategory Category => ActionCategory.StateBased;
        public string Description => PromptHelper.Get("Handler_ExpSet_Description", VPetLLM.Instance.Settings.PromptLanguage);

        public Task Execute(int value, IMainWindow mainWindow)
        {
            SetExpDirect(mainWindow, value);
            mainWindow.Main.LabelDisplayShowChangeNumber("经验 -> {0:f0}".Translate(), value);
            return Task.CompletedTask;
        }

        public Task Execute(string value, IMainWindow mainWindow)
        {
            if (string.IsNullOrEmpty(value))
                return Task.CompletedTask;

            var parts = value.Split(':', 2);
            if (parts.Length != 2)
                return Task.CompletedTask;

            var cmd = parts[0].Trim().ToLower();
            var val = parts[1].Trim();

            switch (cmd)
            {
                case "level":
                    if (int.TryParse(val, out int level) && level >= 1)
                    {
                        SetLevelDirect(mainWindow, level);
                        mainWindow.Main.LabelDisplayShowChangeNumber("等级 -> {0}".Translate(), level);
                    }
                    break;
                case "levelmax":
                    if (int.TryParse(val, out int levelMax) && levelMax >= 0)
                    {
                        SetLevelMaxDirect(mainWindow, levelMax);
                        mainWindow.Main.LabelDisplayShowChangeNumber("等级上限 -> {0}".Translate(), levelMax);
                    }
                    break;
                case "exp":
                    if (double.TryParse(val, out double exp))
                    {
                        SetExpDirect(mainWindow, exp);
                        mainWindow.Main.LabelDisplayShowChangeNumber("经验 -> {0:f0}".Translate(), exp);
                    }
                    break;
            }

            return Task.CompletedTask;
        }

        public Task Execute(IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }

        public int GetAnimationDuration(string animationName) => 0;

        internal static void SetExpDirect(IMainWindow mainWindow, double value)
        {
            VPetHostAdapter.TrySetExpRaw(mainWindow, value);
        }

        internal static void SetLevelDirect(IMainWindow mainWindow, int level)
        {
            VPetHostAdapter.TrySetLevel(mainWindow, level);
        }

        internal static void SetLevelMaxDirect(IMainWindow mainWindow, int levelMax)
        {
            VPetHostAdapter.TrySetLevelMax(mainWindow, levelMax);
        }
    }
}