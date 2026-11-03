using VPetLLM;
using System.Text;
using VPetLLM.Core.Abstractions.Base;
using VPetLLM.Core.Data.Database;
using VPetLLM.Core.Data.Managers;
using VPetLLM.Infrastructure.Exceptions;
using VPetLLM.Utils.Common;

// 溢出总结「自适应分批」的回归检查。
//
// 起因是一条真实日志：一次总结请求覆盖 2151 条消息、152304 tokens，失败后
// 错误文案被当成总结提交，2151 条原文的总结就此变成一行报错。修复分两层：
//   1. Summarize 失败改为抛异常（错误文案再也不会被当成总结）；
//   2. 积压按预算切片、逐片提交、逐片推进检查点，这份检查钉的是第 2 层。
//
// 必须成立的性质：
//   A. 超预算的积压会被切成多片，而不是一次性发出去；
//   B. 每片独立总结，请求里不带已有总结（带了模型就把旧总结抄一遍再续写），
//      注入用的是全部分段按时间拼起来的结果；
//   C. 单条消息本身就超预算时跳过它，不卡住后面的消息；
//   D. 连续失败视为服务故障而非内容过长，整轮放弃，绝不逐条跳完整段积压；
//   F. 与长度无关的失败（传输异常/超时/网关错误页）不退让、不跳过，检查点原地不动；
//   G. 单条小消息被 400 时不跳过——毛病不在它；
//   H. 退让条数不跨轮残留；
//   I. 重启后从上次总结到的下一条接着做，不重复总结最后一条（曾经差一，732 对相邻分段里 74 对重叠）；
//   J. 已记下的事实不再存第二遍，但数字变了的算更新；
//   K. 有效条目超预算时只合并旧条目，合并请求里没有对话原文；
//   L. 合并失败什么都不丢；
//   M. 旧库里的替换制滚动总结只认最新一行，新分段接在它后面。
//   N~V 见 MemoryGraphChecks.cs：空片落标记行、合并失败退避、语义去重、记忆原子与星云图。
//
// F~H 的起因是 2026-10-08 的日志：EdgeOne 15 秒回源超时被当成「内容太长」，
// 175 条积压一路二分到单条，几轮下来连跳 6 条，且下一轮一上来就是单条。

