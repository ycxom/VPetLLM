using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows.Controls;
using VPetLLM.Core.Services;
using VPetLLM.UI.Windows;

namespace VPetLLM.OllamaChecks;

/// <summary>下载慢时的两条出路：安装包改走代理，模型断开重连。</summary>
internal static partial class Program
{
    private static readonly Uri TestProxy = new("http://127.0.0.1:7890");

    private static readonly TimeSpan ShortStall = TimeSpan.FromMilliseconds(200);

    private static async Task CheckDownloadProxyAsync()
    {
        var official = $"https://github.com/ollama/ollama/releases/download/v{Version}/OllamaSetup.exe";

        using (var box = new TempBox())
        {
            var mirror = new SetupServer();
            var proxy = new SetupServer();
            var used = new List<Uri>();
            var installer = box.ProxyInstaller(proxy, used, ShortStall, mirror.MirrorRoute());
            installer.UseDownloadProxy(TestProxy);
            var path = await installer.DownloadAsync(Version, null, default);
            Check("代理：内容完整", File.ReadAllBytes(path).SequenceEqual(Setup));
            Check("代理：经代理请求官方地址", proxy.Urls.SequenceEqual(new[] { official }), string.Join(",", proxy.Urls));
            Check("代理：连的是用户填的代理", used.SequenceEqual(new[] { TestProxy }), string.Join(",", used));
            Check("代理：好用时不再走镜像", mirror.Urls.Count == 0, string.Join(",", mirror.Urls));
        }

        // 卡住的线路要等 30 秒才会被判死，换代理必须当场断开，并接着已下的部分
        using (var box = new TempBox())
        {
            OllamaInstaller? installer = null;
            var stuck = new SetupServer { HangAfter = 100_000, OnHang = () => installer!.UseDownloadProxy(TestProxy) };
            var proxy = new SetupServer();
            var used = new List<Uri>();
            installer = box.ProxyInstaller(proxy, used, TimeSpan.FromSeconds(30), stuck.MirrorRoute());
            var clock = Stopwatch.StartNew();
            var path = await installer.DownloadAsync(Version, null, default).WaitAsync(TimeSpan.FromSeconds(20));
            Check("中途换代理：内容完整", File.ReadAllBytes(path).SequenceEqual(Setup));
            Check("中途换代理：当场断开原线路", clock.Elapsed < TimeSpan.FromSeconds(5), clock.Elapsed.ToString());
            Check("中途换代理：从已下载处续传", proxy.Ranges.SequenceEqual(new long?[] { 100_000 }), string.Join(",", proxy.Ranges));
            Check("中途换代理：原线路没有重试", stuck.Urls.Count == 1, string.Join(",", stuck.Urls));
        }

        using (var box = new TempBox())
        {
            var mirror = new SetupServer();
            var proxy = new SetupServer { RefuseConnection = true };
            var installer = box.ProxyInstaller(proxy, new List<Uri>(), ShortStall, mirror.MirrorRoute());
            installer.UseDownloadProxy(TestProxy);
            var path = await installer.DownloadAsync(Version, null, default);
            Check("代理连不上：退回原来的线路", File.ReadAllBytes(path).SequenceEqual(Setup) && proxy.Urls.Count == 1 && mirror.Urls.Count == 1,
                $"proxy={proxy.Urls.Count} mirror={mirror.Urls.Count}");
        }

        var direct = 0;
        var lookup = new OllamaInstaller(_ => Task.FromResult<IReadOnlyList<DownloadRoute>>(Array.Empty<DownloadRoute>()),
            directHandler: () => new FakeHandler((_, _) =>
            {
                direct++;
                throw new HttpRequestException("blocked");
            }),
            proxyHandler: _ => new FakeHandler((_, _) => Task.FromResult(Text(HttpStatusCode.OK,
                """{"url":"https://github.com/ollama/ollama/releases/download/v0.41.0/OllamaSetup.exe"}"""))));
        lookup.UseDownloadProxy(TestProxy);
        var latest = await lookup.GetLatestVersionAsync(default);
        Check("查版本：设了代理先经代理查", latest == "0.41.0" && direct == 0, $"{latest} direct={direct}");

        Check("代理地址：没写协议当 http", OllamaInstaller.ParseProxy(" 127.0.0.1:7890 ") is { Scheme: "http", Host: "127.0.0.1", Port: 7890 });
        Check("代理地址：认 socks5", OllamaInstaller.ParseProxy("socks5://127.0.0.1:1080")?.Scheme == "socks5");
        foreach (var bad in new[] { null, "", "   ", "ftp://127.0.0.1:21", "http://" })
            Check($"代理地址：拒绝 \"{bad}\"", OllamaInstaller.ParseProxy(bad) is null);
    }

