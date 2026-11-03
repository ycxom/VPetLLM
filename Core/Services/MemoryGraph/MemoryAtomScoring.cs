namespace VPetLLM.Core.Services.MemoryGraph
{
    /// <summary>
    /// 原子的寿命与衰减。
    ///
    /// 寿命 = 类型基础天数 × (0.5 + 重要度) × (1 + min(0.5, 0.1 × 复述次数))。
    /// 年龄从最近一次被提到算起，所以常被复述的事实一直新鲜，没人再提的慢慢淡出。
    ///   - 事件 7 天、偏好 60 天、身份事实 180 天、未分类 30 天：指数衰减，半衰期 = 寿命/2
    ///   - 关系 90 天：线性衰减到 0
    ///   - 计划：活到事件日之后 2 天左右，之后几乎归零（阶跃），过期的计划不该再被召回
    /// </summary>
    public static class MemoryAtomScoring
    {
        public const double ActiveThreshold = 0.3;
        public const double FadingThreshold = 0.1;

        public static double TtlDays(MemoryAtom atom)
        {
            var baseDays = atom.Type switch
            {
                MemoryAtomType.Episodic => 7.0,
                MemoryAtomType.Planned => 2.0 + DaysUntilEvent(atom),
                MemoryAtomType.Factual => 180.0,
                MemoryAtomType.Relational => 90.0,
                MemoryAtomType.Preference => 60.0,
                _ => 30.0
            };

            var importance = Math.Clamp(atom.Importance, 0.0, 1.0);
            var reinforcement = Math.Max(0, atom.Reinforcement);
            return baseDays * (0.5 + importance) * (1.0 + Math.Min(0.5, 0.1 * reinforcement));
        }

        /// <summary>
        /// 从被提到那天到事件日还有几天。用 LastSeenAt 而不是现在，结果才不随时间漂移；
        /// 没解析出日期的计划按一周算。
        /// </summary>
        private static double DaysUntilEvent(MemoryAtom atom)
        {
            if (atom.EventTime is not DateTime eventTime)
                return 5.0;

            var seen = atom.LastSeenAt.ToLocalTime().Date;
            return Math.Max(0.0, (eventTime.Date - seen).TotalDays);
        }

        /// <summary>0~1，1 = 刚被提到。</summary>
        public static double Decay(MemoryAtom atom, DateTime nowUtc)
        {
            var ttl = Math.Max(0.5, TtlDays(atom));
            var age = Math.Max(0.0, (nowUtc - atom.LastSeenAt).TotalDays);

            return atom.Type switch
            {
                MemoryAtomType.Planned => age <= ttl ? 1.0 : 0.05,
                MemoryAtomType.Relational => Math.Clamp(1.0 - age / ttl, 0.0, 1.0),
                _ => Math.Pow(0.5, age / (ttl / 2.0))
            };
        }

        public static MemoryAtomStatus StatusOf(double decay) => decay >= ActiveThreshold
            ? MemoryAtomStatus.Active
            : decay >= FadingThreshold ? MemoryAtomStatus.Fading : MemoryAtomStatus.Expired;

        public static MemoryAtomStatus StatusOf(MemoryAtom atom, DateTime nowUtc) => StatusOf(Decay(atom, nowUtc));
    }
}
