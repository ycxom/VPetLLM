using VPetLLM;
using System.Text;
using VPetLLM.Core.Abstractions.Base;
using VPetLLM.Core.Data.Managers;
using VPetLLM.Infrastructure.Exceptions;

// 溢出总结「自适应分批」的回归检查。
//
// 起因是一条真实日志：一次总结请求覆盖 2151 条消息、152304 tokens，失败后
// 错误文案被当成总结提交，2151 条原文的总结就此变成一行报错。修复分两层：
//   1. Summarize 失败改为抛异常（错误文案再也不会被当成总结）；
//   2. 积压按预算切片、逐片提交、逐片推进检查点，这份检查钉的是第 2 层。
//
// 四个必须成立的性质：
//   A. 超预算的积压会被切成多片，而不是一次性发出去；
//   B. 分片之间靠滚动总结串起来——第 N 片的结果是第 N+1 片的 previous context；
//   C. 单条消息本身就超预算时跳过它，不卡住后面的消息；
//   D. 连续失败视为服务故障而非内容过长，整轮放弃，绝不逐条跳完整段积压。

static class Program
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
        Test_RollingSummaryChainsAcrossBatches();
        Test_SkipsSingleOversizeMessage();
        Test_AbortsAfterConsecutiveFailures();
        Test_RetreatsOnFailureThenSucceeds();

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

    // B：滚动总结必须在分片之间传递，否则每片都从零开始、前面的事实就丢了
    static void Test_RollingSummaryChainsAcrossBatches()
    {
        Console.WriteLine("B. 分片间滚动总结衔接");
        using var harness = new Harness(contextTokens: 4000);
        harness.Core.SummaryFactory = index => $"ROLLING-{index}";

        harness.Run(MakeMessages(60, tokensEach: 200));

        Check("至少两片", harness.Core.Calls.Count >= 2, $"{harness.Core.Calls.Count} 片");

        var firstHasNoPrevious = !harness.Core.Calls[0].SystemPrompt.Contains("ROLLING-");
        Check("第一片没有前序总结", firstHasNoPrevious);

        var chained = true;
        for (var i = 1; i < harness.Core.Calls.Count; i++)
        {
            if (!harness.Core.Calls[i].SystemPrompt.Contains($"ROLLING-{i - 1}"))
            {
                chained = false;
                break;
            }
        }
        Check("第 N 片带着第 N-1 片的结果作为 previous context", chained);
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

    // D：服务故障时不能把积压逐条跳完
    static void Test_AbortsAfterConsecutiveFailures()
    {
        Console.WriteLine("D. 连续失败中止而非逐条跳过");
        using var harness = new Harness(contextTokens: 4000);
        harness.Core.FailAlways = true;

        harness.Run(MakeMessages(50, tokensEach: 100));

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
