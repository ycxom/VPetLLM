using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using VPetLLM.Core.Data.Database;

namespace VPetLLM.Core.Services
{
    /// <summary>一条线路的一次探测结果。<see cref="Failure"/> 为 null 表示拿到了能用的清单。</summary>
    public sealed record GitHubProxyProbe(string Route, long LatencyMs, string? ContentHash, string? Failure)
    {
        public bool Succeeded => Failure is null;
    }

    /// <summary>
    /// 决定采信哪一份插件清单。
    ///
    /// 插件 DLL 和附属文件的 SHA256 都写在清单里，清单本身又经代理下载：代理能改清单就能
    /// 同时改文件和哈希，后面的哈希校验全部失效，等于远程代码执行。所以 DLL 走哪条线路都
    /// 无所谓（有哈希兜着），唯独清单不能随便信一个代理，要互相对内容。
    /// </summary>
    public static class GitHubProxyVerdict
    {
        // 不是 2：同一运营方常有多个域名（hk.gh-proxy.com 就是 302 到 gh-proxy.org），两票可能是一家。
        public const int Quorum = 3;

        /// <returns>采信的内容哈希；null 表示还要再等，<paramref name="finished"/> 时表示定不下来。</returns>
        public static string? Decide(IReadOnlyCollection<GitHubProxyProbe> probes, bool finished)
        {
            var succeeded = probes.Where(p => p.Succeeded).ToList();

            // 直连拿到的就是 GitHub 自己的内容，不用投票
            var direct = succeeded.FirstOrDefault(p => p.Route == GitHubProxyPool.DirectRoute);
            if (direct is not null) return direct.ContentHash;

            var groups = succeeded.GroupBy(p => p.ContentHash).OrderByDescending(g => g.Count()).ToList();
            if (groups.Count == 0) return null;

            // 打平不选：一比一时谁也证明不了谁是真的，宁可这次打不开商店。
            // 只有一家响应时会被采信，这和以前只用一个代理的信任程度相同。
            var leadsAlone = groups.Count == 1 || groups[0].Count() > groups[1].Count();
            return leadsAlone && (finished || groups[0].Count() >= Quorum) ? groups[0].Key : null;
        }
    }

    /// <summary>
    /// GitHub 代理池：同步云端列表、测速入库、选出下载线路。
    ///
    /// 测速直接请求真实的 PluginList.json 并按内容判定：实测 ghproxy.link 这类已废弃的
    /// 代理对任何路径都回 200 加一个落地页，看状态码会判成可用。清单用的也是探测时拿到的
    /// 那份字节，不再请求第二次，否则代理可以探测时给真货、之后给假货。
    ///
    /// 只在打开商店和下载时才跑，启动时不联网。线路用字符串表示：代理前缀，直连是
    /// <see cref="DirectRoute"/>。
    /// </summary>
    public sealed class GitHubProxyPool
    {
        public const string DirectRoute = "";

        private static readonly string[] DefaultListUrls =
        {
            "https://vpetllm.ycxom.top/api/GitHub_Proxy.json",
            "https://vpetllm.ycxom.com/api/GitHub_Proxy.json"
        };

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(8);

        // 批量更新时一轮探测管整批，不用每个插件都测一次
        private static readonly TimeSpan ResultsFreshFor = TimeSpan.FromMinutes(10);

        private const int MaxCatalogBytes = 4 * 1024 * 1024;
        private const int MaxDownloadRoutes = 4;

        private sealed record RoundOutcome(byte[]? Catalog, string? Failure);

        private sealed class ProbeRound
        {
            public TaskCompletionSource<RoundOutcome> Outcome { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task Completed { get; set; } = Task.CompletedTask;
        }

        private sealed record ProbedRoute(GitHubProxyProbe Probe, byte[]? Content);

        private readonly object _gate = new();
        private readonly GitHubProxyStore _store;
        private readonly string _catalogUrl;
        private readonly IReadOnlyList<string> _listUrls;
        private readonly Func<string, CancellationToken, Task<byte[]>> _fetch;
        private readonly Func<DateTime> _clock;

