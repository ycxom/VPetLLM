using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using VPetLLM.Core.Services;
using VPetLLM.UI.Windows;

namespace VPetLLM.OllamaChecks;

internal static partial class Program
{
    private static readonly Lazy<string> LibraryCacheRoot = new(() => Directory.CreateTempSubdirectory("vpetllm-ollama-library-").FullName);

    private static string NewLibraryCache() => Path.Combine(LibraryCacheRoot.Value, Guid.NewGuid().ToString("N"));

    private static void DeleteLibraryCaches()
    {
        if (!LibraryCacheRoot.IsValueCreated) return;
        try { Directory.Delete(LibraryCacheRoot.Value, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string RealCard()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("VPetLLM.OllamaChecks.qwen3-card.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Card(string name, string description, string badges = "tools 8b", string pulls = "1.2K")
    {
        var spans = string.Concat(badges.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b =>
            $"""<span class="inline-flex items-center rounded-md bg-indigo-50 px-2 text-indigo-600">{b}</span>"""));
        return $"""
            <li class="flex items-baseline border-b border-neutral-200 py-6">
              <a href="/library/{name}" class="group w-full space-y-5">
                <div title="{name}"><h2><span>{name}</span></h2><p class="max-w-lg break-words text-neutral-800 text-md">{description}</p></div>
                <div><div class="flex">{spans}</div>
                <p><span class="flex"><span >{pulls}</span><span class="hidden sm:flex">&nbsp;Pulls</span></span></p></div>
              </a>
            </li>
            """;
    }

    private static string Fillers(int count) => string.Concat(Enumerable.Range(0, count).Select(i => Card($"filler{i:00}", $"Filler number {i}.", "vision 8b")));

    private static string Page(params string[] cards) => $"<html><body><ul>{string.Concat(cards)}{Fillers(30)}</ul></body></html>";

    private static string ManifestBody(long size)
        => $$"""{"schemaVersion":2,"config":{"size":487},"layers":[{"size":{{size - 100}}},{"size":60},{"size":40}]}""";

    /// <summary>假的 ollama.com 和模型仓库，只认真实地址，地址拼错就是 404。</summary>
    private sealed class FakeOllamaCom : HttpMessageHandler
    {
        private static readonly Regex Manifest = new(@"^https://registry\.ollama\.ai/v2/library/([^/]+)/manifests/([^/]+)$");

        private int _active;
        private int _peak;
        private int _listRequests;

        public string Html { get; set; } = Page();
        public HttpStatusCode ListStatus { get; set; } = HttpStatusCode.OK;
        public bool Offline { get; set; }
        public bool ListTimesOut { get; set; }
        public TaskCompletionSource? ListGate { get; set; }
        public bool ManifestTimesOut { get; set; }
        public Dictionary<string, long> Sizes { get; } = new();
        public Dictionary<string, string> Bodies { get; } = new();
        public TaskCompletionSource? ManifestGate { get; set; }
        public ConcurrentQueue<string> Looked { get; } = new();
        public ConcurrentQueue<string> Unexpected { get; } = new();
        public int ListRequests => Volatile.Read(ref _listRequests);
        public int Peak => Volatile.Read(ref _peak);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == "https://ollama.com/library")
            {
                Interlocked.Increment(ref _listRequests);
                if (Offline) throw new HttpRequestException("offline");
                if (ListTimesOut) throw new TaskCanceledException("timeout");
                if (ListGate is { } listGate) await listGate.Task.WaitAsync(token);
                return Text(ListStatus, Html);
            }

            var match = Manifest.Match(url);
            if (!match.Success)
            {
                Unexpected.Enqueue(url);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var key = $"{match.Groups[1].Value}:{match.Groups[2].Value}";
            Looked.Enqueue(key);
            var active = Interlocked.Increment(ref _active);
            lock (Looked) _peak = Math.Max(_peak, active);
            try
            {
                if (Offline) throw new HttpRequestException("offline");
                if (ManifestTimesOut) throw new TaskCanceledException("timeout");
                if (ManifestGate is { } gate) await gate.Task.WaitAsync(token);
                else await Task.Delay(5, token);

                if (Bodies.TryGetValue(key, out var body)) return Text(HttpStatusCode.OK, body);
                return Sizes.TryGetValue(key, out var size)
                    ? Text(HttpStatusCode.OK, ManifestBody(size))
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private static void CheckFit()
    {
        const double headroom = 1.3;

        var strong = new OllamaHardware(32 * GB, 12 * GB, "RTX");
        var smooth = OllamaModelCatalog.SmoothLimit(strong);
        var usable = OllamaModelCatalog.UsableLimit(strong);
        Check("显存预留 30%", smooth == (long)(12 * GB / headroom), smooth.ToString());
        Check("能跑上限：内存加显存留出系统占用", usable == (long)((32 + 12 - 4) * GB / headroom), usable.ToString());
        Check("刚好放得下算流畅", OllamaModelCatalog.Assess(smooth, strong) == OllamaFit.Smooth);
        Check("超一点就不算流畅", OllamaModelCatalog.Assess(smooth + 1, strong) == OllamaFit.Slow);
        Check("能跑上限内算慢", OllamaModelCatalog.Assess(usable, strong) == OllamaFit.Slow);
        Check("超过上限算带不动", OllamaModelCatalog.Assess(usable + 1, strong) == OllamaFit.TooLarge);

        var cpu = new OllamaHardware(16 * GB, 0);
        Check("无显卡时流畅上限 3 GB", OllamaModelCatalog.SmoothLimit(cpu) == 3 * GB, OllamaModelCatalog.SmoothLimit(cpu).ToString());
        Check("无显卡时内存留出 6 GB 给系统", OllamaModelCatalog.SmoothLimit(new OllamaHardware(8 * GB, 0)) == 2 * GB);
        Check("内存太小时上限不为负", OllamaModelCatalog.SmoothLimit(new OllamaHardware(4 * GB, 0)) == 0
                                     && OllamaModelCatalog.UsableLimit(new OllamaHardware(2 * GB, 0)) == 0);
        Check("4 GB 内存勉强能跑 3 GB 以下",
            OllamaModelCatalog.UsableLimit(new OllamaHardware(8 * GB, 0)) == (long)(4 * GB / headroom));

        var noAvx = new OllamaHardware(32 * GB, 0, "", 8, HasAvx2: false);
        var fewCores = new OllamaHardware(32 * GB, 0, "", 3);
        Check("没有 AVX2 不算流畅", OllamaModelCatalog.Assess(GB, noAvx) == OllamaFit.Slow);
        Check("不到 4 核不算流畅", OllamaModelCatalog.Assess(GB, fewCores) == OllamaFit.Slow);
        Check("4 核算流畅", OllamaModelCatalog.Assess(GB, new OllamaHardware(32 * GB, 0, "", 4)) == OllamaFit.Smooth);

        Check("不到 1 GiB 的核显不算显存", new OllamaHardware(16 * GB, GB - 1).UsableVideoMemory == 0);
        Check("1 GiB 算显存", new OllamaHardware(16 * GB, GB).UsableVideoMemory == GB);

        Check("不知道配置时不判断", OllamaModelCatalog.Assess(GB, null) == OllamaFit.Unknown);
        Check("不知道体积时不判断", OllamaModelCatalog.Assess(0, strong) == OllamaFit.Unknown);

        var sizes = new Dictionary<string, long> { ["4b"] = 2 * GB, ["8b"] = 5 * GB, ["14b"] = 9 * GB, ["70b"] = 40 * GB };
        Check("选最大的流畅规格", OllamaModelCatalog.ChooseTag(sizes, strong) == "14b", OllamaModelCatalog.ChooseTag(sizes, strong) ?? "null");
        Check("都不流畅时选最小的", OllamaModelCatalog.ChooseTag(sizes, new OllamaHardware(4 * GB, 0)) == "4b");
        Check("不知道配置时选最小的", OllamaModelCatalog.ChooseTag(sizes, null) == "4b");
        Check("没有体积时不选", OllamaModelCatalog.ChooseTag(new Dictionary<string, long>(), strong) is null);

        OllamaLibraryModel Family(string name, params string[] capabilities)
            => new(name, "", capabilities, new[] { "8b" }, "1K");

        var library = new[] { Family("no-tools", "vision"), Family("qwen3", "tools"), Family("qwen3-coder", "tools"), Family("gamma-coder", "tools"), Family("glm-ocr", "tools") }
            .Concat(Enumerable.Range(0, 20).Select(i => Family($"pick{i}", "tools"))).ToList();
        var picks = OllamaModelCatalog.PopularCandidates(library).Select(m => m.Name).ToList();
        Check("热门候选：要能调工具", !picks.Contains("no-tools"));
        Check("热门候选：内置名单已有的不重复", !picks.Contains("qwen3"));
        Check("热门候选：写代码的模型不适合桌宠聊天", !picks.Any(n => n.Contains("code")));
        Check("热门候选：识字的模型不适合桌宠聊天", !picks.Any(n => n.Contains("ocr")));
        Check("热门候选：保持热度顺序并限量", picks.SequenceEqual(Enumerable.Range(0, 12).Select(i => $"pick{i}")), string.Join(",", picks));
    }

    private static void CheckLibraryParse()
    {
        var real = OllamaLibrary.Parse(RealCard()).Single();
        Check("真实卡片：名字", real.Name == "qwen3", real.Name);
        Check("真实卡片：说明", real.Description.StartsWith("Qwen3 is the latest generation"), real.Description);
        Check("真实卡片：能力", real.Capabilities.SequenceEqual(new[] { "tools", "thinking" }), string.Join(",", real.Capabilities));
        Check("真实卡片：规格", real.Sizes.SequenceEqual(new[] { "0.6b", "1.7b", "4b", "8b", "14b", "30b", "32b", "235b" }), string.Join(",", real.Sizes));
        Check("真实卡片：下载量", real.Pulls == "42.2M", real.Pulls);
        Check("真实卡片：能聊天且可以本地跑", real.IsChatModel && !real.IsCloudOnly);

        var models = OllamaLibrary.Parse(string.Concat(
            Card("mix", "Sizes", "tools 8x7b e4b 1.5b 270m 2t 8x22b"),
            Card("cloudonly", "Hosted", "tools cloud"),
            Card("cloudlocal", "Both", "tools cloud e4b 26b"),
            Card("vectors", "Embeddings", "embedding 384m"),
            Card("gate", "Rerank", "decision 4b"),
            Card("voice", "A Text-To-Speech model.", "2b"),
            Card("listener", "Hears things", "tools audio 8b"),
            Card("plain", "No badges", ""),
            Card("upper", "Case", "Tools"),
            Card("entities", "Fish &amp; chips &#39;n&#39; &lt;b&gt;", "8b"),
            Card("markup", "Has <b>bold</b>\n  and\tspaces", "8b"),
            Card("long", new string('x', 1000), "8b"),
            Card("nopulls", "No count", "8b", ""),
            Card("mix", "Duplicate", "8b"),
            Card("Upper-Case", "Bad", "8b"),
            Card("..%2f..%2fetc", "Bad", "8b"),
            Card("-dash", "Bad", "8b"),
            Card("has space", "Bad", "8b"),
            Card(new string('a', 101), "Bad", "8b"),
            "<link rel=\"preload\" href=\"/library/phantom\"><a href=\"/library/alsophantom\">x</a>"));

        OllamaLibraryModel Get(string name) => models.Single(m => m.Name == name);
        Check("解析：各种规格写法都认", Get("mix").Sizes.SequenceEqual(new[] { "8x7b", "e4b", "1.5b", "270m", "2t", "8x22b" }), string.Join(",", Get("mix").Sizes));
        Check("解析：规格不混进能力", Get("mix").Capabilities.SequenceEqual(new[] { "tools" }));
        Check("解析：一张卡片的徽章不串到下一张", Get("cloudonly").Sizes.Count == 0);
        Check("解析：纯云端", Get("cloudonly").IsCloudOnly);
        Check("解析：云端加规格的可以本地跑", !Get("cloudlocal").IsCloudOnly && Get("cloudlocal").Tags.SequenceEqual(new[] { "e4b", "26b" }));
        Check("解析：嵌入模型不能聊天", !Get("vectors").IsChatModel);
        Check("解析：decision 模型不能聊天", !Get("gate").IsChatModel);
        Check("解析：语音合成模型不能聊天", !Get("voice").IsChatModel);
        Check("解析：带 audio 的可以聊天", Get("listener").IsChatModel);
        Check("解析：没有规格时拉 latest", Get("plain").Tags.SequenceEqual(new[] { "latest" }));
        Check("解析：能力统一小写", Get("upper").Capabilities.SequenceEqual(new[] { "tools" }));
        Check("解析：实体解码", Get("entities").Description == "Fish & chips 'n' <b>", Get("entities").Description);
        Check("解析：去掉标签和多余空白", Get("markup").Description == "Has bold and spaces", Get("markup").Description);
        Check("解析：说明限长", Get("long").Description.Length == 300, Get("long").Description.Length.ToString());
        Check("解析：没有下载量也不丢模型", Get("nopulls").Pulls == "");
        Check("解析：重复的只留第一个", models.Count(m => m.Name == "mix") == 1 && Get("mix").Description == "Sizes");
        Check("解析：奇怪的名字不要", !models.Any(m => m.Name is "Upper-Case" or "-dash" or "has space" || m.Name.Contains('%') || m.Name.Length > 100),
            string.Join(",", models.Select(m => m.Name)));
        Check("解析：100 字符的名字可以", OllamaLibrary.Parse(Card(new string('a', 100), "ok")).Count == 1);
        Check("解析：不是卡片的链接不算", !models.Any(m => m.Name.Contains("phantom")));

        Check("解析：别的网页解析不出东西", OllamaLibrary.Parse("<html><body>maintenance</body></html>").Count == 0);
        var searched = new OllamaLibraryModel("alpha", "Mixture of experts", Array.Empty<string>(), Array.Empty<string>(), "");
        Check("搜索：名字不分大小写", searched.Matches("ALPHA"));
        Check("搜索：说明不分大小写", searched.Matches("MIXTURE"));
        Check("搜索：不沾边的不算", !searched.Matches("gemma"));
    }

    private static async Task CheckLibraryCacheAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var directory = NewLibraryCache();
        var net = new FakeOllamaCom { Html = Page(Card("alpha", "First", "tools 8b")) };
        OllamaLibrary Open(FakeOllamaCom handler) => new(handler, directory, () => now);

        Check("缓存：第一次打开没有缓存", await Open(net).LoadCachedAsync(default) is null);

        var models = await Open(net).RefreshAsync(default);
        Check("刷新：拿到列表", models?.Count == 31, models?.Count.ToString() ?? "null");
        Check("刷新：没有打到别的地址", net.Unexpected.IsEmpty, string.Join(",", net.Unexpected));

        var cached = await Open(net).LoadCachedAsync(default);
        Check("缓存：内容原样读回", cached is { IsFresh: true } && cached.Models[0] is { Name: "alpha", Description: "First", Pulls: "1.2K" }
                                 && cached.Models[0].Capabilities.SequenceEqual(new[] { "tools" }) && cached.Models[0].Tags.SequenceEqual(new[] { "8b" }),
            cached?.Models[0].ToString() ?? "null");

        now += TimeSpan.FromHours(12) - TimeSpan.FromMinutes(1);
        Check("缓存：12 小时内算新的", (await Open(net).LoadCachedAsync(default))!.IsFresh);
        now += TimeSpan.FromMinutes(1);
        Check("缓存：到 12 小时算旧的，但还能用", (await Open(net).LoadCachedAsync(default)) is { IsFresh: false, Models.Count: 31 });

        // 取不到新的时旧缓存不能被冲掉
        async Task<bool> KeepsCache(string name, Action<FakeOllamaCom> setup)
        {
            var broken = new FakeOllamaCom();
            setup(broken);
            var result = await Open(broken).RefreshAsync(default);
            var after = await Open(net).LoadCachedAsync(default);
            var ok = result is null && after?.Models[0].Name == "alpha";
            Check(name, ok, $"result={result?.Count} first={after?.Models[0].Name}");
            return ok;
        }

        await KeepsCache("刷新失败：断网时保留缓存", n => n.Offline = true);
        await KeepsCache("刷新失败：超时算失败不算取消", n => n.ListTimesOut = true);
        await KeepsCache("刷新失败：服务端报错保留缓存", n => n.ListStatus = HttpStatusCode.InternalServerError);
        await KeepsCache("刷新失败：页面改版解析不出来保留缓存", n => n.Html = "<html>new design</html>");
        await KeepsCache("刷新失败：响应大得离谱不读", n => n.Html = Page() + new string(' ', 9 * 1024 * 1024));
        await KeepsCache("刷新失败：只解析出 19 个不当真", n => n.Html = string.Concat(Enumerable.Range(0, 19).Select(i => Card($"few{i}", "x"))));

        var exact = new FakeOllamaCom { Html = string.Concat(Enumerable.Range(0, 20).Select(i => Card($"few{i}", "x"))) };
        Check("刷新：20 个算正常", (await new OllamaLibrary(exact, NewLibraryCache()).RefreshAsync(default))?.Count == 20);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Check("刷新：已取消时不发请求", await ThrowsAsync<OperationCanceledException>(() => Open(net).RefreshAsync(cancelled.Token)));

        var stuck = new FakeOllamaCom { ListGate = new TaskCompletionSource() };
        using var midway = new CancellationTokenSource();
        var pending = Open(stuck).RefreshAsync(midway.Token);
        while (stuck.ListRequests == 0) await Task.Delay(5);
        midway.Cancel();
        Check("刷新：请求途中取消要传出去，不能当成连不上", await ThrowsAsync<OperationCanceledException>(() => pending));

        var rewritten = NewLibraryCache();
        await new OllamaLibrary(new FakeOllamaCom { Html = Page(Card("older", "x")) }, rewritten).RefreshAsync(default);
        await new OllamaLibrary(new FakeOllamaCom { Html = Page(Card("newer", "x")) }, rewritten).RefreshAsync(default);
        var latest = await new OllamaLibrary(new FakeOllamaCom(), rewritten).LoadCachedAsync(default);
        Check("缓存：新列表覆盖旧的", latest?.Models[0].Name == "newer", latest?.Models[0].Name ?? "null");

        var listPath = Path.Combine(directory, "library.json");
        Check("缓存：确实写在指定目录", File.Exists(listPath));
        Check("缓存：写完不留临时文件", Directory.GetFiles(directory, "*.tmp").Length == 0);

        var original = File.ReadAllText(listPath);
        File.WriteAllText(listPath, original.Replace("\"filler29\"", "\"../../evil\""));
        var filtered = await Open(net).LoadCachedAsync(default);
        Check("缓存：被改过的名字不信", filtered?.Models.Count == 30 && filtered.Models.All(m => !m.Name.Contains('/')), filtered?.Models.Count.ToString() ?? "null");

        var badTag = Regex.Replace(original, @"(""Name"":""filler28"".*?""Sizes"":\[)""8b""", @"$1""../x""");
        Check("缓存：确实改到了规格", badTag != original);
        File.WriteAllText(listPath, badTag);
        var noBadTag = await Open(net).LoadCachedAsync(default);
        Check("缓存：被改过的规格不信", noBadTag?.Models.Count == 30 && noBadTag.Models.All(m => m.Sizes.All(s => !s.Contains('/'))), noBadTag?.Models.Count.ToString() ?? "null");

        File.WriteAllText(listPath, "{not json");
        Check("缓存：文件坏了当没有", await Open(net).LoadCachedAsync(default) is null);
        File.WriteAllText(listPath, """{"FetchedAt":"2026-01-01T00:00:00+00:00","Models":[{"Name":"a","Description":"","Capabilities":[],"Sizes":[],"Pulls":""}]}""");
        Check("缓存：太短的列表当没有", await Open(net).LoadCachedAsync(default) is null);

        var unwritable = Path.Combine(LibraryCacheRoot.Value, Guid.NewGuid().ToString("N"));
        File.WriteAllText(unwritable, "");
        Check("缓存：写不进去时刷新照样成功", (await new OllamaLibrary(new FakeOllamaCom(), unwritable).RefreshAsync(default))?.Count == 30);
    }

    private static async Task CheckLibrarySizesAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var directory = NewLibraryCache();
        var net = new FakeOllamaCom { Sizes = { ["qwen3:8b"] = 5225387677, ["qwen3:4b"] = 2497293444, ["solo:latest"] = 123456 } };
        OllamaLibrary Open(FakeOllamaCom handler) => new(handler, directory, () => now);
        var library = Open(net);

        var family = new OllamaLibraryModel("qwen3", "", new[] { "tools" }, new[] { "4b", "8b", "235b" }, "");
        var sizes = await library.GetSizesAsync(family, default);
        Check("体积：只算各层之和，不算 config", sizes.GetValueOrDefault("8b") == 5225387677 && sizes.GetValueOrDefault("4b") == 2497293444, string.Join(",", sizes));
        Check("体积：查不到的规格不在结果里", !sizes.ContainsKey("235b"));
        Check("体积：没打到别的地址", net.Unexpected.IsEmpty, string.Join(",", net.Unexpected));

        var before = net.Looked.Count;
        await library.GetSizesAsync(family, default);
        Check("体积：查到的不重复查，查不到的下次再试", net.Looked.Count == before + 1, $"{before}->{net.Looked.Count}");

        var offline = new FakeOllamaCom { Offline = true };
        Check("体积：换进程还记得", await Open(offline).GetSizeAsync("qwen3:8b", default) == 5225387677);
        now += TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1);
        Check("体积：7 天内不过期", await Open(offline).GetSizeAsync("qwen3:8b", default) == 5225387677);
        now += TimeSpan.FromMinutes(1);
        Check("体积：7 天后重新查", await Open(offline).GetSizeAsync("qwen3:8b", default) is null && offline.Looked.Contains("qwen3:8b"));

        Check("体积：不写标签查 latest", await Open(net).GetSizeAsync("solo", default) == 123456);

        var cloud = new FakeOllamaCom { Bodies = { ["a:latest"] = """{"layers":[]}""" } };
        var bad = new OllamaLibrary(cloud, NewLibraryCache());
        Check("体积：云端模型没有层当查不到", await bad.GetSizeAsync("a", default) is null);
        cloud.Bodies["b:latest"] = """{"layers":[{"size":-5}]}""";
        Check("体积：负数不算", await bad.GetSizeAsync("b", default) is null);
        cloud.Bodies["c:latest"] = "<html>";
        Check("体积：不是 JSON 不算", await bad.GetSizeAsync("c", default) is null);
        cloud.Bodies["d:latest"] = """{"layers":[{"size":"huge"}]}""";
        Check("体积：字段类型不对不算", await bad.GetSizeAsync("d", default) is null);
        cloud.Bodies["e:latest"] = """{"config":{"size":10}}""";
        Check("体积：没有 layers 不算", await bad.GetSizeAsync("e", default) is null);

        var slow = new FakeOllamaCom { ManifestTimesOut = true };
        Check("体积：超时当查不到，不算取消", await new OllamaLibrary(slow, NewLibraryCache()).GetSizeAsync("a", default) is null);

        cloud.Bodies["a:latest"] = ManifestBody(1000);
        Check("体积：失败结果不缓存", await bad.GetSizeAsync("a", default) == 1000);

        foreach (var name in new[] { "../../admin", "hf.co/org/model:Q4", "Upper", "a b", "a:b/c", "a:../x" })
        {
            var strict = new FakeOllamaCom();
            var result = await new OllamaLibrary(strict, NewLibraryCache()).GetSizeAsync(name, default);
            Check($"体积：{name} 不发请求", result is null && strict.Looked.IsEmpty && strict.Unexpected.IsEmpty, string.Join(",", strict.Unexpected));
        }

        var busy = new FakeOllamaCom();
        var tags = Enumerable.Range(0, 12).Select(i => $"{i + 1}b").ToList();
        foreach (var tag in tags) busy.Sizes[$"big:{tag}"] = 1000;
        var many = await new OllamaLibrary(busy, NewLibraryCache()).GetSizesAsync(new OllamaLibraryModel("big", "", Array.Empty<string>(), tags, ""), default);
        Check("体积：规格多时全部查到", many.Count == 12, many.Count.ToString());
        Check("体积：并发不超过 4 个", busy.Peak == 4, busy.Peak.ToString());

        var stuck = new FakeOllamaCom { ManifestGate = new TaskCompletionSource() };
        using var cancel = new CancellationTokenSource();
        var pending = new OllamaLibrary(stuck, NewLibraryCache()).GetSizeAsync("slow", cancel.Token);
        while (stuck.Looked.IsEmpty) await Task.Delay(5);
        cancel.Cancel();
        Check("体积：可以取消", await ThrowsAsync<OperationCanceledException>(() => pending));

        var freed = new FakeOllamaCom { Sizes = { ["ok:latest"] = 1000 } };
        var shared = new OllamaLibrary(freed, NewLibraryCache());
        for (var i = 0; i < 6; i++)
        {
            using var gone = new CancellationTokenSource();
            gone.Cancel();
            await CaptureAsync(() => shared.GetSizeAsync("ok", gone.Token));
        }
        Check("体积：取消不占着并发名额", await shared.GetSizeAsync("ok", default).WaitAsync(TimeSpan.FromSeconds(5)) == 1000);
    }

    private static string LibraryPage() => Page(
        Card("alpha", "First family.", "tools 4b 8b 70b", "3.5M"),
        Card("bravo", "Second family, quite heavy.", "tools 20b"),
        Card("vectors", "Embeddings.", "embedding 384m"),
        Card("cloudy", "Hosted only.", "tools cloud"),
        Card("speaker", "A text-to-speech model.", "2b"),
        Card("plainmodel", "No sizes at all.", ""));

    private static bool Sized(OllamaLibraryRow row) => row.Options.All(o => o.SizeBytes > 0);

    private static FakeOllamaCom LibraryNet()
    {
        var net = new FakeOllamaCom
        {
            Html = LibraryPage(),
            Sizes =
            {
                ["alpha:4b"] = 2 * GB,
                ["alpha:8b"] = 5 * GB,
                ["alpha:70b"] = 40 * GB,
                ["bravo:20b"] = 20 * GB,
                ["plainmodel:latest"] = GB
            }
        };
        for (var i = 0; i < 30; i++) net.Sizes[$"filler{i:00}:8b"] = 4 * GB;
        return net;
    }

    private static void CheckLibraryWindow()
    {
        OllamaLibrary Library(FakeOllamaCom net, string? cache = null, Func<DateTimeOffset>? now = null) => new(net, cache ?? NewLibraryCache(), now);

        var installed = new OllamaInstallation(@"C:\Ollama", "0.40.13");

        // 主流程：好电脑，本地服务在跑
        var net = LibraryNet();
        var api = new FakeApi { Running = true, PullAddsModel = true };
        var installer = new FakeDeployInstaller(installed) { Api = api, FreeSpace = 200 * GB };
        var window = new ManagerWindow(installer, api, model: "old-model", library: Library(net), hardware: new OllamaHardware(32 * GB, 12 * GB, "Test GPU"));

        Pump(() => window.Text("Tb_Hardware").Contains("模型盘"));
        Check("模型库：只是打开管理器不联网", net.ListRequests == 0 && net.Looked.IsEmpty);
        var hardware = window.Text("Tb_Hardware");
        Check("摘要：内存", hardware.Contains("内存 32.0 GB"), hardware);
        Check("摘要：显卡和显存", hardware.Contains("显卡 Test GPU，显存 12.0 GB"), hardware);
        Check("摘要：模型盘剩余", hardware.Contains("模型盘剩余 200.0 GB"), hardware);
        Check("摘要：能力上限", window.Text("Tb_Capacity") == "能流畅运行约 9.2 GB 以内的模型，约 30.8 GB 以内能跑但比较慢。", window.Text("Tb_Capacity"));
        Check("摘要：配置够时没有警告", !window.Shown("Tb_SlowWarning"));

        window.OpenLibrary();
        Check("模型库：打开页面才联网", net.ListRequests == 1, net.ListRequests.ToString());
        var names = window.Library.Select(r => r.Name).ToList();
        Check("模型库：按热度列出能聊天的本地模型", names.Take(3).SequenceEqual(new[] { "alpha", "bravo", "plainmodel" }), string.Join(",", names.Take(5)));
        Check("模型库：不列嵌入、云端和语音模型", !names.Intersect(new[] { "vectors", "cloudy", "speaker" }).Any());
        Check("模型库：显示数量", window.Text("Tb_LibraryState").Contains($"共 {names.Count} 个模型"), window.Text("Tb_LibraryState"));
        Check("模型库：不显示重试", !window.Shown("Btn_LibraryRetry"));

        var realized = window.RealizedRows();
        Check("模型库：只生成看得见的行", realized > 0 && realized < names.Count / 2, $"{realized}/{names.Count}");

        var alpha = window.Library[0];
        Check("模型库：显示能力和下载量", alpha.Details == "tools · 3.5M 次下载", alpha.Details);
        Pump(() => Sized(alpha));
        Check("模型库：默认选能流畅运行的最大规格", alpha.Selected.Tag == "8b" && alpha.Selected.Model == "alpha:8b", alpha.Selected.ToString());
        Check("模型库：徽章说得动不动得动", alpha is { Fit: OllamaFit.Smooth, FitText: "能流畅运行" }, alpha.FitText);
        Check("模型库：下拉项带体积", alpha.Options.Select(o => o.Text).SequenceEqual(new[] { "4b · 2.0 GB", "8b · 5.0 GB", "70b · 40.0 GB" }), string.Join(",", alpha.Options.Select(o => o.Text)));
        Check("模型库：下拉框真的显示了选中项", window.ComboText("alpha") == "8b · 5.0 GB", window.ComboText("alpha"));
        Pump(() => Sized(window.Library[1]));
        Check("模型库：放不下显存的标成慢", window.Library[1] is { Fit: OllamaFit.Slow, FitText: "能跑，但比较慢" }, window.Library[1].FitText);
        Check("模型库：滚不到的行不查体积", net.Looked.Count(k => k.StartsWith("filler")) < 30, net.Looked.Count.ToString());

        Pump(() => window.Catalog.Any(r => r.Name == "alpha:8b"));
        var catalog = window.Catalog;
        Check("推荐页：第一行是推荐模型", catalog[0] is { Badge: "推荐", Fit: OllamaFit.Smooth } && catalog[0].Name == Recommended.Name, catalog[0].ToString());
        Check("推荐页：推荐标记只有一个", catalog.Count(r => r.Badge == "推荐") == 1);
        Check("推荐页：其余标出适配结果", catalog.Skip(1).All(r => r.Badge == "能流畅运行"), string.Join(",", catalog.Select(r => r.Badge)));
        Check("推荐页：从模型库补上能流畅运行的", catalog.Single(r => r.Name == "alpha:8b") is { Size: "5.0 GB", Description: "First family." });
        Check("推荐页：带不动的不补", !catalog.Any(r => r.Name.StartsWith("bravo")));
        Check("推荐页：不能调工具的不补", !catalog.Any(r => r.Name.StartsWith("filler")));

        Check("模型库：没下载的行显示下载", alpha.Action is { Text: "下载", Enabled: true, Installed: false }, alpha.Action.ToString());

        window.ClickLibrary("alpha");
        window.Finish();
        Check("模型库：下载选中的规格", api.Pulled.SequenceEqual(new[] { "alpha:8b" }), string.Join(",", api.Pulled));
        Check("模型库：拿体积查了磁盘", installer.FreeSpaceQueries.Count > 0);
        Check("模型库：下完变成使用", window.Library[0].Action is { Text: "使用", Installed: true }, window.Library[0].Action.ToString());

        window.ClickLibrary("alpha");
        Check("模型库：已下载的点了是选用", window.Chosen.SequenceEqual(new[] { ("alpha:8b", Url) }) && api.Pulled.Count == 1, string.Join(",", window.Chosen));
        Pump(() => window.Library[0].Action.Text == "使用中");
        Check("模型库：选用后不能再点", window.Library[0].Action is { Text: "使用中", Enabled: false }, window.Library[0].Action.ToString());
        Check("模型库：选用后行里的按钮也变", window.LibraryButton("alpha") == "使用中", window.LibraryButton("alpha"));

        Check("模型库：能流畅运行是绿色", window.LibraryBadge("alpha") == "SuccessBrush:能流畅运行", window.LibraryBadge("alpha"));
        Check("模型库：放不下显存是黄色", window.LibraryBadge(window.Library[1].Name).StartsWith("WarningBrush:"), window.LibraryBadge(window.Library[1].Name));
        window.PickLibrary("alpha", 2);
        Check("模型库：下拉框选中写回这一行", alpha.Selected.Tag == "70b", alpha.Selected.Tag);
        Check("模型库：切规格时徽章跟着变", alpha is { Fit: OllamaFit.TooLarge, FitText: "这台电脑带不动" }, alpha.FitText);
        Check("模型库：切规格时按钮跟着变", alpha.Action is { Text: "下载", Enabled: true }, alpha.Action.ToString());
        Check("模型库：行里的徽章跟着变", window.LibraryBadge("alpha") == "ErrorBrush:这台电脑带不动", window.LibraryBadge("alpha"));
        Check("模型库：行里的按钮跟着变", window.LibraryButton("alpha") == "下载", window.LibraryButton("alpha"));
        alpha.Selected = null!;
        Check("模型库：WPF 写回 null 时不丢选中项", alpha.Selected.Tag == "70b");

        window.ClickLibrary("alpha");
        window.Finish();
        Check("确认：带不动的模型先问", window.Confirms.Count == 1 && window.Confirms[0].Contains("alpha:70b") && window.Confirms[0].Contains("40.0 GB"), string.Join("|", window.Confirms));
        Check("确认：拒绝就不下载", api.Pulled.Count == 1);
        window.Answer = true;
        window.ClickLibrary("alpha");
        window.Finish();
        Check("确认：同意才下载", api.Pulled.Last() == "alpha:70b", string.Join(",", api.Pulled));

        window.Search("alpha");
        Check("搜索：按名字", window.Library.Single().Name == "alpha");
        Check("搜索：显示筛选后的数量", window.Text("Tb_LibraryState").Contains("共 1 个模型"), window.Text("Tb_LibraryState"));
        window.Search("quite heavy");
        Check("搜索：按说明", window.Library.Single().Name == "bravo");
        window.Search("zzz");
        Check("搜索：没有结果时说明", window.Library.Count == 0 && window.Text("Tb_LibraryState") == "没有找到匹配的模型。", window.Text("Tb_LibraryState"));
        window.Search("");
        Check("搜索：清空后恢复", window.Library.Count == names.Count, window.Library.Count.ToString());

        var pullGate = new TaskCompletionSource();
        api.PullGate = pullGate;
        api.PullAddsModel = false;
        window.Answer = false;
        Pump(() => Sized(window.Library[1]));
        window.ClickLibrary("bravo");
        Pump(() => api.Pulled.Last() == "bravo:20b");
        Check("进行中：行按钮不可点", !window.Library[0].Action.Enabled && !window.Library[1].Action.Enabled);
        Check("进行中：列表还能滚动", window.Enabled("Items_Library"));
        pullGate.SetResult();
        window.Finish();
        Check("结束后：行按钮恢复", window.Library[1].Action.Enabled, window.Library[1].Action.ToString());
        api.PullGate = null;

        window.Find<TextBox>("Tb_CustomModel").Text = "alpha:70b";
        var refused = api.Pulled.Count;
        var confirms = window.Confirms.Count;
        window.Click("Btn_PullCustom");
        window.Finish();
        Check("手填：查到体积后一样先问", window.Confirms.Count == confirms + 1 && api.Pulled.Count == refused, string.Join("|", window.Confirms));

        var other = window.Find<TextBox>("Tb_CustomModel");
        other.Text = "bravo:unknown";
        window.Click("Btn_PullCustom");
        window.Finish();
        Check("手填：查不到体积照样下载", api.Pulled.Last() == "bravo:unknown", string.Join(",", api.Pulled));

        var remoteUrl = "http://192.168.1.5:11434";
        Call(window.Window, "Rebind", remoteUrl, "", (Func<string, string, bool>)((_, _) => true));
        Pump(() => !window.Catalog.Any(r => r.Name == "alpha:8b"));
        Check("切到远程：别的电脑判断不了，不留本机的推荐", window.Catalog.All(r => r.Badge == "" && r.Fit == OllamaFit.Unknown), string.Join(",", window.Catalog.Select(r => r.Badge)));
        Check("切到远程：补上的模型不留下", window.Catalog.Count == OllamaModelCatalog.Models.Count, string.Join(",", window.Catalog.Select(r => r.Name)));
        Check("切到远程：模型库按新地址重建", window.Library.All(r => r.FitText == ""));
        window.Close();

        // 还没选模型：推荐页补上的能直接下，下完自动选用
        var firstApi = new FakeApi { Running = true, PullAddsModel = true };
        var first = new ManagerWindow(new FakeDeployInstaller(installed) { Api = firstApi, FreeSpace = 200 * GB }, firstApi, library: Library(LibraryNet()), hardware: StrongHardware);
        first.OpenLibrary();
        Pump(() => first.Catalog.Any(r => r.Name == "alpha:8b"));
        first.ClickCatalog("alpha:8b");
        first.Finish();
        Check("推荐页：补上的模型能直接下载", firstApi.Pulled.SequenceEqual(new[] { "alpha:8b" }), string.Join(",", firstApi.Pulled));
        Check("推荐页：第一个模型下完自动选用", first.Chosen.SequenceEqual(new[] { ("alpha:8b", Url) }), string.Join(",", first.Chosen));
        first.OpenLibrary();
        Pump(() => first.Library[0].Action.Text == "使用中");
        Check("两页状态一致：模型库也显示使用中", first.Library[0].Action is { Text: "使用中", Enabled: false }, first.Library[0].Action.ToString());
        first.Close();

        // 滚得快：刚露面就划走的行不发请求。这页全是进不了推荐页的模型，免得热门查询替它们查了
        var fast = LibraryNet();
        fast.Html = Page();
        var scrolling = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, library: Library(fast), hardware: StrongHardware);
        scrolling.OpenLibrary(until: () => scrolling.RealizedRows() > 0);
        Pump(() => true);
        scrolling.ScrollLibraryToEnd();
        // 要等过虚拟化卸载旧行的那 500 毫秒，卸载之后也不能再去查
        var settle = DateTime.UtcNow.AddMilliseconds(800);
        Pump(() => DateTime.UtcNow >= settle);
        var looked = fast.Looked.ToList();
        Check("滚动：划走的行不查体积", !looked.Contains("filler00:8b"), string.Join(",", looked));
        Check("滚动：停下的行要查", looked.Contains("filler29:8b"), string.Join(",", looked));
        scrolling.Close();

        // 划走又马上划回来：行还没卸载，不会再有 Loaded，延迟到了也得查
        var bounced = LibraryNet();
        bounced.Html = Page();
        var bounce = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, library: Library(bounced), hardware: StrongHardware);
        bounce.OpenLibrary(until: () => bounce.RealizedRows() > 0);
        Pump(() => true);
        bounce.ScrollLibraryToEnd();
        Pump(() => true);
        bounce.ScrollLibraryToStart();
        Pump(() => bounced.Looked.Contains("filler00:8b"));
        Check("划回来：还没卸载的行也要查", bounced.Looked.Contains("filler00:8b"), string.Join(",", bounced.Looked));
        var bounceSettle = DateTime.UtcNow.AddMilliseconds(800);
        Pump(() => DateTime.UtcNow >= bounceSettle);
        Check("划回来：这次划走的行不查", !bounced.Looked.Contains("filler29:8b"), string.Join(",", bounced.Looked));
        bounce.Close();

        // 切到别的页：行卸载了，等着的查询要结束，不然切回来时新旧两个各查一遍
        var flipped = LibraryNet();
        flipped.Html = Page();
        flipped.ManifestGate = new TaskCompletionSource();
        var flip = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, library: Library(flipped), hardware: StrongHardware);
        flip.OpenLibrary(until: () => flip.RealizedRows() > 0);
        flip.Find<TabControl>("Tabs").SelectedIndex = 0;
        Pump(() => true);
        var flipSettle = DateTime.UtcNow.AddMilliseconds(400);
        Pump(() => DateTime.UtcNow >= flipSettle);
        Check("切走：卸载的行不查体积", !flipped.Looked.Contains("filler00:8b"), string.Join(",", flipped.Looked));
        flip.OpenLibrary(until: () => flipped.Looked.Contains("filler00:8b"));
        var flipBack = DateTime.UtcNow.AddMilliseconds(400);
        Pump(() => DateTime.UtcNow >= flipBack);
        Check("切回来：只查一遍", flipped.Looked.Count(k => k == "filler00:8b") == 1, string.Join(",", flipped.Looked));

