using System.IO;
using VPetLLM.Core.Data.Database;
using VPetLLM.Core.Data.Managers;
using VPetLLM.Core.Services.Embedding;
using VPetLLM.Core.Services.MemoryGraph;
using VPetLLM.Utils.Common;

// 分段总结遗留问题的修复 + 记忆原子/星云图。
//
//   N. 空片（模型什么都没总结出来、或全是重复）落一行标记，重启后检查点不退回去重新总结；
//   O. 合并一直失败时指数退避，不再每来一片新分段就白发一次合并请求；
//   P. 编辑器能列出已合并/空片行，默认只列生效的；
//   Q. 语义去重：说法不同的同一件事只存一遍，但否定、数字不同的不算重复；
//   R. 原子分类与衰减；
//   S. 实体标记解析与实体归一（用户/桌宠固定成同一个点）；
//   T. 分段总结落库后拆成原子，重复的事实加固而不是再建一条；
//   U. Louvain 能把两个团分开；
//   V. 星云布局同种子可复现。

/// <summary>
/// 假向量：含「草莓」的都映射到同一个方向（模拟「说法不同、意思相同」），
/// 其余每个不同文本各占一个正交方向，彼此余弦为 0。
/// </summary>
internal sealed class FakeEmbeddingProvider : IEmbeddingProvider
{
    private const int Dimensions = 512;
    private readonly Dictionary<string, int> _slots = new(StringComparer.Ordinal);

    public string ModelKey => "fake-embedding";

    internal int Calls { get; private set; }

    public Task<IReadOnlyList<float[]?>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        Calls++;
        var result = new List<float[]?>(texts.Count);
        foreach (var text in texts)
        {
            var vector = new float[Dimensions];
            if (text.Contains("草莓", StringComparison.Ordinal))
            {
                vector[0] = 1;
            }
            else
            {
                if (!_slots.TryGetValue(text, out var slot))
                    _slots[text] = slot = 1 + _slots.Count % (Dimensions - 1);
                vector[slot] = 1;
            }
            result.Add(vector);
        }
        return Task.FromResult<IReadOnlyList<float[]?>>(result);
    }
}

static partial class Program
{
    // N：最后一片是空片时，旧逻辑不落行，重启后检查点退回上一片末尾，这片会被重新总结
    static void Test_EmptySegmentSurvivesRestart()
    {
        Console.WriteLine("N. 空片落标记行");
        using var harness = new Harness(contextTokens: 4000);
        harness.Core.SummaryFactory = index => index == 0 ? "- 第一片里聊到的事实" : "";

        var history = MakeMessages(120, tokensEach: 200);
        harness.Run(history);

        var calls = harness.Core.Calls.Count;
        Check("至少两片", calls >= 2, $"{calls} 片");
        Check("只有第一片生效", harness.ActiveRowCount == 1, $"active={harness.ActiveRowCount}");
        Check("空片也落了行", harness.SummaryRowCount == calls, $"rows={harness.SummaryRowCount} calls={calls}");
        Check("检查点做完", harness.Checkpoint == 119, $"checkpoint={harness.Checkpoint}");

        harness.Reopen();
        Check("重启后检查点不退回", harness.Checkpoint == 119, $"checkpoint={harness.Checkpoint}");
        Check("重启后注入的总结只有第一片", harness.Manager.LatestSummary == "- 第一片里聊到的事实",
            harness.Manager.LatestSummary ?? "null");

        harness.Run(history);
        Check("空片不会被重新总结", harness.Core.Calls.Count == calls, $"{calls} → {harness.Core.Calls.Count}");
    }

    // O：合并失败后按 2、4、8、16 片退避
    static void Test_ConsolidationFailureBacksOff()
    {
        Console.WriteLine("O. 合并失败退避");
        using var harness = new Harness(contextTokens: 4000);
        harness.Settings.MaxSummaryTokens = 200;
        harness.Core.FailConsolidation = true;
        harness.Core.SummaryFactory = index => $"- 片{index}：" + new string('b', 320);

        harness.Run(MakeMessages(400, tokensEach: 200));

        var segments = harness.Core.Calls.Count(c => !c.IsConsolidation);
        var attempts = harness.Core.Calls.Count(c => c.IsConsolidation);
        var summary = harness.Manager.LatestSummary ?? "";
        Check("分段足够多", segments >= 12, $"{segments} 片");
        Check("确实尝试过不止一次", attempts >= 2, $"{attempts} 次");
        // 超预算从第 3 片开始；不退避的话之后每片都会试一次
        Check("退避后尝试次数远少于分段数", attempts * 2 < segments - 2, $"{attempts} 次 / {segments} 片");
        Check("检查点照常做完", harness.Checkpoint == 399, $"checkpoint={harness.Checkpoint}");
        Check("每一片都还在", Enumerable.Range(0, segments).All(i => summary.Contains($"- 片{i}：")));
    }

