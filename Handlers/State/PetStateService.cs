using LinePutScript.Localization.WPF;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core.Services;

namespace VPetLLM.Handlers.State
{
    /// <summary>
    /// 数值状态修改的统一入口：限幅（可选）→ 写入存档 → 数值变化弹窗。
    /// Handler 不得直接修改 Core.Save 数值，统一走这里，保证限幅与显示一致且不遗漏。
    /// </summary>
    public static class PetStateService
    {
        private static bool LimitEnabled => VPetLLM.Instance?.Settings?.LimitStateChanges == true;

        /// <summary>
        /// 修改经验值。正值经 Exp 属性写入（保留升级结算）；
        /// 负值直写私有字段绕过升级结算，避免扣减触发等级重算。
        /// </summary>
        public static void ChangeExp(IMainWindow mainWindow, double delta)
        {
            double applied = 0;

            if (delta > 0)
            {
                applied = delta;
                if (LimitEnabled)
                {
                    int safeInput = delta > int.MaxValue ? int.MaxValue : (int)Math.Round(delta);
                    applied = StateChangeLimiter.LimitStateChange(safeInput, mainWindow.Core.Save.Exp);
                }
                mainWindow.Core.Save.Exp += applied;
            }
            else if (delta < 0)
            {
                applied = delta;
                VPetHostAdapter.TrySetExpRaw(mainWindow, mainWindow.Core.Save.Exp + delta);
            }

            ShowChange(mainWindow, "经验 ", applied);
        }

        /// <summary>
        /// 修改健康值。
        /// </summary>
        public static void ChangeHealth(IMainWindow mainWindow, int delta)
        {
            var applied = LimitEnabled
                ? StateChangeLimiter.LimitStateChange(delta, mainWindow.Core.Save.Health)
                : delta;

            mainWindow.Core.Save.Health += applied;
            ShowChange(mainWindow, "健康 ", applied);
        }

        /// <summary>
        /// 修改心情值（经 FeelingChange，保留宿主的心情联动逻辑）。
        /// </summary>
        public static void ChangeFeeling(IMainWindow mainWindow, int delta)
        {
            var applied = LimitEnabled
                ? StateChangeLimiter.LimitStateChange(delta, mainWindow.Core.Save.Feeling)
                : delta;

            mainWindow.Core.Save.FeelingChange(applied);
            ShowChange(mainWindow, "心情 ", applied);
        }

        private static void ShowChange(IMainWindow mainWindow, string label, double applied)
        {
            mainWindow.Main.LabelDisplayShowChangeNumber(
                label.Translate() + (applied > 0 ? "+" : "") + "{0:f0}", applied);
        }
    }
}