        // 直连和用户配置的代理不进库，它们的结果留在内存，见 GitHubProxyStore
        private readonly Dictionary<string, GitHubProxyProbe> _sessionProbes = new(StringComparer.OrdinalIgnoreCase);

        private ProbeRound? _round;
        private DateTime _lastRoundAt = DateTime.MinValue;

        public GitHubProxyPool(string catalogUrl)
            : this(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VPetLLM", "settings.db"),
                catalogUrl, DefaultListUrls, FetchAsync, () => DateTime.UtcNow)
        {
        }

        public GitHubProxyPool(
            string dbPath,
            string catalogUrl,
            IReadOnlyList<string> listUrls,
            Func<string, CancellationToken, Task<byte[]>> fetch,
            Func<DateTime> clock)
        {
            _store = new GitHubProxyStore(dbPath);
            _catalogUrl = catalogUrl;
            _listUrls = listUrls;
            _fetch = fetch;
            _clock = clock;
        }

        /// <summary>拉取商店清单。拿不到或内容互相矛盾时抛异常，调用方退回离线显示。</summary>
        public async Task<Dictionary<string, JObject>> FetchCatalogAsync(string? configuredProxy)
        {
            var outcome = await JoinOrStartRound(configuredProxy).Outcome.Task.ConfigureAwait(false);
            if (outcome.Catalog is null)
            {
                throw new InvalidOperationException(outcome.Failure);
            }
            return ParseCatalog(outcome.Catalog)!;
        }

        /// <summary>
        /// 下载用的线路，快的在前，结果过期就先测一轮。文件有哈希校验，线路不用互相核对；
        /// 全都测不通时仍给出配置的代理和直连，让下载自己去试。
        /// </summary>
        public async Task<IReadOnlyList<string>> GetDownloadRoutesAsync(string? configuredProxy)
        {
            ProbeRound? round;
            lock (_gate)
            {
                round = _round;
                if (round is null && _clock() - _lastRoundAt >= ResultsFreshFor)
                {
                    round = JoinOrStartRound(configuredProxy);
                }
            }

            if (round is not null)
            {
                await round.Outcome.Task.ConfigureAwait(false);
            }
            return RankRoutes(configuredProxy);
        }

        /// <summary>等后台探测跑完，只给检查用。</summary>
        public async Task WaitForIdleAsync()
        {
            ProbeRound? round;
            lock (_gate) round = _round;
            if (round is not null) await round.Completed.ConfigureAwait(false);
        }

        private ProbeRound JoinOrStartRound(string? configuredProxy)
        {
            lock (_gate)
            {
                if (_round is not null) return _round;

                var round = new ProbeRound();
                _round = round;
                _lastRoundAt = _clock();
                round.Completed = Task.Run(() => RunRoundAsync(round, configuredProxy));
                return round;
            }
        }

        private async Task RunRoundAsync(ProbeRound round, string? configuredProxy)
        {
            try
            {
                // 列表站点比 GitHub 代理还慢时不该拖住商店：库里有地址就边测边同步，新地址下一轮再测。
                // 只有库是空的（第一次用）才必须等，不然这轮没东西可测。
                var syncTask = SyncListAsync();
                if (ReadStore().Count == 0)
                {
                    await syncTask.ConfigureAwait(false);
                }

                var pending = BuildCandidates(configuredProxy).Select(ProbeAsync).ToList();
                var done = new List<ProbedRoute>();
                string? acceptedHash = null;

                while (pending.Count > 0)
                {
                    var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(finished);

                    var probed = await finished.ConfigureAwait(false);
                    done.Add(probed);
                    Record(probed.Probe);

                    // 够数就先把清单交出去，剩下的探测继续跑完落库，慢线路不拖累界面
                    if (acceptedHash is null)
                    {
                        acceptedHash = GitHubProxyVerdict.Decide(done.Select(d => d.Probe).ToList(), pending.Count == 0);
                        if (acceptedHash is not null)
                        {
                            var catalog = done.First(d => d.Probe.ContentHash == acceptedHash).Content;
                            round.Outcome.TrySetResult(new RoundOutcome(catalog, null));
                        }
                    }
                }

                // 内容对不上的多半只是缓存旧，但也可能是在投毒，这一轮都不用它
                foreach (var dissenter in done.Where(d => d.Probe.Succeeded && d.Probe.ContentHash != acceptedHash))
                {
                    Record(dissenter.Probe with { Failure = "清单内容与其他线路不一致" });
                }

                LogRound(done, acceptedHash);
                round.Outcome.TrySetResult(new RoundOutcome(null, DescribeFailure(done)));
                await syncTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Log($"GitHub 代理探测失败: {ex.Message}");
                round.Outcome.TrySetResult(new RoundOutcome(null, ex.Message));
            }
            finally
            {
                lock (_gate) _round = null;
            }
        }