    private static async Task CheckReconnectAsync()
    {
        static OllamaManagerService Service(FakeApi api)
            => new(Url, new FakeDeployInstaller(null) { Api = api }, api) { ReconnectDelay = TimeSpan.FromMilliseconds(10) };

        {
            var api = new FakeApi { Running = true, PullScript = n => n <= 2 ? FakeApi.StuckPull(100, GB) : null };
            var service = Service(api);
            var reports = new SemaphoreSlim(0);
            var pull = service.PullModelAsync("qwen3:4b", 0, new InlineProgress(_ => reports.Release()), default);

            var first = await reports.WaitAsync(TimeSpan.FromSeconds(5));
            service.ReconnectPull();
            var second = await reports.WaitAsync(TimeSpan.FromSeconds(5));
            service.ReconnectPull();
            var error = await CaptureAsync(() => pull.WaitAsync(TimeSpan.FromSeconds(5)));
            Check("重连：每次都断开重新请求，最后下完", first && second && error is null && api.Pulled.Count == 3,
                $"{first} {second} {error?.GetType().Name} pulls={api.Pulled.Count}");
        }

        {
            var api = new FakeApi
            {
                Running = true,
                PullScript = n => n switch
                {
                    1 => FakeApi.StuckPull(100, GB),
                    2 => FakeApi.PullFails("context canceled"),
                    _ => null
                }
            };
            var service = Service(api);
            var reports = new SemaphoreSlim(0);
            var pull = service.PullModelAsync("qwen3:4b", 0, new InlineProgress(_ => reports.Release()), default);
            await reports.WaitAsync(TimeSpan.FromSeconds(5));
            service.ReconnectPull();
            var error = await CaptureAsync(() => pull.WaitAsync(TimeSpan.FromSeconds(5)));
            Check("重连：上一轮没收完报的 canceled 再试", error is null && api.Pulled.Count == 3, $"{error?.Message} pulls={api.Pulled.Count}");
        }

        {
            var api = new FakeApi { Running = true, PullScript = n => n == 1 ? FakeApi.StuckPull(100, GB) : FakeApi.PullFails("context canceled") };
            var service = Service(api);
            var reports = new SemaphoreSlim(0);
            var pull = service.PullModelAsync("qwen3:4b", 0, new InlineProgress(_ => reports.Release()), default);
            await reports.WaitAsync(TimeSpan.FromSeconds(5));
            service.ReconnectPull();
            var error = await CaptureAsync(() => pull.WaitAsync(TimeSpan.FromSeconds(5)));
            Check("重连：一直 canceled 时有限次后报错", error is OllamaOperationException { Failure: OllamaFailure.Api } && api.Pulled.Count == 7,
                $"{error?.GetType().Name} pulls={api.Pulled.Count}");
        }

        {
            var api = new FakeApi { Running = true, PullScript = _ => FakeApi.PullFails("context canceled") };
            var error = await CaptureAsync(() => Service(api).PullModelAsync("qwen3:4b", 0, null, default));
            Check("没重连过的 canceled 照常报错", error is OllamaOperationException { Failure: OllamaFailure.Api } && api.Pulled.Count == 1,
                $"{error?.GetType().Name} pulls={api.Pulled.Count}");
        }

        {
            var api = new FakeApi { Running = true, PullScript = _ => FakeApi.StuckPull(100, GB) };
            using var cancel = new CancellationTokenSource();
            var reports = new SemaphoreSlim(0);
            var pull = Service(api).PullModelAsync("qwen3:4b", 0, new InlineProgress(_ => reports.Release()), cancel.Token);
            await reports.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            var error = await CaptureAsync(() => pull.WaitAsync(TimeSpan.FromSeconds(5)));
            Check("真取消不当成重连", error is OperationCanceledException && api.Pulled.Count == 1, $"{error?.GetType().Name} pulls={api.Pulled.Count}");
        }

        Service(new FakeApi { Running = true }).ReconnectPull();
        Check("没在下载时重连什么也不做", true);
    }