    // P：已合并的行默认不列，勾上「显示已合并/空片」才列
    static void Test_EditorCanListInactiveRows()
    {
        Console.WriteLine("P. 编辑器列出已合并行");
        using var harness = new Harness(contextTokens: 4000);
        harness.Settings.MaxSummaryTokens = 200;
        harness.Core.SummaryFactory = index => $"- 片{index}：" + new string('b', 320);

        harness.Run(MakeMessages(120, tokensEach: 200));

        var manager = harness.Manager;
        Check("确实有被合并的行", harness.ActiveRowCount < harness.SummaryRowCount,
            $"active={harness.ActiveRowCount} rows={harness.SummaryRowCount}");
        Check("默认只数生效的", manager.GetSummaryCount() == harness.ActiveRowCount,
            $"{manager.GetSummaryCount()} vs {harness.ActiveRowCount}");
        Check("勾选后全数", manager.GetSummaryCount(includeInactive: true) == harness.SummaryRowCount,
            $"{manager.GetSummaryCount(includeInactive: true)} vs {harness.SummaryRowCount}");

        var all = manager.GetSummariesPage(0, 1000, includeInactive: true);
        var active = manager.GetSummariesPage(0, 1000);
        Check("勾选后的页里有已合并行", all.Count == harness.SummaryRowCount && all.Any(r => r.Superseded),
            $"count={all.Count}");
        Check("默认页里没有已合并行", active.All(r => !r.Superseded) && active.Count == harness.ActiveRowCount);
        Check("默认页的行都在生效集合里", active.All(r => manager.ActiveSummaryIds.Contains(r.Id)));
    }

    // Q：字面去重认不出换了说法的同一件事
    static void Test_SemanticDuplicatesAreDropped()
    {
        Console.WriteLine("Q. 语义去重");
        using var harness = new Harness(contextTokens: 4000);
        var provider = new FakeEmbeddingProvider();
        harness.Manager.EmbeddingService = new EmbeddingService(provider,
            new EmbeddingStore(Path.Combine(harness.TempDirectory, "embeddings.db")));
        harness.Core.SummaryFactory = index => index switch
        {
            0 => "- 用户最喜欢的水果是草莓\n- 片0独有甲",
            1 => "- 主人最爱吃的果子是草莓呀\n- 片1独有乙",
            2 => "- 主人不喜欢草莓\n- 片2独有丙",
            _ => $"- 片{index}独有编号{index}"
        };

        harness.Run(MakeMessages(120, tokensEach: 200));

        var calls = harness.Core.Calls.Count;
        var summary = harness.Manager.LatestSummary ?? "";
        Check("至少三片", calls >= 3, $"{calls} 片");
        Check("确实调用了向量", provider.Calls > 0);
        Check("换了说法的同一件事只存一遍", !summary.Contains("果子"), summary);
        Check("原来那条还在", Occurrences(summary, "最喜欢的水果是草莓") == 1);
        Check("否定的不算重复", summary.Contains("主人不喜欢草莓"));
        Check("各片独有的事实都在", new[] { "片0独有甲", "片1独有乙", "片2独有丙" }.All(summary.Contains));

        var data = harness.Manager.LoadMemoryGraphData();
        var original = data?.Atoms.FirstOrDefault(a => a.Content.Contains("最喜欢的水果是草莓"));
        Check("语义重复加固了原来那条原子", original is { Reinforcement: >= 1 },
            $"reinforcement={original?.Reinforcement}");
        Check("没有为重复的说法另建原子", data is not null && !data.Atoms.Any(a => a.Content.Contains("果子")));
    }

