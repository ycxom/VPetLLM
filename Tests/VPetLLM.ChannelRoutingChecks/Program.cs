using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VPetLLM;
using VPetLLM.Core.Abstractions.Base;
using VPetLLM.Core.Data.Database;
using VPetLLM.Core.Data.Managers;
using VPetLLM.Core.Providers.Chat;
using VPetLLM.Core.Routing;
using VPetLLM.Infrastructure.Exceptions;
using VPetLLM.Services;
using VPetLLM.Configuration;

// 插件接口一律写全别名（VPetLLM.Core 下有同名旧接口残留，裸类型名会撞上）
using ChannelModeDefinition = VPetLLM.Core.Abstractions.Interfaces.Plugin.ChannelModeDefinition;
using IChannelModeProvider = VPetLLM.Core.Abstractions.Interfaces.Plugin.IChannelModeProvider;
using PluginManager = VPetLLM.Utils.Plugin.PluginManager;

// 统一渠道路由的回归检查。
//
// 这次改动把"选一个提供商 → 在它的节点里轮询 → 降级列表（其实从没接上）"
// 换成了跨类型的"优先级分层 + 层内按权重 + 失败转移"。这里把几条最容易被改坏的规则钉死：
//   1. 选路：停用/用途/视觉过滤、优先级分层、权重分配、关掉失败转移时只给一个；
//   2. 迁移：旧的"当前提供商 + 负载均衡 + 降级列表"折算成优先级后，运行时行为不变；
//   3. OpenAI 两种协议：地址拼接、Responses 多段文本、图片部件格式；
//   4. 端到端：本地起两个假服务，第一个 500、第二个正常 —— 总结和测试走的是真实请求代码；
//   5. 识图的临时 core：不读不写主上下文，也不动全局的 KeepContext。