    private static void CheckSlowBanner()
    {
        var model = OllamaModelCatalog.Models[0].Name;

        {
            var api = new FakeApi { Running = true, PullAddsModel = true, PullScript = n => n == 1 ? FakeApi.StuckPull(100, 10 * GB) : null };
            var window = new ManagerWindow(new FakeDeployInstaller(null) { Api = api }, api);
            window.Window.SlowPeriod = TimeSpan.FromMilliseconds(200);
            window.ClickCatalog(model);
            Pump(() => window.Shown("Panel_Slow"));
            Check("模型慢：出现提示", window.Shown("Panel_Slow"));
            Check("模型慢：给断开重连，不问代理", window.Shown("Btn_Reconnect") && !window.Shown("Panel_Proxy") && !window.Shown("Btn_UseProxy"));
            Check("模型慢：说清多慢", window.Text("Tb_SlowHint").Contains("KB/s"), window.Text("Tb_SlowHint"));

            window.Click("Btn_Reconnect");
            Check("重连：提示收起", !window.Shown("Panel_Slow"));
            window.Finish();
            Check("重连：重新请求后下完", api.Pulled.Count == 2 && window.Chosen.Count == 1, $"pulls={api.Pulled.Count} chosen={window.Chosen.Count}");
            window.Close();
        }

        {
            var api = new FakeApi { Running = true, PullScript = _ => FakeApi.StuckPull(100, 10 * GB) };
            var window = new ManagerWindow(new FakeDeployInstaller(null) { Api = api }, api);
            window.Window.SlowPeriod = TimeSpan.FromMilliseconds(100);
            window.ClickCatalog(model);
            Pump(() => window.Shown("Panel_Slow"));
            window.Click("Btn_SlowDismiss");
            var until = DateTime.UtcNow.AddMilliseconds(600);
            Pump(() => window.Shown("Panel_Slow") || DateTime.UtcNow > until);
            Check("不用了：这次任务不再提示", !window.Shown("Panel_Slow"));
            Check("不用了：不打断下载", api.Pulled.Count == 1 && !window.Idle, $"pulls={api.Pulled.Count}");
            window.Click("Btn_Cancel");
            window.Finish();
            window.Close();
        }

        {
            // 只差最后一点时慢也不提示，几秒就下完了
            var api = new FakeApi { Running = true, PullScript = _ => FakeApi.StuckPull(10 * GB - 1, 10 * GB) };
            var window = new ManagerWindow(new FakeDeployInstaller(null) { Api = api }, api);
            window.Window.SlowPeriod = TimeSpan.FromMilliseconds(100);
            window.ClickCatalog(model);
            var until = DateTime.UtcNow.AddMilliseconds(600);
            Pump(() => window.Shown("Panel_Slow") || DateTime.UtcNow > until);
            Check("快下完了：不提示", !window.Shown("Panel_Slow"));
            window.Click("Btn_Cancel");
            window.Finish();
            window.Close();
        }

        var saved = _settings!.Proxy;
        var remembered = typeof(winOllamaManager).GetField("_downloadProxy", BindingFlags.Static | BindingFlags.NonPublic)!;
        try
        {
            // 只在内存里改，不保存
            _settings.Proxy = new Setting.ProxySetting { IsEnabled = true, Protocol = "socks", Address = "127.0.0.1:1080" };
            var api = new FakeApi();
            var installer = new FakeDeployInstaller(null) { Api = api, InstallStartsServer = true, SlowUntilProxy = true };
            var window = new ManagerWindow(installer, api);
            window.Window.SlowPeriod = TimeSpan.FromMilliseconds(200);
            window.Click("Btn_Install");
            Pump(() => window.Shown("Panel_Slow"));
            Check("安装包慢：问要不要用代理", window.Shown("Panel_Slow") && window.Shown("Panel_Proxy") && window.Shown("Btn_UseProxy") && !window.Shown("Btn_Reconnect"));

            var box = window.Find<TextBox>("Tb_Proxy");
            Check("安装包慢：预填设置里开着的代理", box.Text == "socks5://127.0.0.1:1080", box.Text);

            box.Text = "ftp://127.0.0.1:21";
            window.Click("Btn_UseProxy");
            Check("代理地址不对：说明格式", window.Text("Tb_SlowHint").Contains("代理地址不对"), window.Text("Tb_SlowHint"));
            Check("代理地址不对：不改用", installer.DownloadProxy is null && window.Shown("Panel_Slow"));

            box.Text = "127.0.0.1:7890";
            window.Click("Btn_UseProxy");
            Check("用代理：提示收起并说明", !window.Shown("Panel_Slow") && window.Text("Tb_Status").Contains("127.0.0.1:7890"), window.Text("Tb_Status"));
            window.Finish();
            Check("用代理：安装器接着下载", installer.Log.Contains($"proxy {TestProxy}"), string.Join(",", installer.Log));
            Check("用代理：没写进设置", _settings.Proxy.Address == "127.0.0.1:1080" && _settings.Proxy.Protocol == "socks");
            window.Close();

            var again = new FakeDeployInstaller(null) { Api = api };
            var next = new ManagerWindow(again, api);
            Check("再开窗口：沿用这次的代理", again.DownloadProxy == TestProxy, again.DownloadProxy?.ToString() ?? "null");
            next.Close();
        }
        finally
        {
            _settings.Proxy = saved;
            remembered.SetValue(null, null);
        }

        using var local = new FakeLocalProxy(FakeLocalProxy.Mode.NotProxy);
        try
        {
            _settings.Proxy = new Setting.ProxySetting { IsEnabled = false };
            var api = new FakeApi();
            var installer = new FakeDeployInstaller(null) { Api = api, InstallStartsServer = true, SlowUntilProxy = true };
            var detector = new LocalProxyDetector
            {
                Candidates = new[] { new LocalProxyCandidate(local.Port, "Clash") },
                ProbeTimeout = TimeSpan.FromMilliseconds(500),
                SystemProxy = _ => null
            };
            var window = new ManagerWindow(installer, api, proxies: detector);
            window.Window.SlowPeriod = TimeSpan.FromMilliseconds(200);
            window.Click("Btn_Install");
            Pump(() => window.Text("Tb_ProxyFound").Contains("没找到"));
            var box = window.Find<TextBox>("Tb_Proxy");
            Check("自动检测：弹提示就去找，没找到说怎么填", window.Text("Tb_ProxyFound").Contains("没找到本机代理") && box.Text == "",
                $"{window.Text("Tb_ProxyFound")} / {box.Text}");

            // 用户看到提示才去开代理软件，再点一次检测
            local.Current = FakeLocalProxy.Mode.Http;
            box.Text = "乱填的";
            window.Click("Btn_DetectProxy");
            var expected = $"http://127.0.0.1:{local.Port}";
            Pump(() => box.Text == expected);
            Check("再检测：找到后替换框里的内容", box.Text == expected, box.Text);
            Check("再检测：说明找到的是谁", window.Text("Tb_ProxyFound").Contains($"127.0.0.1:{local.Port}") && window.Text("Tb_ProxyFound").Contains("Clash"),
                window.Text("Tb_ProxyFound"));

            window.Click("Btn_UseProxy");
            window.Finish();
            Check("检测到的代理：点一下就用上", installer.Log.Contains($"proxy {expected}/"), string.Join(",", installer.Log));
            window.Close();
        }
        finally
        {
            _settings.Proxy = saved;
            remembered.SetValue(null, null);
        }

        try
        {
            _settings.Proxy = new Setting.ProxySetting { IsEnabled = false };
            var api = new FakeApi();
            var installer = new FakeDeployInstaller(null) { Api = api, InstallStartsServer = true, SlowUntilProxy = true };
            var detector = new LocalProxyDetector
            {
                Candidates = Array.Empty<LocalProxyCandidate>(),
                SystemProxy = _ => new Uri("http://127.0.0.1:7890")
            };
            var window = new ManagerWindow(installer, api, proxies: detector);
            window.Window.SlowPeriod = TimeSpan.FromMilliseconds(200);
            window.Click("Btn_Install");
            Pump(() => window.Text("Tb_ProxyFound").Contains("系统代理"));
            Check("系统代理开着：说下载已经经过它", window.Text("Tb_ProxyFound").Contains("系统代理已经开着") && window.Text("Tb_ProxyFound").Contains("127.0.0.1:7890"),
                window.Text("Tb_ProxyFound"));
            window.Click("Btn_Cancel");
            window.Finish();
            window.Close();
        }
        finally
        {
            _settings.Proxy = saved;
            remembered.SetValue(null, null);
        }
    }

