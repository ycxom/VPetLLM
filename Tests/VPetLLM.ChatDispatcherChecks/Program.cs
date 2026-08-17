using System.Reflection;
using VPetLLM.Utils.Common;

// ChatDispatcher 的调度行为回归检查：短时间内的多路灌入必须合并成一次请求，
// 且任何时刻只允许一次请求在飞。宿主与网络都不在场，靠反射装上发送替身来观测。

var failures = new List<string>();

void Check(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
    }
}

// ── 装上发送替身 ────────────────────────────────────────────────────────────
var dispatches = new List<string>();
var dispatchLock = new object();
int inFlight = 0;
int maxInFlight = 0;
int sendDelayMs = 0;

Func<string, IReadOnlyList<byte[]>?, bool, Task<string>> stub = async (text, images, isRetry) =>
{
    lock (dispatchLock)
    {
        inFlight++;
        maxInFlight = Math.Max(maxInFlight, inFlight);
        dispatches.Add(text);
    }

    if (sendDelayMs > 0)
        await Task.Delay(sendDelayMs);

    lock (dispatchLock)
    {
        inFlight--;
    }

    return "ok:" + text;
};

var stubField = typeof(ChatDispatcher).GetField("_sendStub", BindingFlags.Static | BindingFlags.NonPublic);
if (stubField is null)
{
    Console.Error.WriteLine("ChatDispatcher._sendStub 不存在，调度行为无法验证。");
    return 1;
}
stubField.SetValue(null, stub);

void Reset()
{
    lock (dispatchLock)
    {
        dispatches.Clear();
        maxInFlight = 0;
    }
}

List<string> Snapshot()
{
    lock (dispatchLock) return new List<string>(dispatches);
}

// ── 1. 抖动窗口内的多条灌入合并成一次请求 ───────────────────────────────────
Reset();
{
    var tasks = new[]
    {
        ChatDispatcher.SubmitAsync("a", ChatPriority.User, source: "t1"),
        ChatDispatcher.SubmitAsync("b", ChatPriority.User, source: "t1"),
        ChatDispatcher.SubmitAsync("c", ChatPriority.User, source: "t1")
    };
    var results = await Task.WhenAll(tasks);
    var sent = Snapshot();

    Check(sent.Count == 1, $"窗口内的 3 条灌入应合并为 1 次请求，实际发出 {sent.Count} 次。");
    Check(sent.Count == 1 && sent[0] == "a\nb\nc", $"合并后的 prompt 应按到达顺序拼接，实际为 \"{string.Join("|", sent)}\"。");
    Check(results.All(r => r == "ok:a\nb\nc"), "同一批的调用方应拿到同一份回复。");
}

// ── 2. 合并时用户消息排在交互/插件事件之前 ──────────────────────────────────
Reset();
{
    var tasks = new[]
    {
        ChatDispatcher.SubmitAsync("plugin-result", ChatPriority.Plugin, source: "t2"),
        ChatDispatcher.SubmitAsync("touch", ChatPriority.Interaction, source: "t2"),
        ChatDispatcher.SubmitAsync("user-says", ChatPriority.User, source: "t2")
    };
    await Task.WhenAll(tasks);
    var sent = Snapshot();

    Check(sent.Count == 1, $"不同来源的灌入同样应合并为 1 次请求，实际 {sent.Count} 次。");
    Check(sent.Count == 1 && sent[0] == "user-says\ntouch\nplugin-result",
        $"合并应按优先级排序（用户 → 交互 → 插件），实际为 \"{string.Join("|", sent)}\"。");
}

// ── 3. 独占灌入不与任何人合并 ───────────────────────────────────────────────
Reset();
{
    var tasks = new[]
    {
        ChatDispatcher.SubmitAsync("before", ChatPriority.User, source: "t3"),
        ChatDispatcher.SubmitAsync("exclusive", ChatPriority.Plugin, source: "t3", exclusive: true),
        ChatDispatcher.SubmitAsync("after", ChatPriority.User, source: "t3")
    };
    var results = await Task.WhenAll(tasks);
    var sent = Snapshot();

    Check(sent.Count == 3, $"独占项应把批次切成 3 次请求，实际 {sent.Count} 次（{string.Join(" | ", sent)}）。");
    Check(sent.Contains("exclusive"), "独占项必须单独成批，不能被并进别人的 prompt。");
    Check(results[1] == "ok:exclusive", "独占项的调用方必须拿到只属于它自己的回复。");
}

// ── 4. 任何时刻只有一次请求在飞 ─────────────────────────────────────────────
Reset();
sendDelayMs = 200;
{
    var first = ChatDispatcher.SubmitAsync("slow-1", ChatPriority.User, source: "t4", exclusive: true);

    // 等第一条真正进入发送中，再灌第二条 —— 这正是过去产生并发的时序
    while (true)
    {
        lock (dispatchLock)
        {
            if (inFlight > 0) break;
        }
        await Task.Delay(10);
    }

    var second = ChatDispatcher.SubmitAsync("slow-2", ChatPriority.User, source: "t4", exclusive: true);
    await Task.WhenAll(first, second);

    Check(maxInFlight == 1, $"同一时刻只允许一次 LLM 请求在飞，实测峰值 {maxInFlight}。");
    Check(Snapshot().SequenceEqual(new[] { "slow-1", "slow-2" }), "串行化后两次请求应保持提交顺序。");
}
sendDelayMs = 0;

// ── 5. 空灌入不该惊动 LLM ───────────────────────────────────────────────────
Reset();
{
    var result = await ChatDispatcher.SubmitAsync("   ", ChatPriority.System, source: "t5");
    Check(Snapshot().Count == 0, "纯空白的灌入不应发出请求。");
    Check(result == "", "空灌入应直接返回空串。");
}

stubField.SetValue(null, null);

if (failures.Count > 0)
{
    Console.Error.WriteLine("ChatDispatcher checks failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(" - " + failure);
    }
    return 1;
}

Console.WriteLine("ChatDispatcher checks passed.");
return 0;
