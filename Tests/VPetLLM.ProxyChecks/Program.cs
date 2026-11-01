using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using VPetLLM.Core.Data.Database;
using VPetLLM.Core.Services;

var failures = new List<string>();

void Check(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
    }
}

var pluginAssembly = typeof(global::VPetLLM.VPetLLM).Assembly;
var routingPolicyType = pluginAssembly.GetType("VPetLLM.Core.Services.ProxyRoutingPolicy");
var healthPolicyType = pluginAssembly.GetType("VPetLLM.Core.Services.ProxyHealthPolicy");

Check(routingPolicyType is not null,
    "ProxyRoutingPolicy must keep plugin-store routing independent from the main proxy.");
Check(healthPolicyType is not null,
    "ProxyHealthPolicy must aggregate multiple connectivity probes.");

if (routingPolicyType is not null)
{
    var resolve = routingPolicyType.GetMethod(
        "ResolvePluginStore",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    Check(resolve is not null, "ResolvePluginStore must be available.");

    if (resolve is not null)
    {
        var disabled = resolve.Invoke(null, [false, "https://ghfast.top"]);
        var enabled = resolve.Invoke(null, [true, "https://ghfast.top"]);
        var disabledMode = disabled?.GetType().GetProperty("Mode")?.GetValue(disabled)?.ToString();
        var enabledMode = enabled?.GetType().GetProperty("Mode")?.GetValue(enabled)?.ToString();

        Check(disabledMode == "Direct",
            "Plugin store must stay direct when its own switch is off, regardless of main proxy settings.");
        Check(enabledMode == "UrlRewrite",
            "Plugin store must use its own mirror when its own switch is on.");

        var localProxy = resolve.Invoke(null, [true, "127.0.0.1:7890"]);
        var localProxyMode = localProxy?.GetType().GetProperty("Mode")?.GetValue(localProxy)?.ToString();
        Check(localProxyMode == "HttpProxy",
            "Plugin store must continue supporting an explicit host:port proxy.");
    }
}

if (healthPolicyType is not null)
{
    var isAvailable = healthPolicyType.GetMethod(
        "IsAvailable",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    Check(isAvailable is not null, "IsAvailable must be available.");

    if (isAvailable is not null)
    {
        Check((bool)isAvailable.Invoke(null, [new[] { false, true, false }])!,
            "One successful endpoint must prove that the proxy is available.");
        Check(!(bool)isAvailable.Invoke(null, [new[] { false, false, false }])!,
            "All attempted endpoints failing must report the proxy unavailable.");
    }

    var endpoints = healthPolicyType.GetField(
        "ProbeEndpoints",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as Array;
    Check(endpoints is { Length: >= 2 },
        "Proxy health checks must not depend on a single external endpoint.");
}

var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var settingsXaml = XDocument.Load(Path.Combine(repositoryRoot, "UI", "Windows", "winSettingNew.xaml"));
XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
var pluginStoreToggle = settingsXaml.Descendants()
    .FirstOrDefault(element => (string?)element.Attribute(x + "Name") == "CheckBox_PluginStore_UseProxy");
Check(pluginStoreToggle is not null, "Plugin-store proxy toggle must exist.");
if (pluginStoreToggle is not null)
{
    Check(pluginStoreToggle.Attribute("IsEnabled") is null,
        "Plugin-store proxy toggle must not be disabled by the main proxy switch.");
    Check(!pluginStoreToggle.Ancestors().Attributes("IsEnabled")
            .Any(attribute => attribute.Value.Contains("CheckBox_Proxy_IsEnabled", StringComparison.Ordinal)),
        "Plugin-store proxy settings must not be nested in the main proxy enabled panel.");
}

const string CatalogUrl = "https://raw.githubusercontent.com/o/r/main/PluginList.json";

async Task Scenario(string name, Func<ProxyFixture, Task> body)
{
    using var fixture = new ProxyFixture();
    try
    {
        await body(fixture).WaitAsync(TimeSpan.FromSeconds(30));
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: threw {ex.GetType().Name}: {ex.Message}");
    }
}

await Scenario("first run", async f =>
{
    f.List("https://a.test", "https://b.test", "https://c.test");
    f.Serve("https://a.test", "https://b.test", "https://c.test", "https://mine.test");

    var catalog = await f.Fetch();
    await f.Pool.WaitForIdleAsync();

    Check(catalog["A"]["SHA256"]?.ToString() == "genuine", "The catalog must come from the probes.");
    var rows = f.Rows();
    Check(rows.Count == 3, "List proxies must be stored; the configured proxy and direct must not be.");
    Check(rows.All(r => r.IsAvailable && r.LatencyMs is not null && r.LastProbedAt is not null),
        "Probe results must be written to the database.");
    Check(f.Reopen().GetAll().Count == 3, "Stored proxies must survive reopening the database.");
});

await Scenario("removed from list", async f =>
{
    f.List("https://a.test", "https://b.test", "https://c.test");
    f.Serve("https://a.test", "https://b.test", "https://c.test");
    await f.Fetch();
    await f.Pool.WaitForIdleAsync();

    f.List("https://a.test", "https://b.test");
    await f.Fetch();
    await f.Pool.WaitForIdleAsync();

    Check(f.Rows().Select(r => r.Url).OrderBy(u => u).SequenceEqual(new[] { "https://a.test", "https://b.test" }),
        "A proxy removed from the list must be deleted from the database.");
    Check(f.Rows().All(r => r.LatencyMs is not null), "Kept proxies must keep their probe results.");
    Check(!(await f.Pool.GetDownloadRoutesAsync("https://mine.test")).Contains("https://c.test"),
        "A removed proxy must not be used any more.");
});

await Scenario("sync keeps probe results", f =>
{
    var store = f.Reopen();
    store.Sync(new[] { "https://a.test" });
    store.RecordProbe("https://a.test", 120, null);
    store.Sync(new[] { "https://a.test", "https://b.test" });

    var kept = f.Rows().Single(r => r.Url == "https://a.test");
    Check(kept.LatencyMs == 120 && kept.IsAvailable, "Syncing the list must not wipe what is known about a kept proxy.");
    return Task.CompletedTask;
});

await Scenario("list damaged or unreachable", async f =>
{
    f.List("https://a.test", "https://b.test");
    f.Serve("https://a.test", "https://b.test");
    await f.Fetch();
    await f.Pool.WaitForIdleAsync();

    foreach (var damaged in new[] { "<html>404</html>", "{}", "{\"proxies\":\"x\"}", "{\"proxies\":[\"http://a.test\"]}" })
    {
        f.Net.ServeText(ProxyFixture.ListUrl, damaged);
        f.Net.ServeText(ProxyFixture.BackupListUrl, damaged);
        await f.Fetch();
        await f.Pool.WaitForIdleAsync();
        Check(f.Rows().Count == 2, $"A damaged list must not clear the database: {damaged}");
    }

    f.Net.Drop(ProxyFixture.ListUrl);
    f.Net.Drop(ProxyFixture.BackupListUrl);
    await f.Fetch();
    await f.Pool.WaitForIdleAsync();
    Check(f.Rows().Count == 2, "An unreachable list must not clear the database.");
    Check(f.Net.Count(ProxyFixture.Via("https://a.test")) == 6, "Stored proxies must still be probed without the list.");
});

await Scenario("list emptied", async f =>
{
    f.List("https://a.test");
    f.Serve("https://a.test");
    await f.Fetch();
    await f.Pool.WaitForIdleAsync();

    f.List();
    f.Serve("https://mine.test");
    await f.Fetch();
    await f.Pool.WaitForIdleAsync();
    Check(f.Rows().Count == 0, "A valid empty list means every proxy was removed.");
});

await Scenario("list filtering and fallback", async f =>
{
    f.Net.ServeText(ProxyFixture.BackupListUrl, "{\"proxies\":[\"https://a.test/\",\"https://A.test\",\"http://b.test\","
        + "\"https://u:p@c.test\",\"https://d.test/?x=1\",\"ftp://e.test\",5,null,\" https://f.test \"]}");
    f.Serve("https://a.test", "https://f.test");
    await f.Fetch();
    await f.Pool.WaitForIdleAsync();

    Check(f.Rows().Select(r => r.Url).OrderBy(u => u).SequenceEqual(new[] { "https://a.test", "https://f.test" }),
        "Only plain https proxy addresses may enter the database, and the backup site must be tried.");
});

await Scenario("list site speed", async f =>
{
    var list = Encoding.UTF8.GetBytes("{\"proxies\":[\"https://a.test\"]}");
    f.Net.Serve(ProxyFixture.ListUrl, list, 100);
    f.Serve("https://a.test");
    await f.Fetch(null);
    await f.Pool.WaitForIdleAsync();
    Check(f.Net.Count(ProxyFixture.Via("https://a.test")) == 1, "The first round must wait for the list, it has nothing else to probe.");

    var gate = new TaskCompletionSource<byte[]>();
    f.Net.Gate(ProxyFixture.ListUrl, gate);
    var catalog = await f.Fetch(null).WaitAsync(TimeSpan.FromSeconds(3));
    Check(catalog.ContainsKey("A"), "Later rounds must not wait for a slow list site.");

    gate.SetResult(list);
    await f.Pool.WaitForIdleAsync();
});

await Scenario("landing page", async f =>
{
    f.List("https://fake.test", "https://a.test");
    f.Serve("https://a.test");
    f.Net.ServeText(ProxyFixture.Via("https://fake.test"), "<html>this proxy moved</html>");
    await f.Fetch();
    await f.Pool.WaitForIdleAsync();

    var row = f.Rows().Single(r => r.Url == "https://fake.test");
    Check(!row.IsAvailable && row.FailureReason is not null, "A landing page answered with 200 must not count as available.");
    Check(!(await f.Pool.GetDownloadRoutesAsync(null)).Contains("https://fake.test"), "A landing page must not be used.");
});

await Scenario("catalog shape", async f =>
{
    f.List("https://a.test", "https://b.test");
    f.Net.ServeText(ProxyFixture.Via("https://a.test"), "{\"error\":\"nope\"}");
    f.Net.ServeText(ProxyFixture.Via("https://b.test"), "{\"A\":{\"Name\":\"no file\"}}");
    var failed = false;
    try { await f.Fetch(); } catch (InvalidOperationException) { failed = true; }
    Check(failed, "JSON that is not a plugin catalog must not be accepted.");
});

await Scenario("catalog with BOM", async f =>
{
    f.List("https://a.test");
    f.Net.Serve(ProxyFixture.Via("https://a.test"), new byte[] { 0xEF, 0xBB, 0xBF }.Concat(ProxyFixture.Genuine).ToArray());
    var catalog = await f.Fetch();
    Check(catalog.ContainsKey("A"), "The live PluginList.json starts with a BOM and must parse.");
});

await Scenario("direct beats fast poison", async f =>
{
    f.List("https://evil.test", "https://evil2.test");
    f.Net.Serve(ProxyFixture.Via("https://evil.test"), ProxyFixture.Poisoned);
    f.Net.Serve(ProxyFixture.Via("https://evil2.test"), ProxyFixture.Poisoned);
    f.Net.Serve(CatalogUrl, ProxyFixture.Genuine, 150);

    var catalog = await f.Fetch();
    await f.Pool.WaitForIdleAsync();

    Check(catalog["A"]["SHA256"]?.ToString() == "genuine", "Direct content must win over agreeing proxies.");
    Check(f.Rows().All(r => !r.IsAvailable), "Proxies contradicting direct must be marked unavailable.");
    var routes = await f.Pool.GetDownloadRoutesAsync(null);
    Check(!routes.Contains("https://evil.test") && !routes.Contains("https://evil2.test"),
        "Proxies contradicting direct must not serve downloads.");
});

await Scenario("majority beats fast poison", async f =>
{
    f.List("https://evil.test", "https://a.test", "https://b.test", "https://c.test");
    f.Net.Serve(ProxyFixture.Via("https://evil.test"), ProxyFixture.Poisoned);
    f.Serve(100, "https://a.test", "https://b.test", "https://c.test");

    var catalog = await f.Fetch();
    await f.Pool.WaitForIdleAsync();

    Check(catalog["A"]["SHA256"]?.ToString() == "genuine", "The majority must win over a faster proxy.");
    Check(!f.Rows().Single(r => r.Url == "https://evil.test").IsAvailable, "The outvoted proxy must be marked unavailable.");
});

await Scenario("late dissenter", async f =>
{
    var gate = new TaskCompletionSource<byte[]>();
    f.List("https://a.test", "https://b.test", "https://c.test", "https://late.test");
    f.Serve("https://a.test", "https://b.test", "https://c.test");
    f.Net.Gate(ProxyFixture.Via("https://late.test"), gate);

    var catalog = await f.Fetch();   // 不提前返回的话这里会一直等到 gate 放行，被外层超时判失败
    Check(catalog.ContainsKey("A"), "Three agreeing proxies must be enough without waiting for the slow one.");

    gate.SetResult(ProxyFixture.Poisoned);
    await f.Pool.WaitForIdleAsync();
    Check(!f.Rows().Single(r => r.Url == "https://late.test").IsAvailable,
        "A proxy answering after the verdict must still be checked against it.");
});

await Scenario("two proxies are not a quorum", async f =>
{
    var gate = new TaskCompletionSource<byte[]>();
    f.List("https://a.test", "https://b.test", "https://late.test");
    f.Serve("https://a.test", "https://b.test");
    f.Net.Gate(ProxyFixture.Via("https://late.test"), gate);

    var fetch = f.Fetch();
    await Task.Delay(200);
    Check(!fetch.IsCompleted, "Two proxies may be one operator and must not decide on their own.");

    gate.SetResult(ProxyFixture.Genuine);
    Check((await fetch).ContainsKey("A"), "The round must finish once the last proxy answers.");
});

await Scenario("tie", async f =>
{
    f.List("https://a.test", "https://b.test");
    f.Net.Serve(ProxyFixture.Via("https://a.test"), ProxyFixture.Genuine);
    f.Net.Serve(ProxyFixture.Via("https://b.test"), ProxyFixture.Poisoned);

    var failed = false;
    try { await f.Fetch(); } catch (InvalidOperationException) { failed = true; }
    Check(failed, "A one-to-one conflict must fail closed.");
});

await Scenario("single responder", async f =>
{
    f.List("https://a.test", "https://dead.test");
    f.Serve("https://a.test");
    Check((await f.Fetch()).ContainsKey("A"), "A single responder is accepted, same trust as the old single proxy.");
});

await Scenario("nothing reachable", async f =>
{
    var failed = false;
    try { await f.Fetch(); } catch (InvalidOperationException) { failed = true; }
    Check(failed, "With no route the caller must fall back to offline mode.");
    var routes = await f.Pool.GetDownloadRoutesAsync("https://mine.test");
    Check(routes.SequenceEqual(new[] { "https://mine.test", GitHubProxyPool.DirectRoute }),
        "Downloads must still try the configured proxy and direct when nothing was probed successfully.");
});

await Scenario("hanging route", async f =>
{
    f.Net.Hang(CatalogUrl);
    var fetch = f.Fetch();
    f.Serve("https://mine.test");
    Check((await fetch).ContainsKey("A"), "A hanging route must time out instead of blocking the store forever.");
});

await Scenario("no second request", async f =>
{
    f.List("https://a.test", "https://b.test", "https://c.test");
    f.Serve("https://a.test", "https://b.test", "https://c.test");

    var first = f.Fetch();
    var second = f.Fetch();
    await Task.WhenAll(first, second);
    await f.Pool.WaitForIdleAsync();
    await f.Pool.GetDownloadRoutesAsync("https://mine.test");

    Check(f.Net.Count(ProxyFixture.Via("https://a.test")) == 1,
        "The catalog must be the bytes from the probe, and concurrent callers must share one round.");
});

await Scenario("freshness", async f =>
{
    f.List("https://a.test");
    f.Serve("https://a.test");
    await f.Pool.GetDownloadRoutesAsync(null);
    await f.Pool.WaitForIdleAsync();
    await f.Pool.GetDownloadRoutesAsync(null);
    Check(f.Net.Count(ProxyFixture.Via("https://a.test")) == 1, "Batched downloads must reuse a fresh probe.");

    f.Now += TimeSpan.FromMinutes(11);
    await f.Pool.GetDownloadRoutesAsync(null);
    await f.Pool.WaitForIdleAsync();
    Check(f.Net.Count(ProxyFixture.Via("https://a.test")) == 2, "Stale results must trigger a new probe.");
});

await Scenario("ranking", async f =>
{
    f.List("https://slow.test", "https://fast.test", "https://mid.test");
    f.Net.Serve(ProxyFixture.Via("https://slow.test"), ProxyFixture.Genuine, 900);
    f.Net.Serve(ProxyFixture.Via("https://fast.test"), ProxyFixture.Genuine, 50);
    f.Net.Serve(ProxyFixture.Via("https://mid.test"), ProxyFixture.Genuine, 450);
    f.Net.Serve(CatalogUrl, ProxyFixture.Genuine, 1300);
    await f.Fetch(null);
    await f.Pool.WaitForIdleAsync();

    var routes = await f.Pool.GetDownloadRoutesAsync(null);
    Check(routes.SequenceEqual(new[] { "https://fast.test", "https://mid.test", "https://slow.test", GitHubProxyPool.DirectRoute }),
        $"Downloads must use the fastest route first, got: {string.Join(", ", routes)}");
});

await Scenario("ranking covers unlisted routes", async f =>
{
    f.List("https://a.test");
    f.Net.Serve(ProxyFixture.Via("https://a.test"), ProxyFixture.Genuine, 800);
    f.Net.Serve(ProxyFixture.Via("https://mine.test"), ProxyFixture.Genuine, 20);
    f.Net.Serve(CatalogUrl, ProxyFixture.Genuine, 400);
    await f.Fetch("https://mine.test");
    await f.Pool.WaitForIdleAsync();

    var routes = await f.Pool.GetDownloadRoutesAsync("https://mine.test");
    Check(routes.SequenceEqual(new[] { "https://mine.test", GitHubProxyPool.DirectRoute, "https://a.test" }),
        $"Direct and the configured proxy must be ranked by speed like any other route, got: {string.Join(", ", routes)}");
});

await Scenario("configured proxy", async f =>
{
    f.List("https://a.test");
    f.Serve("https://a.test", "https://mine.test");
    await f.Fetch("https://mine.test/");
    await f.Pool.WaitForIdleAsync();

    Check((await f.Pool.GetDownloadRoutesAsync("https://mine.test")).Contains("https://mine.test"),
        "The configured proxy must take part in the ranking.");
    Check(!(await f.Pool.GetDownloadRoutesAsync("https://other.test")).Contains("https://mine.test"),
        "A proxy the user no longer configures must stop being used.");
});

await Scenario("configured proxy also listed", async f =>
{
    f.List("https://a.test");
    f.Serve("https://a.test");
    await f.Fetch("https://a.test");
    await f.Pool.WaitForIdleAsync();
    Check(f.Net.Count(ProxyFixture.Via("https://a.test")) == 1, "A route must be probed once per round.");
});

await Scenario("many routes", async f =>
{
    var proxies = Enumerable.Range(0, 8).Select(i => $"https://p{i}.test").ToArray();
    f.List(proxies);
    f.Serve(proxies);
    await f.Fetch(null);
    await f.Pool.WaitForIdleAsync();
    Check((await f.Pool.GetDownloadRoutesAsync(null)).Count <= 5, "Downloads must not walk through every proxy.");
});

await Scenario("database unusable", async f =>
{
    var blocker = Path.Combine(f.Directory, "blocker");
    File.WriteAllText(blocker, "not a directory");
    var pool = new GitHubProxyPool(Path.Combine(blocker, "settings.db"), CatalogUrl,
        new[] { ProxyFixture.ListUrl }, f.Net.Fetch, () => f.Now);
    f.List("https://a.test");
    f.Serve("https://mine.test");

    Check((await pool.FetchCatalogAsync("https://mine.test")).ContainsKey("A"), "A broken database must not break the store.");
    Check((await pool.GetDownloadRoutesAsync("https://mine.test"))[0] == "https://mine.test",
        "Session results must still rank routes without a database.");
});

if (routingPolicyType is not null)
{
    var resolve = routingPolicyType.GetMethod(
        "ResolvePluginStore",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    foreach (var mirror in new[] { "https://gh-proxy.com", "https://ghproxy.vip" })
    {
        var decision = resolve?.Invoke(null, [true, mirror]);
        Check(decision?.GetType().GetProperty("Mode")?.GetValue(decision)?.ToString() == "UrlRewrite",
            $"{mirror} from the proxy list must be treated as a mirror when typed by hand.");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("Proxy regression checks failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }
    return 1;
}

Console.WriteLine("Proxy regression checks passed.");
return 0;

sealed class FakeNetwork
{
    private readonly Dictionary<string, Func<CancellationToken, Task<byte[]>>> _responses = new();
    private readonly List<string> _requests = new();

    public void Serve(string url, byte[] body, int delayMs = 0)
    {
        lock (_responses)
        {
            _responses[url] = async token =>
            {
                if (delayMs > 0) await Task.Delay(delayMs, token);
                return body;
            };
        }
    }

    public void ServeText(string url, string body) => Serve(url, Encoding.UTF8.GetBytes(body));

    public void Gate(string url, TaskCompletionSource<byte[]> gate)
    {
        lock (_responses) _responses[url] = token => gate.Task.WaitAsync(token);
    }

    public void Hang(string url)
    {
        lock (_responses)
        {
            _responses[url] = async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Array.Empty<byte>();
            };
        }
    }

    public void Drop(string url)
    {
        lock (_responses) _responses.Remove(url);
    }

    public int Count(string url)
    {
        lock (_requests) return _requests.Count(r => r == url);
    }

    public Task<byte[]> Fetch(string url, CancellationToken cancellationToken)
    {
        lock (_requests) _requests.Add(url);
        lock (_responses)
        {
            return _responses.TryGetValue(url, out var respond)
                ? respond(cancellationToken)
                : Task.FromException<byte[]>(new HttpRequestException("unreachable"));
        }
    }
}

sealed class ProxyFixture : IDisposable
{
    public const string ListUrl = "https://list-a.test/GitHub_Proxy.json";
    public const string BackupListUrl = "https://list-b.test/GitHub_Proxy.json";
    private const string CatalogPath = "raw.githubusercontent.com/o/r/main/PluginList.json";

    public static readonly byte[] Genuine = Encoding.UTF8.GetBytes(
        "{\"A\":{\"Name\":\"a\",\"File\":\"https://github.com/o/r/raw/a.dll\",\"SHA256\":\"genuine\"}}");
    public static readonly byte[] Poisoned = Encoding.UTF8.GetBytes(
        "{\"A\":{\"Name\":\"a\",\"File\":\"https://github.com/o/r/raw/a.dll\",\"SHA256\":\"poisoned\"}}");

    public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"proxychecks-{Guid.NewGuid():N}");
    public string DbPath => Path.Combine(Directory, "settings.db");
    public FakeNetwork Net { get; } = new();
    public DateTime Now { get; set; } = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    public GitHubProxyPool Pool { get; }

    public ProxyFixture()
    {
        System.IO.Directory.CreateDirectory(Directory);
        Pool = new GitHubProxyPool(DbPath, $"https://{CatalogPath}", new[] { ListUrl, BackupListUrl }, Net.Fetch, () => Now);
    }

    public static string Via(string proxy) => $"{proxy}/{CatalogPath}";

    public void List(params string[] proxies)
        => Net.ServeText(ListUrl, new JObject { ["proxies"] = new JArray(proxies.Cast<object>().ToArray()) }.ToString());

    public void Serve(params string[] proxies) => Serve(0, proxies);

    public void Serve(int delayMs, params string[] proxies)
    {
        foreach (var proxy in proxies) Net.Serve(Via(proxy), Genuine, delayMs);
    }

    public Task<Dictionary<string, JObject>> Fetch(string? configured = "https://mine.test")
        => Pool.FetchCatalogAsync(configured);

    public GitHubProxyStore Reopen() => new(DbPath);

    public IReadOnlyList<GitHubProxyRecord> Rows() => Reopen().GetAll();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }
}