        private List<string> BuildCandidates(string? configuredProxy)
        {
            var routes = new List<string> { DirectRoute };
            routes.AddRange(ReadStore().Select(r => r.Url));

            var configured = NormalizeConfigured(configuredProxy);
            if (configured is not null) routes.Add(configured);

            return routes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private async Task<ProbedRoute> ProbeAsync(string route)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                using var timeout = new CancellationTokenSource(ProbeTimeout);
                var content = await _fetch(BuildUrl(route, _catalogUrl), timeout.Token).ConfigureAwait(false);
                watch.Stop();

                if (ParseCatalog(content) is null)
                {
                    return new ProbedRoute(new GitHubProxyProbe(route, watch.ElapsedMilliseconds, null, "返回的不是插件清单"), null);
                }

                var hash = Convert.ToHexString(SHA256.HashData(content));
                return new ProbedRoute(new GitHubProxyProbe(route, watch.ElapsedMilliseconds, hash, null), content);
            }
            catch (Exception ex)
            {
                return new ProbedRoute(new GitHubProxyProbe(route, watch.ElapsedMilliseconds, null, ex.Message), null);
            }
        }

        private void Record(GitHubProxyProbe probe)
        {
            lock (_gate) _sessionProbes[probe.Route] = probe;

            try
            {
                _store.RecordProbe(probe.Route, probe.Succeeded ? probe.LatencyMs : null, probe.Failure);
            }
            catch (Exception ex)
            {
                Logger.Log($"保存 GitHub 代理测速结果失败: {ex.Message}");
            }
        }

        private IReadOnlyList<string> RankRoutes(string? configuredProxy)
        {
            var configured = NormalizeConfigured(configuredProxy);
            var latency = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            foreach (var record in ReadStore())
            {
                if (record.IsAvailable && record.LatencyMs is long ms) latency[record.Url] = ms;
            }

            lock (_gate)
            {
                foreach (var probe in _sessionProbes.Values)
                {
                    var unlisted = probe.Route == DirectRoute
                        || string.Equals(probe.Route, configured, StringComparison.OrdinalIgnoreCase);
                    if (!unlisted) continue;

                    if (probe.Succeeded) latency[probe.Route] = probe.LatencyMs;
                    else latency.Remove(probe.Route);
                }
            }

            var routes = latency.OrderBy(pair => pair.Value).Select(pair => pair.Key).Take(MaxDownloadRoutes).ToList();
            if (configured is not null && !routes.Contains(configured, StringComparer.OrdinalIgnoreCase))
            {
                routes.Add(configured);
            }
            if (!routes.Contains(DirectRoute))
            {
                routes.Add(DirectRoute);
            }
            return routes;
        }