        // 从列表上摘下来、Unloaded 还没轮到的行：量过尺寸，但已经不在列表里
        var detached = new Border { Width = 100, Height = 100 };
        detached.Measure(new System.Windows.Size(100, 100));
        detached.Arrange(new System.Windows.Rect(0, 0, 100, 100));
        Check("摘下来的行不算在屏幕上", Call(flip.Window, "IsOnScreen", detached) is false);
        flipped.ManifestGate.SetResult();
        flip.Close();

        // 换下拉项时 WPF 会把同一项写回来，这不算用户选了规格
        OllamaTagOption Option(string tag, long size) => new(tag, $"plain:{tag}", size, OllamaFit.Unknown, "", tag);
        var plain = new OllamaLibraryRow(new OllamaLibraryModel("plain", "", Array.Empty<string>(), new[] { "4b", "8b" }, ""), "",
            new[] { Option("4b", 0), Option("8b", 0) }, _ => new OllamaRowAction("下载", true, false));
        plain.Selected = plain.Selected;
        plain.SetOptions(new[] { Option("4b", GB), Option("8b", 2 * GB) }, "8b");
        Check("写回原选中项：不算用户选的", plain.Selected.Tag == "8b", plain.Selected.Tag);

        // 行回到屏幕上会再查一遍，内容没变就不能换列表，不然用户展开着的下拉框会被收起
        var unchanged = plain.Options;
        var raised = new List<string?>();
        plain.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        plain.SetOptions(new[] { Option("4b", GB), Option("8b", 2 * GB) }, "8b");
        Check("重查体积：内容没变不动下拉框", ReferenceEquals(plain.Options, unchanged) && raised.Count == 0, string.Join(",", raised));