static class Program
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  [PASS] {name}"); }
        else { _fail++; Console.WriteLine($"  [FAIL] {name}  {detail}"); }
    }

    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        var dir = Path.Combine(Path.GetTempPath(), $"vpet-channel-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Test_RouterTiersAndFilters(dir);
            Test_PurposeRouting(dir);
            Test_PurposeSets(dir);
            Test_PurposeRegistry();
            Test_RouterWeights(dir);
            Test_Migration(dir);
            Test_OpenAIProtocols();
            Test_EditorHelpers(dir);
            Test_FreeProbePrompt();
            Test_EndToEndFailover(dir).GetAwaiter().GetResult();
            Test_VisionLeavesContextAlone(dir).GetAwaiter().GetResult();
            Test_OllamaDropsUnsupportedThinking(dir).GetAwaiter().GetResult();
            Test_OllamaContextLength(dir).GetAwaiter().GetResult();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"===== 通过 {_pass} / 失败 {_fail} =====");
        return _fail == 0 ? 0 : 1;
    }

    // ─────────────────────────────────────────────────────────────
    static Setting NewSettings(string dir)
    {
        var s = new Setting(Path.Combine(dir, Guid.NewGuid().ToString("N")));
        s.OpenAI.OpenAINodes.Clear();
        s.Gemini.GeminiNodes.Clear();
        s.Ollama.OllamaNodes.Clear();
        s.LMStudio.LMStudioNodes.Clear();
        s.Free.FreeNodes.Clear();
        s.EnableFallback = true;
        return s;
    }

    static T Add<T>(Setting s, T node, int priority, int weight = 0, bool enabled = true) where T : Setting.ChannelNodeBase
    {
        node.Priority = priority;
        node.Weight = weight;
        node.Enabled = enabled;
        s.AddChannel(node);
        return node;
    }

    // ═════════════════════════════════════════════════════════════
    static void Test_RouterTiersAndFilters(string dir)
    {
        Console.WriteLine("[1] 选路：分层与过滤");
        var s = NewSettings(dir);
        var high = Add(s, new Setting.OpenAINodeSetting { Name = "high" }, 10);
        var low = Add(s, new Setting.GeminiNodeSetting { Name = "low" }, 1);
        var off = Add(s, new Setting.OllamaNodeSetting { Name = "off" }, 99, enabled: false);
        var summarizer = Add(s, new Setting.LMStudioNodeSetting { Name = "sum", Mode = Setting.ChannelMode.CompressionOnly }, 5);

        var plan = ChannelRouter.Plan(s, "Chat");
        Check("停用的渠道不参与，哪怕优先级最高", !plan.Contains(off));
        Check("优先级高的排前面（跨类型）", plan.Count == 2 && plan[0] == high && plan[1] == low,
              string.Join(",", plan.Select(p => p.Name)));
        Check("仅总结的渠道不接聊天", !plan.Contains(summarizer));

        // "无限制"匹配任何用途（沿用旧语义）："仅总结"只是把渠道挡在聊天之外，不独占总结
        var compress = ChannelRouter.Plan(s, "Compression");
        Check("总结请求：仅总结的渠道参与，与无限制渠道一起按优先级排",
              compress.SequenceEqual(new Setting.ChannelNodeBase[] { high, summarizer, low }),
              string.Join(",", compress.Select(p => p.Name)));

        s.EnableFallback = false;
        Check("关掉失败转移：只给排第一的那个", ChannelRouter.Plan(s, "Chat").SequenceEqual(new[] { high }));
        s.EnableFallback = true;

        low.EnableVision = true;
        var vision = ChannelRouter.Plan(s, "Chat", requireVision: true);
        Check("看图只走开了视觉的渠道", vision.Count == 1 && vision[0] == low);

        high.Enabled = low.Enabled = false;
        Check("没有匹配用途的渠道时退回全部可用的", ChannelRouter.Plan(s, "Chat").SequenceEqual(new Setting.ChannelNodeBase[] { summarizer }));
    }

    // ═════════════════════════════════════════════════════════════
    static void Test_PurposeRouting(string dir)
    {
        Console.WriteLine("[1.5] 选路：Memory / Diary 用途");
        var s = NewSettings(dir);
        var chat = Add(s, new Setting.OpenAINodeSetting { Name = "chat" }, 10);
        var compressor = Add(s, new Setting.LMStudioNodeSetting { Name = "comp", Mode = Setting.ChannelMode.CompressionOnly }, 5);
        var diary = Add(s, new Setting.GeminiNodeSetting { Name = "diary", Mode = Setting.ChannelMode.DiaryOnly }, 5);
        var memory = Add(s, new Setting.OllamaNodeSetting { Name = "mem", Mode = Setting.ChannelMode.MemoryOnly }, 5);

        var d = ChannelRouter.Plan(s, "Diary");
        Check("日记走「无限制」+「仅日记」（无限制匹配一切用途）",
              d.SequenceEqual(new Setting.ChannelNodeBase[] { chat, diary }), string.Join(",", d.Select(p => p.Name)));
        Check("「仅总结」「仅记忆」不接日记", !d.Contains(compressor) && !d.Contains(memory));

        var m = ChannelRouter.Plan(s, "Memory");
        Check("记忆整理走「无限制」+「仅记忆」",
              m.SequenceEqual(new Setting.ChannelNodeBase[] { chat, memory }), string.Join(",", m.Select(p => p.Name)));
        Check("「仅总结」「仅日记」不接记忆", !m.Contains(compressor) && !m.Contains(diary));

        var c = ChannelRouter.Plan(s, "Compression");
        Check("压缩走「无限制」+「仅总结」",
              c.SequenceEqual(new Setting.ChannelNodeBase[] { chat, compressor }), string.Join(",", c.Select(p => p.Name)));
        Check("「仅记忆」「仅日记」不接压缩", !c.Contains(memory) && !c.Contains(diary));
        var o = ChannelRouter.Plan(s, "Overflow");
        Check("溢出总结与压缩同路（仅总结渠道接 Overflow）", o.SequenceEqual(c));

        // 回退链：没有「仅日记」渠道时，日记回退到「无限制」，再退回全部
        diary.Enabled = memory.Enabled = compressor.Enabled = false;
        var fb = ChannelRouter.Plan(s, "Diary");
        Check("没有专用渠道时日记回退到「无限制」", fb.SequenceEqual(new Setting.ChannelNodeBase[] { chat }),
              string.Join(",", fb.Select(p => p.Name)));

        // 连「无限制」都没有：ChatOnly / CompressionOnly 也兜底接日记（不过滤）
        var chatOnly = Add(s, new Setting.OpenAINodeSetting { Name = "chatOnly", Mode = Setting.ChannelMode.ChatOnly }, 3);
        var compOnly = Add(s, new Setting.LMStudioNodeSetting { Name = "compOnly", Mode = Setting.ChannelMode.CompressionOnly }, 2);
        chat.Enabled = diary.Enabled = false;
        var all = ChannelRouter.Plan(s, "Diary");
        Check("连「无限制」都没有时日记不过滤（兜底全部）",
              all.SequenceEqual(new Setting.ChannelNodeBase[] { chatOnly, compOnly }),
              string.Join(",", all.Select(p => p.Name)));

        diary.Enabled = true;
        Check("专用渠道恢复后日记不再全量回退",
              ChannelRouter.Plan(s, "Diary").SequenceEqual(new Setting.ChannelNodeBase[] { diary }));

        // 枚举值不许变：已存库的渠道按数字反序列化，插队换值=静默换用途
        Check("枚举数值稳定（Unrestricted=0 ChatOnly=1 CompressionOnly=2 MemoryOnly=3 DiaryOnly=4 PluginDefined=100）",
              (int)Setting.ChannelMode.Unrestricted == 0 && (int)Setting.ChannelMode.ChatOnly == 1 &&
              (int)Setting.ChannelMode.CompressionOnly == 2 && (int)Setting.ChannelMode.MemoryOnly == 3 &&
              (int)Setting.ChannelMode.DiaryOnly == 4 && (int)Setting.ChannelMode.PluginDefined == 100);
    }

    // ═════════════════════════════════════════════════════════════
    static void Test_PurposeSets(string dir)
    {
        Console.WriteLine("[1.6] 用途：复选保存（ApplyPurposes）、多选路由、旧数据推导");
        var s = NewSettings(dir);

        // ApplyPurposes 同步矩阵：Purposes 是路由真值，Mode/PluginModeId 只为旧宿主回滚保留
        var n = Add(s, new Setting.OpenAINodeSetting { Name = "n" }, 1);
        Setting.ApplyPurposes(n, Array.Empty<string>());
        Check("空列表 = 无限制（Mode 影子同步回 Unrestricted）",
              n.Purposes is { Count: 0 } && n.Mode == Setting.ChannelMode.Unrestricted && n.PluginModeId is null,
              $"{n.Purposes?.Count}/{n.Mode}/{n.PluginModeId}");

        foreach (var (key, mode) in new[]
        {
            ("Chat", Setting.ChannelMode.ChatOnly),
            ("Compression", Setting.ChannelMode.CompressionOnly),
            ("Memory", Setting.ChannelMode.MemoryOnly),
            ("Diary", Setting.ChannelMode.DiaryOnly)
        })
        {
            Setting.ApplyPurposes(n, new[] { key });
            Check($"单选 {key} → 影子 Mode 同步成 {mode}",
                  n.Purposes is { Count: 1 } && n.Purposes[0] == key && n.Mode == mode && n.PluginModeId is null,
                  $"{string.Join("+", n.Purposes ?? new List<string>())}/{n.Mode}/{n.PluginModeId}");
        }

        Setting.ApplyPurposes(n, new[] { "Weather" });
        Check("单选插件用途 → PluginDefined + PluginModeId 记键（旧宿主靠它继续路由）",
              n.Purposes![0] == "Weather" && n.Mode == Setting.ChannelMode.PluginDefined && n.PluginModeId == "Weather",
              $"{n.Mode}/{n.PluginModeId}");

        Setting.ApplyPurposes(n, new[] { "Chat", "Diary" });
        Check("多选 → PluginDefined 且 PluginModeId 置空（旧宿主推导成无限制，降级为兜底可接受）",
              n.Purposes is { Count: 2 } && n.Mode == Setting.ChannelMode.PluginDefined && n.PluginModeId is null,
              $"{string.Join("+", n.Purposes ?? new List<string>())}/{n.Mode}/{n.PluginModeId}");

        Setting.ApplyPurposes(n, new[] { "Diary", "Diary", "Chat", "Diary" });
        Check("重复勾选去重且保序", n.Purposes is { Count: 2 } && n.Purposes.SequenceEqual(new[] { "Diary", "Chat" }),
              string.Join("+", n.Purposes ?? new List<string>()));

        // 多选路由：multi 接 Compression 和 Diary，不接 Chat（旁边放无限制渠道兜底，避免触发"全部兜底"分支）
        var chatNode = Add(s, new Setting.OpenAINodeSetting { Name = "chatNode" }, 1);
        var multi = Add(s, new Setting.OpenAINodeSetting { Name = "multi" }, 1);
        Setting.ApplyPurposes(multi, new[] { "Compression", "Diary" });
        Check("多选渠道接 Diary", ChannelRouter.Plan(s, "Diary").Contains(multi));
        Check("多选渠道接 Compression", ChannelRouter.Plan(s, "Compression").Contains(multi));
        Check("多选渠道不接 Chat", !ChannelRouter.Plan(s, "Chat").Contains(multi));

        Setting.ApplyPurposes(multi, Array.Empty<string>());
        Check("显式空列表 = 无限制，匹配一切用途",
              ChannelRouter.Plan(s, "Chat").Contains(multi) && ChannelRouter.Plan(s, "Overflow").Contains(multi));

        // Overflow 是 Compression 的别名
        var other = Add(s, new Setting.OpenAINodeSetting { Name = "other" }, 1);
        Setting.ApplyPurposes(other, new[] { "Diary" });
        Setting.ApplyPurposes(multi, new[] { "Compression" });
        Check("Overflow 别名命中 Purposes=[Compression]", ChannelRouter.Plan(s, "Overflow").Contains(multi));
        Check("Overflow 别名不会命中别的用途", !ChannelRouter.Plan(s, "Overflow").Contains(other));

        // 旧数据（Purposes=null）从 Mode 推导 —— 不做落盘迁移（构造期绝不 Save）
        var legacy = new Setting.GeminiNodeSetting { Name = "legacy", Mode = Setting.ChannelMode.DiaryOnly };
        Check("Purposes=null 时从 Mode 推导（DiaryOnly→[Diary]）",
              Setting.GetNodePurposes(legacy).SequenceEqual(new[] { "Diary" }));
        Check("推导不落盘（Purposes 仍是 null，构造期绝不 Save）", legacy.Purposes is null);
        Check("Unrestricted 推导成空列表", Setting.PurposesFromMode(Setting.ChannelMode.Unrestricted, null).Count == 0);
        var legacyPlugin = new Setting.OllamaNodeSetting { Name = "lp", Mode = Setting.ChannelMode.PluginDefined, PluginModeId = "Weather" };
        Check("旧插件用途也能推导（PluginDefined+Weather→[Weather]）",
              Setting.GetNodePurposes(legacyPlugin).SequenceEqual(new[] { "Weather" }));
        Check("PluginDefined 但键为空 → 空列表=无限制",
              Setting.GetNodePurposes(new Setting.OllamaNodeSetting { Name = "x", Mode = Setting.ChannelMode.PluginDefined }).Count == 0);

        // 副本与类型转换都要带上 Purposes
        var withPurposes = Add(s, new Setting.OpenAINodeSetting { Name = "wp" }, 1);
        Setting.ApplyPurposes(withPurposes, new[] { "Diary", "Memory" });
        var clone = (Setting.OpenAINodeSetting)Setting.CloneChannel(withPurposes);
        Check("CloneChannel 保留 Purposes",
              clone.Purposes is { Count: 2 } && clone.Purposes[0] == "Diary" && clone.Purposes[1] == "Memory",
              string.Join("+", clone.Purposes ?? new List<string>()));
        var cloneLegacy = (Setting.OpenAINodeSetting)Setting.CloneChannel(new Setting.OpenAINodeSetting { Name = "np", Mode = Setting.ChannelMode.DiaryOnly });
        Check("旧数据副本 Purposes 仍为 null（推导不受副本影响）",
              cloneLegacy.Purposes is null && Setting.GetNodePurposes(cloneLegacy).Count == 1);
        var converted = Setting.ConvertChannel(withPurposes, Setting.ChannelKind.Gemini);
        Check("ConvertChannel 换类型也带 Purposes",
              converted.Purposes is { Count: 2 } && converted.Purposes.SequenceEqual(new[] { "Diary", "Memory" }),
              string.Join("+", converted.Purposes ?? new List<string>()));
    }

    // ═════════════════════════════════════════════════════════════
    static void Test_PurposeRegistry()
    {
        Console.WriteLine("[1.7] 用途注册表：插件停用/卸载后用途消失");
        var diary = new ChannelModeDefinition { ModeId = "Diary", DisplayName = "日记", Description = "d" };
        var good = new FakeModePlugin("good", new[] { diary, new ChannelModeDefinition { ModeId = "Chat", DisplayName = "冒牌聊天" }, new ChannelModeDefinition { ModeId = "  " } }, throwInGet: false);
        var bad = new FakeModePlugin("bad", new[] { new ChannelModeDefinition { ModeId = "Weather", DisplayName = "天气" } }, throwInGet: true);
        Func<string, string, string> T = (_, fallback) => fallback;

        try
        {
            PluginManager.Plugins.Add(good);
            good.Enabled = true;
            var purposes = ChannelPurposeRegistry.GetPurposes(T);
            Check("内置三项在列且非插件来源",
                  purposes.Where(p => !p.IsFromPlugin).Select(p => p.Key).SequenceEqual(new[] { "Chat", "Compression", "Memory" }),
                  string.Join(",", purposes.Select(p => p.Key)));
            Check("启用插件的用途在列（Diary，带插件给的显示名）",
                  purposes.Any(p => p.Key == "Diary" && p.IsFromPlugin && p.DisplayName == "日记"),
                  string.Join(",", purposes.Select(p => p.Key)));
            Check("插件注册内置键被忽略（Chat 只有一个且是内置）",
                  purposes.Count(p => p.Key == "Chat") == 1 && !purposes.First(p => p.Key == "Chat").IsFromPlugin);
            Check("空白 ModeId 被忽略", purposes.All(p => !string.IsNullOrWhiteSpace(p.Key)));

            good.Enabled = false;
            Check("插件停用 → 它的用途从列表消失", !ChannelPurposeRegistry.GetPurposes(T).Any(p => p.Key == "Diary"));
            Check("停用后 DescribeKey 落回原始键（已存配置里的键不丢、不误显示）",
                  ChannelPurposeRegistry.DescribeKey("Diary", T) == "Diary");

            good.Enabled = true;
            PluginManager.Plugins.Add(bad);
            bad.Enabled = true;
            var mixed = ChannelPurposeRegistry.GetPurposes(T);
            Check("一个插件 GetCustomModes 抛异常被吞掉，其它插件的用途不受影响",
                  mixed.Any(p => p.Key == "Diary") && !mixed.Any(p => p.Key == "Weather"),
                  string.Join(",", mixed.Select(p => p.Key)));

            Check("DescribeKey：插件用途给插件显示名", ChannelPurposeRegistry.DescribeKey("Diary", T) == "日记");
            Check("DescribeKey：内置走本地化词条", ChannelPurposeRegistry.DescribeKey("Chat", T) == "聊天");
            Check("DescribeKey：未知键落回原始键", ChannelPurposeRegistry.DescribeKey("Ghost", T) == "Ghost");
        }
        finally
        {
            PluginManager.Plugins.Remove(good);
            PluginManager.Plugins.Remove(bad);
        }
        Check("清理后注册表回到内置三项", ChannelPurposeRegistry.GetPurposes(T).Count == 3);
    }

    /// <summary>假的 IChannelModeProvider 插件：只给注册表用，不实现任何真实行为。</summary>
    sealed class FakeModePlugin(string name, ChannelModeDefinition[] modes, bool throwInGet) : IChannelModeProvider
    {
        public string Name { get; } = name;
        public string Author { get; } = "test";
        public string Description { get; } = "";
        public string Parameters { get; } = "";
        public string Examples { get; } = "";
        public bool Enabled { get; set; }
        public string FilePath { get; set; } = "";
        public void Initialize(global::VPetLLM.VPetLLM plugin) { }
        public void Unload() { }
        public IReadOnlyList<ChannelModeDefinition> GetCustomModes()
        {
            if (throwInGet) throw new InvalidOperationException("插件坏了");
            return modes;
        }
    }

    static void Test_RouterWeights(string dir)
    {
        Console.WriteLine("[2] 选路：同层按权重");
        var s = NewSettings(dir);
        var heavy = Add(s, new Setting.OpenAINodeSetting { Name = "heavy" }, 0, weight: 3);
        var light = Add(s, new Setting.OpenAINodeSetting { Name = "light" }, 0, weight: 1);
        var zero = Add(s, new Setting.OpenAINodeSetting { Name = "zero" }, 0, weight: 0);

        var rng = new Random(42);
        int heavyFirst = 0, zeroLast = 0;
        const int N = 8000;
        for (int i = 0; i < N; i++)
        {
            var plan = ChannelRouter.Plan(s, "Chat", nextDouble: rng.NextDouble);
            if (plan[0] == heavy) heavyFirst++;
            if (plan[^1] == zero) zeroLast++;
        }
        var ratio = heavyFirst / (double)N;
        Check("权重 3:1 → 约 75% 先选重的", Math.Abs(ratio - 0.75) < 0.03, $"实际 {ratio:P1}");
        Check("有正有零时，权重 0 的总在本层最后", zeroLast == N, $"{zeroLast}/{N}");

        heavy.Weight = light.Weight = 0;
        int zFirst = 0;
        for (int i = 0; i < N; i++)
            if (ChannelRouter.Plan(s, "Chat", nextDouble: rng.NextDouble)[0] == zero) zFirst++;
        Check("权重全为 0 时均匀随机", Math.Abs(zFirst / (double)N - 1 / 3.0) < 0.03, $"实际 {zFirst / (double)N:P1}");
    }

    // ═════════════════════════════════════════════════════════════
    static void Test_Migration(string dir)
    {
        Console.WriteLine("[3] 迁移：旧选路规则折算成优先级");
        var unify = typeof(Setting).GetMethod("UnifyChannelsIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // 场景 A：主提供商 OpenAI（负载均衡），降级列表里启用了 Gemini、停用了 Ollama
        {
            var s = NewSettings(dir);
            s.ChannelsUnified = false;
            s.EnableFallback = true;
            s.Provider = Setting.LLMType.OpenAI;
            s.OpenAI.EnableLoadBalancing = true;
            var oa1 = new Setting.OpenAINodeSetting { Name = "oa1", Url = "https://a/v1" };
            var oa2 = new Setting.OpenAINodeSetting { Name = "oa2", Url = "https://b/v1/responses" };
            s.OpenAI.OpenAINodes.AddRange(new[] { oa1, oa2 });
            var gm = new Setting.GeminiNodeSetting { Name = "gm" };
            s.Gemini.GeminiNodes.Add(gm);
            var ol = new Setting.OllamaNodeSetting { Name = "ol" };
            s.Ollama.OllamaNodes.Add(ol);
            s.FallbackProviders = new List<Setting.ProviderFallbackConfig>
            {
                new() { ProviderType = "Gemini", IsEnabled = true, Priority = 0 },
                new() { ProviderType = "Ollama", IsEnabled = false, Priority = 1 },
            };

            var migrated = (bool)unify.Invoke(s, null)!;
            Check("A: 迁移执行且只执行一次", migrated && s.ChannelsUnified && !(bool)unify.Invoke(s, null)!);
            Check("A: 主提供商的节点同层最高", oa1.Priority == 100 && oa2.Priority == 100 && oa1.Enabled && oa2.Enabled);
            Check("A: 降级列表里启用的提供商成了下一层", gm.Enabled && gm.Priority == 50, $"gm {gm.Enabled}/{gm.Priority}");
            Check("A: 其它提供商停用（原本就不会被用到）", !ol.Enabled);
            Check("A: 补出了一个 Free 渠道但不启用", s.Free.FreeNodes.Count == 1 && !s.Free.FreeNodes[0].Enabled);
            Check("A: URL 带 /responses 的节点协议记为 Responses",
                  oa2.ApiFormat == Setting.OpenAIApiFormat.Responses && oa1.ApiFormat == Setting.OpenAIApiFormat.ChatCompletions);
            var ids = s.EnumerateChannels().Select(c => c.Id).ToList();
            Check("A: 渠道编号齐全且不重复", ids.All(i => i > 0) && ids.Distinct().Count() == ids.Count, string.Join(",", ids));
            s.SyncPrimaryProvider();
            Check("A: 主提供商仍是 OpenAI", s.Provider == Setting.LLMType.OpenAI);
        }

        // 场景 B：主提供商 Gemini，没开负载均衡，固定用第 2 个启用节点；没开降级
        {
            var s = NewSettings(dir);
            s.ChannelsUnified = false;
            s.EnableFallback = false;
            s.Provider = Setting.LLMType.Gemini;
            s.Gemini.EnableLoadBalancing = false;
            s.Gemini.CurrentNodeIndex = 1;
            var g = Enumerable.Range(0, 3).Select(i => new Setting.GeminiNodeSetting { Name = "g" + i }).ToList();
            s.Gemini.GeminiNodes.AddRange(g);
            unify.Invoke(s, null);

            var plan = ChannelRouter.Plan(s, "Chat");
            Check("B: 原先固定用的那个节点排第一", plan.Count == 1 && plan[0] == g[1], string.Join(",", plan.Select(p => p.Name)));
            Check("B: 没开降级的用户，迁移后失败转移仍是关的", !s.EnableFallback);
        }

        // 场景 C：旧版 OpenAI 负载均衡带节点转移 —— 迁移后保住"失败换节点"
        {
            var s = NewSettings(dir);
            s.ChannelsUnified = false;
            s.EnableFallback = false;
            s.Provider = Setting.LLMType.OpenAI;
            s.OpenAI.EnableLoadBalancing = true;
            s.OpenAI.OpenAINodes.AddRange(new[] { new Setting.OpenAINodeSetting { Name = "x" }, new Setting.OpenAINodeSetting { Name = "y" } });
            unify.Invoke(s, null);
            Check("C: OpenAI 多节点负载均衡的用户保留失败转移", s.EnableFallback);
        }
    }

    // ═════════════════════════════════════════════════════════════
    static void Test_OpenAIProtocols()
    {
        Console.WriteLine("[4] OpenAI 两种协议");
        var build = typeof(OpenAIChatCore).GetMethod("BuildEndpointUrl", BindingFlags.Static | BindingFlags.NonPublic)!;
        string Url(string url, Setting.OpenAIApiFormat f)
            => (string)build.Invoke(null, new object[] { new Setting.OpenAINodeSetting { Url = url, ApiFormat = f } })!;

        var chat = Setting.OpenAIApiFormat.ChatCompletions;
        var resp = Setting.OpenAIApiFormat.Responses;
        Check("base /v1 + Chat", Url("https://api.openai.com/v1", chat) == "https://api.openai.com/v1/chat/completions");
        Check("base /v1 + Responses", Url("https://api.openai.com/v1/", resp) == "https://api.openai.com/v1/responses");
        Check("不带 /v1 的地址补上 /v1", Url("https://relay.example.com", resp) == "https://relay.example.com/v1/responses");
        Check("完整 Chat 端点 + 选了 Responses → 以协议为准",
              Url("https://x.com/v1/chat/completions", resp) == "https://x.com/v1/responses");
        Check("自定义路径的完整端点 → 只换结尾",
              Url("https://x.com/api/openai/responses", chat) == "https://x.com/api/openai/chat/completions");

        var extract = typeof(OpenAIChatCore).GetMethod("ExtractTextFromResponsesOutput", BindingFlags.Static | BindingFlags.NonPublic)!;
        var multi = JObject.Parse("""
            {"output":[
              {"type":"reasoning","summary":[]},
              {"type":"message","content":[{"type":"output_text","text":"你好，"},{"type":"output_text","text":"主人"}]},
              {"type":"message","content":[{"type":"output_text","text":"！"}]}
            ]}
            """);
        Check("Responses：多条 message、多段 output_text 全部拼起来",
              (string)extract.Invoke(null, new object[] { multi })! == "你好，主人！");
        Check("Responses：只有顶层 output_text 的网关也认",
              (string)extract.Invoke(null, new object[] { JObject.Parse("""{"output_text":"hi"}""") })! == "hi");

        var buildImg = typeof(ChatCoreBase).GetMethod("BuildResponsesMultimodalContent", BindingFlags.Static | BindingFlags.NonPublic)!;
        var parts = JArray.FromObject(buildImg.Invoke(null, new object[] { "看图", new List<byte[]> { new byte[] { 1, 2, 3 } } })!);
        Check("Responses 图片部件：input_text + input_image（image_url 是字符串）",
              parts[0]["type"]?.ToString() == "input_text"
              && parts[1]["type"]?.ToString() == "input_image"
              && parts[1]["image_url"]?.Type == JTokenType.String
              && parts[1]["image_url"]!.ToString().StartsWith("data:image/png;base64,"),
              parts.ToString(Formatting.None));
    }

    // ═════════════════════════════════════════════════════════════
    static void Test_EditorHelpers(string dir)
    {
        Console.WriteLine("[5] 编辑页：副本写回与改类型");
        var s = NewSettings(dir);
        var live = Add(s, new Setting.OpenAINodeSetting { Name = "live", ApiKey = "k", Url = "https://relay/v1", Model = "m1" }, 3);
        live.Stats.RequestCount = 5;

        var draft = (Setting.OpenAINodeSetting)Setting.CloneChannel(live);
        draft.Model = "m2";
        draft.Stats.RequestCount = 0;           // 编辑期间的副本统计是旧的
        live.Stats.RequestCount = 6;            // 后台请求又跑了一次
        var statsRef = live.Stats;
        Setting.CopyChannelInto(draft, live);
        Check("写回：字段更新", live.Model == "m2");
        Check("写回：统计不被副本覆盖（对象和数值都保留）", ReferenceEquals(live.Stats, statsRef) && live.Stats.RequestCount == 6,
              $"count={live.Stats.RequestCount}");

        var asResp = Setting.ConvertChannel(live, Setting.ChannelKind.OpenAIResponses);
        Check("Chat → Responses：同一种节点，只换协议", asResp is Setting.OpenAINodeSetting o && o.ApiFormat == Setting.OpenAIApiFormat.Responses && o.Model == "m2");

        var asGemini = (Setting.GeminiNodeSetting)Setting.ConvertChannel(live, Setting.ChannelKind.Gemini);
        Check("OpenAI → Gemini：路由字段、地址、密钥带过去", asGemini.Id == live.Id && asGemini.Priority == 3 && asGemini.ApiKey == "k" && asGemini.Url == "https://relay/v1");
        Check("OpenAI → Gemini：模型换成新类型的默认值", asGemini.Model != "m2");

        s.ReplaceChannel(live, asGemini);
        Check("换类型后原节点移出、新节点加入且编号不变",
              !s.OpenAI.OpenAINodes.Contains(live) && s.Gemini.GeminiNodes.Contains(asGemini) && asGemini.Id == live.Id);
    }

    // ═════════════════════════════════════════════════════════════
    static async Task Test_EndToEndFailover(string dir)
    {
        Console.WriteLine("[6] 端到端：真实请求代码 + 本地假服务");
        using var server = new FakeServer();

        var s = NewSettings(dir);
        s.KeepContext = false;
        s.LLMRequestTimeoutSeconds = 10;
        var bad = Add(s, new Setting.OpenAINodeSetting { Name = "bad", Url = server.Url("bad"), ApiKey = "x", Model = "m" }, 10);
        var good = Add(s, new Setting.OpenAINodeSetting { Name = "good", Url = server.Url("good"), ApiKey = "x", Model = "m" }, 1);
        var resp = Add(s, new Setting.OpenAINodeSetting
        {
            Name = "resp", Url = server.Url("resp"), ApiKey = "x", Model = "m",
            ApiFormat = Setting.OpenAIApiFormat.Responses
        }, 0, enabled: false);

        var core = new RoutedChatCore(s, null!, null!);

        string? summary = null;
        try { summary = await core.Summarize("sys", "user"); }
        catch (Exception ex) { summary = "EXCEPTION " + ex.Message; }
        Check("总结：第一个渠道 500 → 自动转到第二个", summary == "OK-good", summary ?? "null");
        Check("总结：失败和成功各记一次", bad.Stats.FailureCount == 1 && good.Stats.RequestCount == 1 && good.Stats.FailureCount == 0,
              $"bad {bad.Stats.RequestCount}/{bad.Stats.FailureCount} good {good.Stats.RequestCount}/{good.Stats.FailureCount}");
        Check("总结：Chat 协议打到 /chat/completions", server.Paths.Contains("/good/v1/chat/completions"), string.Join(" ", server.Paths));

        s.EnableFallback = false;
        var threw = false;
        try { await core.Summarize("sys", "user"); } catch (SummarizeFailedException) { threw = true; }
        Check("关掉失败转移：第一个失败就直接报错", threw);
        s.EnableFallback = true;

        var test = await core.TestChannelAsync(resp);
        Check("测试停用的 Responses 渠道也能跑，且打到 /responses",
              test.Success && server.Paths.Contains("/resp/v1/responses"), test.Error ?? string.Join(" ", server.Paths));
        Check("测试结果：多段文本拼接 + 记入统计", test.Reply == "OK-resp" && resp.Stats.LastTestOk == true && resp.Stats.LastTestLatencyMs >= 0,
              test.Reply ?? "");
        var body = server.Bodies.TryGetValue("/resp/v1/responses", out var b) ? JObject.Parse(b) : null;
        Check("Responses 请求体用 input 而不是 messages", body?["input"] is JArray && body["messages"] is null, b ?? "");

        var badTest = await core.TestChannelAsync(bad);
        Check("测试失败时给出真实原因（而不是'总结失败'）", !badTest.Success && badTest.Error?.Contains("500") == true, badTest.Error ?? "");

        var models = await core.FetchModelsAsync(good);
        Check("拉模型：/models 与请求地址同一套规则", models.SequenceEqual(new[] { "m-a", "m-b" }), string.Join(",", models));
    }

    const string HistoryMarker = "HISTORY-MARKER";

    /// <summary>
    /// 把 core 的历史换到临时库、灌一条记号：core 共享用户真实的聊天记录，
    /// 这里的检查一旦失败，消息就会写进去。
    /// </summary>
    static HistoryManager RedirectHistory(ChatCoreBase core, string dbPath)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var history = core.HistoryManager;
        typeof(HistoryManager).GetField("_database", flags)!.SetValue(history, new ChatHistoryDatabase(dbPath));
        typeof(HistoryManager).GetField("_history", flags)!.SetValue(history,
            new List<Message> { new Message { Role = "user", Content = HistoryMarker } });
        return history;
    }

    sealed class VisionUnderTest(Setting settings, string dbPath)
        : PreprocessingMultimodal(settings, (global::VPetLLM.VPetLLM)RuntimeHelpers.GetUninitializedObject(typeof(global::VPetLLM.VPetLLM)))
    {
        public List<HistoryManager> Histories { get; } = new();

        protected override ChatCoreBase? CreateCore(string providerType, VisionNodeIdentifier? node, Setting.ChannelNodeBase? channel)
        {
            var core = base.CreateCore(providerType, node, channel)!;
            lock (Histories) Histories.Add(RedirectHistory(core, dbPath));
            return core;
        }
    }

    static async Task Test_VisionLeavesContextAlone(string dir)
    {
        Console.WriteLine("[7] 识图：不读不写主上下文，也不动全局 KeepContext");
        using var server = new FakeServer();

        var s = NewSettings(dir);
        s.KeepContext = true;
        s.OverflowMode = Setting.ContextOverflowMode.Compression;
        s.Records.AutoDecrementWeights = false;
        s.LLMRequestTimeoutSeconds = 10;
        var node = Add(s, new Setting.OpenAINodeSetting
        {
            Name = "slow", Url = server.Url("slow"), ApiKey = "x", Model = "m", EnableVision = true
        }, 1);

        var path = "/slow/v1/chat/completions";
        var image = new byte[] { 1, 2, 3 };
        var db = Path.Combine(dir, "vision-history.db");
        var vision = new VisionUnderTest(s, db);
        var call = typeof(PreprocessingMultimodal).GetMethod("CallChatWithImageForDescription",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task<string> Describe() => Task.Run(() =>
            (Task<string>)call.Invoke(vision, new object?[] { "OpenAI", null, image, "describe", node })!);

        // 两次识图重叠：旧做法 A 存 true、B 存 false，最后还原成 false
        var everFalse = false;
        using var stop = new CancellationTokenSource();
        var watcher = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                if (!s.KeepContext) everFalse = true;
        });
        var first = Describe();
        await Task.Delay(100);
        var second = Describe();
        var replies = await Task.WhenAll(first, second);
        stop.Cancel();
        await watcher;

        Check("两次识图都拿到描述", replies.All(r => r == "OK-slow"), string.Join(",", replies));
        Check("识图途中全局 KeepContext 一直是 true（主聊天不受影响）", !everFalse);
        Check("重叠识图之后 KeepContext 还是 true", s.KeepContext);
        var body = JObject.Parse(server.Bodies[path]);
        Check("识图请求只有 system + 这条提示词，不带主上下文",
              body["messages"]!.Count() == 2 && !server.Bodies[path].Contains(HistoryMarker), server.Bodies[path]);
        Check("识图不写历史", vision.Histories.Count == 2 && vision.Histories.All(h => h.GetHistory().Count == 1),
              string.Join(",", vision.Histories.Select(h => h.GetHistory().Count)));

        // 对照：普通 core 照常读写上下文，否则上面的断言什么也没证明
        var plain = new OpenAIChatCore(node, s, null!, null!);
        var plainHistory = RedirectHistory(plain, db);
        await plain.ChatWithImage("describe", image);
        Check("普通 core 带上主上下文", server.Bodies[path].Contains(HistoryMarker), server.Bodies[path]);
        Check("普通 core 写历史", plainHistory.GetHistory().Count == 3, $"{plainHistory.GetHistory().Count}");

        // 用户自己关掉保持上下文：普通 core 也要听
        s.KeepContext = false;
        var off = new OpenAIChatCore(node, s, null!, null!);
        var offHistory = RedirectHistory(off, db);
        await off.ChatWithImage("describe", image);
        Check("用户关掉保持上下文后不带历史", !server.Bodies[path].Contains(HistoryMarker), server.Bodies[path]);
        Check("用户关掉保持上下文后不写历史", offHistory.GetHistory().Count == 1, $"{offHistory.GetHistory().Count}");
        s.KeepContext = true;

        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "VPetLLM.csproj")))
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))!;
        var direct = Directory.GetFiles(Path.Combine(root, "Core"), "*.cs", SearchOption.AllDirectories)
            .Sum(f => Regex.Matches(File.ReadAllText(f), @"Settings\?\.KeepContext").Count);
        Check("core 里只有一处直接读设置，其余都走 KeepContextEnabled", direct == 1, $"{direct} 处");
    }

    /// <summary>
    /// Free 的连接测试：网关只放行 Prompt.json 里的提示词（freechat_prompt_not_allowed），
    /// 否则伪装成随机 5xx 拒掉。Free 响应必须是原生密钥加密的，假服务走不到这里，
    /// 所以直接比对网关仓库那份名单，两边改漂移了这里会红。
    /// </summary>
    static void Test_FreeProbePrompt()
    {
        Console.WriteLine("== Free 连接测试提示词 ==");

        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "VPetLLM.csproj")))
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))!;
        VPetLLM.Utils.Common.PromptHelper.LoadPrompts(
            Path.Combine(root, "3000_VPetLLM", "plugin", "VPetLLM_lang", "Language.json"));

        var free = RoutedChatCore.ProbePromptFor(new Setting.FreeNodeSetting());
        var other = RoutedChatCore.ProbePromptFor(new Setting.OpenAINodeSetting());
        Check("其它渠道仍用最短的探测词，不为测试跑一遍总结提示词", other.Contains("connectivity probe"), other);

        var gateway = Path.Combine(root, "..", "VPetLLM-Services-Authentication",
            "standalone", "config", "auth-FreeChat", "Prompt.json");
        if (!File.Exists(gateway))
        {
            Console.WriteLine($"  [SKIP] 找不到网关的 Prompt.json: {gateway}");
            return;
        }

        var allowed = JObject.Parse(File.ReadAllText(gateway)).Properties()
            .SelectMany(p => ((JObject)p.Value).Properties().Select(l => l.Value.ToString()));
        Check("Free 探测提示词在网关放行名单里，请求能到后端", allowed.Contains(free), free);
    }

    /// <summary>
    /// 节点设了思考强度、模型却不支持思考（qwen2.5 等），Ollama 整个请求 400。
    /// 要自动去掉 think 重发、不把这次失败报给用户，并且记住，后面不再白撞一次。
    /// </summary>
    static async Task Test_OllamaDropsUnsupportedThinking(string dir)
    {
        Console.WriteLine("[8] Ollama：模型不支持思考时去掉 think 重发");
        using var server = new FakeOllama();
        var s = NewSettings(dir);
        s.LLMRequestTimeoutSeconds = 10;
        var db = Path.Combine(dir, "ollama-history.db");

        async Task<(string Reply, List<string> Errors)> Ask(string model, Setting.ThinkingEffort effort, bool streaming)
        {
            var node = new Setting.OllamaNodeSetting
            {
                Name = "local", Url = server.Url, Model = model, ThinkingEffort = effort, EnableStreaming = streaming
            };
            var core = new OllamaChatCore(node, s, null!, null!);
            RedirectHistory(core, db);
            var errors = new List<string>();
            var replies = new List<string>();
            core.SetErrorHandler(errors.Add);
            // Chat 恒返回空串，非流式的回复走 ResponseHandler
            core.SetResponseHandler(replies.Add);
            await core.Chat("hi");
            return (string.Concat(replies), errors);
        }

        var first = await Ask("qwen2.5:3b", Setting.ThinkingEffort.Medium, false);
        var sent = server.Take();
        Check("★★ 拒收后去掉 think 重发，拿到回复", sent.Count == 2 && sent[0].Think && !sent[1].Think && first.Reply.Contains("OK-ollama"),
              $"{string.Join(",", sent.Select(r => r.Think))} reply={first.Reply}");
        Check("★ 这次失败不报给用户", first.Errors.Count == 0, string.Join(" | ", first.Errors));

        await Ask("qwen2.5:3b", Setting.ThinkingEffort.Medium, false);
        sent = server.Take();
        Check("★ 记住了：同一模型下次直接不带 think", sent.Count == 1 && !sent[0].Think, string.Join(",", sent.Select(r => r.Think)));

        var stream = await Ask("llama3.2:1b", Setting.ThinkingEffort.High, true);
        sent = server.Take();
        Check("流式同样去掉 think 重发", sent.Count == 2 && sent[0].Think && !sent[1].Think && stream.Errors.Count == 0,
              $"{string.Join(",", sent.Select(r => r.Think))} errors={stream.Errors.Count}");

        await Ask("qwen3:4b", Setting.ThinkingEffort.Medium, false);
        sent = server.Take();
        Check("★ 支持思考的模型照常带 think（不是一刀切全关）", sent.Count == 1 && sent[0].Think, string.Join(",", sent.Select(r => r.Think)));

        await Ask("gemma3:1b", Setting.ThinkingEffort.Default, false);
        sent = server.Take();
        Check("没设思考强度时本来就不带", sent.Count == 1 && !sent[0].Think, string.Join(",", sent.Select(r => r.Think)));
    }

    /// <summary>
    /// 用户截图：Ollama 默认窗口 4096，光系统提示词就 6840 tokens，弹"上下文装不下"而 Ollama 渠道没地方调窗口。
    /// 节点填了上下文窗口后，聊天和总结都要带 options.num_ctx；没填时不带（跟随 Ollama 默认）。
    /// </summary>
    static async Task Test_OllamaContextLength(string dir)
    {
        Console.WriteLine("[9] Ollama：上下文窗口随请求发送 num_ctx");
        using var server = new FakeOllama();
        var s = NewSettings(dir);
        s.LLMRequestTimeoutSeconds = 10;
        var db = Path.Combine(dir, "ollama-ctx-history.db");

        async Task<(string Reply, List<string> Errors, OllamaChatCore Core)> Ask(string model, int contextLength, bool streaming = false, bool advanced = false)
        {
            var node = new Setting.OllamaNodeSetting
            {
                Name = "local", Url = server.Url, Model = model,
                EnableStreaming = streaming, EnableAdvanced = advanced, Temperature = 0.3, MaxTokens = 321
            };
            // 窗口是那台 Ollama 的属性，在一键化本地部署里按地址设，不在渠道上
            s.Ollama.SetContextLength(server.Url, contextLength);
            var core = new OllamaChatCore(node, s, null!, null!);
            RedirectHistory(core, db);
            var errors = new List<string>();
            var replies = new List<string>();
            core.SetErrorHandler(errors.Add);
            core.SetResponseHandler(replies.Add);
            await core.Chat("hi");
            return (string.Concat(replies), errors, core);
        }

        List<(string Path, JObject Json)> Sent() { lock (server.Bodies) return server.Bodies.ToList(); }
        JObject? Opts((string Path, JObject Json) b) => b.Json["options"] as JObject;

        var bare = await Ask("window-a", 0);
        var bodies = Sent();
        Check("★ 没填窗口：不带 num_ctx，复现截图那句“上下文装不下 6840 / 4096”",
              bodies.Count >= 1 && bodies.All(b => Opts(b)?["num_ctx"] is null) &&
              bare.Errors.Count == 1 && bare.Errors[0].Contains("上下文装不下") && bare.Errors[0].Contains("6840 / 4096"),
              $"bodies={bodies.Count} errors={string.Join(" | ", bare.Errors)}");
        Check("★ 报错指向一键化本地部署里的上下文窗口，而不是笼统的“服务端调大”",
              bare.Errors.Count == 1 && bare.Errors[0].Contains("一键化本地部署") && bare.Errors[0].Contains("上下文窗口"),
              string.Join(" | ", bare.Errors));
        server.Take();

        var fixedRun = await Ask("window-a", 8192);
        bodies = Sent();
        Check("★★ 填了 8192：请求带 num_ctx=8192，拿到回复且不报错",
              bodies.Count == 1 && Opts(bodies[0])?["num_ctx"]?.Value<int>() == 8192 &&
              fixedRun.Reply.Contains("OK-ollama") && fixedRun.Errors.Count == 0,
              $"bodies={bodies.Count} opts={(bodies.Count > 0 ? Opts(bodies[0]) : null)} reply={fixedRun.Reply} errors={string.Join(" | ", fixedRun.Errors)}");
        Check("没勾自定义生成参数时只带窗口，不夹带温度/最大输出",
              bodies.Count == 1 && Opts(bodies[0])?["temperature"] is null && Opts(bodies[0])?["num_predict"] is null,
              $"{(bodies.Count > 0 ? Opts(bodies[0]) : null)}");

        var summary = await fixedRun.Core.Summarize("sys", "content");
        bodies = Sent();
        var gen = bodies.Where(b => b.Path == "/api/generate").Select(b => b.Json["options"] as JObject).LastOrDefault();
        Check("★ 总结也带同一个 num_ctx（不一致的话 Ollama 每次切换都要重载模型）",
              summary.Contains("OK-summary") && gen?["num_ctx"]?.Value<int>() == 8192,
              $"summary={summary} opts={gen}");
        server.Take();

        await Ask("window-a", 16384, streaming: true, advanced: true);
        bodies = Sent();
        Check("流式 + 自定义生成参数：三个参数都在",
              bodies.Count == 1 && Opts(bodies[0])?["num_ctx"]?.Value<int>() == 16384 &&
              Opts(bodies[0])?["temperature"]?.Value<double>() == 0.3 && Opts(bodies[0])?["num_predict"]?.Value<int>() == 321,
              $"{(bodies.Count > 0 ? Opts(bodies[0]) : null)}");
        server.Take();

        await Ask("plain", 0);
        bodies = Sent();
        Check("没填窗口也没勾生成参数：和以前一样不带 options",
              bodies.Count == 1 && Opts(bodies[0]) is null,
              $"{(bodies.Count > 0 ? Opts(bodies[0]) : null)}");
        server.Take();

        var ollama = new Setting.OllamaSetting();
        Check("没设过的地址是 0（不发送，跟随 Ollama）", ollama.GetContextLength("http://localhost:11434") == 0);
        ollama.SetContextLength("http://localhost:11434", 8192);
        Check("★ 本机的几种写法算同一台 Ollama",
              ollama.GetContextLength("http://127.0.0.1:11434/") == 8192 && ollama.GetContextLength("http://LOCALHOST:11434") == 8192,
              string.Join(",", ollama.ContextLengths.Keys));
        Check("别的端口/别的机器互不影响",
              ollama.GetContextLength("http://localhost:11435") == 0 && ollama.GetContextLength("http://192.168.1.5:11434") == 0);

        // 设置整份是按 JSON 存进库的：存能读回、重复加载不叠加
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(ollama);
        var back = Newtonsoft.Json.JsonConvert.DeserializeObject<Setting.OllamaSetting>(json)!;
        Newtonsoft.Json.JsonConvert.PopulateObject(json, back);
        Check("窗口设置能存能读", back.GetContextLength("http://localhost:11434") == 8192 && back.ContextLengths.Count == 1, json);
        back.SetContextLength("http://localhost:11434", 0);
        Check("选回“跟随默认”就删掉这条", back.ContextLengths.Count == 0 && back.GetContextLength("http://localhost:11434") == 0);
    }

    /// <summary>只认 /api/chat 的假 Ollama：除 qwen3 外的模型带 think 一律 400，和真 Ollama 的报错原文一致。</summary>
    sealed class FakeOllama : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly List<(string Model, bool Think)> _requests = new();

        public FakeOllama()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://localhost:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(Loop);
        }

        public string Url { get; }

        /// <summary>收到的原始请求（路径+JSON），Take 时一起清空。</summary>
        public List<(string Path, JObject Json)> Bodies { get; } = new();

        public List<(string Model, bool Think)> Take()
        {
            lock (_requests)
            {
                var taken = _requests.ToList();
                _requests.Clear();
                Bodies.Clear();
                return taken;
            }
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync();
                var json = JObject.Parse(body);
                var model = json["model"]!.ToString();
                var think = json["think"] is not null;
                lock (_requests)
                {
                    _requests.Add((model, think));
                    Bodies.Add((ctx.Request.Url!.AbsolutePath, json));
                }

                // window-* 模型模拟"系统提示词就有 6840 tokens"：窗口取 num_ctx，没带就是 Ollama 默认 4096，报错原文同用户截图
                var window = (json["options"] as JObject)?["num_ctx"]?.Value<int>() ?? 4096;

                string reply;
                if (model.StartsWith("window-") && window < 6840)
                {
                    ctx.Response.StatusCode = 400;
                    reply = new JObject
                    {
                        ["error"] = new JObject
                        {
                            ["code"] = 400,
                            ["message"] = $"request (6840 tokens) exceeds the available context size ({window} tokens), try increasing it",
                            ["type"] = "exceed_context_size_error",
                            ["n_prompt_tokens"] = 6840,
                            ["n_ctx"] = window
                        }
                    }.ToString(Formatting.None);
                }
                else if (ctx.Request.Url!.AbsolutePath == "/api/generate")
                {
                    reply = new JObject { ["response"] = "OK-summary", ["done"] = true }.ToString(Formatting.None);
                }
                else if (think && !model.StartsWith("qwen3"))
                {
                    ctx.Response.StatusCode = 400;
                    reply = new JObject { ["error"] = $"\"{model}\" does not support thinking" }.ToString(Formatting.None);
                }
                else if (json["stream"]?.Value<bool>() == true)
                {
                    reply = "{\"message\":{\"role\":\"assistant\",\"content\":\"OK-ollama\"},\"done\":false}\n" +
                            "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}\n";
                }
                else
                {
                    reply = "{\"message\":{\"role\":\"assistant\",\"content\":\"OK-ollama\"},\"done\":true}";
                }

                var bytes = Encoding.UTF8.GetBytes(reply);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
        }
    }

    /// <summary>最小的 OpenAI 兼容假服务：/bad 一律 500，/good 走 Chat，/resp 走 Responses。</summary>
    sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly int _port;
        public List<string> Paths { get; } = new();
        public Dictionary<string, string> Bodies { get; } = new();

        public FakeServer()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();
            _ = Task.Run(Loop);
        }

        public string Url(string name) => $"http://localhost:{_port}/{name}/v1";

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                var path = ctx.Request.Url!.AbsolutePath;
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync();
                lock (Paths) { Paths.Add(path); Bodies[path] = body; }
                if (path.StartsWith("/slow/")) await Task.Delay(300);

                int status = 200;
                string reply;
                if (path.StartsWith("/bad/"))
                {
                    status = 500;
                    reply = """{"error":{"message":"upstream exploded"}}""";
                }
                else if (path.EndsWith("/models"))
                {
                    reply = """{"data":[{"id":"m-b"},{"id":"m-a"}]}""";
                }
                else if (path.EndsWith("/responses"))
                {
                    reply = """{"output":[{"type":"message","content":[{"type":"output_text","text":"OK-"},{"type":"output_text","text":"resp"}]}],"usage":{"total_tokens":5}}""";
                }
                else
                {
                    var name = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
                    reply = "{\"choices\":[{\"message\":{\"content\":\"OK-" + name + "\"}}],\"usage\":{\"total_tokens\":7}}";
                }

                var bytes = Encoding.UTF8.GetBytes(reply);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
        }
    }
}
