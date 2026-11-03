namespace VPetLLM.Core.Services.MemoryGraph
{
    /// <summary>
    /// 加权无向图的 Louvain 社区划分（局部移动 + 聚合，反复到模块度不再上升）。
    ///
    /// 结果是确定的：节点按下标顺序访问，增益相同时留在原社区、否则取编号小的社区。
    /// 同样的输入永远得到同样的颜色分组，星云图不会每次打开都换一种配色。
    /// 社区编号按规模从大到小重排，0 是最大的社区。
    /// </summary>
    public static class Louvain
    {
        private const int MaxPasses = 32;
        private const int MaxLevels = 16;

        /// <param name="nodeCount">节点数。</param>
        /// <param name="edges">(a, b, 权重)，a == b 的自环、权重 ≤ 0 的边被忽略，重复边权重相加。</param>
        public static int[] Detect(int nodeCount, IEnumerable<(int A, int B, double Weight)> edges)
        {
            var membership = new int[nodeCount];
            for (var i = 0; i < nodeCount; i++)
                membership[i] = i;
            if (nodeCount == 0)
                return membership;

            // 当前层的图：邻接表（含聚合后产生的自环）
            var adjacency = new List<Dictionary<int, double>>(nodeCount);
            for (var i = 0; i < nodeCount; i++)
                adjacency.Add(new Dictionary<int, double>());
            foreach (var (a, b, w) in edges)
            {
                if (a == b || w <= 0 || a < 0 || b < 0 || a >= nodeCount || b >= nodeCount)
                    continue;
                adjacency[a][b] = adjacency[a].GetValueOrDefault(b) + w;
                adjacency[b][a] = adjacency[b].GetValueOrDefault(a) + w;
            }

            for (var level = 0; level < MaxLevels; level++)
            {
                var n = adjacency.Count;
                var community = new int[n];
                var degree = new double[n];
                var total = new double[n];
                double m2 = 0;
                for (var i = 0; i < n; i++)
                {
                    community[i] = i;
                    foreach (var (_, w) in adjacency[i])
                        degree[i] += w;
                    total[i] = degree[i];
                    m2 += degree[i];
                }
                if (m2 <= 0)
                    break;

                var moved = false;
                var toCommunity = new Dictionary<int, double>();
                for (var pass = 0; pass < MaxPasses; pass++)
                {
                    var changed = false;
                    for (var i = 0; i < n; i++)
                    {
                        var current = community[i];
                        toCommunity.Clear();
                        foreach (var (j, w) in adjacency[i])
                        {
                            if (j == i)
                                continue;
                            toCommunity[community[j]] = toCommunity.GetValueOrDefault(community[j]) + w;
                        }

                        total[current] -= degree[i];
                        var best = current;
                        var bestGain = toCommunity.GetValueOrDefault(current) - total[current] * degree[i] / m2;
                        foreach (var (c, weightTo) in toCommunity.OrderBy(kv => kv.Key))
                        {
                            var gain = weightTo - total[c] * degree[i] / m2;
                            if (gain > bestGain + 1e-12)
                            {
                                best = c;
                                bestGain = gain;
                            }
                        }
                        total[best] += degree[i];

                        if (best != current)
                        {
                            community[i] = best;
                            changed = true;
                            moved = true;
                        }
                    }
                    if (!changed)
                        break;
                }

                if (!moved)
                    break;

                // 重新编号并把原始节点映射到新社区
                var renumber = new Dictionary<int, int>();
                foreach (var c in community)
                {
                    if (!renumber.ContainsKey(c))
                        renumber[c] = renumber.Count;
                }
                for (var v = 0; v < nodeCount; v++)
                    membership[v] = renumber[community[membership[v]]];

                // 聚合：每个社区缩成一个点，社区内部的边变成自环
                var next = new List<Dictionary<int, double>>(renumber.Count);
                for (var c = 0; c < renumber.Count; c++)
                    next.Add(new Dictionary<int, double>());
                for (var i = 0; i < n; i++)
                {
                    var ci = renumber[community[i]];
                    foreach (var (j, w) in adjacency[i])
                    {
                        var cj = renumber[community[j]];
                        next[ci][cj] = next[ci].GetValueOrDefault(cj) + w;
                    }
                }
                adjacency = next;
                if (adjacency.Count == n)
                    break;
            }

            // 按规模从大到小编号；同样大小按最小成员下标
            var order = membership
                .Select((c, v) => (c, v))
                .GroupBy(x => x.c)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Min(x => x.v))
                .Select((g, rank) => (g.Key, rank))
                .ToDictionary(x => x.Key, x => x.rank);
            for (var v = 0; v < nodeCount; v++)
                membership[v] = order[membership[v]];
            return membership;
        }

        /// <summary>模块度 Q，测试用来确认划分确实比「全部一个社区」好。</summary>
        public static double Modularity(int nodeCount, IReadOnlyList<(int A, int B, double Weight)> edges, int[] membership)
        {
            var degree = new double[nodeCount];
            double m = 0;
            var inside = new Dictionary<int, double>();
            foreach (var (a, b, w) in edges)
            {
                if (a == b || w <= 0)
                    continue;
                degree[a] += w;
                degree[b] += w;
                m += w;
                if (membership[a] == membership[b])
                    inside[membership[a]] = inside.GetValueOrDefault(membership[a]) + w;
            }
            if (m <= 0)
                return 0;

            var totals = new Dictionary<int, double>();
            for (var v = 0; v < nodeCount; v++)
                totals[membership[v]] = totals.GetValueOrDefault(membership[v]) + degree[v];

            double q = 0;
            foreach (var (c, tot) in totals)
                q += inside.GetValueOrDefault(c) / m - Math.Pow(tot / (2 * m), 2);
            return q;
        }
    }
}