    // R：分类决定寿命，寿命决定衰减
    static void Test_ClassifierAndDecay()
    {
        Console.WriteLine("R. 原子分类与衰减");
        var nowLocal = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Local);
        Check("计划", MemoryAtomClassifier.Classify("主人打算下周三去体检", nowLocal).Type == MemoryAtomType.Planned);
        Check("偏好", MemoryAtomClassifier.Classify("主人不吃香菜", nowLocal).Type == MemoryAtomType.Preference);
        Check("关系", MemoryAtomClassifier.Classify("小林是主人的同事", nowLocal).Type == MemoryAtomType.Relational);
        Check("身份事实", MemoryAtomClassifier.Classify("主人的生日是5月3日", nowLocal).Type == MemoryAtomType.Factual);
        Check("事件", MemoryAtomClassifier.Classify("昨天和主人去了海边", nowLocal).Type == MemoryAtomType.Episodic);

        var plannedDate = MemoryAtomClassifier.Classify("主人打算下周三去体检", nowLocal).EventTime;
        Check("下周三解析成日期", plannedDate == new DateTime(2026, 11, 11), $"{plannedDate:yyyy-MM-dd}");

        var now = DateTime.UtcNow;
        MemoryAtom Atom(MemoryAtomType type, double ageDays, DateTime? eventTime = null) => new()
        {
            Type = type,
            Importance = 0.5,
            Confidence = 0.8,
            LastSeenAt = now.AddDays(-ageDays),
            CreatedAt = now.AddDays(-ageDays),
            EventTime = eventTime
        };

        Check("刚提到的事件是活跃的", MemoryAtomScoring.StatusOf(Atom(MemoryAtomType.Episodic, 0), now) == MemoryAtomStatus.Active);
        Check("两周前的事件已过期", MemoryAtomScoring.StatusOf(Atom(MemoryAtomType.Episodic, 14), now) == MemoryAtomStatus.Expired);
        Check("一个月前的身份事实还活跃", MemoryAtomScoring.StatusOf(Atom(MemoryAtomType.Factual, 30), now) == MemoryAtomStatus.Active);

        var seenLocal = now.AddDays(-10).ToLocalTime().Date;
        Check("过了日子的计划过期",
            MemoryAtomScoring.StatusOf(Atom(MemoryAtomType.Planned, 10, seenLocal.AddDays(3)), now) == MemoryAtomStatus.Expired);
        Check("还没到日子的计划活跃",
            MemoryAtomScoring.StatusOf(Atom(MemoryAtomType.Planned, 1, now.ToLocalTime().Date.AddDays(3)), now) == MemoryAtomStatus.Active);

