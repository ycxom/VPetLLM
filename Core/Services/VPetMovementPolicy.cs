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

        /// <summary>
        /// 「现在能不能把宿主正在放的动画换掉」——全项目唯一的判定入口。
        /// 返回 null 表示可以换；返回字符串表示不能换的原因（可直接进日志）。
        ///
        /// 之前这套判断在三个地方各抄了一份，而且已经开始漂移：
        ///   · AnimationSynchronizer.CanExecuteAnimation —— 调了 IsAnimationProtected，又手抄一遍 Touch/Switch/Raised；
        ///   · AnimationSynchronizer.GetBlockingReason  —— 手抄的那份漏了 Switch_Thirsty / Switch_Hunger；
        ///   · AnimationCoordinator.ExecuteFallbackAsync —— 完全手写，只认 5 种，
        ///     漏掉全部 Switch_* 和 StartUP / Shutdown，也没考虑 Say+语音 和宿主瞬时动画。
        ///     结果就是动画出错回退时，会在 VPet 自己的过渡/启动/关机动画中间强行 DisplayToNomal()。
        ///
        /// 现在三处都从这里取结论，清单只有这一份。
        /// </summary>
        public static string GetAnimationOverrideBlockReason(IMainWindow mainWindow)
        {
            var main = mainWindow?.Main;
            if (main is null)
                return "mainWindow is null";

            var displayType = main.DisplayType;
            if (displayType is not null)
            {
                if (IsAnimationProtected(displayType.Type))
                    return $"Protected host animation in progress ({displayType.Type})";

                // 说话动画 + 正在放语音：打断了口型就停了，音频还在响，对不上。
                if (displayType.Type == GraphType.Say && main.PlayingVoice)
                    return "Say animation with voice playing";
            }

            // 宿主自己甩出来的一次性 Work / Sleep 动画（见 IsTransientHostAnimation 的说明）。
            if (IsTransientHostAnimation(mainWindow))
                return $"Transient host animation in progress ({displayType?.Type})";

            return null;
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