static partial class Program
{
    static int _pass;
    static int _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine($"  [PASS] {name}");
        }
        else
        {
            _fail++;
            Console.WriteLine($"  [FAIL] {name}  {detail}");
        }
    }

    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        Test_SplitsOversizedBacklog();
        Test_SegmentsAreSummarizedIndependently();
        Test_SkipsSingleOversizeMessage();
        Test_AbortsAfterConsecutiveFailures();
        Test_RetreatsOnFailureThenSucceeds();
        Test_TransportFailureNeitherRetreatsNorSkips();
        Test_SmallSingleMessageRejectedIsNotSkipped();
        Test_RetreatDoesNotLeakIntoNextRound();
        Test_RestartResumesWithoutOverlap();
        Test_KnownFactsAreNotStoredTwice();
        Test_ConsolidatesOnlyOldEntries();
        Test_ConsolidationFailureKeepsSegments();
        Test_LegacyRollingRowIsTheBase();
        Test_EmptySegmentSurvivesRestart();
        Test_ConsolidationFailureBacksOff();
        Test_EditorCanListInactiveRows();
        Test_SemanticDuplicatesAreDropped();
        Test_ClassifierAndDecay();
        Test_MarkersAndEntityResolution();
        Test_SegmentsBecomeAtoms();
        Test_LouvainSplitsCliques();
        Test_NebulaLayoutIsDeterministic();

        Console.WriteLine();
        Console.WriteLine($"通过 {_pass}，失败 {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    // A：一段远超单次预算的积压必须被切片
    static void Test_SplitsOversizedBacklog()
    {
        Console.WriteLine("A. 超预算积压切片");
        using var harness = new Harness(contextTokens: 4000);

        // 每条约 200 tokens × 120 条，远超单次可携带量
        harness.Run(MakeMessages(120, tokensEach: 200));

        Check("发出了多次请求", harness.Core.Calls.Count > 1,
            $"实际 {harness.Core.Calls.Count} 次");
        Check("没有任何一次请求超出上下文预算",
            harness.Core.Calls.All(call => call.TotalTokens <= 4000),
            $"最大 {(harness.Core.Calls.Count == 0 ? 0 : harness.Core.Calls.Max(c => c.TotalTokens))}");
        // keepCount = min(threshold, count) = 1，最后 1 条按设计留在窗口里不总结
        Check("除保留窗口外全部覆盖", harness.Checkpoint == 119,
            $"checkpoint={harness.Checkpoint}");
        Check("落库分片数与请求数一致", harness.SummaryRowCount == harness.Core.Calls.Count,
            $"rows={harness.SummaryRowCount} calls={harness.Core.Calls.Count}");
    }

    // B：旧总结一旦进了请求，模型就会抄一遍再「补充」，同样的事实越堆越多
    static void Test_SegmentsAreSummarizedIndependently()
    {
        Console.WriteLine("B. 分片独立总结");
        using var harness = new Harness(contextTokens: 4000);
        harness.Core.SummaryFactory = index => $"- SEG{index}-fact";

        harness.Run(MakeMessages(60, tokensEach: 200));

        var calls = harness.Core.Calls;
        Check("至少两片", calls.Count >= 2, $"{calls.Count} 片");

        var leaked = false;
        for (var i = 1; i < calls.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (calls[i].SystemPrompt.Contains($"SEG{j}-") || calls[i].UserContent.Contains($"SEG{j}-"))
                    leaked = true;
            }
        }
        Check("后面的请求里没有前面分片的总结", !leaked);
        Check("请求里不再要求续写旧总结",
            calls.All(c => !c.SystemPrompt.Contains("EXTEND") && !c.SystemPrompt.Contains("Previous summary")));

        var summary = harness.Manager.LatestSummary ?? "";
        var positions = Enumerable.Range(0, calls.Count).Select(i => summary.IndexOf($"SEG{i}-", StringComparison.Ordinal)).ToList();
        Check("注入的总结按时间包含全部分片",
            positions.All(p => p >= 0) && positions.Zip(positions.Skip(1)).All(pair => pair.First < pair.Second),
            $"positions=[{string.Join(",", positions)}]");
    }

    // C：单条就超预算 —— 跳过它，后面的照常总结
    static void Test_SkipsSingleOversizeMessage()
    {
        Console.WriteLine("C. 单条超长跳过");
        using var harness = new Harness(contextTokens: 4000);

        var messages = MakeMessages(10, tokensEach: 100);
        messages[4].Content = new string('x', 40000);   // 约 10000 tokens，单条就超预算

        harness.Run(messages);

        Check("检查点越过了超长那条（并停在保留窗口前）", harness.Checkpoint == 9,
            $"checkpoint={harness.Checkpoint}");
        Check("超长消息没有被发出去",
            harness.Core.Calls.All(call => !call.UserContent.Contains(new string('x', 1000))));
        Check("其余消息仍被总结", harness.SummaryRowCount > 0,
            $"rows={harness.SummaryRowCount}");
    }

    // D：一直被 400、且每条都大到「可能是它太长」——最多跳两条就判定为故障
    static void Test_AbortsAfterConsecutiveFailures()
    {
        Console.WriteLine("D. 连续失败中止而非逐条跳过");
        using var harness = new Harness(contextTokens: 4000);
        harness.Core.FailAlwaysBadRequest = true;

        // 每条约 1600 tokens：超过预算的一半，又不至于单条就超预算被预先跳过
        harness.Run(MakeMessages(50, tokensEach: 1600));

        Check("检查点没有推到底", harness.Checkpoint < 50, $"checkpoint={harness.Checkpoint}");
        Check("跳过的条数很少（未吞掉整段积压）", harness.Checkpoint <= 3,
            $"checkpoint={harness.Checkpoint}");
        Check("没有落任何总结", harness.SummaryRowCount == 0,
            $"rows={harness.SummaryRowCount}");
    }

    // 失败退让：前几次失败后按更小的批量重试，最终应当做完
    static void Test_RetreatsOnFailureThenSucceeds()
    {
        Console.WriteLine("E. 失败退让后完成");
        using var harness = new Harness(contextTokens: 100000);
        harness.Core.FailWhenMessagesExceed = 8;   // 模拟服务端比我们估得更严

        harness.Run(MakeMessages(32, tokensEach: 50));

        Check("最终除保留窗口外全部覆盖", harness.Checkpoint == 31, $"checkpoint={harness.Checkpoint}");
        Check("成功的请求都不超过 8 条",
            harness.Core.Calls.Where(c => c.Succeeded).All(c => c.MessageCount <= 8));
        Check("确实发生过退让", harness.Core.Calls.Any(c => !c.Succeeded));
    }

    // F：日志原样复现——传输层失败时既不切小，也不跳过
    static void Test_TransportFailureNeitherRetreatsNorSkips()
    {
        Console.WriteLine("F. 传输失败不退让不跳过");
        using var harness = new Harness(contextTokens: 100000);
        harness.Core.FailAlwaysTransport = true;

        harness.Run(MakeMessages(176, tokensEach: 70));

        Check("只发了一次就放弃", harness.Core.Calls.Count == 1, $"实际 {harness.Core.Calls.Count} 次");
        Check("检查点原地不动", harness.Checkpoint == 0, $"checkpoint={harness.Checkpoint}");
        Check("没有落任何总结", harness.SummaryRowCount == 0, $"rows={harness.SummaryRowCount}");

        // 服务恢复后下一轮照常做完
        harness.Core.FailAlwaysTransport = false;
        harness.Run(MakeMessages(176, tokensEach: 70));
        Check("恢复后全部覆盖", harness.Checkpoint == 175, $"checkpoint={harness.Checkpoint}");
    }

    // G：单条小消息被 400——问题不在它，不能跳过
    static void Test_SmallSingleMessageRejectedIsNotSkipped()
    {
        Console.WriteLine("G. 小消息被拒不跳过");
        using var harness = new Harness(contextTokens: 4000);
        harness.Core.FailAlwaysBadRequest = true;

        harness.Run(MakeMessages(50, tokensEach: 100));

        Check("确实退让到了单条", harness.Core.Calls.Any(c => c.MessageCount == 1));
        Check("检查点原地不动", harness.Checkpoint == 0, $"checkpoint={harness.Checkpoint}");
    }

    // H：上一轮退让到单条后中止，下一轮必须回到按预算取批
    static void Test_RetreatDoesNotLeakIntoNextRound()
    {
        Console.WriteLine("H. 退让条数不跨轮残留");
        using var harness = new Harness(contextTokens: 100000);
        harness.Core.FailAlwaysBadRequest = true;

        var messages = MakeMessages(40, tokensEach: 50);
        harness.Run(messages);
        var firstRoundCalls = harness.Core.Calls.Count;

        harness.Core.FailAlwaysBadRequest = false;
        harness.Run(messages);

        var nextRoundFirst = harness.Core.Calls[firstRoundCalls];
        Check("下一轮第一片不是单条", nextRoundFirst.MessageCount > 1,
            $"实际 {nextRoundFirst.MessageCount} 条");
        Check("下一轮做完", harness.Checkpoint == 39, $"checkpoint={harness.Checkpoint}");
    }

    // I：库里存的是分段的闭区间终点，恢复时曾当成开区间用，重启后最后一条又被总结一遍
    static void Test_RestartResumesWithoutOverlap()
    {
        Console.WriteLine("I. 重启续做不重叠");
        using var harness = new Harness(contextTokens: 4000);
        harness.Run(MakeMessages(60, tokensEach: 200));
        var before = harness.Checkpoint;
        var summaryBefore = harness.Manager.LatestSummary;

        harness.Reopen();
        Check("重启后检查点不变", harness.Checkpoint == before, $"{before} → {harness.Checkpoint}");
        Check("重启后注入的总结不变", harness.Manager.LatestSummary == summaryBefore);

        var callsBefore = harness.Core.Calls.Count;
        harness.Run(MakeMessages(80, tokensEach: 200));
        var first = harness.Core.Calls[callsBefore];
        Check("续做从下一条开始", first.UserContent.Contains($"]: m{before} "));
        Check("上次总结过的最后一条没再发", !first.UserContent.Contains($"]: m{before - 1} "));
    }

    // J：名字、偏好这类事实每片都会被提一次
    static void Test_KnownFactsAreNotStoredTwice()
    {
        Console.WriteLine("J. 已知事实去重");
        using var harness = new Harness(contextTokens: 4000);
        harness.Core.SummaryFactory = index => index switch
        {
            0 => "- 用户名叫小明，最喜欢的水果是草莓\n- 用户生日是2000年5月3日\n- 片0独有的事实编号0",
            1 => "- 用户名叫小明，最喜欢的水果是草莓。",
            _ => $"用户信息：\n- 用户名叫小明，最喜欢的水果是草莓。\n\n- 用户生日是2000年5月4日\n- 片{index}独有的事实编号{index}"
        };

        harness.Run(MakeMessages(120, tokensEach: 200));

        var calls = harness.Core.Calls.Count;
        var summary = harness.Manager.LatestSummary ?? "";
        Check("至少三片", calls >= 3, $"{calls} 片");
        Check("重复的事实只存一遍", Occurrences(summary, "最喜欢的水果是草莓") == 1,
            $"{Occurrences(summary, "最喜欢的水果是草莓")} 遍");
        Check("内容全被去掉的标题不留", !summary.Contains("用户信息"));
        Check("数字变了算更新，新旧都在",
            Occurrences(summary, "5月3日") == 1 && Occurrences(summary, "5月4日") == 1);
        Check("各片独有的事实都在",
            Enumerable.Range(0, calls).Where(i => i != 1).All(i => summary.Contains($"编号{i}")));
        // 全是重复的那片只落一行空片标记：不进提示词，但重启后检查点不会退回去重新总结它
        Check("全是重复的那片不进提示词", harness.ActiveRowCount == calls - 1,
            $"active={harness.ActiveRowCount} calls={calls}");
        Check("全是重复的那片留了空片标记", harness.SummaryRowCount == calls,
            $"rows={harness.SummaryRowCount} calls={calls}");
        Check("全是重复的那片照样推进检查点", harness.Checkpoint == 119, $"checkpoint={harness.Checkpoint}");
    }

    // K：合并只碰旧条目，近期分段原样保留
    static void Test_ConsolidatesOnlyOldEntries()
    {
        Console.WriteLine("K. 超预算合并旧条目");
        using var harness = new Harness(contextTokens: 4000);
        harness.Settings.MaxSummaryTokens = 200;
        // 每条约 80 tokens，三条就超 200
        harness.Core.SummaryFactory = index => $"- 片{index}：" + new string('b', 320);

        harness.Run(MakeMessages(120, tokensEach: 200));

        var calls = harness.Core.Calls;
        var merges = calls.Where(c => c.IsConsolidation).ToList();
        var segments = calls.Count(c => !c.IsConsolidation);
        var summary = harness.Manager.LatestSummary ?? "";
        Check("发生过合并", merges.Count > 0);
        Check("合并请求里没有对话原文", merges.All(c => !c.UserContent.Contains("]: m")));
        Check("被合并的条目不再生效", harness.ActiveRowCount < harness.SummaryRowCount,
            $"active={harness.ActiveRowCount} rows={harness.SummaryRowCount}");
        Check("注入的总结不超预算", TokenCounter.EstimateTokenCount(summary) <= 200,
            $"{TokenCounter.EstimateTokenCount(summary)} tokens");
        Check("合并块在最前", summary.StartsWith("- merged-old-facts"));
        Check("最新一片原样保留", summary.Contains($"- 片{segments - 1}：" + new string('b', 320)));
        Check("检查点做完", harness.Checkpoint == 119, $"checkpoint={harness.Checkpoint}");
    }

    // L：合并失败不能拿失败文案顶替，也不能丢分段
    static void Test_ConsolidationFailureKeepsSegments()
    {
        Console.WriteLine("L. 合并失败保留分段");
        using var harness = new Harness(contextTokens: 4000);
        harness.Settings.MaxSummaryTokens = 200;
        harness.Core.FailConsolidation = true;
        harness.Core.SummaryFactory = index => $"- 片{index}：" + new string('b', 320);

        harness.Run(MakeMessages(120, tokensEach: 200));

        var segments = harness.Core.Calls.Count(c => !c.IsConsolidation);
        var summary = harness.Manager.LatestSummary ?? "";
        Check("确实尝试过合并", harness.Core.Calls.Any(c => c.IsConsolidation));
        Check("检查点照常做完", harness.Checkpoint == 119, $"checkpoint={harness.Checkpoint}");
        Check("没有条目被标为失效", harness.ActiveRowCount == harness.SummaryRowCount,
            $"active={harness.ActiveRowCount} rows={harness.SummaryRowCount}");
        Check("每一片都还在", Enumerable.Range(0, segments).All(i => summary.Contains($"- 片{i}：")));
    }

    // M：改版前每行都是前一行的超集，全拼起来就是同一份事实重复 N 遍
    static void Test_LegacyRollingRowIsTheBase()
    {
        Console.WriteLine("M. 旧库滚动总结兼容");
        using var harness = new Harness(contextTokens: 4000);
        harness.SeedSummary("- OLD-V1 旧版本", 0, 9, OverflowSummaryKind.Rolling);
        harness.SeedSummary("- OLD-V1 旧版本\n- OLD-V2 新增", 0, 19, OverflowSummaryKind.Rolling);
        harness.Reopen();

        Check("检查点接在旧总结之后", harness.Checkpoint == 20, $"checkpoint={harness.Checkpoint}");
        Check("只用最新一行", harness.Manager.LatestSummary == "- OLD-V1 旧版本\n- OLD-V2 新增",
            harness.Manager.LatestSummary ?? "null");

        harness.Core.SummaryFactory = index => $"- NEW{index}";
        harness.Run(MakeMessages(60, tokensEach: 200));

        var first = harness.Core.Calls[0];
        var summary = harness.Manager.LatestSummary ?? "";
        Check("从第 20 条开始", first.UserContent.Contains("]: m20 ") && !first.UserContent.Contains("]: m19 "));
        Check("旧事实只出现一遍", Occurrences(summary, "OLD-V1") == 1);
        Check("新分段接在旧总结后面",
            summary.StartsWith("- OLD-V1") && summary.IndexOf("NEW0", StringComparison.Ordinal) > summary.IndexOf("OLD-V2", StringComparison.Ordinal));
    }

    static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    static List<Message> MakeMessages(int count, int tokensEach)
    {
        // TokenCounter 对纯 ASCII 大致按 4 字符 1 token 估
        var body = new string('a', Math.Max(1, tokensEach * 4));
        return Enumerable.Range(0, count)
            .Select(i => new Message
            {
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"m{i} {body}"
            })
            .ToList();
    }
}