        // 体积还没回来时用户已经选了规格，结果回来不能改掉
        var gated = LibraryNet();
        gated.ManifestGate = new TaskCompletionSource();
        var waiting = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, library: Library(gated), hardware: StrongHardware);
        waiting.OpenLibrary();
        var row = waiting.Library[0];
        Check("体积没回来：显示大小未知", row.Options[0].Text == "4b · 大小未知" && row.FitText == "", row.Options[0].Text);
        Pump(() => gated.Looked.Contains("alpha:70b"));
        row.Selected = row.Options[2];
        gated.ManifestGate.SetResult();
        Pump(() => Sized(row));
        Check("体积回来：用户选的规格不被改掉", row.Selected.Tag == "70b" && row.Selected.SizeBytes == 40 * GB && row.Fit == OllamaFit.TooLarge, row.Selected.ToString());
        Check("体积回来：下拉框跟上新选项", waiting.ComboText("alpha") == "70b · 40.0 GB", waiting.ComboText("alpha"));
        waiting.Close();

        // 部分规格查不到
        var partial = LibraryNet();
        partial.Sizes.Remove("alpha:70b");
        var incomplete = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, library: Library(partial), hardware: StrongHardware);
        incomplete.OpenLibrary();
        var missing = incomplete.Library[0];
        Pump(() => missing.Selected.SizeBytes > 0);
        Check("部分查不到：能查到的照常选", missing.Selected.Tag == "8b", missing.Selected.ToString());
        Check("部分查不到：标成大小未知", missing.Options[2].Text == "70b · 大小未知", missing.Options[2].Text);
        Check("部分查不到：下次滚回来再查", !Sized(missing));
        Check("部分查不到：下拉框显示选中项", incomplete.ComboText("alpha") == "8b · 5.0 GB", incomplete.ComboText("alpha"));

        void ReopenLibrary(Func<bool> until)
        {
            // 不让消息循环转一下，标签页其实没切走，行也不会重新加载
            incomplete.Find<TabControl>("Tabs").SelectedIndex = 0;
            Pump(() => true);
            incomplete.OpenLibrary(until);
        }

        var lookedBefore = partial.Looked.Count(k => k == "alpha:70b");
        ReopenLibrary(() => partial.Looked.Count(k => k == "alpha:70b") > lookedBefore);
        var pause = DateTime.UtcNow.AddMilliseconds(300);
        Pump(() => DateTime.UtcNow >= pause);
        Check("切回来：查不到的再查一次", partial.Looked.Count(k => k == "alpha:70b") > lookedBefore);
        Check("再次查到一样的结果：下拉框不被清空", incomplete.ComboText("alpha") == "8b · 5.0 GB", incomplete.ComboText("alpha"));

        partial.Sizes["alpha:70b"] = 40 * GB;
        var lookedAgain = partial.Looked.Count(k => k == "alpha:70b");
        ReopenLibrary(() => Sized(incomplete.Library[0]));
        Check("后来查到了：确实又查了", partial.Looked.Count(k => k == "alpha:70b") > lookedAgain);
        Check("后来查到了：补上体积", incomplete.Library[0].Options[2].Text == "70b · 40.0 GB", incomplete.Library[0].Options[2].Text);
        Check("后来查到了：下拉框不被清空", incomplete.ComboText("alpha") == "8b · 5.0 GB", incomplete.ComboText("alpha"));
        incomplete.Close();

        // 缓存够新就不联网
        var cache = NewLibraryCache();
        var seeded = Library(LibraryNet(), cache);
        Task.Run(() => seeded.RefreshAsync(default)).GetAwaiter().GetResult();

        var quiet = LibraryNet();
        var cachedWindow = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, library: Library(quiet, cache), hardware: StrongHardware);
        cachedWindow.OpenLibrary();
        Check("缓存：够新就不取列表", quiet.ListRequests == 0 && cachedWindow.Library[0].Name == "alpha", quiet.ListRequests.ToString());
        cachedWindow.Close();

        // 缓存过期又断网
        var later = DateTimeOffset.UtcNow.AddHours(13);
        var offline = LibraryNet();
        offline.Offline = true;
        var staleWindow = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, library: Library(offline, cache, () => later), hardware: StrongHardware);
        staleWindow.OpenLibrary(until: () => staleWindow.Shown("Btn_LibraryRetry"));
        Check("过期又断网：继续用旧列表", staleWindow.Library.Count > 0, staleWindow.Library.Count.ToString());
        Check("过期又断网：说明是旧的", staleWindow.Text("Tb_LibraryState") == "暂时连不上 ollama.com，显示的是上次保存的列表。", staleWindow.Text("Tb_LibraryState"));
        Check("过期又断网：给重试", staleWindow.Shown("Btn_LibraryRetry"));

        offline.Offline = false;
        offline.Html = Page(Card("zulu", "Newer list.", "tools 4b"));
        var attempts = offline.ListRequests;
        staleWindow.Click("Btn_LibraryRetry");
        Pump(() => staleWindow.Library.Any(r => r.Name == "zulu"));
        Check("重试：拿到新列表", offline.ListRequests == attempts + 1 && staleWindow.Library[0].Name == "zulu", staleWindow.Library.FirstOrDefault()?.Name ?? "");
        Pump(() => !staleWindow.Shown("Btn_LibraryRetry"));
        Check("重试：成功后不再提示", !staleWindow.Text("Tb_LibraryState").Contains("连不上"), staleWindow.Text("Tb_LibraryState"));
        staleWindow.Close();

        // 第一次打开就没网
        var cut = LibraryNet();
        cut.Offline = true;
        var failing = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, library: Library(cut), hardware: StrongHardware);
        failing.OpenLibrary(until: () => failing.Shown("Btn_LibraryRetry"));
        Check("没网没缓存：说清楚", failing.Text("Tb_LibraryState").Contains("暂时获取不到"), failing.Text("Tb_LibraryState"));
        Check("没网没缓存：推荐页不受影响", failing.Catalog.Count == OllamaModelCatalog.Models.Count, failing.Catalog.Count.ToString());
        var tries = cut.ListRequests;
        failing.Click("Btn_LibraryRetry");
        Pump(() => cut.ListRequests > tries && failing.Shown("Btn_LibraryRetry"));
        Check("没网没缓存：重试失败还能再试", failing.Shown("Btn_LibraryRetry") && failing.Text("Tb_LibraryState").Contains("暂时获取不到"), failing.Text("Tb_LibraryState"));
        failing.Close();

        // 远程地址
        var far = LibraryNet();
        var farWindow = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, url: remoteUrl, library: Library(far), hardware: new OllamaHardware(4 * GB, 0));
        Check("远程：说明看不到那台电脑的配置", farWindow.Text("Tb_Hardware").Contains("192.168.1.5") && farWindow.Text("Tb_Hardware").Contains("看不到"), farWindow.Text("Tb_Hardware"));
        Check("远程：不说本机能力", farWindow.Text("Tb_Capacity") == "", farWindow.Text("Tb_Capacity"));
        Check("远程：不按本机配置警告", !farWindow.Shown("Tb_SlowWarning"));
        Check("远程：名单不标推荐和适配", farWindow.Catalog.All(r => r.Badge == ""));
        farWindow.OpenLibrary();
        var farRow = farWindow.Library[0];
        Pump(() => Sized(farRow));
        Check("远程：有体积但不判断", farRow.Fit == OllamaFit.Unknown && farRow.FitText == "" && farRow.Selected.SizeBytes > 0, farRow.Selected.ToString());
        Check("远程：默认选最小规格", farRow.Selected.Tag == "4b", farRow.Selected.Tag);
        Check("远程：不从模型库补推荐", farWindow.Catalog.Count == OllamaModelCatalog.Models.Count && far.Looked.All(k => !k.StartsWith("bravo") || farWindow.RealizedRows() > 1));
        farWindow.Close();

        // 上面那台本机本来就没有流畅的模型，要本机够强才看得出“不按本机补推荐”
        var farStrongNet = LibraryNet();
        var farStrong = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, url: remoteUrl, library: Library(farStrongNet), hardware: StrongHardware);
        farStrong.OpenLibrary();
        Pump(() => Sized(farStrong.Library[0]));
        var farSettle = DateTime.UtcNow.AddMilliseconds(300);
        Pump(() => DateTime.UtcNow >= farSettle);
        Check("远程：本机再强也不补推荐", Sized(farStrong.Library[0]) && farStrong.Catalog.Count == OllamaModelCatalog.Models.Count, string.Join(",", farStrong.Catalog.Select(r => r.Name)));
        farStrong.Close();

        // 只看下载模型页、从没点开模型库的用户，也要补上热门模型
        var catalogNet = LibraryNet();
        var catalogOnly = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, library: Library(catalogNet), hardware: StrongHardware);
        catalogOnly.Find<TabControl>("Tabs").SelectedIndex = 1;
        Pump(() => catalogOnly.Catalog.Any(r => r.Name.StartsWith("alpha")));
        Check("推荐页：不用点开模型库也能补上", catalogOnly.Catalog.Any(r => r.Name.StartsWith("alpha")), string.Join(",", catalogOnly.Catalog.Select(r => r.Name)));
        catalogOnly.Close();

        // 推荐页补热门：不能聊天和纯云端的不算，够数就停
        var hot = LibraryNet();
        hot.Html = Page(new[] { Card("talker", "A text-to-speech model.", "tools 2b"), Card("cloudy", "Hosted only.", "tools cloud") }
            .Concat(Enumerable.Range(0, 6).Select(i => Card($"hot{i}", $"Popular {i}.", "tools 4b"))).ToArray());
        hot.Sizes["talker:2b"] = GB;
        for (var i = 0; i < 6; i++) hot.Sizes[$"hot{i}:4b"] = 2 * GB;
        var crowded = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, library: Library(hot), hardware: StrongHardware);
        crowded.OpenLibrary(until: () => crowded.Catalog.Count(r => r.Name.StartsWith("hot")) >= 4);
        var hotSettle = DateTime.UtcNow.AddMilliseconds(300);
        Pump(() => DateTime.UtcNow >= hotSettle);
        var added = crowded.Catalog.Where(r => r.Name.StartsWith("hot")).Select(r => r.Name).Order().ToList();
        Check("推荐页：最多补 4 个，取最热门的", added.SequenceEqual(new[] { "hot0:4b", "hot1:4b", "hot2:4b", "hot3:4b" }), string.Join(",", added));
        Check("推荐页：语音合成模型不补", crowded.Catalog.All(r => !r.Name.StartsWith("talker")), string.Join(",", crowded.Catalog.Select(r => r.Name)));
        Check("推荐页：纯云端不去查体积", !hot.Looked.Contains("cloudy:latest"), string.Join(",", hot.Looked));
        crowded.Close();

        // 切走时还在路上的热门查询要作废，不然远程地址的推荐页会冒出按本机配置选的模型
        var racing = LibraryNet();
        racing.ManifestGate = new TaskCompletionSource();
        var inFlight = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, library: Library(racing), hardware: StrongHardware);
        inFlight.OpenLibrary(until: () => racing.Looked.Contains("alpha:70b"));
        Call(inFlight.Window, "Rebind", remoteUrl, "", (Func<string, string, bool>)((_, _) => true));
        racing.ManifestGate.SetResult();
        var raceSettle = DateTime.UtcNow.AddMilliseconds(300);
        Pump(() => DateTime.UtcNow >= raceSettle);
        Check("切走：在途的推荐作废", inFlight.Catalog.Count == OllamaModelCatalog.Models.Count, string.Join(",", inFlight.Catalog.Select(r => r.Name)));
        inFlight.Close();

        // 查询已经结束、回调还排在 UI 线程上时切走，一样要作废。故意不转消息循环来卡住这个窗口
        var landing = LibraryNet();
        landing.ManifestGate = new TaskCompletionSource();
        var queued = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, library: Library(landing), hardware: StrongHardware);
        queued.OpenLibrary(until: () => landing.Looked.Contains("alpha:70b"));
        landing.ManifestGate.SetResult();
        Thread.Sleep(300);
        Call(queued.Window, "Rebind", remoteUrl, "", (Func<string, string, bool>)((_, _) => true));
        var landSettle = DateTime.UtcNow.AddMilliseconds(300);
        Pump(() => DateTime.UtcNow >= landSettle);
        Check("切走：排着队的结果也作废", queued.Catalog.Count == OllamaModelCatalog.Models.Count, string.Join(",", queued.Catalog.Select(r => r.Name)));
        queued.Close();

        // 能力这一行：cloud 对本地规格没意义；新模型页面上可能还没有下载量
        var unusual = LibraryNet();
        unusual.Html = Page(Card("mixed", "Local and cloud.", "tools cloud 8b"), Card("brandnew", "Just published.", "vision 8b", ""));
        var detailed = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, library: Library(unusual), hardware: StrongHardware);
        detailed.OpenLibrary();
        var mixedRow = detailed.Library.Single(r => r.Name == "mixed");
        var brandNewRow = detailed.Library.Single(r => r.Name == "brandnew");
        Check("模型库：不显示 cloud 徽章", mixedRow.Details == "tools · 1.2K 次下载", mixedRow.Details);
        Check("模型库：没有下载量就不写", brandNewRow.Details == "vision", brandNewRow.Details);
        detailed.Close();

        // 列表还没回来时在两页之间切换，不能再发一次请求
        var held = LibraryNet();
        held.ListGate = new TaskCompletionSource();
        var pending = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, library: Library(held), hardware: StrongHardware);
        pending.OpenLibrary(until: () => held.ListRequests > 0);
        Check("加载中：说正在获取，不说失败", pending.Text("Tb_LibraryState") == "正在获取模型列表…", pending.Text("Tb_LibraryState"));
        Check("加载中：不给重试", !pending.Shown("Btn_LibraryRetry"));
        foreach (var index in new[] { 1, 2, 1, 2 })
        {
            pending.Find<TabControl>("Tabs").SelectedIndex = index;
            Pump(() => true);
        }
        var switched = DateTime.UtcNow.AddMilliseconds(200);
        Pump(() => DateTime.UtcNow >= switched);
        Check("加载中切页：切页真的发生了", pending.Find<TabControl>("Tabs").SelectedIndex == 2);
        Check("加载中切页：不重复请求", held.ListRequests == 1, held.ListRequests.ToString());
        held.ListGate.SetResult();
        Pump(() => pending.Library.Count > 0);
        Check("加载中切页：放行后列表正常出来", pending.Library.Count > 0 && held.ListRequests == 1, held.ListRequests.ToString());
        pending.Close();

        // 查不出磁盘空间时 GetFreeSpace 给 long.MaxValue，界面上不能冒出“剩余 8 EB”
        var unknownDisk = new FakeDeployInstaller(installed) { Api = api };
        var noDisk = new ManagerWindow(unknownDisk, api, library: Library(LibraryNet()), hardware: StrongHardware);
        Pump(() => unknownDisk.FreeSpaceQueries.Count > 0);
        var diskSettle = DateTime.UtcNow.AddMilliseconds(500);
        Pump(() => DateTime.UtcNow >= diskSettle);
        Check("磁盘查不出：确实查过", unknownDisk.FreeSpaceQueries.Count > 0);
        Check("磁盘查不出：不显示模型盘剩余", !noDisk.Text("Tb_Hardware").Contains("模型盘"), noDisk.Text("Tb_Hardware"));
        noDisk.Close();

        // 核显的那点共享显存不算显卡，也不把它报给用户
        var integrated = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, library: Library(LibraryNet()), hardware: new OllamaHardware(16 * GB, GB / 2, "Intel UHD"));
        Check("核显：说没有独立显卡", integrated.Text("Tb_Hardware").Contains("没有检测到独立显卡"), integrated.Text("Tb_Hardware"));
        Check("核显：不报显存", !integrated.Text("Tb_Hardware").Contains("显存"), integrated.Text("Tb_Hardware"));
        integrated.Close();

        // 配置偏低：能跑但慢
        var slowPc = new ManagerWindow(new FakeDeployInstaller(installed) { Api = api }, api, library: Library(LibraryNet()), hardware: new OllamaHardware(8 * GB, 0, "", 8, HasAvx2: false));
        Check("配置偏低：警告回复慢", slowPc.Shown("Tb_SlowWarning") && slowPc.Text("Tb_SlowWarning").Contains("比较慢"), slowPc.Text("Tb_SlowWarning"));
        Check("配置偏低：不标推荐", slowPc.Catalog.All(r => r.Badge != "推荐"), string.Join(",", slowPc.Catalog.Select(r => r.Badge)));
        Check("配置偏低：能力说明", slowPc.Text("Tb_Capacity") == "本地模型只能慢慢跑，约 3.1 GB 以内勉强可用。", slowPc.Text("Tb_Capacity"));
        Check("配置偏低：慢的排在带不动的前面", slowPc.Catalog.Select(r => r.Rank).SequenceEqual(slowPc.Catalog.Select(r => r.Rank).Order()));
        Check("配置偏低：有带不动的", slowPc.Catalog.Any(r => r is { Fit: OllamaFit.TooLarge, Badge: "这台电脑带不动" }));
        slowPc.Close();

        // 配置太低：一键部署也要先问，不然白下 1.7 GB
        var weakApi = new FakeApi { PullAddsModel = true };
        var weakInstaller = new FakeDeployInstaller(null) { Api = weakApi, StartStartsServer = true };
        var weakPc = new ManagerWindow(weakInstaller, weakApi, library: Library(LibraryNet()), hardware: new OllamaHardware(2 * GB, 0));
        Check("配置太低：警告跑不起来", weakPc.Shown("Tb_SlowWarning") && weakPc.Text("Tb_SlowWarning").Contains("跑不起来"), weakPc.Text("Tb_SlowWarning"));
        Check("配置太低：能力说明", weakPc.Text("Tb_Capacity") == "这台电脑配置偏低，不建议运行本地模型。", weakPc.Text("Tb_Capacity"));

        weakPc.Click("Btn_Deploy");
        weakPc.Finish();
        Check("配置太低：部署前先问", weakPc.Confirms.Count == 1, weakPc.Confirms.Count.ToString());
        Check("配置太低：拒绝后什么都不动", !weakInstaller.Log.Any(l => l.StartsWith("download")) && weakApi.Pulled.Count == 0, string.Join(",", weakInstaller.Log));
        weakPc.Answer = true;
        weakPc.Click("Btn_Deploy");
        Pump(() => weakApi.Pulled.Count == 1);
        weakPc.Finish();
        Check("配置太低：同意后照常部署", weakInstaller.Log.Any(l => l.StartsWith("download")), string.Join(",", weakInstaller.Log));
        weakPc.Close();

        // 手上已有能聊天的模型时部署不下载，配置再低也没什么好问的
        var stockedApi = new FakeApi { Running = true, Models = { new OllamaLocalModel("old-model:latest", GB, "llama") } };
        var stocked = new ManagerWindow(new FakeDeployInstaller(null) { Api = stockedApi }, stockedApi, model: "gone:latest", library: Library(LibraryNet()), hardware: new OllamaHardware(2 * GB, 0));
        stocked.Click("Btn_Deploy");
        Pump(() => stocked.Chosen.Count > 0 || stocked.Confirms.Count > 0);
        stocked.Finish();
        Check("配置太低但已有模型：不用问", stocked.Confirms.Count == 0, stocked.Confirms.Count.ToString());
        Check("配置太低但已有模型：直接选用", stockedApi.Pulled.Count == 0 && stocked.Chosen.SequenceEqual(new[] { ("old-model:latest", Url) }), string.Join(",", stocked.Chosen));
        stocked.Close();

        // 拒绝时不能先把 Ollama 装上
        var quietApi = new FakeApi();
        var quietInstaller = new FakeDeployInstaller(null) { Api = quietApi };
        var picker = new ManagerWindow(quietInstaller, quietApi, library: Library(LibraryNet()), hardware: new OllamaHardware(16 * GB, 0));
        var huge = OllamaModelCatalog.Models.MaxBy(m => m.SizeBytes)!;
        picker.Find<TextBox>("Tb_CustomModel").Text = "alpha:70b";
        picker.Click("Btn_PullCustom");
        picker.Finish();
        Check("确认：拒绝时不去安装 Ollama", picker.Confirms.Count == 1 && !quietInstaller.Log.Any(l => l.StartsWith("download") || l == "start"), string.Join(",", quietInstaller.Log));
        Check("确认：名单里的大模型也走同一条路", huge.SizeBytes > 0);
        picker.Close();
    }

    /// <summary>连真的 ollama.com，确认页面没改版、规格真能拉。只读，不下载模型。</summary>
    private static async Task<int> RunLibrarySmokeAsync()
    {
        var cache = NewLibraryCache();
        var library = new OllamaLibrary(cacheDirectory: cache);

        var models = await library.RefreshAsync(default);
        Check("真实页面：解析出来", models is { Count: > 100 }, models?.Count.ToString() ?? "null");
        if (models is null) return 1;

        Check("真实页面：有说明", models.Count(m => m.Description.Length > 0) > models.Count * 9 / 10);
        Check("真实页面：有下载量", models.Count(m => m.Pulls.Length > 0) > models.Count * 9 / 10);
        Check("真实页面：有规格", models.Count(m => m.Sizes.Count > 0) > models.Count / 2);
        Check("真实页面：能看出能力", models.Any(m => m.Capabilities.Contains("tools")) && models.Any(m => m.Capabilities.Contains("embedding")));
        Check("真实页面：能看出纯云端", models.Any(m => m.IsCloudOnly));
        Check("真实页面：能聊天的本地模型够多", models.Count(m => m.IsChatModel && !m.IsCloudOnly) > 100);

        foreach (var entry in OllamaModelCatalog.Models)
        {
            var size = await library.GetSizeAsync(entry.Name, default);
            Check($"内置名单 {entry.Name} 的体积和官方一致", size == entry.SizeBytes, $"{size} vs {entry.SizeBytes}");
        }

        foreach (var model in models.Where(m => m.IsChatModel && !m.IsCloudOnly).Take(12))
        {
            var sizes = await library.GetSizesAsync(model, default);
            Check($"{model.Name} 的规格都拉得到", sizes.Count == model.Tags.Count, $"{sizes.Count}/{model.Tags.Count}: {string.Join(",", model.Tags.Except(sizes.Keys))}");
        }

        var hardware = OllamaHardware.Detect();
        var candidates = OllamaModelCatalog.PopularCandidates(models);
        Console.WriteLine($"本机 {hardware}");
        Console.WriteLine($"流畅上限 {OllamaModelCatalog.SmoothLimit(hardware) / (double)GB:0.0} GB，能跑上限 {OllamaModelCatalog.UsableLimit(hardware) / (double)GB:0.0} GB");
        Console.WriteLine($"推荐 {OllamaModelCatalog.Recommend(hardware).Name}");
        Console.WriteLine($"热门候选 {string.Join(",", candidates.Select(m => m.Name))}");

        DeleteLibraryCaches();
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
