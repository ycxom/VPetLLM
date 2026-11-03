namespace VPetLLM.Core.Services.MemoryGraph
{
    public enum NebulaNodeKind
    {
        Entity,
        Fact
    }

    public sealed class NebulaNode
    {
        public int Index { get; init; }
        public NebulaNodeKind Kind { get; init; }
        public string Label { get; init; } = "";

        /// <summary>实体节点：person / topic。事实节点为空。</summary>
        public string EntityType { get; init; } = "";

        public MemoryEntity? Entity { get; init; }
        public MemoryAtom? Atom { get; init; }

        /// <summary>实体：所连事实的分数之和；事实：衰减 × (0.5 + 重要度) × 置信度。</summary>
        public double Weight { get; init; }

        /// <summary>事实节点的衰减状态；实体节点恒为 Active。</summary>
        public MemoryAtomStatus Status { get; init; }

        /// <summary>用户或桌宠本身。几乎每条事实都连着它们，划社区时它们的边要降权。</summary>
        public bool IsAnchor { get; init; }

        /// <summary>实体节点：连着几条有效事实。</summary>
        public int FactCount { get; init; }

        public double Radius { get; set; }
        public int Community { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public List<int> Neighbors { get; } = new();
    }

    public readonly record struct NebulaEdge(int A, int B, double Weight);

    public sealed class NebulaGraph
    {
        public List<NebulaNode> Nodes { get; init; } = new();
        public List<NebulaEdge> Edges { get; init; } = new();
        public int CommunityCount { get; init; }
        public int EntityCount { get; init; }
        public int FactCount { get; init; }

        /// <summary>没过期的原子总数（含没画出来的）。</summary>
        public int LiveAtomCount { get; init; }

        public static NebulaGraph Empty { get; } = new();
    }

    public sealed class NebulaOptions
    {
        /// <summary>画出事实节点（实体—事实二部图）；关掉时只画实体，实体之间按共现连线。</summary>
        public bool IncludeFacts { get; init; } = true;
        public int MaxFacts { get; init; } = 360;
        public int MaxEntities { get; init; } = 160;
        public int Iterations { get; init; } = 300;
        public int Seed { get; init; } = 7;
    }

    /// <summary>
    /// 把原子和实体变成一张可以画的「关系星云」：选点、连边、划社区、算布局。
    /// 纯计算、不碰 UI，界面在后台线程调用它。过期原子不画；淡出中的原子画得暗一些。
    /// </summary>
    public static class MemoryGraphBuilder
    {
        /// <summary>连着用户/桌宠的边在划社区和布局时的折扣，不然所有东西都被吸成一团。</summary>
        public const double AnchorEdgeFactor = 0.2;

        public static NebulaGraph Build(MemoryGraphData data, DateTime nowUtc, NebulaOptions? options = null)
        {
            options ??= new NebulaOptions();

            var live = new List<(MemoryAtom Atom, double Score, MemoryAtomStatus Status)>();
            foreach (var atom in data.Atoms.OrderBy(a => a.Id))
            {
                var decay = MemoryAtomScoring.Decay(atom, nowUtc);
                var status = MemoryAtomScoring.StatusOf(decay);
                if (status == MemoryAtomStatus.Expired)
                    continue;
                live.Add((atom, decay * (0.5 + Math.Clamp(atom.Importance, 0, 1)) * Math.Clamp(atom.Confidence, 0.05, 1), status));
            }

            var entityWeight = new Dictionary<long, double>();
            var entityFacts = new Dictionary<long, int>();
            foreach (var (atom, score, _) in live)
            {
                foreach (var id in atom.EntityIds.Distinct())
                {
                    if (!data.Entities.ContainsKey(id))
                        continue;
                    entityWeight[id] = entityWeight.GetValueOrDefault(id) + score;
                    entityFacts[id] = entityFacts.GetValueOrDefault(id) + 1;
                }
            }

            static bool IsAnchorKey(string key) => key == MemoryEntityResolver.UserKey || key == MemoryEntityResolver.PetKey;

            var selectedEntities = entityWeight
                .OrderByDescending(kv => IsAnchorKey(data.Entities[kv.Key].Key))
                .ThenByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .Take(Math.Max(0, options.MaxEntities))
                .Select(kv => kv.Key)
                .ToList();
            if (selectedEntities.Count == 0)
                return new NebulaGraph { LiveAtomCount = live.Count };

            var nodes = new List<NebulaNode>();
            var entityIndex = new Dictionary<long, int>();
            var maxEntityWeight = selectedEntities.Max(id => entityWeight[id]);
            foreach (var id in selectedEntities)
            {
                var entity = data.Entities[id];
                var weight = entityWeight[id];
                entityIndex[id] = nodes.Count;
                nodes.Add(new NebulaNode
                {
                    Index = nodes.Count,
                    Kind = NebulaNodeKind.Entity,
                    Label = entity.Label,
                    EntityType = entity.Type,
                    Entity = entity,
                    Weight = weight,
                    Status = MemoryAtomStatus.Active,
                    IsAnchor = IsAnchorKey(entity.Key),
                    FactCount = entityFacts.GetValueOrDefault(id),
                    Radius = 6 + 14 * Math.Sqrt(weight / Math.Max(1e-9, maxEntityWeight))
                });
            }

            var edgeWeights = new Dictionary<(int, int), double>();
            void AddEdge(int a, int b, double w)
            {
                if (a == b)
                    return;
                var key = a < b ? (a, b) : (b, a);
                edgeWeights[key] = edgeWeights.GetValueOrDefault(key) + w;
            }

            var factCount = 0;
            if (options.IncludeFacts)
            {
                var facts = live
                    .Where(x => x.Atom.EntityIds.Any(entityIndex.ContainsKey))
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => x.Atom.Id)
                    .Take(Math.Max(0, options.MaxFacts))
                    .ToList();
                var maxScore = facts.Count > 0 ? facts.Max(f => f.Score) : 1;
                foreach (var (atom, score, status) in facts)
                {
                    var index = nodes.Count;
                    nodes.Add(new NebulaNode
                    {
                        Index = index,
                        Kind = NebulaNodeKind.Fact,
                        Label = atom.Content,
                        Atom = atom,
                        Weight = score,
                        Status = status,
                        Radius = 2.5 + 3.5 * score / Math.Max(1e-9, maxScore)
                    });
                    foreach (var id in atom.EntityIds.Distinct())
                    {
                        if (entityIndex.TryGetValue(id, out var e))
                            AddEdge(e, index, score);
                    }
                }
                factCount = facts.Count;

                // 人与人的关系不经过事实节点也要看得出来：同一条事实里出现的两个人直接连一条
                foreach (var (atom, score, _) in live)
                {
                    var people = atom.EntityIds.Distinct()
                        .Where(id => entityIndex.ContainsKey(id) && data.Entities[id].Type == MemoryEntityType.Person)
                        .Select(id => entityIndex[id])
                        .ToList();
                    for (var i = 0; i < people.Count; i++)
                        for (var j = i + 1; j < people.Count; j++)
                            AddEdge(people[i], people[j], score);
                }
            }
            else
            {
                foreach (var (atom, score, _) in live)
                {
                    var members = atom.EntityIds.Distinct()
                        .Where(entityIndex.ContainsKey)
                        .Select(id => entityIndex[id])
                        .ToList();
                    for (var i = 0; i < members.Count; i++)
                        for (var j = i + 1; j < members.Count; j++)
                            AddEdge(members[i], members[j], score);
                }
            }

            var edges = edgeWeights
                .OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2)
                .Select(kv => new NebulaEdge(kv.Key.Item1, kv.Key.Item2, kv.Value))
                .ToList();
            foreach (var edge in edges)
            {
                nodes[edge.A].Neighbors.Add(edge.B);
                nodes[edge.B].Neighbors.Add(edge.A);
            }

            // 划社区和布局都用降过权的边
            var damped = edges
                .Select(e => (e.A, e.B, e.Weight * (nodes[e.A].IsAnchor || nodes[e.B].IsAnchor ? AnchorEdgeFactor : 1.0)))
                .ToList();

            var communities = Louvain.Detect(nodes.Count, damped);
            for (var i = 0; i < nodes.Count; i++)
                nodes[i].Community = communities[i];

            ForceLayout.Run(nodes, damped, options.Iterations, options.Seed);

            return new NebulaGraph
            {
                Nodes = nodes,
                Edges = edges,
                CommunityCount = communities.Length == 0 ? 0 : communities.Max() + 1,
                EntityCount = selectedEntities.Count,
                FactCount = factCount,
                LiveAtomCount = live.Count
            };
        }
    }

    /// <summary>
    /// Fruchterman–Reingold 力导向布局，外加两种引力：拉向本社区质心（让同色的点聚成团）、
    /// 拉向原点（不连通的部分不会越飘越远）。斥力两两计算 O(n²)，点数上限几百，够用。
    /// 初始位置用固定种子，同样的图每次排出来一样。
    /// </summary>
    public static class ForceLayout
    {
        public static void Run(IReadOnlyList<NebulaNode> nodes, IReadOnlyList<(int A, int B, double Weight)> edges, int iterations, int seed)
        {
            var n = nodes.Count;
            if (n == 0)
                return;

            var random = new Random(seed);
            var x = new double[n];
            var y = new double[n];
            var dx = new double[n];
            var dy = new double[n];

            var communityCount = nodes.Max(node => node.Community) + 1;
            for (var i = 0; i < n; i++)
            {
                var c = nodes[i].Community;
                var angle = 2 * Math.PI * c / Math.Max(1, communityCount);
                var ring = communityCount <= 1 ? 0 : 350;
                x[i] = Math.Cos(angle) * ring + (random.NextDouble() - 0.5) * 160;
                y[i] = Math.Sin(angle) * ring + (random.NextDouble() - 0.5) * 160;
            }

            var maxWeight = edges.Count > 0 ? edges.Max(e => e.Weight) : 1;
            var k = Math.Clamp(Math.Sqrt(1_000_000.0 / n), 24, 160);
            var k2 = k * k;
            var cx = new double[communityCount];
            var cy = new double[communityCount];
            var counts = new int[communityCount];

            iterations = Math.Max(1, iterations);
            for (var iter = 0; iter < iterations; iter++)
            {
                Array.Clear(dx);
                Array.Clear(dy);

                for (var i = 0; i < n; i++)
                {
                    for (var j = i + 1; j < n; j++)
                    {
                        var ddx = x[i] - x[j];
                        var ddy = y[i] - y[j];
                        var d2 = ddx * ddx + ddy * ddy;
                        if (d2 < 0.01)
                        {
                            // 重合时按下标推开，保持确定性
                            ddx = 0.1 * (i - j);
                            ddy = 0.1;
                            d2 = ddx * ddx + ddy * ddy;
                        }
                        var f = k2 / d2;
                        dx[i] += ddx * f;
                        dy[i] += ddy * f;
                        dx[j] -= ddx * f;
                        dy[j] -= ddy * f;
                    }
                }

                foreach (var (a, b, w) in edges)
                {
                    var ddx = x[a] - x[b];
                    var ddy = y[a] - y[b];
                    var d = Math.Sqrt(ddx * ddx + ddy * ddy);
                    if (d < 1e-6)
                        continue;
                    var strength = Math.Clamp(w / maxWeight, 0.15, 1.0);
                    var f = d / k * strength;   // = d²/k 再除以 d 得到单位向量系数
                    dx[a] -= ddx * f;
                    dy[a] -= ddy * f;
                    dx[b] += ddx * f;
                    dy[b] += ddy * f;
                }

                Array.Clear(cx);
                Array.Clear(cy);
                Array.Clear(counts);
                for (var i = 0; i < n; i++)
                {
                    var c = nodes[i].Community;
                    cx[c] += x[i];
                    cy[c] += y[i];
                    counts[c]++;
                }
                for (var c = 0; c < communityCount; c++)
                {
                    if (counts[c] > 0)
                    {
                        cx[c] /= counts[c];
                        cy[c] /= counts[c];
                    }
                }

                for (var i = 0; i < n; i++)
                {
                    var c = nodes[i].Community;
                    var gx = x[i] - cx[c];
                    var gy = y[i] - cy[c];
                    var gd = Math.Sqrt(gx * gx + gy * gy);
                    dx[i] -= gx * 0.15 * gd / k;
                    dy[i] -= gy * 0.15 * gd / k;

                    var od = Math.Sqrt(x[i] * x[i] + y[i] * y[i]);
                    dx[i] -= x[i] * 0.05 * od / k;
                    dy[i] -= y[i] * 0.05 * od / k;
                }

                // 温度线性降到 1：前期大步走出初始布局，后期只做微调
                var temperature = 1 + 99.0 * (1 - (double)iter / iterations);
                for (var i = 0; i < n; i++)
                {
                    var len = Math.Sqrt(dx[i] * dx[i] + dy[i] * dy[i]);
                    if (len < 1e-9)
                        continue;
                    var step = Math.Min(len, temperature);
                    x[i] += dx[i] / len * step;
                    y[i] += dy[i] / len * step;
                }
            }

            var meanX = x.Average();
            var meanY = y.Average();
            for (var i = 0; i < n; i++)
            {
                nodes[i].X = x[i] - meanX;
                nodes[i].Y = y[i] - meanY;
            }
        }
    }
}
