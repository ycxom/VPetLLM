using System.Formats.Asn1;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VPetLLM.Core.Services;

namespace VPetLLM.OllamaChecks;

internal static partial class Program
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++;
        else _fail++;
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(ok || detail.Length == 0 ? "" : $" -- {detail}")}");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length == 2 && args[0] == "--smoke")
                return Task.Run(() => RunSmokeAsync(args[1])).GetAwaiter().GetResult();

            if (args.Length == 2 && args[0] == "--smoke-start")
                return Task.Run(() => RunStartSmokeAsync(args[1])).GetAwaiter().GetResult();

            if (args.Length == 1 && args[0] == "--smoke-library")
                return Task.Run(RunLibrarySmokeAsync).GetAwaiter().GetResult();

            if (args.Length == 2 && args[0] == "--render")
            {
                RenderWindows(args[1]);
                return 0;
            }

            if (args.Length >= 1 && args[0] == "--scroll-perf")
            {
                ScrollPerf(args[1..]);
                return 0;
            }

            CheckCatalog();
            CheckFit();
            CheckLibraryParse();
            Task.Run(CheckLibraryCacheAsync).GetAwaiter().GetResult();
            Task.Run(CheckLibrarySizesAsync).GetAwaiter().GetResult();
            Task.Run(CheckClientAsync).GetAwaiter().GetResult();
            Task.Run(CheckDownloadAsync).GetAwaiter().GetResult();
            Task.Run(CheckLatestVersionAsync).GetAwaiter().GetResult();
            CheckPublisherIdentity();
            Task.Run(CheckInstallAsync).GetAwaiter().GetResult();
            Task.Run(CheckManagerAsync).GetAwaiter().GetResult();
            CheckServerStartInfo();
            CheckWindows();
            CheckLocalization();
        }
        catch (Exception ex)
        {
            Check("no unexpected exception", false, ex.ToString());
        }

        DeleteLibraryCaches();
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    private const long GB = 1024L * 1024 * 1024;

    private static void CheckCatalog()
    {
        string Pick(double videoGb, double memoryGb)
            => OllamaModelCatalog.Recommend(new OllamaHardware((long)(memoryGb * GB), (long)(videoGb * GB))).Name;

        Check("12GB 显卡推荐最强档", Pick(12, 32) == "qwen3.5:9b", Pick(12, 32));
        Check("8GB 显卡放得下 qwen3:8b", Pick(7, 32) == "qwen3:8b", Pick(7, 32));
        Check("6GB 显卡退到 4b", Pick(6, 32) == "qwen3:4b", Pick(6, 32));
        Check("无独显 16GB 内存走 CPU 档", Pick(0, 16) == "qwen3:4b", Pick(0, 16));
        Check("无独显 64GB 内存也不推荐 CPU 跑不动的模型", Pick(0, 64) == "qwen3:4b", Pick(0, 64));
        Check("8GB 内存退到入门款", Pick(0, 8) == "qwen2.5:3b", Pick(0, 8));
        Check("什么都不够时给最小的", Pick(0, 2) == "qwen2.5:3b", Pick(0, 2));
        Check("核显 512MB 不算数", Pick(0.5, 16) == "qwen3:4b", Pick(0.5, 16));

        var low = new OllamaHardware(4 * GB, 0);
        Check("低配判定为不流畅", OllamaModelCatalog.Assess(OllamaModelCatalog.Models[0].SizeBytes, low) != OllamaFit.Smooth);

        Check("名单里的名字全部合法", OllamaModelCatalog.Models.All(m => OllamaClient.IsValidModelName(m.Name)));
        Check("名单不重复", OllamaModelCatalog.Models.Select(m => m.Name).Distinct().Count() == OllamaModelCatalog.Models.Count);
        Check("分档模型中有 CPU 档", OllamaModelCatalog.Models.Any(m => m.Tiered && OllamaModelCatalog.Assess(m.SizeBytes, new OllamaHardware(16 * GB, 0)) == OllamaFit.Smooth));

        Check("QWORD 显存", OllamaHardware.ParseMemorySize(12820938752L) == 12820938752L);
        Check("DWORD 高位不当负数", OllamaHardware.ParseMemorySize(unchecked((int)0xFFF00000)) == 0xFFF00000L);
        Check("8 字节二进制显存", OllamaHardware.ParseMemorySize(BitConverter.GetBytes(5L * GB)) == 5L * GB);
        Check("4 字节二进制显存", OllamaHardware.ParseMemorySize(BitConverter.GetBytes(0x80000000u)) == 0x80000000L);
        Check("其他类型当 0", OllamaHardware.ParseMemorySize("8GB") == 0 && OllamaHardware.ParseMemorySize(null) == 0);

        var real = OllamaHardware.Detect();
        Check("本机内存能读出来", real.MemoryBytes > GB, real.ToString());
    }

    private static readonly Uri Local = new("http://localhost:11434");

    private static async Task CheckClientAsync()
    {
        Check("模型名：普通", OllamaClient.IsValidModelName("qwen3:4b"));
        Check("模型名：无标签", OllamaClient.IsValidModelName("nomic-embed-text"));
        Check("模型名：命名空间", OllamaClient.IsValidModelName("library/qwen3:4b"));
        Check("模型名：hf.co", OllamaClient.IsValidModelName("hf.co/bartowski/Llama-3.2-GGUF:Q4_K_M"));
        foreach (var bad in new[] { "", " ", "a b", "a;b", "../x", "a/../b", ":tag", "a:", "a:b:c", "a\nb", "-rf", new string('a', 201) })
            Check($"模型名：拒绝 {bad.Replace("\n", "\\n")}", !OllamaClient.IsValidModelName(bad));

        // 版本
        var version = await new OllamaClient(Local, Reply(HttpStatusCode.OK, "{\"version\":\"0.40.13\"}")).GetVersionAsync(default);
        Check("读到版本号", version == "0.40.13", version ?? "null");

        Check("连不上返回 null", await new OllamaClient(Local, new FakeHandler((_, _) => throw new HttpRequestException("refused"))).GetVersionAsync(default) is null);
        Check("端口上是别的服务返回 null", await new OllamaClient(Local, Reply(HttpStatusCode.OK, "<html>nginx</html>")).GetVersionAsync(default) is null);
        Check("错误状态码即使带版本号也返回 null", await new OllamaClient(Local, Reply(HttpStatusCode.ServiceUnavailable, "{\"version\":\"0.40.13\"}")).GetVersionAsync(default) is null);

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var hanging = new OllamaClient(Local, new FakeHandler(async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage();
            }));
            Check("用户取消不被吞成服务未运行",
                await ThrowsAsync<OperationCanceledException>(() => hanging.GetVersionAsync(cancelled.Token)));
        }

        // 列表
        var models = await new OllamaClient(Local, Reply(HttpStatusCode.OK,
            """{"models":[{"name":"qwen3:4b","size":2497293444,"details":{"family":"qwen3"}},{"name":"bge-m3:latest","size":1157672268,"details":{"family":"bert"}},{"name":"nomic-embed-text:latest","size":274302030,"details":{"family":"nomic-bert"}},{"name":"Alpha:1","size":1,"details":{}}]}""")).ListModelsAsync(default);
        Check("模型列表按名字排序", models.Select(m => m.Name).SequenceEqual(new[] { "Alpha:1", "bge-m3:latest", "nomic-embed-text:latest", "qwen3:4b" }),
            string.Join(",", models.Select(m => m.Name)));
        Check("读到模型大小", models[3].SizeBytes == 2497293444);
        Check("嵌入模型不算聊天模型", !models[1].IsChatModel && !models[2].IsChatModel);
        Check("聊天模型算聊天模型", models[0].IsChatModel && models[3].IsChatModel);

        Check("空列表", (await new OllamaClient(Local, Reply(HttpStatusCode.OK, """{"models":null}""")).ListModelsAsync(default)).Count == 0);

        var listError = await CaptureAsync(() => new OllamaClient(Local, Reply(HttpStatusCode.InternalServerError, """{"error":"disk broken"}""")).ListModelsAsync(default));
        Check("列表失败带 Ollama 原话", listError is OllamaOperationException { Failure: OllamaFailure.Api, Message: "disk broken" }, listError?.ToString() ?? "null");

        // 下载模型
        string? pullBody = null;
        string? pullPath = null;
        var progress = new List<OllamaTaskProgress>();
        var pullLines = new[]
        {
            """{"status":"pulling manifest"}""",
            """{"status":"pulling aaa","digest":"sha256:aaa","total":100,"completed":40}""",
            """{"status":"pulling bbb","digest":"sha256:bbb","total":300}""",
            """{"status":"pulling aaa","digest":"sha256:aaa","total":100,"completed":100}""",
            """{"status":"pulling bbb","digest":"sha256:bbb","total":300,"completed":150}""",
            "not json at all",
            "",
            """{"status":"verifying sha256 digest"}""",
            """{"status":"writing manifest"}""",
            """{"status":"removing any unused layers"}""",
            """{"status":"success"}"""
        };
        var client = new OllamaClient(Local, new FakeHandler(async (request, _) =>
        {
            pullPath = request.RequestUri!.AbsolutePath + "|" + request.Method;
            pullBody = await request.Content!.ReadAsStringAsync();
            return Text(HttpStatusCode.OK, string.Join("\n", pullLines) + "\n");
        }));
        await client.PullAsync("qwen3:4b", 1000, new InlineProgress(progress.Add), default);

        Check("拉取走 POST /api/pull", pullPath == "/api/pull|POST", pullPath ?? "null");
        Check("请求体带模型和流式", pullBody == """{"model":"qwen3:4b","stream":true}""", pullBody ?? "null");

        var second = progress.FirstOrDefault(p => p.Completed == 40 && p.Total == 1000);
        Check("多层进度累加且不小于已知体积", second is not null, string.Join(";", progress));
        Check("后续层进度不丢", progress.Any(p => p.Completed == 250), string.Join(";", progress));
        Check("进度阶段：清单", progress.First().Phase == OllamaTaskPhase.PullingManifest);
        Check("进度阶段：下载", progress[1].Phase == OllamaTaskPhase.PullingModel);
        Check("进度阶段：校验", progress.Any(p => p.Phase == OllamaTaskPhase.VerifyingModel));
        Check("进度阶段：写入", progress.Any(p => p.Phase == OllamaTaskPhase.WritingModel));
        Check("坏行不打断下载", progress.Count == pullLines.Length - 3, progress.Count.ToString());

        progress.Clear();
        await new OllamaClient(Local, new FakeHandler((_, _) => Task.FromResult(Text(HttpStatusCode.OK,
            """
            {"status":"pulling aaa","digest":"sha256:aaa","total":5000,"completed":1}
            {"status":"success"}
            """)))).PullAsync("x", 1000, new InlineProgress(progress.Add), default);
        Check("实际体积大于目录体积时用实际的", progress.Single().Total == 5000, progress.Single().ToString());

        var failLine = await CaptureAsync(() => new OllamaClient(Local, new FakeHandler((_, _) => Task.FromResult(Text(HttpStatusCode.OK,
            """
            {"status":"pulling manifest"}
            {"error":"pull model manifest: file does not exist"}
            {"status":"success"}
            """)))).PullAsync("x", 0, null, default));
        Check("流中错误行抛异常并带原话", failLine is OllamaOperationException { Message: "pull model manifest: file does not exist" }, failLine?.ToString() ?? "null");

        var truncated = await CaptureAsync(() => new OllamaClient(Local, new FakeHandler((_, _) => Task.FromResult(Text(HttpStatusCode.OK,
            """{"status":"pulling aaa","digest":"sha256:aaa","total":100,"completed":100}""")))).PullAsync("x", 0, null, default));
        Check("没收到 success 不算完成", truncated is OllamaOperationException, truncated?.ToString() ?? "null");

        var rejected = await CaptureAsync(() => new OllamaClient(Local, Reply((HttpStatusCode)412,
            """{"error":"pull model manifest: 412: requires a newer version of Ollama"}""")).PullAsync("x", 0, null, default));
        Check("HTTP 错误带原话（版本过旧提示要能看到）", rejected?.Message.Contains("newer version") == true, rejected?.ToString() ?? "null");

        var refused = await CaptureAsync(() => new OllamaClient(Local, new FakeHandler((_, _) => throw new HttpRequestException("refused"))).PullAsync("x", 0, null, default));
        Check("连接失败转成可显示的异常", refused is OllamaOperationException { Failure: OllamaFailure.Api }, refused?.ToString() ?? "null");

        using (var cancel = new CancellationTokenSource())
        {
            var stream = new ScriptedStream(
                Encoding.UTF8.GetBytes("""{"status":"pulling aaa","digest":"sha256:aaa","total":100,"completed":1}""" + "\n"));
            var pending = new OllamaClient(Local, new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })))
                .PullAsync("x", 0, new InlineProgress(_ => cancel.Cancel()), cancel.Token);
            Check("拉取中取消", await ThrowsAsync<OperationCanceledException>(() => pending));
        }

        // 取消时流恰好读完：读这一步不会抛，也要算用户取消，不能说成下载失败
        using (var cancel = new CancellationTokenSource())
        {
            var stream = new ScriptedStream(
                Encoding.UTF8.GetBytes("""{"status":"pulling aaa","digest":"sha256:aaa","total":100,"completed":1}""" + "\n"), hang: false);
            var pending = new OllamaClient(Local, new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })))
                .PullAsync("x", 0, new InlineProgress(_ => cancel.Cancel()), cancel.Token);
            Check("取消时流恰好结束", await ThrowsAsync<OperationCanceledException>(() => pending));
        }

        // 删除
        string? deleted = null;
        await new OllamaClient(Local, new FakeHandler(async (request, _) =>
        {
            deleted = request.Method + "|" + request.RequestUri!.AbsolutePath + "|" + await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        })).DeleteAsync("qwen3:4b", default);
        Check("删除请求格式", deleted == """DELETE|/api/delete|{"model":"qwen3:4b"}""", deleted ?? "null");

        var missing = await CaptureAsync(() => new OllamaClient(Local, Reply(HttpStatusCode.NotFound, """{"error":"model not found"}""")).DeleteAsync("x", default));
        Check("删除失败带原话", missing?.Message == "model not found", missing?.ToString() ?? "null");
    }

    private static readonly byte[] Setup = BuildSetup();

    private static byte[] BuildSetup()
    {
        var bytes = new byte[300_000];
        new Random(7).NextBytes(bytes);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        return bytes;
    }

    private const string Version = "0.40.13";

    private static async Task CheckDownloadAsync()
    {
        // 新下载
        using (var box = new TempBox())
        {
            var server = new SetupServer();
            var path = await box.Installer(server.Route()).DownloadAsync(Version, null, default);
            Check("新下载：内容完整", File.ReadAllBytes(path).SequenceEqual(Setup));
            Check("新下载：不带 Range", server.Ranges.Single() is null);
            Check("新下载：不留 .part", !File.Exists(box.Part));
            Check("新下载：文件名含版本", Path.GetFileName(path) == $"OllamaSetup-{Version}.exe", path);
            Check("新下载：地址是官方发布地址", server.Urls.Single() == $"https://github.com/ollama/ollama/releases/download/v{Version}/OllamaSetup.exe", server.Urls.Single());
        }

        // 续传
        using (var box = new TempBox())
        {
            box.WritePart(100_000);
            var server = new SetupServer();
            var progress = new List<OllamaTaskProgress>();
            var path = await box.Installer(server.Route()).DownloadAsync(Version, new InlineProgress(progress.Add), default);
            Check("续传：Range 从已有长度开始", server.Ranges.Single() == 100_000, string.Join(",", server.Ranges));
            Check("续传：内容完整", File.ReadAllBytes(path).SequenceEqual(Setup));
            Check("续传：总量取 Content-Range", progress.Last().Total == Setup.Length, progress.Last().ToString());
        }

        // 不支持 Range 的线路排到后面，优先用能续传的
        using (var box = new TempBox())
        {
            box.WritePart(100_000);
            var noRange = new SetupServer { IgnoreRange = true };
            var ranged = new SetupServer();
            var path = await box.Installer(noRange.Route(), ranged.Route()).DownloadAsync(Version, null, default);
            Check("不续传的线路不会冲掉已有进度", ranged.Ranges.Single() == 100_000, string.Join(",", ranged.Ranges));
            Check("换到能续传的线路后内容完整", File.ReadAllBytes(path).SequenceEqual(Setup));
        }

        // 全都不支持时才重头下
        using (var box = new TempBox())
        {
            box.WritePart(100_000);
            var noRange = new SetupServer { IgnoreRange = true };
            var path = await box.Installer(noRange.Route()).DownloadAsync(Version, null, default);
            Check("全部不支持续传时重头下载且内容完整", File.ReadAllBytes(path).SequenceEqual(Setup));
        }

        // .part 已经完整
        using (var box = new TempBox())
        {
            box.WritePart(Setup.Length);
            var server = new SetupServer();
            var path = await box.Installer(server.Route()).DownloadAsync(Version, null, default);
            Check("416 且长度相等视为已完成", File.ReadAllBytes(path).SequenceEqual(Setup));
            Check("416 且长度相等时不重下", server.Ranges.Single() == Setup.Length, string.Join(",", server.Ranges));
        }

        // .part 比文件还长（换了版本内容或之前写坏）
        using (var box = new TempBox())
        {
            box.WritePart(Setup.Length + 10);
            var server = new SetupServer();
            var path = await box.Installer(server.Route()).DownloadAsync(Version, null, default);
            Check("416 且长度不符时丢弃重下", File.ReadAllBytes(path).SequenceEqual(Setup), new FileInfo(path).Length.ToString());
        }

        // 代理回网页
        using (var box = new TempBox())
        {
            var html = new SetupServer { Payload = Encoding.UTF8.GetBytes("<html>proxy error</html>") };
            var good = new SetupServer();
            var path = await box.Installer(html.Route(), good.Route()).DownloadAsync(Version, null, default);
            Check("网页响应被拦下并换线路", File.ReadAllBytes(path).SequenceEqual(Setup));
        }

        using (var box = new TempBox())
        {
            var html = new SetupServer { Payload = Encoding.UTF8.GetBytes("<html>proxy error</html>") };
            var error = await CaptureAsync(() => box.Installer(html.Route()).DownloadAsync(Version, null, default));
            Check("只有网页响应时报下载失败", error is OllamaOperationException { Failure: OllamaFailure.Download }, error?.ToString() ?? "null");
            Check("网页不会变成 exe", !File.Exists(box.Target));
        }

        // Content-Range 起点不对
        using (var box = new TempBox())
        {
            box.WritePart(100_000);
            var skewed = new SetupServer { RangeSkew = 500 };
            var good = new SetupServer();
            var path = await box.Installer(skewed.Route(), good.Route()).DownloadAsync(Version, null, default);
            Check("Content-Range 起点不符不写入", File.ReadAllBytes(path).SequenceEqual(Setup));
            Check("Content-Range 起点不符时进度不动", good.Ranges.Single() == 100_000, string.Join(",", good.Ranges));
        }

        // 连接中途断开：进度留下，下一条线路接上
        using (var box = new TempBox())
        {
            var broken = new SetupServer { CutAfter = 120_000 };
            var good = new SetupServer();
            var path = await box.Installer(broken.Route(), good.Route()).DownloadAsync(Version, null, default);
            Check("中途断开后接着下", good.Ranges.Single() == 120_000, string.Join(",", good.Ranges));
            Check("接续后内容完整", File.ReadAllBytes(path).SequenceEqual(Setup));
        }

        // 只有一条线路，断点后再连一次能下完
        using (var box = new TempBox())
        {
            var flaky = new SetupServer { CutAfter = 200_000 };
            var path = await box.Installer(flaky.Route()).DownloadAsync(Version, null, default);
            Check("单线路断了只要有进度就继续", File.ReadAllBytes(path).SequenceEqual(Setup) && flaky.Ranges.Count == 2, string.Join(",", flaky.Ranges));
        }

        // 停滞
        using (var box = new TempBox())
        {
            var stuck = new SetupServer { HangAfter = 50_000 };
            var good = new SetupServer();
            var installer = box.Installer(stuck.Route(), good.Route());
            var path = await installer.DownloadAsync(Version, null, default);
            Check("线路卡住后换线路且保留进度", good.Ranges.Single() == 50_000, string.Join(",", good.Ranges));
            Check("卡住换线路后内容完整", File.ReadAllBytes(path).SequenceEqual(Setup));
        }

        // 响应头都不回
        using (var box = new TempBox())
        {
            var silent = new SetupServer { HangBeforeHeaders = true };
            var good = new SetupServer();
            var path = await box.Installer(silent.Route(), good.Route()).DownloadAsync(Version, null, default);
            Check("不回响应头的线路被跳过", File.ReadAllBytes(path).SequenceEqual(Setup));
        }

        // 龟速线路
        using (var box = new TempBox())
        {
            var slow = new SetupServer { ChunkDelay = TimeSpan.FromMilliseconds(30), ChunkSize = 2_000 };
            var good = new SetupServer();
            var installer = new OllamaInstaller(_ => Task.FromResult<IReadOnlyList<DownloadRoute>>(new[] { slow.Route(), good.Route() }), cacheDirectory: box.Directory)
            {
                SlowWindow = TimeSpan.FromMilliseconds(100),
                MinWindowBytes = 10 * 1024 * 1024
            };
            var path = await installer.DownloadAsync(Version, null, default);
            Check("龟速线路被换掉", good.Ranges.Count == 1 && good.Ranges[0] > 0, string.Join(",", good.Ranges));
            Check("换掉龟速线路后内容完整", File.ReadAllBytes(path).SequenceEqual(Setup));
        }

        // 全部失败
        using (var box = new TempBox())
        {
            box.WritePart(100_000);
            var dead = new SetupServer { RefuseConnection = true };
            var error = await CaptureAsync(() => box.Installer(dead.Route(), dead.Route()).DownloadAsync(Version, null, default));
            Check("全部线路失败时报下载失败", error is OllamaOperationException { Failure: OllamaFailure.Download }, error?.ToString() ?? "null");
            Check("失败后保留已下载部分", new FileInfo(box.Part).Length == 100_000);
            Check("失败前每条线路都试过", dead.Urls.Count == 2, dead.Urls.Count.ToString());
        }

        // 取消
        using (var box = new TempBox())
        {
            using var cancel = new CancellationTokenSource();
            var server = new SetupServer { HangAfter = 50_000, OnHang = cancel.Cancel };
            var error = await CaptureAsync(() => box.Installer(server.Route()).DownloadAsync(Version, null, cancel.Token));
            Check("取消抛 OperationCanceled", error is OperationCanceledException, error?.ToString() ?? "null");
            Check("取消后保留进度", File.Exists(box.Part) && new FileInfo(box.Part).Length == 50_000, File.Exists(box.Part) ? new FileInfo(box.Part).Length.ToString() : "missing");
        }

        // 版本号进路径和地址之前要验
        using (var box = new TempBox())
        {
            var server = new SetupServer();
            foreach (var evil in new[] { "..\\..\\evil", "0.1.0/../../x", "1.2", "1.2.3-rc1", "" })
            {
                var error = await CaptureAsync(() => box.Installer(server.Route()).DownloadAsync(evil, null, default));
                Check($"拒绝版本号 {evil}", error is OllamaOperationException { Failure: OllamaFailure.VersionLookup }, error?.ToString() ?? "null");
            }
            Check("拒绝后没有发出请求", server.Urls.Count == 0);
        }

        // 缓存管理
        using (var box = new TempBox())
        {
            File.WriteAllBytes(Path.Combine(box.Directory, "OllamaSetup-0.1.0.exe"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(box.Directory, "OllamaSetup-0.1.0.exe.part"), new byte[] { 1 });
            var unrelated = Path.Combine(box.Directory, "keep.txt");
            File.WriteAllBytes(unrelated, new byte[] { 1 });
            await box.Installer(new SetupServer().Route()).DownloadAsync(Version, null, default);
            Check("旧版本残留被清理", Directory.GetFiles(box.Directory, "OllamaSetup-0.1.0*").Length == 0);
            Check("不碰无关文件", File.Exists(unrelated));
        }

        using (var box = new TempBox())
        {
            File.WriteAllBytes(box.Target, Setup);
            var server = new SetupServer();
            var path = await box.Installer(server.Route()).DownloadAsync(Version, null, default);
            Check("已下好的不重复下载", path == box.Target && server.Urls.Count == 0);
        }

        // 磁盘
        using (var box = new TempBox())
        {
            var server = new SetupServer();
            var error = await CaptureAsync(() => box.Installer(1 * GB, server.Route()).DownloadAsync(Version, null, default));
            Check("磁盘不够时下载前就报错", error is OllamaOperationException { Failure: OllamaFailure.DiskSpace, Available: 1 * GB, Required: > 6 * GB }, error?.ToString() ?? "null");
            Check("磁盘不够时不发请求", server.Urls.Count == 0);
        }

        using (var box = new TempBox())
        {
            box.WritePart(100_000);
            var server = new SetupServer();
            var error = await CaptureAsync(() => box.Installer(1 * GB, server.Route()).DownloadAsync(Version, null, default));
            Check("已下载部分从所需空间里扣掉", error is OllamaOperationException { Required: 8 * GB - 100_000 }, error?.ToString() ?? "null");
        }

        var unc = new OllamaInstaller(_ => Task.FromResult<IReadOnlyList<DownloadRoute>>(Array.Empty<DownloadRoute>()));
        Check("网络路径查不到空间时不阻塞", unc.GetFreeSpace(@"\\nas\models") == long.MaxValue);
    }

    private static async Task CheckLatestVersionAsync()
    {
        async Task<string?> Lookup(Func<string, HttpResponseMessage> respond, params string[] routeUrls)
        {
            Func<HttpMessageHandler> handler = () => new FakeHandler((request, _) =>
            {
                var response = respond(request.RequestUri!.ToString());
                response.RequestMessage ??= request;
                return Task.FromResult(response);
            });
            var routes = routeUrls.Select(u => new DownloadRoute(u, handler)).ToList();
            return await new OllamaInstaller(_ => Task.FromResult<IReadOnlyList<DownloadRoute>>(routes), handler).GetLatestVersionAsync(default);
        }

        const string Proxy = "https://proxy.test/https://github.com/ollama/ollama/releases/latest";

        var fromUpdate = await Lookup(url => url.Contains("ollama.com/api/update")
            ? Text(HttpStatusCode.OK, """{"url":"https://github.com/ollama/ollama/releases/download/v0.40.13/OllamaSetup.exe"}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound), Proxy);
        Check("从官方更新接口取版本", fromUpdate == "0.40.13", fromUpdate ?? "null");

        var fromGitHub = await Lookup(url => url.Contains("api.github.com")
            ? Text(HttpStatusCode.OK, """{"tag_name": "v0.41.2","name":"v0.41.2"}""")
            : new HttpResponseMessage(HttpStatusCode.Forbidden), Proxy);
        Check("官方接口失败时用 GitHub API", fromGitHub == "0.41.2", fromGitHub ?? "null");

        var fromRedirect = await Lookup(url =>
        {
            if (url != Proxy) throw new HttpRequestException("blocked");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://proxy.test/https://github.com/ollama/ollama/releases/tag/v0.42.0"),
                Content = new StringContent("<html></html>")
            };
        }, Proxy);
        Check("直连都断时经代理跟随跳转取版本", fromRedirect == "0.42.0", fromRedirect ?? "null");

        var rc = await Lookup(url => url.Contains("api.github.com")
            ? Text(HttpStatusCode.OK, """{"tag_name":"v0.42.0-rc1"}""")
            : url == Proxy
                ? Text(HttpStatusCode.OK, """<a href="/ollama/ollama/releases/tag/v0.41.9">""")
                : new HttpResponseMessage(HttpStatusCode.NotFound), Proxy);
        Check("跳过预发布版", rc == "0.41.9", rc ?? "null");

        var none = await Lookup(_ => throw new HttpRequestException("offline"), Proxy);
        Check("全部查不到返回 null", none is null);

        var routeFailure = await new OllamaInstaller(_ => throw new HttpRequestException("pool down"),
            () => new FakeHandler((request, _) => request.RequestUri!.Host == "github.com"
                ? Task.FromResult(Text(HttpStatusCode.OK, """/tag/v0.43.1"""))
                : throw new HttpRequestException("offline"))).GetLatestVersionAsync(default);
        Check("线路池失败时回退直连", routeFailure == "0.43.1", routeFailure ?? "null");

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var error = await CaptureAsync(() => new OllamaInstaller(_ => Task.FromResult<IReadOnlyList<DownloadRoute>>(Array.Empty<DownloadRoute>()),
            () => new FakeHandler((_, _) => throw new HttpRequestException("x"))).GetLatestVersionAsync(cancel.Token));
        Check("查询版本可取消", error is OperationCanceledException, error?.ToString() ?? "null");
    }

    private static X500DistinguishedName Subject(Action<X500DistinguishedNameBuilder> build)
    {
        var builder = new X500DistinguishedNameBuilder();
        build(builder);
        return builder.Build();
    }

    private static void Serial(X500DistinguishedNameBuilder b, string value)
        => b.Add("2.5.4.5", value, UniversalTagNumber.PrintableString);

    // X500DistinguishedNameBuilder 造不出多值 RDN，只能手写 DER
    private static X500DistinguishedName MultiValued(params (string Oid, string Value)[][] names)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            foreach (var name in names)
            {
                using (writer.PushSetOf())
                {
                    foreach (var (oid, value) in name)
                    {
                        using (writer.PushSequence())
                        {
                            writer.WriteObjectIdentifier(oid);
                            writer.WriteCharacterString(UniversalTagNumber.UTF8String, value);
                        }
                    }
                }
            }
        }
        return new X500DistinguishedName(writer.Encode());
    }

    private static void RejectMultiValued(string name, X500DistinguishedName subject)
    {
        // 外层只接 CryptographicException，这里抛别的异常同样算没拒绝干净
        try
        {
            Check($"拒绝：{name}", !AuthenticodeVerifier.IsOllamaPublisher(subject), subject.Name);
        }
        catch (InvalidOperationException ex)
        {
            Check($"拒绝：{name}", false, ex.Message);
        }
    }

    private static void CheckPublisherIdentity()
    {
        var real = Subject(b =>
        {
            b.Add("1.3.6.1.4.1.311.60.2.1.3", "CA", UniversalTagNumber.PrintableString);
            b.Add("2.5.4.15", "Private Organization", UniversalTagNumber.UTF8String);
            Serial(b, "2713355");
            b.AddCountryOrRegion("CA");
            b.AddStateOrProvinceName("Ontario");
            b.AddLocalityName("Toronto");
            b.AddOrganizationName("Ollama Inc.");
            b.AddCommonName("Ollama Inc.");
        });
        Check("真实发布者主体通过", AuthenticodeVerifier.IsOllamaPublisher(real), real.Name);

        void Reject(string name, X500DistinguishedName subject)
            => Check($"拒绝：{name}", !AuthenticodeVerifier.IsOllamaPublisher(subject), subject.Name);

        Reject("注册号不符", Subject(b => { Serial(b, "9999999"); b.AddOrganizationName("Ollama Inc."); b.AddCommonName("Ollama Inc."); }));
        Reject("没有注册号", Subject(b => { b.AddOrganizationName("Ollama Inc."); b.AddCommonName("Ollama Inc."); }));
        Reject("CN 不符", Subject(b => { Serial(b, "2713355"); b.AddOrganizationName("Ollama Inc."); b.AddCommonName("Ollama Incorporated"); }));
        Reject("O 不符", Subject(b => { Serial(b, "2713355"); b.AddOrganizationName("Evil Inc."); b.AddCommonName("Ollama Inc."); }));
        Reject("没有 O", Subject(b => { Serial(b, "2713355"); b.AddCommonName("Ollama Inc."); }));
        Reject("没有 CN", Subject(b => { Serial(b, "2713355"); b.AddOrganizationName("Ollama Inc."); }));
        Reject("大小写不同", Subject(b => { Serial(b, "2713355"); b.AddOrganizationName("OLLAMA INC."); b.AddCommonName("OLLAMA INC."); }));
        Reject("名字藏在别的字段值里", Subject(b =>
        {
            Serial(b, "9999999");
            b.AddOrganizationName("Evil, CN=Ollama Inc., O=Ollama Inc., serialNumber=2713355");
            b.AddCommonName("Evil");
        }));
        Reject("注册号写在 OU 里", Subject(b =>
        {
            b.AddOrganizationalUnitName("2713355");
            b.AddOrganizationName("Ollama Inc.");
            b.AddCommonName("Ollama Inc.");
        }));
        Reject("同一字段重复出现", Subject(b =>
        {
            Serial(b, "2713355");
            b.AddOrganizationName("Evil Inc.");
            b.AddOrganizationName("Ollama Inc.");
            b.AddCommonName("Ollama Inc.");
        }));
        Reject("注册号重复出现", Subject(b =>
        {
            Serial(b, "2713355");
            Serial(b, "9999999");
            b.AddOrganizationName("Ollama Inc.");
            b.AddCommonName("Ollama Inc.");
        }));

        RejectMultiValued("字段挤在同一个多值 RDN 里", MultiValued(
            new[] { ("2.5.4.5", "2713355"), ("2.5.4.3", "Ollama Inc.") },
            new[] { ("2.5.4.10", "Ollama Inc.") }));
        RejectMultiValued("三个字段都对但多一个多值 RDN", MultiValued(
            new[] { ("2.5.4.5", "2713355") },
            new[] { ("2.5.4.10", "Ollama Inc.") },
            new[] { ("2.5.4.3", "Ollama Inc.") },
            new[] { ("2.5.4.11", "a"), ("2.5.4.7", "b") }));
    }

    private static readonly string? RealOllama = FindRealOllama();

    private static string? FindRealOllama()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
        return File.Exists(path) ? path : null;
    }

    private static string? VerifyFile(string path)
    {
        using var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return AuthenticodeVerifier.CheckOllamaPublisher(path, guard.SafeFileHandle);
    }

    private static async Task CheckInstallAsync()
    {
        using var box = new TempBox();

        var unsigned = Path.Combine(box.Directory, "unsigned.exe");
        File.WriteAllBytes(unsigned, Setup);
        Check("无签名文件被拒绝", VerifyFile(unsigned) is not null);

        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (File.Exists(dotnet))
        {
            var reason = VerifyFile(dotnet);
            Check("有效签名但不是 Ollama 被拒绝", reason?.StartsWith("signer ") == true, reason ?? "null");
        }
        else
        {
            Check("找到别家签名的文件", false, dotnet);
        }

        if (RealOllama is null)
        {
            Check("本机有已签名的 ollama.exe 用于真实校验", false, "未安装 Ollama");
        }
        else
        {
            var genuine = VerifyFile(RealOllama);
            Check("真实 Ollama 签名通过", genuine is null, genuine ?? "");

            var tampered = Path.Combine(box.Directory, "tampered.exe");
            File.Copy(RealOllama, tampered);
            using (var file = new FileStream(tampered, FileMode.Open, FileAccess.ReadWrite))
            {
                file.Seek(file.Length / 2, SeekOrigin.Begin);
                var value = file.ReadByte();
                file.Seek(-1, SeekOrigin.Current);
                file.WriteByte((byte)(value ^ 0xFF));
            }
            var reason = VerifyFile(tampered);
            Check("改动一个字节后被拒绝", reason is not null && reason.StartsWith("WinVerifyTrust"), reason ?? "null");

            // 真实签名 + 安装流程（运行步骤替换掉，不真的安装）
            var fake = new FakeSetupInstaller(box.Directory, 0);
            var setup = Path.Combine(box.Directory, "setup-ok.exe");
            File.Copy(RealOllama, setup);
            var phases = new List<OllamaTaskPhase>();
            await fake.InstallAsync(setup, new InlineProgress(p => phases.Add(p.Phase)), default);
            Check("验签通过才运行", fake.Ran == 1);
            Check("运行时文件被占住不能改写", fake.BlockedWrite);
            Check("先验签后安装", phases.SequenceEqual(new[] { OllamaTaskPhase.VerifyingSetup, OllamaTaskPhase.InstallingSetup }), string.Join(",", phases));
            Check("安装成功后删安装包", !File.Exists(setup));

            var failing = new FakeSetupInstaller(box.Directory, 3);
            var kept = Path.Combine(box.Directory, "setup-fail.exe");
            File.Copy(RealOllama, kept);
            var error = await CaptureAsync(() => failing.InstallAsync(kept, null, default));
            Check("安装程序失败时抛错并带退出码", error is OllamaOperationException { Failure: OllamaFailure.Setup, Message: "3" }, error?.ToString() ?? "null");
            Check("安装失败时留下安装包供重试", File.Exists(kept));
        }

        var runner = new FakeSetupInstaller(box.Directory, 0);
        var bad = Path.Combine(box.Directory, "setup-bad.exe");
        File.WriteAllBytes(bad, Setup);
        var rejected = await CaptureAsync(() => runner.InstallAsync(bad, null, default));
        Check("验签失败报 Signature", rejected is OllamaOperationException { Failure: OllamaFailure.Signature }, rejected?.ToString() ?? "null");
        Check("验签失败绝不运行", runner.Ran == 0);
        Check("验签失败立刻删除", !File.Exists(bad));
    }

    private static async Task CheckManagerAsync()
    {
        foreach (var (url, local) in new[]
                 {
                     ("http://localhost:11434", true), ("http://127.0.0.1:11434", true), ("http://[::1]:11434", true),
                     ("http://192.168.1.2:11434", false), ("https://ollama.example.com", false)
                 })
        {
            var service = new OllamaManagerService(url, new FakeDeployInstaller(null));
            Check($"本机判定 {url}", service.IsLocal == local);
        }
        Check("空地址用默认", new OllamaManagerService(" ", new FakeDeployInstaller(null)).Url == OllamaManagerService.DefaultUrl);

        var installed = new OllamaInstallation(@"C:\Ollama", "0.32.5");
        var model = OllamaModelCatalog.Models[0];

        Check("服务器路径", installed.ServerPath == @"C:\Ollama\ollama.exe");

        // 状态
        var status = new OllamaStatus(true, installed, null);
        Check("未运行时用安装版本", status.Version == "0.32.5" && !status.IsRunning);
        Check("运行版本优先", (status with { RunningVersion = "0.33.0" }).Version == "0.33.0");
        Check("有新版", status.IsUpgradeAvailable("0.40.13"));
        Check("数字比较不是字符串比较", new OllamaStatus(true, installed with { Version = "0.9.0" }, null).IsUpgradeAvailable("0.10.0"));
        Check("相同版本不提示", !status.IsUpgradeAvailable("0.32.5"));
        Check("本机更新时不提示降级", !status.IsUpgradeAvailable("0.30.0"));
        Check("查不到最新版不提示", !status.IsUpgradeAvailable(null));
        Check("版本未知不提示", !new OllamaStatus(true, installed with { Version = null }, null).IsUpgradeAvailable("0.40.13"));

        // 全新电脑
        {
            var api = new FakeApi();
            var installer = new FakeDeployInstaller(null) { Api = api, InstallStartsServer = true };
            var result = await new OllamaManagerService(OllamaManagerService.DefaultUrl, installer, api) { StartTimeout = TimeSpan.FromSeconds(2) }
                .DeployAsync(model, null, default);
            Check("全新：先下载再安装", installer.Log.SequenceEqual(new[] { "latest", "download 0.40.13", "install setup.exe" }), string.Join(",", installer.Log));
            Check("全新：安装后不重复启动服务", !installer.Log.Contains("start"));
            Check("全新：拉推荐模型", api.Pulled.SequenceEqual(new[] { model.Name }), string.Join(",", api.Pulled));
            Check("全新：返回模型名", result == model.Name, result);
        }

        // 安装后托盘没把服务拉起来
        {
            var api = new FakeApi();
            var installer = new FakeDeployInstaller(null) { Api = api, InstallCreates = installed, StartStartsServer = true };
            await new OllamaManagerService(OllamaManagerService.DefaultUrl, installer, api) { PostInstallGrace = TimeSpan.Zero, StartTimeout = TimeSpan.FromSeconds(2) }
                .DeployAsync(model, null, default);
            Check("安装后服务没起来时自己启动", installer.Log.Last() == "start", string.Join(",", installer.Log));
        }

        // 托盘过一会儿才把服务拉起来
        {
            var api = new FakeApi();
            var installer = new FakeDeployInstaller(null) { Api = api, InstallCreates = installed, InstallStartsServerAfter = TimeSpan.FromMilliseconds(100) };
            await new OllamaManagerService(OllamaManagerService.DefaultUrl, installer, api) { PostInstallGrace = TimeSpan.FromSeconds(3), StartTimeout = TimeSpan.FromSeconds(2) }
                .DeployAsync(model, null, default);
            Check("安装后先等托盘拉起服务，不抢着再开一个", !installer.Log.Contains("start"), string.Join(",", installer.Log));
        }

        // 查不到版本
        {
            var api = new FakeApi();
            var installer = new FakeDeployInstaller(null) { Api = api, Latest = null };
            var error = await CaptureAsync(() => new OllamaManagerService(OllamaManagerService.DefaultUrl, installer, api).DeployAsync(model, null, default));
            Check("查不到版本报 VersionLookup", error is OllamaOperationException { Failure: OllamaFailure.VersionLookup }, error?.ToString() ?? "null");
            Check("查不到版本时不下载", installer.Log.SequenceEqual(new[] { "latest" }), string.Join(",", installer.Log));
        }

        // 已装未运行
        {
            var api = new FakeApi { Models = { new OllamaLocalModel("mine:1", 1, "qwen3") } };
            var installer = new FakeDeployInstaller(installed) { Api = api, StartStartsServer = true };
            var phases = new List<OllamaTaskPhase>();
            var result = await new OllamaManagerService(OllamaManagerService.DefaultUrl, installer, api) { StartTimeout = TimeSpan.FromSeconds(2) }
                .DeployAsync(model, new InlineProgress(p => phases.Add(p.Phase)), default);
            Check("已装：不重装不升级", installer.Log.SequenceEqual(new[] { "start" }), string.Join(",", installer.Log));
            Check("已装：有模型时不下载", api.Pulled.Count == 0);
            Check("已装：用已有模型", result == "mine:1", result);
            Check("已装：报告启动阶段", phases.Contains(OllamaTaskPhase.StartingServer));
        }

        // 已在运行
        {
            var api = new FakeApi { Running = true, Models = { new OllamaLocalModel("bge-m3:latest", 1, "bert") } };
            var installer = new FakeDeployInstaller(installed) { Api = api };
            var result = await new OllamaManagerService(OllamaManagerService.DefaultUrl, installer, api).DeployAsync(model, null, default);
            Check("只有嵌入模型时仍下载聊天模型", api.Pulled.SequenceEqual(new[] { model.Name }) && result == model.Name, string.Join(",", api.Pulled));
            Check("运行中不启动", installer.Log.Count == 0, string.Join(",", installer.Log));
        }

        {
            var api = new FakeApi
            {
                Running = true,
                Models = { new OllamaLocalModel("a:1", 1, "x"), new OllamaLocalModel(model.Name, 1, "x") }
            };
            var result = await new OllamaManagerService(OllamaManagerService.DefaultUrl, new FakeDeployInstaller(installed) { Api = api }, api).DeployAsync(model, null, default);
            Check("已有推荐模型时优先用它", result == model.Name && api.Pulled.Count == 0, result);
        }

        // 运行中但本机检测不到安装（服务是别的方式装的）
        {
            var api = new FakeApi { Running = true };
            var installer = new FakeDeployInstaller(null) { Api = api };
            await new OllamaManagerService(OllamaManagerService.DefaultUrl, installer, api).DeployAsync(model, null, default);
            Check("服务已在运行时不安装", !installer.Log.Any(), string.Join(",", installer.Log));
        }

        // 远程
        {
            var api = new FakeApi();
            var installer = new FakeDeployInstaller(null) { Api = api };
            var error = await CaptureAsync(() => new OllamaManagerService("http://192.168.1.2:11434", installer, api).DeployAsync(model, null, default));
            Check("远程未运行报错", error is OllamaOperationException { Failure: OllamaFailure.ServerStart }, error?.ToString() ?? "null");
            Check("远程地址绝不在本机安装或启动", installer.Log.Count == 0, string.Join(",", installer.Log));
            Check("远程地址不检测本机安装", !installer.Detected);
        }

        {
            var api = new FakeApi { Running = true };
            var installer = new FakeDeployInstaller(installed) { Api = api, FreeSpace = 1 };
            var remote = new OllamaManagerService("http://192.168.1.2:11434", installer, api);
            await remote.PullModelAsync(model.Name, model.SizeBytes, null, default);
            Check("远程拉模型不看本机磁盘", api.Pulled.Count == 1);
            var state = await remote.GetStatusAsync(default);
            Check("远程状态不含本机安装", state is { IsLocal: false, Installation: null, IsRunning: true }, state.ToString());
        }

        // 启动失败
        {
            var api = new FakeApi();
            var installer = new FakeDeployInstaller(installed) { Api = api };
            var error = await CaptureAsync(() => new OllamaManagerService(OllamaManagerService.DefaultUrl, installer, api) { StartTimeout = TimeSpan.Zero }.EnsureRunningAsync(null, default));
            Check("启动超时报 ServerStart", error is OllamaOperationException { Failure: OllamaFailure.ServerStart }, error?.ToString() ?? "null");
        }

        {
            var api = new FakeApi();
            var installer = new FakeDeployInstaller(installed) { Api = api, StartThrows = true };
            var error = await CaptureAsync(() => new OllamaManagerService(OllamaManagerService.DefaultUrl, installer, api).EnsureRunningAsync(null, default));
            Check("启动不了进程时报 ServerStart", error is OllamaOperationException { Failure: OllamaFailure.ServerStart }, error?.ToString() ?? "null");
        }

        {
            var api = new FakeApi();
            var error = await CaptureAsync(() => new OllamaManagerService(OllamaManagerService.DefaultUrl, new FakeDeployInstaller(null) { Api = api }, api).EnsureRunningAsync(null, default));
            Check("没安装时启动报 NotInstalled", error is OllamaOperationException { Failure: OllamaFailure.NotInstalled }, error?.ToString() ?? "null");
        }

        // 磁盘
        {
            var api = new FakeApi { Running = true };
            var size = model.SizeBytes;
            var installer = new FakeDeployInstaller(installed) { Api = api, FreeSpace = size };
            var error = await CaptureAsync(() => new OllamaManagerService(OllamaManagerService.DefaultUrl, installer, api).PullModelAsync(model.Name, size, null, default));
            Check("模型放不下时不开始下载", error is OllamaOperationException { Failure: OllamaFailure.DiskSpace } && api.Pulled.Count == 0, error?.ToString() ?? "null");
            Check("磁盘检查看的是模型目录", installer.FreeSpaceQueries.Contains(OllamaManagerService.ModelsDirectory), string.Join(",", installer.FreeSpaceQueries));

            installer.FreeSpace = size * 2;
            await new OllamaManagerService(OllamaManagerService.DefaultUrl, installer, api).PullModelAsync(model.Name, size, null, default);
            Check("空间够时下载", api.Pulled.Count == 1);

            installer.FreeSpace = 1;
            await new OllamaManagerService(OllamaManagerService.DefaultUrl, installer, api).PullModelAsync("custom:latest", 0, null, default);
            Check("体积未知的自定义模型不做磁盘预判", api.Pulled.Count == 2);
        }
    }

    private static void CheckServerStartInfo()
    {
        var info = OllamaInstaller.CreateServerStartInfo(new OllamaInstallation(@"C:\Ollama", "0.40.13"), new Uri("http://127.0.0.1:11555"));
        Check("服务听在设置填的端口", info.Environment["OLLAMA_HOST"] == "127.0.0.1:11555", info.Environment["OLLAMA_HOST"] ?? "null");
        Check("服务用的模型目录和查空间的是同一个", info.Environment.TryGetValue("OLLAMA_MODELS", out var models) && models == OllamaManagerService.ModelsDirectory, models ?? "null");
        Check("用 serve 启动而不是托盘程序", info.Arguments == "serve" && Path.GetFileName(info.FileName) == "ollama.exe", info.FileName);
        Check("启动服务不弹窗口", info.CreateNoWindow && !info.UseShellExecute);
    }

    /// <summary>对一个真实的 Ollama 服务跑完整的模型流程。服务由调用方用隔离的端口和模型目录启动。</summary>
    private static async Task<int> RunSmokeAsync(string url)
    {
        const string small = "all-minilm:l6-v2";

        var installer = new OllamaInstaller(_ => Task.FromResult<IReadOnlyList<DownloadRoute>>(Array.Empty<DownloadRoute>()));
        var service = new OllamaManagerService(url, installer);

        var status = await service.GetStatusAsync(default);
        Check("真实服务：读到版本", status.IsRunning, status.ToString());
        Check("真实服务：检测到本机安装", status.Installation is not null, status.ToString());
        Console.WriteLine($"  版本 {status.Version}，安装目录 {status.Installation?.Directory}");

        Check("真实服务：初始没有模型", (await service.ListModelsAsync(default)).Count == 0);

        using (var cancel = new CancellationTokenSource())
        {
            var phases = new List<OllamaTaskProgress>();
            var progress = new InlineProgress(p =>
            {
                lock (phases) phases.Add(p);
                if (p.Completed > 1024 * 1024) cancel.Cancel();
            });
            var error = await CaptureAsync(() => service.PullModelAsync(small, 45_000_000, progress, cancel.Token));
            Check("真实服务：中途取消", error is OperationCanceledException, error?.ToString() ?? "null");
            Check("真实服务：取消前有进度", phases.Any(p => p.Completed > 0 && p.Total >= 45_000_000), phases.LastOrDefault()?.ToString() ?? "none");
        }

        var seen = new HashSet<OllamaTaskPhase>();
        var peak = 0L;
        await service.PullModelAsync(small, 45_000_000, new InlineProgress(p =>
        {
            lock (seen) seen.Add(p.Phase);
            Interlocked.Exchange(ref peak, Math.Max(Interlocked.Read(ref peak), p.Completed));
        }), default);
        Check("真实服务：续传后下载完成", peak > 0, peak.ToString());
        Check("真实服务：经历下载和校验阶段", seen.Contains(OllamaTaskPhase.PullingModel) && seen.Contains(OllamaTaskPhase.VerifyingModel), string.Join(",", seen));

        var models = await service.ListModelsAsync(default);
        Check("真实服务：列表里有新模型", models.Any(m => m.Name == small), string.Join(",", models.Select(m => $"{m.Name}/{m.Family}/{m.SizeBytes}")));
        Check("真实服务：嵌入模型被识别", models.Single(m => m.Name == small).IsChatModel == false, models.Single().ToString());

        var bogus = await CaptureAsync(() => service.PullModelAsync("vpetllm-no-such-model:none", 0, null, default));
        Check("真实服务：不存在的模型报错带原话", bogus is OllamaOperationException { Failure: OllamaFailure.Api }, bogus?.ToString() ?? "null");
        Console.WriteLine($"  Ollama 原话: {bogus?.Message}");

        await service.DeleteModelAsync(small, default);
        Check("真实服务：删除后列表为空", (await service.ListModelsAsync(default)).Count == 0);

        var missing = await CaptureAsync(() => service.DeleteModelAsync(small, default));
        Check("真实服务：删除不存在的模型报错", missing is OllamaOperationException, missing?.ToString() ?? "null");

        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>真实的版本查询和服务拉起。端口上要没有服务，拉起的进程由调用方负责结束。</summary>
    private static async Task<int> RunStartSmokeAsync(string url)
    {
        var installer = new OllamaInstaller(_ => Task.FromResult<IReadOnlyList<DownloadRoute>>(Array.Empty<DownloadRoute>()));
        var service = new OllamaManagerService(url, installer);

        var latest = await service.GetLatestVersionAsync(default);
        Check("真实网络：查到最新版本", latest is not null && System.Text.RegularExpressions.Regex.IsMatch(latest, @"^\d+\.\d+\.\d+$"), latest ?? "null");
        Console.WriteLine($"  最新版本 {latest}");

        Check("拉起前端口上没有服务", !(await service.GetStatusAsync(default)).IsRunning);

        var phases = new List<OllamaTaskPhase>();
        await service.EnsureRunningAsync(new InlineProgress(p => phases.Add(p.Phase)), default);
        var status = await service.GetStatusAsync(default);
        Check("真实服务：拉起后在指定端口上响应", status.IsRunning, status.ToString());
        Check("真实服务：报告了启动阶段", phases.Contains(OllamaTaskPhase.StartingServer), string.Join(",", phases));
        Console.WriteLine($"  版本 {status.Version}");

        phases.Clear();
        await service.EnsureRunningAsync(new InlineProgress(p => phases.Add(p.Phase)), default);
        Check("真实服务：已运行时不重复启动", phases.Count == 0, string.Join(",", phases));

        const string small = "all-minilm:l6-v2";
        await service.PullModelAsync(small, 45_000_000, null, default);
        var blobs = Directory.GetFiles(OllamaManagerService.ModelsDirectory, "*", SearchOption.AllDirectories);
        Check("真实服务：模型落在 OLLAMA_MODELS 指定的目录", blobs.Length > 0, OllamaManagerService.ModelsDirectory);

        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    private static HttpMessageHandler Reply(HttpStatusCode status, string body)
        => new FakeHandler((_, _) => Task.FromResult(Text(status, body)));

    private static HttpResponseMessage Text(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8) };

    private static async Task<bool> ThrowsAsync<T>(Func<Task> action) where T : Exception
        => await CaptureAsync(action) is T;

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>同步回调的 IProgress，Progress&lt;T&gt; 走线程池，顺序和时机都不定。</summary>
    private sealed class InlineProgress(Action<OllamaTaskProgress> report) : IProgress<OllamaTaskProgress>
    {
        public void Report(OllamaTaskProgress value) => report(value);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    /// <summary>先吐完固定内容，再挂到取消为止；hang 为 false 时直接结束，不看取消。</summary>
    private sealed class ScriptedStream(byte[] head, bool hang = true) : Stream
    {
        private int _position;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_position < head.Length)
            {
                var count = Math.Min(buffer.Length, head.Length - _position);
                head.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }
            if (hang) await Task.Delay(Timeout.Infinite, token);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>可配置各种毛病的安装包下载服务器。</summary>
    private sealed class SetupServer
    {
        public byte[] Payload { get; init; } = Setup;
        public bool IgnoreRange { get; init; }
        public bool RefuseConnection { get; init; }
        public bool HangBeforeHeaders { get; init; }
        public long RangeSkew { get; init; }
        public int? CutAfter { get; set; }
        public int? HangAfter { get; init; }
        public Action? OnHang { get; init; }
        public TimeSpan ChunkDelay { get; init; }
        public int ChunkSize { get; init; } = 64 * 1024;

        public List<long?> Ranges { get; } = new();
        public List<string> Urls { get; } = new();

        public DownloadRoute Route() => new($"https://github.com/ollama/ollama/releases/download/v{Version}/OllamaSetup.exe", () => new FakeHandler(HandleAsync));

        private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken token)
        {
            Urls.Add(request.RequestUri!.ToString());
            var from = request.Headers.Range?.Ranges.Single().From;
            Ranges.Add(from);

            if (RefuseConnection) throw new HttpRequestException("refused");
            if (HangBeforeHeaders)
            {
                await Task.Delay(Timeout.Infinite, token);
            }

            if (from >= Payload.Length)
            {
                var unsatisfied = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                unsatisfied.Content.Headers.ContentRange = new ContentRangeHeaderValue(Payload.Length);
                return unsatisfied;
            }

            var ranged = from is not null && !IgnoreRange;
            var start = ranged ? (int)(from!.Value + RangeSkew) : 0;
            var end = Payload.Length;
            var hang = false;
            if (HangAfter > start) { end = HangAfter.Value; hang = true; }

            var cut = CutAfter;
            if (cut > start)
            {
                end = cut.Value;
                CutAfter = null;
            }

            var content = new StreamContent(new ThrottledStream(Payload, start, end, hang ? OnHang : null, hang, ChunkSize, ChunkDelay));
            content.Headers.ContentLength = Payload.Length - start;
            var response = new HttpResponseMessage(ranged ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
            if (ranged)
                content.Headers.ContentRange = new ContentRangeHeaderValue(start, Payload.Length - 1, Payload.Length);
            return response;
        }
    }

    private sealed class ThrottledStream(byte[] payload, int start, int end, Action? onHang, bool hang, int chunkSize, TimeSpan delay) : Stream
    {
        private int _position = start;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);

            if (_position >= end)
            {
                if (!hang) return 0;
                onHang?.Invoke();
                await Task.Delay(Timeout.Infinite, token);
            }

            var count = Math.Min(Math.Min(buffer.Length, chunkSize), end - _position);
            payload.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TempBox : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("vpetllm-ollama-").FullName;
        public string Target => Path.Combine(Directory, $"OllamaSetup-{Version}.exe");
        public string Part => Target + ".part";

        public void WritePart(int length)
        {
            var bytes = new byte[length];
            Array.Copy(Setup, bytes, Math.Min(length, Setup.Length));
            File.WriteAllBytes(Part, bytes);
        }

        public OllamaInstaller Installer(params DownloadRoute[] routes) => Installer(long.MaxValue, routes);

        public OllamaInstaller Installer(long freeSpace, params DownloadRoute[] routes)
            => new LimitedDiskInstaller(freeSpace, routes, Directory)
            {
                StallTimeout = TimeSpan.FromMilliseconds(200),
                ConnectTimeout = TimeSpan.FromMilliseconds(200)
            };

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class LimitedDiskInstaller(long freeSpace, IReadOnlyList<DownloadRoute> routes, string directory)
        : OllamaInstaller(_ => Task.FromResult(routes), cacheDirectory: directory)
    {
        public override long GetFreeSpace(string path) => freeSpace;
    }

    /// <summary>验签走真实逻辑，只替换运行安装程序这一步。</summary>
    private sealed class FakeSetupInstaller(string directory, int exitCode)
        : OllamaInstaller(_ => Task.FromResult<IReadOnlyList<DownloadRoute>>(Array.Empty<DownloadRoute>()), cacheDirectory: directory)
    {
        public int Ran { get; private set; }
        public bool BlockedWrite { get; private set; }

        protected override Task<int> RunSetupAsync(string setupPath, CancellationToken token)
        {
            Ran++;
            try
            {
                using var _ = new FileStream(setupPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            }
            catch (IOException)
            {
                BlockedWrite = true;
            }
            return Task.FromResult(exitCode);
        }
    }

    private sealed class FakeDeployInstaller(OllamaInstallation? installation)
        : OllamaInstaller(_ => Task.FromResult<IReadOnlyList<DownloadRoute>>(Array.Empty<DownloadRoute>()))
    {
        private OllamaInstallation? _installation = installation;

        public FakeApi? Api { get; init; }
        public string? Latest { get; init; } = "0.40.13";
        public bool InstallStartsServer { get; init; }
        public TimeSpan? InstallStartsServerAfter { get; init; }
        public TaskCompletionSource? InstallGate { get; init; }
        public OllamaInstallation? InstallCreates { get; init; }
        public bool StartStartsServer { get; init; }
        public bool StartThrows { get; init; }
        public long FreeSpace { get; set; } = long.MaxValue;
        public OllamaOperationException? DownloadThrows { get; init; }

        public List<string> Log { get; } = new();
        public List<string> FreeSpaceQueries { get; } = new();
        public bool Detected { get; private set; }

        public override OllamaInstallation? Detect()
        {
            Detected = true;
            return _installation;
        }

        public override Task<string?> GetLatestVersionAsync(CancellationToken token)
        {
            Log.Add("latest");
            return Task.FromResult(Latest);
        }

        public override Task<string> DownloadAsync(string version, IProgress<OllamaTaskProgress>? progress, CancellationToken token)
        {
            if (DownloadThrows is not null) throw DownloadThrows;
            Log.Add("download " + version);
            return Task.FromResult("setup.exe");
        }

        public override async Task InstallAsync(string setupPath, IProgress<OllamaTaskProgress>? progress, CancellationToken token)
        {
            Log.Add("install " + setupPath);
            progress?.Report(new OllamaTaskProgress(OllamaTaskPhase.InstallingSetup));
            if (InstallGate is not null) await InstallGate.Task;
            _installation = InstallCreates ?? new OllamaInstallation(@"C:\Ollama", "0.40.13");
            if (InstallStartsServer) Api!.Running = true;
            if (InstallStartsServerAfter is { } delay) _ = Task.Delay(delay).ContinueWith(_ => Api!.Running = true);
        }

        public override void StartServer(OllamaInstallation target, Uri endpoint)
        {
            if (StartThrows) throw new System.ComponentModel.Win32Exception(5);
            Log.Add("start");
            if (StartStartsServer) Api!.Running = true;
        }

        public override long GetFreeSpace(string path)
        {
            FreeSpaceQueries.Add(path);
            return FreeSpace;
        }
    }

    /// <summary>够用的假 Ollama：能查版本、列模型、拉模型。</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        public bool Running { get; set; }
        public List<OllamaLocalModel> Models { get; init; } = new();
        public List<string> Pulled { get; } = new();
        public List<string> Deleted { get; } = new();
        public TaskCompletionSource? TagsGate { get; set; }
        public int TagRequests { get; private set; }
        public bool PullAddsModel { get; set; }
        public string? PullError { get; init; }
        public TaskCompletionSource? PullGate { get; set; }
        public bool PullCancelled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!Running) throw new HttpRequestException("refused");

            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/version":
                    return Text(HttpStatusCode.OK, """{"version":"0.40.13"}""");
                case "/api/delete":
                    var deleting = Newtonsoft.Json.Linq.JObject.Parse(await request.Content!.ReadAsStringAsync(token))["model"]!.ToString();
                    Deleted.Add(deleting);
                    Models.RemoveAll(m => m.Name == deleting);
                    return Text(HttpStatusCode.OK, "");
                case "/api/tags":
                    TagRequests++;
                    if (TagsGate is { } tagsGate) await tagsGate.Task.WaitAsync(token);
                    var entries = Models.Select(m => new Newtonsoft.Json.Linq.JObject
                    {
                        ["name"] = m.Name,
                        ["size"] = m.SizeBytes,
                        ["details"] = new Newtonsoft.Json.Linq.JObject { ["family"] = m.Family }
                    });
                    return Text(HttpStatusCode.OK, new Newtonsoft.Json.Linq.JObject { ["models"] = new Newtonsoft.Json.Linq.JArray(entries) }.ToString());
                case "/api/pull":
                    var body = await request.Content!.ReadAsStringAsync(token);
                    var model = Newtonsoft.Json.Linq.JObject.Parse(body)["model"]!.ToString();
                    Pulled.Add(model);
                    if (PullGate is { } gate)
                    {
                        try
                        {
                            await gate.Task.WaitAsync(token);
                        }
                        catch (OperationCanceledException)
                        {
                            PullCancelled = true;
                            throw;
                        }
                    }
                    if (PullError is not null)
                        return Text(HttpStatusCode.OK, new Newtonsoft.Json.Linq.JObject { ["error"] = PullError }.ToString(Newtonsoft.Json.Formatting.None));
                    if (PullAddsModel) Models.Add(new OllamaLocalModel(model, GB, "qwen3"));
                    return Text(HttpStatusCode.OK, """{"status":"success"}""");
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }
}
