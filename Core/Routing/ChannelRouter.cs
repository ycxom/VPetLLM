namespace VPetLLM.Core.Routing
{
    /// <summary>
    /// 跨类型的渠道选路：决定一次请求依次尝试哪些渠道。
    ///
    /// 规则（与 NewAPI 一致）：
    ///   1. 只看启用的渠道；需要看图时只看开了视觉的渠道。
    ///   2. 按用途（渠道模式）过滤：先找专门匹配这个用途的，没有就退回"无限制"的，再没有就不过滤。
    ///   3. 优先级高的一层先用，整层都失败才轮到下一层。
    ///   4. 同一层内按权重随机排序：权重全为 0 时均匀随机；有正有零时，0 的排在这一层最后。
    ///   5. 没开"失败转移"时只返回排在第一个的渠道。
    ///
    /// 纯函数，不读写任何全局状态，方便单独验证。
    /// </summary>
    public static class ChannelRouter
    {
        private static readonly Random SharedRandom = new();
        private static readonly object RandomLock = new();

        public static List<Setting.ChannelNodeBase> Plan(
            Setting settings,
            string? purpose,
            bool requireVision = false,
            Func<double>? nextDouble = null)
        {
            var candidates = settings.EnumerateChannels().Where(c => c.Enabled).ToList();
            if (requireVision)
                candidates = candidates.Where(c => c.EnableVision).ToList();

            candidates = FilterByPurpose(candidates, purpose);
            if (candidates.Count == 0)
                return candidates;

            var random = nextDouble ?? NextSharedDouble;
            var ordered = new List<Setting.ChannelNodeBase>(candidates.Count);
            foreach (var tier in candidates.GroupBy(c => c.Priority).OrderByDescending(g => g.Key))
                ordered.AddRange(WeightedShuffle(tier.ToList(), random));

            if (!settings.EnableFallback && ordered.Count > 1)
                ordered.RemoveRange(1, ordered.Count - 1);

            return ordered;
        }

        private static List<Setting.ChannelNodeBase> FilterByPurpose(List<Setting.ChannelNodeBase> nodes, string? purpose)
        {
            if (string.IsNullOrEmpty(purpose) || nodes.Count == 0)
                return nodes;

            var matching = nodes.Where(n => Setting.IsNodeMatchingPurpose(n.Mode, n.PluginModeId, purpose)).ToList();
            if (matching.Count > 0) return matching;

            var unrestricted = nodes.Where(n => n.Mode == Setting.ChannelMode.Unrestricted).ToList();
            return unrestricted.Count > 0 ? unrestricted : nodes;
        }

        /// <summary>
        /// 加权无放回抽样（Efraimidis–Spirakis）：key = -ln(u) / w，key 小的先出。
        /// </summary>
        private static IEnumerable<Setting.ChannelNodeBase> WeightedShuffle(
            List<Setting.ChannelNodeBase> tier, Func<double> random)
        {
            if (tier.Count <= 1) return tier;

            var anyWeighted = tier.Any(c => c.Weight > 0);
            return tier
                .Select(c =>
                {
                    // u ∈ (0, 1]，避免 ln(0)
                    var u = 1.0 - random();
                    // 有正有零时，权重 0 的整体排到本层最后（彼此之间仍随机）
                    var trailing = anyWeighted && c.Weight <= 0;
                    var key = anyWeighted && !trailing ? -Math.Log(u) / c.Weight : u;
                    return (node: c, trailing, key);
                })
                .OrderBy(x => x.trailing)
                .ThenBy(x => x.key)
                .Select(x => x.node)
                .ToList();
        }

        private static double NextSharedDouble()
        {
            lock (RandomLock) return SharedRandom.NextDouble();
        }
    }
}
