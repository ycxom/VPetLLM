using VPet_Simulator.Core;
using static VPet_Simulator.Core.GraphInfo;

namespace VPetLLM.Handlers.Actions
{
    /// <summary>说话动画怎么播。</summary>
    public enum SayPlanKind
    {
        /// <summary>没有可用动画，只显示气泡。</summary>
        BubbleOnly,
        /// <summary>交给宿主 Main.Say：宿主按当前心情选出的开始/循环段就是 <see cref="SayPlan.Mode"/> 这一套。</summary>
        HostSay,
        /// <summary>宿主选不到同一套（只能随机，或要用非当前心情）：由 SayHandler 自己串开始段→循环段。</summary>
        OwnLoop,
        /// <summary>这套动画只有 Single 段：显示气泡后直接播放。</summary>
        Single,
    }

    public readonly record struct SayPlan(SayPlanKind Kind, IGameSave.ModeType Mode);

    /// <summary>
    /// 决定说话动画用哪种心情的哪一套、由谁来播。
    ///
    /// 以前的做法是把全局 Core.Save.Mode 临时改成动画所在的心情，200ms 后再改回去，
    /// 好让 Main.Say 内部的 FindGraph 选中同一套。这有三个问题：
    ///   · 宿主播完开始段后，循环段和结束段按"当时"的 Mode 重新选——那时早已改回，前后不是一套；
    ///   · 宿主逻辑周期若落在这 200ms 里，会把它当成心情变化，播一段心情切换动画打断说话；
    ///   · Main.Say 先同步跑完所有 SayProcess 回调（TTS、表情包）才读 Mode，超过 200ms 就白改了。
    /// 现在完全不写 Mode：能让宿主按当前心情确定地选中同一套时交给宿主，否则自己串。
    ///
    /// 只依赖"某心情下某段动画是否存在"，不碰宿主对象，便于单测。
    /// </summary>
    public static class SayAnimationPlanner
    {
        private static readonly IGameSave.ModeType[] NonIllModes =
        {
            IGameSave.ModeType.Happy, IGameSave.ModeType.Nomal, IGameSave.ModeType.PoorCondition
        };

        /// <summary>
        /// 模型指定的心情和宠物实际心情冲突时以实际为准：生病就只用生病的动画，没生病也不演生病。
        /// 其余情况尊重模型的指定（比如饿着的时候被夸，演一下开心是合理的）。
        /// </summary>
        public static IGameSave.ModeType ResolveTargetMode(IGameSave.ModeType? requested, IGameSave.ModeType current)
        {
            if (!requested.HasValue)
                return current;
            if (current == IGameSave.ModeType.Ill || requested.Value == IGameSave.ModeType.Ill)
                return current;
            return requested.Value;
        }

        /// <summary>
        /// 复刻宿主 GraphCore.FindGraph 的确定性回退：本心情 → 下一档 → 上一档（不回到 Happy）；
        /// 生病不回退。返回 null 表示宿主会返回 null 或在剩余心情里随机挑——也就是"不确定"。
        /// </summary>
        public static IGameSave.ModeType? HostDeterministicMode(
            Func<AnimatType, IGameSave.ModeType, bool> has, AnimatType animat, IGameSave.ModeType mode)
        {
            if (has(animat, mode))
                return mode;
            if (mode == IGameSave.ModeType.Ill)
                return null;

            int down = (int)mode + 1;
            if (down < 3 && has(animat, (IGameSave.ModeType)down))
                return (IGameSave.ModeType)down;

            int up = (int)mode - 1;
            if (up >= 1 && has(animat, (IGameSave.ModeType)up))
                return (IGameSave.ModeType)up;

            return null;
        }

        /// <summary>
        /// 候选心情顺序：目标心情 → 相邻心情 → 其余非生病心情。生病只认生病。
        /// </summary>
        public static IEnumerable<IGameSave.ModeType> CandidateOrder(IGameSave.ModeType target)
        {
            if (target == IGameSave.ModeType.Ill)
            {
                yield return IGameSave.ModeType.Ill;
                yield break;
            }

            var seen = new HashSet<IGameSave.ModeType> { target };
            yield return target;

            int down = (int)target + 1;
            if (down < 3 && seen.Add((IGameSave.ModeType)down))
                yield return (IGameSave.ModeType)down;

            int up = (int)target - 1;
            if (up >= 0 && seen.Add((IGameSave.ModeType)up))
                yield return (IGameSave.ModeType)up;

            foreach (var mode in NonIllModes)
            {
                if (seen.Add(mode))
                    yield return mode;
            }
        }

        /// <param name="has">某段动画在某心情下是否存在（只看精确匹配该心情的动画）</param>
        /// <param name="target">要表现的心情（已经过 <see cref="ResolveTargetMode"/>）</param>
        /// <param name="current">宠物当前的心情 Core.Save.Mode，宿主按它选动画</param>
        public static SayPlan Plan(Func<AnimatType, IGameSave.ModeType, bool> has,
            IGameSave.ModeType target, IGameSave.ModeType current)
        {
            foreach (var mode in CandidateOrder(target))
            {
                if (has(AnimatType.A_Start, mode) && has(AnimatType.B_Loop, mode))
                {
                    // 宿主的开始段、循环段都会确定地落在这一套上，才放心交给宿主
                    bool hostPicksSame = target == current
                        && HostDeterministicMode(has, AnimatType.A_Start, current) == mode
                        && HostDeterministicMode(has, AnimatType.B_Loop, current) == mode;
                    return new SayPlan(hostPicksSame ? SayPlanKind.HostSay : SayPlanKind.OwnLoop, mode);
                }

                if (has(AnimatType.Single, mode))
                    return new SayPlan(SayPlanKind.Single, mode);
            }

            return new SayPlan(SayPlanKind.BubbleOnly, current);
        }
    }
}
