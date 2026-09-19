using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VPetLLM;
using VPetLLM.Core.Abstractions.Base;
using VPetLLM.Core.Providers.Chat;
using VPetLLM.Core.Routing;
using VPetLLM.Infrastructure.Exceptions;

// 统一渠道路由的回归检查。
//
// 这次改动把"选一个提供商 → 在它的节点里轮询 → 降级列表（其实从没接上）"
// 换成了跨类型的"优先级分层 + 层内按权重 + 失败转移"。这里把几条最容易被改坏的规则钉死：
//   1. 选路：停用/用途/视觉过滤、优先级分层、权重分配、关掉失败转移时只给一个；
//   2. 迁移：旧的"当前提供商 + 负载均衡 + 降级列表"折算成优先级后，运行时行为不变；
//   3. OpenAI 两种协议：地址拼接、Responses 多段文本、图片部件格式；
//   4. 端到端：本地起两个假服务，第一个 500、第二个正常 —— 总结和测试走的是真实请求代码。

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
            Test_RouterWeights(dir);
            Test_Migration(dir);
            Test_OpenAIProtocols();
            Test_EditorHelpers(dir);
            Test_EndToEndFailover(dir).GetAwaiter().GetResult();
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
