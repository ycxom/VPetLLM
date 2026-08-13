using VPet_Simulator.Windows.Interface;
using static VPet_Simulator.Core.GraphInfo;

namespace VPetLLM.Core.Services
{
    /// <summary>
    /// Central policy for interactions that must not replace VPet host-controlled animations.
    /// </summary>
    internal static class VPetMovementPolicy
    {
        public static bool IsAnimationProtected(GraphType type)
        {
            return type is GraphType.Move
                or GraphType.Touch_Head
                or GraphType.Touch_Body
                or GraphType.Raised_Dynamic
                or GraphType.Raised_Static
                or GraphType.Switch_Up
                or GraphType.Switch_Down
                or GraphType.Switch_Thirsty
                or GraphType.Switch_Hunger
                or GraphType.StartUP
                or GraphType.Shutdown;
        }

        /// <summary>
        /// 宿主自己甩出来的一次性动画：图是 Work / Sleep，但宠物并没有真的进入对应状态。
        /// 典型来源是 VPet 生日蛋糕答错后延迟 5 秒、从后台线程播的「假装工作 / 假装睡觉」，
        /// 它只调 Main.Display，不改 WorkingState，所以现有的两道防线都拦不住：
        /// IsAnimationProtected 不含 Work/Sleep，IsPlayingImportantAnimation 认的是 State。
        ///
        /// 真正的工作 / 睡眠会话**不算**在内 —— 那种 State 会是 Work / Sleep，而且能持续几十分钟，
        /// 一并拦下会让 VPetLLM 整个工作期间都动不了。那条路仍旧交给
        /// AnimationStateChecker.IsPlayingImportantAnimation 的排队逻辑。
        ///
        /// 注意 GraphType 里没有 Study / Play，工作学习玩耍共用 GraphType.Work，
        /// 具体种类由 Work.WorkType 区分，这里不需要分。
        /// </summary>
        public static bool IsTransientHostAnimation(IMainWindow mainWindow)
        {
            var displayType = mainWindow?.Main?.DisplayType;
            if (displayType is null)
                return false;

            if (displayType.Type is not (GraphType.Work or GraphType.Sleep))
                return false;

            var state = mainWindow.Main.State;
            return state is not (VPet_Simulator.Core.Main.WorkingState.Work
                              or VPet_Simulator.Core.Main.WorkingState.Sleep);
        }

        public static double ClampWindowCoordinate(
            double target,
            double areaStart,
            double areaLength,
            double windowLength)
        {
            if (!double.IsFinite(target) || !double.IsFinite(areaStart)
                || !double.IsFinite(areaLength) || !double.IsFinite(windowLength))
            {
                return double.IsFinite(areaStart) ? areaStart : 0;
            }

            var usableLength = Math.Max(0, areaLength - Math.Max(0, windowLength));
            return Math.Clamp(target, areaStart, areaStart + usableLength);
        }
    }
}