    private static async Task CheckLocalProxyDetectAsync()
    {
        static LocalProxyDetector Detector(params int[] ports) => new()
        {
            Candidates = ports.Select((port, i) => new LocalProxyCandidate(port, $"软件{i}")).ToList(),
            ProbeHost = "probe.example",
            ProbeTimeout = TimeSpan.FromMilliseconds(500),
            SystemProxy = _ => null
        };

        var closed = FakeLocalProxy.ClosedPort();

        using (var http = new FakeLocalProxy(FakeLocalProxy.Mode.Http))
        {
            var scan = await Detector(closed, http.Port).DetectAsync();
            Check("探测：找到 HTTP 代理", scan.Found == new Uri($"http://127.0.0.1:{http.Port}") && scan.Software == "软件1",
                $"{scan.Found} {scan.Software}");
            Check("探测：真的经它握手", http.Targets.Contains("CONNECT probe.example:443 HTTP/1.1"), string.Join(",", http.Targets));
        }

        using (var socks = new FakeLocalProxy(FakeLocalProxy.Mode.Socks5))
        {
            var scan = await Detector(socks.Port).DetectAsync();
            Check("探测：只认 SOCKS5 的端口按 socks5 用", scan.Found == new Uri($"socks5://127.0.0.1:{socks.Port}"), scan.Found?.ToString() ?? "null");
            Check("探测：SOCKS5 也真的握手", socks.Targets.Contains("socks probe.example:443"), string.Join(",", socks.Targets));
        }

        using (var mixed = new FakeLocalProxy(FakeLocalProxy.Mode.Mixed))
        {
            var scan = await Detector(mixed.Port).DetectAsync();
            Check("探测：混合端口按 HTTP 用", scan.Found?.Scheme == "http", scan.Found?.ToString() ?? "null");
        }

        using (var web = new FakeLocalProxy(FakeLocalProxy.Mode.NotProxy))
        using (var silent = new FakeLocalProxy(FakeLocalProxy.Mode.Silent))
        {
            var clock = Stopwatch.StartNew();
            var scan = await Detector(web.Port, silent.Port, closed).DetectAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check("探测：端口开着但不是代理不算", scan.Found is null, scan.Found?.ToString() ?? "null");
            Check("探测：不回话的端口按时放弃，各端口同时试", clock.Elapsed < TimeSpan.FromSeconds(3), clock.Elapsed.ToString());
        }

        using (var socks = new FakeLocalProxy(FakeLocalProxy.Mode.Socks5))
        using (var http = new FakeLocalProxy(FakeLocalProxy.Mode.Http))
        {
            var scan = await Detector(socks.Port, http.Port).DetectAsync();
            Check("探测：都能用时按列表顺序挑", scan.Found?.Port == socks.Port, scan.Found?.ToString() ?? "null");
        }

        using (var system = new FakeLocalProxy(FakeLocalProxy.Mode.Http))
        using (var other = new FakeLocalProxy(FakeLocalProxy.Mode.Http))
        {
            var scan = await new LocalProxyDetector
            {
                Candidates = Detector(system.Port, other.Port).Candidates,
                ProbeTimeout = TimeSpan.FromMilliseconds(500),
                SystemProxy = _ => new Uri($"http://localhost:{system.Port}")
            }.DetectAsync();
            Check("探测：跳过系统代理已经指着的端口", scan.Found?.Port == other.Port && system.Targets.IsEmpty,
                $"{scan.Found} system={system.Targets.Count}");
            Check("探测：报出系统代理", scan.SystemProxy?.Port == system.Port, scan.SystemProxy?.ToString() ?? "null");
        }

        using (var silent = new FakeLocalProxy(FakeLocalProxy.Mode.Silent))
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var error = await CaptureAsync(() => new LocalProxyDetector
            {
                Candidates = Detector(silent.Port).Candidates,
                ProbeTimeout = TimeSpan.FromSeconds(30),
                SystemProxy = _ => null
            }.DetectAsync(cancel.Token).WaitAsync(TimeSpan.FromSeconds(5)));
            Check("探测：取消就停", error is OperationCanceledException, error?.GetType().Name ?? "null");
        }

