using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using VPetLLM.Services;

namespace VPetLLM.PerfChecks;

/// <summary>诊断扇出的调度规则：不同 host 并发、同一 host 排队、结果保持输入顺序。不走真实网络。</summary>
internal static class FanOutChecks
{
    private static int _failures;

    public static int Run()
    {
        DifferentHostsOverlap().GetAwaiter().GetResult();
        SameHostRunsOneAtATime("https://api.example.com/v1", "https://API.example.com:8443/other", "https://api.example.com/v2");
        SameHostRunsOneAtATime("http://localhost:11434", "http://127.0.0.1:1234", "http://[::1]:8080");
        SameHostRunsOneAtATime("not a url", "not a url");
        SameHostRunsOneAtATime(null, null);
        ResultsKeepInputOrder().GetAwaiter().GetResult();
        OrderWithinHostIsKept().GetAwaiter().GetResult();
        FailurePropagates().GetAwaiter().GetResult();
        AnySucceeded().GetAwaiter().GetResult();
        BothModesOverlap().GetAwaiter().GetResult();
        OneModeFailingKeepsTheOther().GetAwaiter().GetResult();
        OnlyReachableUntestedChannelsGetLlmTest().GetAwaiter().GetResult();

        Console.WriteLine(_failures == 0 ? "fan-out: OK" : $"fan-out: {_failures} FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task DifferentHostsOverlap()
    {
        // 三个 host 必须同时在跑才放行：串行实现到不了这个闸门，超时失败
        var allRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var urls = new[] { "https://a.example/x", "https://b.example/x", "https://c.example/x" };

        var run = DiagnosticService.RunPerHostAsync(urls, u => u, async u =>
        {
            if (Interlocked.Increment(ref running) == urls.Length)
                allRunning.TrySetResult();
            await allRunning.Task;
            return u;
        });

        if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))) != run)
            Fail("different hosts did not overlap");
    }

    private static void SameHostRunsOneAtATime(params string?[] urls)
    {
        var active = 0;
        var maxActive = 0;

        DiagnosticService.RunPerHostAsync(urls, u => u, async u =>
        {
            var now = Interlocked.Increment(ref active);
            lock (urls)
                maxActive = Math.Max(maxActive, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref active);
            return 0;
        }).GetAwaiter().GetResult();

        if (maxActive != 1)
            Fail($"same host ran {maxActive} at once: {urls[0]}");
    }

    private static async Task ResultsKeepInputOrder()
    {
        var urls = new[] { "https://a.example", "https://b.example", "https://a.example/2", "https://c.example" };
        // 排在前面的最慢：按完成顺序收集就会乱
        var results = await DiagnosticService.RunPerHostAsync(urls, u => u, async u =>
        {
            await Task.Delay(u.Contains("a.example") ? 60 : 5);
            return u;
        });

        if (!results.SequenceEqual(urls))
            Fail("results out of input order");
    }

    private static async Task OrderWithinHostIsKept()
    {
        var urls = Enumerable.Range(0, 5).Select(i => $"https://a.example/{i}").ToList();
        var started = new ConcurrentQueue<string>();

        await DiagnosticService.RunPerHostAsync(urls, u => u, async u =>
        {
            started.Enqueue(u);
            await Task.Yield();
            return 0;
        });

        if (!started.SequenceEqual(urls))
            Fail("order within a host changed");
    }

    private static async Task FailurePropagates()
    {
        try
        {
            await DiagnosticService.RunPerHostAsync(new[] { "https://a.example" }, u => u, async u =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
#pragma warning disable CS0162
                return 0;
#pragma warning restore CS0162
            });
            Fail("exception swallowed");
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task AnySucceeded()
    {
        var never = new TaskCompletionSource<bool>().Task;

        // 第一路永远不返回也不能拖住结果
        var race = DiagnosticService.AnySucceededAsync(new[] { never, Task.FromResult(true) });
        if (await Task.WhenAny(race, Task.Delay(TimeSpan.FromSeconds(5))) != race || !race.Result)
            Fail("first success did not win");

        // 先失败的不算结论，还要等后面的
        var slowSuccess = Task.Delay(30).ContinueWith(_ => true);
        if (!await DiagnosticService.AnySucceededAsync(new[] { Task.FromResult(false), slowSuccess }))
            Fail("failure before success decided the race");

        if (await DiagnosticService.AnySucceededAsync(new[] { Task.FromResult(false), Task.FromResult(false) }))
            Fail("all failed but reported success");
        if (await DiagnosticService.AnySucceededAsync(Array.Empty<Task<bool>>()))
            Fail("no probes reported success");
    }

    private static async Task BothModesOverlap()
    {
        // 两路都到了才放行：直连必须等代理开跑才返回，串行实现到不了这里
        var bothRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;

        var run = TryBothModes(async handler =>
        {
            if (Interlocked.Increment(ref running) == 2)
                bothRunning.TrySetResult();
            await bothRunning.Task;
            return (handler.UseProxy, handler.UseProxy ? "proxy" : "direct", new List<string>());
        });

        if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))) != run)
        {
            Fail("direct and proxy did not overlap");
            return;
        }
        if (await run != (false, "direct", true, "proxy"))
            Fail("direct and proxy results mixed up");
    }

    private static async Task OneModeFailingKeepsTheOther()
    {
        var result = await TryBothModes(handler =>
        {
            if (!handler.UseProxy)
                throw new InvalidOperationException("direct down");
            return Task.FromResult((true, "proxy", new List<string>()));
        });

        if (result != (false, "direct down", true, "proxy"))
            Fail("one mode failing lost the other result");
    }

    private static async Task OnlyReachableUntestedChannelsGetLlmTest()
    {
        // 未知渠道类型不会发请求，只走筛选和记账
        var reachable = new ChannelDiagnosticResult { ChannelType = "None", ChannelName = "reachable", ApiAvailable = true };
        var down = new ChannelDiagnosticResult { ChannelType = "None", ChannelName = "down" };
        var tested = new ChannelDiagnosticResult { ChannelType = "None", ChannelName = "tested", ApiAvailable = true, LlmTested = true };
        var started = new List<string>();

        await Service().TestChannelLLMsAsync(new[] { reachable, down, tested }, cr => started.Add(cr.ChannelName));

        if (!started.SequenceEqual(new[] { "reachable" }))
            Fail("wrong channels LLM-tested: " + string.Join(",", started));
        if (!reachable.LlmTested || down.LlmTested)
            Fail("LlmTested bookkeeping wrong");
    }

    private static Task<(bool, string, bool, string)> TryBothModes(
        Func<HttpClientHandler, Task<(bool success, string message, List<string> models)>> request)
    {
        var method = typeof(DiagnosticService).GetMethod("TryBothModesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<(bool, string, bool, string)>)method.Invoke(Service(), new object[] { "OpenAI", request })!;
    }

    private static DiagnosticService Service()
    {
        // 不跑 Setting 构造函数：它会碰真实 settings.db
        var settings = (Setting)RuntimeHelpers.GetUninitializedObject(typeof(Setting));
        settings.Proxy = new Setting.ProxySetting { IsEnabled = true, Address = "127.0.0.1:1" };
        return new DiagnosticService(settings, "zh-hans");
    }

    private static void Fail(string message)
    {
        _failures++;
        Console.WriteLine("FAIL " + message);
    }
}