        var reinforced = Atom(MemoryAtomType.Episodic, 9);
        reinforced.Reinforcement = 5;
        Check("复述过的事实衰减得慢",
            MemoryAtomScoring.Decay(reinforced, now) > MemoryAtomScoring.Decay(Atom(MemoryAtomType.Episodic, 9), now));
    }

    // S：标记只用来建图，落库文本里不留括号；用户换着叫法也是同一个点
    static void Test_MarkersAndEntityResolution()
    {
        Console.WriteLine("S. 实体标记与归一");
        const string raw = "- [[@小林]]喜欢[[登山]]";
        Check("去掉标记", MemoryFactParser.StripMarkers(raw) == "- 小林喜欢登山", MemoryFactParser.StripMarkers(raw));

        var markers = MemoryFactParser.IndexMarkers(raw);
        var lineMarkers = markers.TryGetValue(FactSimilarity.Normalize("- 小林喜欢登山"), out var list) ? list : null;
        Check("按去标记后的行找回标记", lineMarkers is { Count: 2 } && lineMarkers[0].IsPerson && !lineMarkers[1].IsPerson);

        var resolver = new MemoryEntityResolver("小明", "喵", Array.Empty<MemoryEntity>());
        var keys = resolver.Resolve("小林喜欢登山", lineMarkers).Select(e => e.Key).ToList();
        Check("人物与话题都认出来",
            keys.Contains(MemoryEntityResolver.KeyOf(MemoryEntityType.Person, "小林"))
            && keys.Contains(MemoryEntityResolver.KeyOf(MemoryEntityType.Topic, "登山")), string.Join(",", keys));

        Check("用户名字归到用户", resolver.Resolve("小明今天很开心", null).Any(e => e.Key == MemoryEntityResolver.UserKey));
        Check("「主人」标记归到用户",
            resolver.Resolve("主人很开心", new[] { new FactMarker("主人", true) }).Count(e => e.Key == MemoryEntityResolver.UserKey) == 1);
        Check("单字宠物名不做子串匹配", !resolver.Resolve("窗外有猫喵喵叫", null).Any(e => e.Key == MemoryEntityResolver.PetKey));
        Check("拉丁词按整词匹配", !resolver.Resolve("the username field", null).Any(e => e.Key == MemoryEntityResolver.UserKey));
        Check("书名号里的词算话题",
            resolver.Resolve("主人在玩《星露谷物语》", null).Any(e => e.Key == MemoryEntityResolver.KeyOf(MemoryEntityType.Topic, "星露谷物语")));
    }

    // T：分段总结 → 原子 + 实体
    static void Test_SegmentsBecomeAtoms()
    {
        Console.WriteLine("T. 分段拆成原子");
        using var harness = new Harness(contextTokens: 4000);
        harness.Core.SummaryFactory = index => index switch
        {
            0 => "- [[@小林]]是[[@主人]]的同事，周末常一起去[[登山]]\n- 主人最喜欢的水果是草莓",
            1 => "- 主人最喜欢的水果是草莓\n- 主人买了新键盘「HHKB」",
            _ => $"- 片{index}独有编号{index}"
        };

        harness.Run(MakeMessages(120, tokensEach: 200));

        var first = harness.Core.Calls.First(c => !c.IsConsolidation);
        var summary = harness.Manager.LatestSummary ?? "";
        Check("分段请求带上了标记说明", first.SystemPrompt.Contains(MemoryFactParser.MarkerInstruction));
        Check("注入的总结里没有标记", !summary.Contains("[[") && summary.Contains("小林是主人的同事"), summary);

        var data = harness.Manager.LoadMemoryGraphData();
        Check("拿到了图数据", data is not null);
        if (data is null)
            return;

        Check("原子文本里没有标记", data.Atoms.All(a => !a.Content.Contains("[[")));
        var strawberry = data.Atoms.Where(a => a.Content.Contains("草莓")).ToList();
        Check("重复的事实只有一条原子", strawberry.Count == 1, $"{strawberry.Count} 条");
        Check("重复的事实被加固", strawberry.FirstOrDefault()?.Reinforcement >= 1);
        Check("偏好分类", strawberry.FirstOrDefault()?.Type == MemoryAtomType.Preference);

        MemoryEntity? Entity(string key) => data.Entities.Values.FirstOrDefault(e => e.Key == key);
        var lin = Entity(MemoryEntityResolver.KeyOf(MemoryEntityType.Person, "小林"));
        var user = Entity(MemoryEntityResolver.UserKey);
        Check("小林是人物", lin is not null);
        Check("用户只有一个点", user is not null
            && data.Entities.Values.Count(e => e.Type == MemoryEntityType.Person && e.Label is "主人" or "用户") <= 1);
        Check("书名号/引号里的词成了话题", Entity(MemoryEntityResolver.KeyOf(MemoryEntityType.Topic, "HHKB")) is not null);

        var relation = data.Atoms.FirstOrDefault(a => a.Content.Contains("同事"));
        Check("同一条事实同时连着小林和用户",
            relation is not null && lin is not null && user is not null
            && relation.EntityIds.Contains(lin.Id) && relation.EntityIds.Contains(user.Id));

        var graph = MemoryGraphBuilder.Build(data, DateTime.UtcNow);
        var linNode = graph.Nodes.FirstOrDefault(n => n.Entity?.Id == lin?.Id);
        var userNode = graph.Nodes.FirstOrDefault(n => n.Entity?.Id == user?.Id);
        Check("星云里小林和用户直接相连",
            linNode is not null && userNode is not null && linNode.Neighbors.Contains(userNode.Index));
        Check("用户是锚点", userNode?.IsAnchor == true);
    }

    // U：两个团中间一根细线，应该分成两个社区
    static void Test_LouvainSplitsCliques()
    {
        Console.WriteLine("U. Louvain 社区");
        var edges = new List<(int A, int B, double Weight)>();
        for (var group = 0; group < 2; group++)
        {
            for (var i = 0; i < 5; i++)
            {
                for (var j = i + 1; j < 5; j++)
                    edges.Add((group * 5 + i, group * 5 + j, 1));
            }
        }
        edges.Add((4, 5, 1));

        var membership = Louvain.Detect(10, edges);
        Check("第一个团同一社区", membership.Take(5).Distinct().Count() == 1, string.Join(",", membership));
        Check("第二个团同一社区", membership.Skip(5).Distinct().Count() == 1, string.Join(",", membership));
        Check("两个团不同社区", membership[0] != membership[9]);

        var modularity = Louvain.Modularity(10, edges, membership);
        var single = Louvain.Modularity(10, edges, new int[10]);
        Check("模块度明显高于不分社区", modularity > 0.3 && modularity > single + 0.3, $"{modularity:F3} vs {single:F3}");

        Check("空图不炸", Louvain.Detect(0, Array.Empty<(int, int, double)>()).Length == 0);
    }

    // V：同一份数据、同一种子，每次摆出来一样；换种子会变
    static void Test_NebulaLayoutIsDeterministic()
    {
        Console.WriteLine("V. 星云布局");
        var now = DateTime.UtcNow;
        var entities = new Dictionary<long, MemoryEntity>();
        var atoms = new List<MemoryAtom>();
        long nextEntity = 1, nextAtom = 1;

        MemoryEntity AddEntity(string key, string type, string label)
        {
            var e = new MemoryEntity { Id = nextEntity++, Key = key, Type = type, Label = label };
            entities[e.Id] = e;
            return e;
        }

        void AddAtom(string content, double ageDays, MemoryAtomType type, params MemoryEntity[] linked)
        {
            var atom = new MemoryAtom
            {
                Id = nextAtom++,
                Content = content,
                FactKey = FactSimilarity.Normalize(content),
                Type = type,
                Importance = 0.5,
                Confidence = 0.8,
                CreatedAt = now.AddDays(-ageDays),
                LastSeenAt = now.AddDays(-ageDays)
            };
            atom.EntityIds.AddRange(linked.Select(e => e.Id));
            atoms.Add(atom);
        }

        var user = AddEntity(MemoryEntityResolver.UserKey, MemoryEntityType.Person, "主人");
        var groups = new[] { "工作", "游戏", "美食" };
        foreach (var name in groups)
        {
            var hub = AddEntity(MemoryEntityResolver.KeyOf(MemoryEntityType.Topic, name), MemoryEntityType.Topic, name);
            var leaves = Enumerable.Range(0, 4)
                .Select(i => AddEntity(MemoryEntityResolver.KeyOf(MemoryEntityType.Topic, $"{name}{i}"), MemoryEntityType.Topic, $"{name}{i}"))
                .ToList();
            foreach (var leaf in leaves)
            {
                AddAtom($"{name}-{leaf.Label}-a", 1, MemoryAtomType.Factual, hub, leaf);
                AddAtom($"{name}-{leaf.Label}-b", 2, MemoryAtomType.Preference, hub, leaf, user);
            }
        }
        AddAtom("两个月前的一件小事", 60, MemoryAtomType.Episodic, user);

        var data = new MemoryGraphData { Atoms = atoms, Entities = entities };
        var a = MemoryGraphBuilder.Build(data, now, new NebulaOptions { Seed = 7 });
        var b = MemoryGraphBuilder.Build(data, now, new NebulaOptions { Seed = 7 });
        var c = MemoryGraphBuilder.Build(data, now, new NebulaOptions { Seed = 8 });

        Check("节点数一致", a.Nodes.Count == b.Nodes.Count && a.Nodes.Count > 0, $"{a.Nodes.Count}");
        Check("同种子坐标逐个相同",
            a.Nodes.Zip(b.Nodes).All(p => p.First.X == p.Second.X && p.First.Y == p.Second.Y));
        Check("换种子坐标会变", a.Nodes.Zip(c.Nodes).Any(p => Math.Abs(p.First.X - p.Second.X) > 1e-6));
        Check("没有 NaN", a.Nodes.All(n => double.IsFinite(n.X) && double.IsFinite(n.Y) && double.IsFinite(n.Radius)));
        Check("过期的事实不上图", !a.Nodes.Any(n => n.Atom?.Content == "两个月前的一件小事"));
        Check("分出了不止一个社区", a.CommunityCount >= 2, $"{a.CommunityCount}");
        Check("同组的叶子和中心同社区",
            groups.All(name =>
            {
                var hub = a.Nodes.First(n => n.Entity?.Label == name);
                return a.Nodes.Where(n => n.Entity?.Label.StartsWith(name) == true).All(n => n.Community == hub.Community);
            }));

        var entitiesOnly = MemoryGraphBuilder.Build(data, now, new NebulaOptions { IncludeFacts = false });
        Check("关掉事实只剩实体", entitiesOnly.Nodes.All(n => n.Kind == NebulaNodeKind.Entity) && entitiesOnly.Edges.Count > 0);
        Check("空数据不炸", MemoryGraphBuilder.Build(new MemoryGraphData(), now).Nodes.Count == 0);
    }
}