        Check("常见端口：Clash 和 v2rayN 都在", new[] { 7890, 7897, 10808, 10809 }.All(p => LocalProxyDetector.CommonPorts.Any(c => c.Port == p)));
    }

    /// <summary>本机回环上的假代理，按 <see cref="Current"/> 决定怎么回话。</summary>
    private sealed class FakeLocalProxy : IDisposable
    {
        public enum Mode { Http, Socks5, Mixed, NotProxy, Silent }

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public FakeLocalProxy(Mode mode)
        {
            Current = mode;
            _listener.Start();
            _ = AcceptAsync();
        }

        public volatile Mode Current;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public ConcurrentQueue<string> Targets { get; } = new();

        /// <summary>刚刚还能绑、现在没人听的端口。</summary>
        public static int ClosedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true) _ = ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token));
            }
            catch (Exception)
            {
                // 停了
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var token = _stop.Token;
                try
                {
                    var mode = Current;
                    if (mode == Mode.Silent)
                    {
                        await Task.Delay(Timeout.Infinite, token);
                        return;
                    }

                    var one = new byte[1];
                    if (await stream.ReadAsync(one, token) == 0) return;
                    var socks = one[0] == 5;
                    if (socks ? mode is Mode.Http or Mode.NotProxy : mode == Mode.Socks5) return;

                    if (socks)
                    {
                        var methods = new byte[2];
                        await stream.ReadExactlyAsync(methods, token);
                        await stream.WriteAsync(new byte[] { 5, 0 }, token);
                        var head = new byte[5];
                        await stream.ReadExactlyAsync(head, token);
                        var rest = new byte[head[4] + 2];
                        await stream.ReadExactlyAsync(rest, token);
                        Targets.Enqueue($"socks {Encoding.ASCII.GetString(rest, 0, head[4])}:{rest[^2] << 8 | rest[^1]}");
                        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
                    }
                    else
                    {
                        var text = new StringBuilder().Append((char)one[0]);
                        while (!text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                        {
                            if (await stream.ReadAsync(one, token) == 0) return;
                            text.Append((char)one[0]);
                        }
                        Targets.Enqueue(text.ToString().Split("\r\n")[0]);
                        var reply = mode == Mode.NotProxy ? "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n" : "HTTP/1.1 200 Connection established\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), token);
                    }

                    // 等对方挂断
                    await stream.ReadAsync(one, token);
                }
                catch (Exception)
                {
                    // 对方断开或测试结束
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
