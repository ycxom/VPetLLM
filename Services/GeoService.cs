using System.Globalization;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VPetLLM.Services
{
    /// <summary>
    /// 地区检测服务：判断当前网络环境是否位于中国大陆（CN）。
    ///
    /// 结果会**持久化到磁盘**并按网络环境指纹校验，正常情况下每台机器每种网络只查一次，
    /// 有效期内的后续启动完全不联网——这些都是第三方免费接口，每次启动都查会很快撞上限流
    /// （典型如 ipapi.co 的 429 Too Many Requests）。
    ///
    /// 查询时按顺序尝试多个互为备份的接口，任一成功即停；失败的接口按失败类型进入冷却，
    /// 冷却状态同样持久化，被限流的接口不会在下次启动时又被第一个撞上去。
    ///
    /// 所有查询均使用直连（不经过代理），因为此处的目的正是判断真实出口网络位置。
    /// </summary>
    public static class GeoService
    {
        /// <summary>接口查得的结果有效期。地理位置不常变，网络指纹变化时会自动提前失效。</summary>
        private static readonly TimeSpan ApiResultTtl = TimeSpan.FromDays(7);

        /// <summary>本地启发式兜底结果的有效期。它只是猜测，不能像真实查询那样长期沿用。</summary>
        private static readonly TimeSpan HeuristicResultTtl = TimeSpan.FromHours(12);

        /// <summary>被限流（429）的接口冷却时长——多数免费接口按天计配额。</summary>
        private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromHours(24);

        /// <summary>普通失败（超时、DNS、5xx）的冷却时长。</summary>
        private static readonly TimeSpan FailureCooldown = TimeSpan.FromHours(1);

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

        private static readonly object _lock = new object();
        private static readonly SemaphoreSlim _queryGate = new(1, 1);
        private static bool? _cachedResult;
        private static GeoCache? _diskCache;

        private static string CacheFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "VPetLLM", "geo_cache.json");

        /// <summary>
        /// 判断当前网络环境是否很可能位于中国大陆。
        /// 依次尝试：进程内缓存 → 磁盘缓存（未过期且网络指纹一致）→ 联网查询 → 本地启发式。
        /// </summary>
        public static async Task<bool> IsLikelyChinaAsync() => await ResolveAsync(force: false);

        /// <summary>忽略所有缓存重新检测（供设置界面的「重新检测」入口使用）。</summary>
        public static async Task<bool> ForceRefreshAsync() => await ResolveAsync(force: true);

        private static async Task<bool> ResolveAsync(bool force)
        {
            if (!force)
            {
                lock (_lock)
                {
                    if (_cachedResult.HasValue)
                        return _cachedResult.Value;
                }
            }

            // 串行化：启动阶段可能有多处同时问，避免并发打同一批接口。
            await _queryGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!force)
                {
                    lock (_lock)
                    {
                        if (_cachedResult.HasValue)
                            return _cachedResult.Value;
                    }
                }

                var cache = LoadCache();
                var networkKey = GetNetworkFingerprint();

                if (!force && TryUseStoredResult(cache, networkKey, out var stored))
                    return Publish(stored);

                var apiResult = await QueryCountryCodeAsync(cache).ConfigureAwait(false);
                bool result;
                if (apiResult != null)
                {
                    result = string.Equals(apiResult, "CN", StringComparison.OrdinalIgnoreCase);
                    Logger.Log($"GeoService: 通过外部接口检测到国家码 {apiResult}，判定为{(result ? "中国大陆" : "非中国大陆")}（结果已缓存 {ApiResultTtl.TotalDays:0} 天）");
                    cache.CountryCode = apiResult.ToUpperInvariant();
                    cache.FromHeuristic = false;
                }
                else
                {
                    result = IsLikelyChinaByLocalHeuristic();
                    Logger.Log($"GeoService: 外部接口不可用，回退本地判断结果={result}（{HeuristicResultTtl.TotalHours:0} 小时后重试）");
                    cache.CountryCode = result ? "CN" : "";
                    cache.FromHeuristic = true;
                }

                cache.CheckedAtUtc = DateTime.UtcNow;
                cache.NetworkKey = networkKey;
                SaveCache(cache);
                return Publish(result);
            }
            finally
            {
                _queryGate.Release();
            }
        }

        private static bool Publish(bool result)
        {
            lock (_lock) { _cachedResult = result; }
            return result;
        }

        private static bool TryUseStoredResult(GeoCache cache, string networkKey, out bool result)
        {
            result = false;
            if (cache.CheckedAtUtc == default) return false;

            // 换了网络（切了 WiFi、开关了 VPN、插拔网线）就必须重新判断，否则缓存会给出错误答案。
            if (!string.Equals(cache.NetworkKey, networkKey, StringComparison.Ordinal))
            {
                Logger.Log("GeoService: 网络环境已变化，重新检测归属地。");
                return false;
            }

            var ttl = cache.FromHeuristic ? HeuristicResultTtl : ApiResultTtl;
            var age = DateTime.UtcNow - cache.CheckedAtUtc;
            // 系统时间被往回调时 age 为负，同样视为失效。
            if (age < TimeSpan.Zero || age > ttl) return false;

            result = string.Equals(cache.CountryCode, "CN", StringComparison.OrdinalIgnoreCase);
            var source = cache.FromHeuristic ? "本地判断" : $"接口结果 {cache.CountryCode}";
            Logger.Log($"GeoService: 使用已缓存的归属地（{source}，{age.TotalHours:0.#} 小时前检测），本次不联网。");
            return true;
        }

        /// <summary>
        /// 依次尝试多个互为备份的轻量 IP 地理接口，返回两位国家码（如 "CN"），全部失败返回 null。
        /// 处于冷却期的接口直接跳过。
        /// </summary>
        private static async Task<string?> QueryCountryCodeAsync(GeoCache cache)
        {
            var now = DateTime.UtcNow;
            var skipped = 0;

            foreach (var endpoint in Endpoints)
            {
                if (cache.Cooldowns.TryGetValue(endpoint.Url, out var until) && until > now)
                {
                    skipped++;
                    continue;
                }

                var (code, outcome) = await TryEndpointAsync(endpoint).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(code))
                {
                    cache.Cooldowns.Remove(endpoint.Url);
                    return code;
                }

                cache.Cooldowns[endpoint.Url] = now + (outcome == FailureKind.RateLimited ? RateLimitCooldown : FailureCooldown);
            }

            if (skipped > 0)
                Logger.Log($"GeoService: {skipped} 个接口处于冷却期被跳过。");
            return null;
        }

        private static async Task<(string? code, FailureKind outcome)> TryEndpointAsync(GeoEndpoint endpoint)
        {
            try
            {
                using var handler = new HttpClientHandler { UseProxy = false, Proxy = null };
                using var client = new HttpClient(handler) { Timeout = RequestTimeout };
                // 部分接口（如 ifconfig.co）对缺失 UA 的请求会返回 HTML 或直接拒绝。
                client.DefaultRequestHeaders.UserAgent.ParseAdd("VPetLLM/1.0");

                using var response = await client.GetAsync(endpoint.Url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var rateLimited = (int)response.StatusCode == 429;
                    var cooldown = rateLimited ? RateLimitCooldown : FailureCooldown;
                    Logger.Log($"GeoService: {endpoint.Url} 返回 {(int)response.StatusCode}，{cooldown.TotalHours:0.#} 小时内不再尝试。");
                    return (null, rateLimited ? FailureKind.RateLimited : FailureKind.Failed);
                }

                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var code = endpoint.Parse(body);
                if (IsValidCountryCode(code))
                    return (code!.ToUpperInvariant(), FailureKind.None);

                Logger.Log($"GeoService: {endpoint.Url} 返回了无法解析的内容。");
                return (null, FailureKind.Failed);
            }
            catch (Exception ex)
            {
                Logger.Log($"GeoService: {endpoint.Url} 查询失败: {ex.Message}");
                return (null, FailureKind.Failed);
            }
        }

        private static bool IsValidCountryCode(string? code)
            => code is { Length: 2 } && char.IsLetter(code[0]) && char.IsLetter(code[1]);

        private enum FailureKind { None, Failed, RateLimited }

        private sealed record GeoEndpoint(string Url, Func<string, string?> Parse);

        /// <summary>
        /// 备用节点按「稳定性 / 限流宽松度」排序：Cloudflare 的 trace 端点无配额限制且全球可达，
        /// 放在最前面能让绝大多数机器一次命中，后面的免费 API 只有在它不可用时才会被打到。
        /// </summary>
        private static readonly GeoEndpoint[] Endpoints =
        {
            new("https://www.cloudflare.com/cdn-cgi/trace", ParseCloudflareTrace),
            new("http://ip-api.com/json/?fields=status,countryCode", body => ParseJsonField(body, "countryCode", requireSuccessStatus: true)),
            new("https://api.country.is/", body => ParseJsonField(body, "country")),
            new("https://ipwho.is/?fields=country_code", body => ParseJsonField(body, "country_code")),
            new("https://ifconfig.co/country-iso", body => body?.Trim()),
            new("https://ipapi.co/country/", body => body?.Trim()),
        };

        private static string? ParseCloudflareTrace(string body)
        {
            // 响应是 key=value 的多行文本，其中 loc=XX 即客户端所在国家。
            foreach (var line in body.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("loc=", StringComparison.Ordinal))
                    return trimmed[4..].Trim();
            }
            return null;
        }

        private static string? ParseJsonField(string body, string field, bool requireSuccessStatus = false)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (requireSuccessStatus &&
                    (!root.TryGetProperty("status", out var status) || status.GetString() != "success"))
                    return null;
                return root.TryGetProperty(field, out var value) ? value.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // ---------------- 持久化 ----------------

        private sealed class GeoCache
        {
            public string CountryCode { get; set; } = "";
            public bool FromHeuristic { get; set; }
            public DateTime CheckedAtUtc { get; set; }
            public string NetworkKey { get; set; } = "";
            public Dictionary<string, DateTime> Cooldowns { get; set; } = new();
        }

        private static GeoCache LoadCache()
        {
            if (_diskCache is not null) return _diskCache;
            try
            {
                var path = CacheFilePath;
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<GeoCache>(File.ReadAllText(path));
                    if (loaded is not null)
                    {
                        loaded.Cooldowns ??= new Dictionary<string, DateTime>();
                        return _diskCache = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"GeoService: 读取归属地缓存失败，将重新检测: {ex.Message}");
            }
            return _diskCache = new GeoCache();
        }

        private static void SaveCache(GeoCache cache)
        {
            _diskCache = cache;
            try
            {
                // 清掉已过期的冷却记录，避免文件无限增长。
                var now = DateTime.UtcNow;
                foreach (var key in cache.Cooldowns.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                    cache.Cooldowns.Remove(key);

                var path = CacheFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                // 写不进去只是失去持久化能力，本次进程内的结果仍然有效。
                Logger.Log($"GeoService: 保存归属地缓存失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 当前网络环境指纹：取所有已启用网卡的网关地址与本机 IP 的网段。
        /// 换网、开关 VPN 都会改变它，从而让缓存自动失效；同一个网络下重启则保持不变。
        /// 不含完整 IP，避免把可标识信息写进缓存文件。
        /// </summary>
        private static string GetNetworkFingerprint()
        {
            try
            {
                var parts = new List<string>();
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    var props = nic.GetIPProperties();
                    foreach (var gw in props.GatewayAddresses)
                    {
                        if (gw?.Address is not null && gw.Address.ToString() != "0.0.0.0")
                            parts.Add($"gw:{gw.Address}");
                    }
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != global::System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        var octets = addr.Address.ToString().Split('.');
                        if (octets.Length == 4) parts.Add($"net:{octets[0]}.{octets[1]}.{octets[2]}");
                    }
                }

                if (parts.Count == 0) return "no-network";
                parts.Sort(StringComparer.Ordinal);
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
                return Convert.ToHexString(hash, 0, 8);
            }
            catch (Exception ex)
            {
                Logger.Log($"GeoService: 网络指纹计算失败: {ex.Message}");
                // 拿不到指纹时返回固定值：宁可沿用缓存，也不要每次启动都重查。
                return "unknown";
            }
        }

        /// <summary>
        /// 本地启发式：无法联网查询时，凭系统时区与区域/文化信息判断是否很可能位于中国大陆。
        /// 需同时满足时区为 UTC+8 且区域/文化指向中国大陆，尽量降低误判。
        /// </summary>
        private static bool IsLikelyChinaByLocalHeuristic()
        {
            try
            {
                var tz = TimeZoneInfo.Local;
                bool isUtc8 = tz.BaseUtcOffset == TimeSpan.FromHours(8);
                bool tzNameHintsChina =
                    tz.Id.IndexOf("China", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tz.Id.IndexOf("Shanghai", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tz.Id.IndexOf("Urumqi", StringComparison.OrdinalIgnoreCase) >= 0;

                bool regionIsCn = false;
                try
                {
                    regionIsCn = string.Equals(
                        RegionInfo.CurrentRegion.TwoLetterISORegionName, "CN",
                        StringComparison.OrdinalIgnoreCase);
                }
                catch { }

                bool cultureIsCn = CultureInfo.CurrentUICulture.Name
                    .StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase);

                if (tzNameHintsChina)
                    return true;

                return isUtc8 && (regionIsCn || cultureIsCn);
            }
            catch (Exception ex)
            {
                Logger.Log($"GeoService: 本地区域判断异常: {ex.Message}");
                return false;
            }
        }
    }
}