        /// <summary>只有解析成功才动库：列表暂时拉不到时，绝不能把已有的线路清掉。</summary>
        private async Task SyncListAsync()
        {
            foreach (var listUrl in _listUrls)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(ListTimeout);
                    var urls = ParseProxyList(await _fetch(listUrl, timeout.Token).ConfigureAwait(false));
                    if (urls is null)
                    {
                        Logger.Log($"GitHub 代理列表内容无效: {listUrl}");
                        continue;
                    }

                    _store.Sync(urls);
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Log($"同步 GitHub 代理列表失败 ({listUrl}): {ex.Message}");
                }
            }
        }

        private IReadOnlyList<GitHubProxyRecord> ReadStore()
        {
            try
            {
                return _store.GetAll();
            }
            catch (Exception ex)
            {
                Logger.Log($"读取 GitHub 代理数据失败: {ex.Message}");
                return Array.Empty<GitHubProxyRecord>();
            }
        }

        private static void LogRound(List<ProbedRoute> done, string? acceptedHash)
        {
            var fastest = done.Where(d => d.Probe.ContentHash == acceptedHash && acceptedHash is not null)
                .OrderBy(d => d.Probe.LatencyMs)
                .Select(d => d.Probe)
                .FirstOrDefault();

            Logger.Log(fastest is null
                ? $"GitHub 代理探测：{done.Count} 条线路都没有拿到可信的清单"
                : $"GitHub 代理探测：{done.Count(d => d.Probe.ContentHash == acceptedHash)}/{done.Count} 条可用，"
                    + $"最快 {(fastest.Route == DirectRoute ? "直连" : fastest.Route)} {fastest.LatencyMs}ms");
        }

        private static string DescribeFailure(List<ProbedRoute> done)
            => done.Any(d => d.Probe.Succeeded)
                ? "各线路返回的插件清单内容不一致，已停止使用"
                : $"所有线路都无法获取插件清单（共 {done.Count} 条）";

        private static string BuildUrl(string route, string originalUrl)
            => route == DirectRoute
                ? originalUrl
                : ProxyRoutingPolicy.BuildPluginStoreUrl(
                    new PluginStoreProxyDecision(PluginStoreProxyMode.UrlRewrite, route), originalUrl);

        private static string? NormalizeConfigured(string? configuredProxy)
            => string.IsNullOrWhiteSpace(configuredProxy) ? null : configuredProxy.Trim().TrimEnd('/');

        // 线上的 PluginList.json 带 UTF-8 BOM，直接按字符串解析会失败
        private static string DecodeUtf8(byte[] content)
        {
            using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        /// <returns>清单内容；不是一份至少含一个下载地址的清单时返回 null。</returns>
        internal static Dictionary<string, JObject>? ParseCatalog(byte[] content)
        {
            try
            {
                var catalog = JsonConvert.DeserializeObject<Dictionary<string, JObject>>(DecodeUtf8(content));
                return catalog?.Values.Any(entry => !string.IsNullOrEmpty(entry?["File"]?.ToString())) == true ? catalog : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <returns>规范化后的地址；内容无效时返回 null（和合法的空列表区分开，空列表表示全部删除）。</returns>
        internal static IReadOnlyList<string>? ParseProxyList(byte[] content)
        {
            try
            {
                if (JObject.Parse(DecodeUtf8(content))["proxies"] is not JArray entries) return null;

                var urls = entries
                    .Select(entry => NormalizeListedProxy(entry.Type == JTokenType.String ? entry.ToString() : null))
                    .OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return entries.Count > 0 && urls.Count == 0 ? null : urls;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // 清单可以经它下载，所以只收 https，也不收带账号或查询串的地址（前缀拼接后会变成别的东西）
        private static string? NormalizeListedProxy(string? value)
        {
            var url = value?.Trim().TrimEnd('/');
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && uri.UserInfo.Length == 0
                && uri.Query.Length == 0
                && uri.Fragment.Length == 0
                    ? url
                    : null;
        }

        private static async Task<byte[]> FetchAsync(string url, CancellationToken cancellationToken)
        {
            using var handler = new HttpClientHandler { UseProxy = false, Proxy = null };
            using var client = new HttpClient(handler) { MaxResponseContentBufferSize = MaxCatalogBytes };
            return await client.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
        }
    }
}
