using System.Globalization;
using System.Net.Http;

namespace VPetLLM.Services
{
    /// <summary>
    /// 地区检测服务：判断当前网络环境是否位于中国大陆（CN）。
    /// 优先通过外部 IP 地理接口查询国家码，失败时回退到本地时区/区域启发式判断。
    /// 所有查询均使用直连（不经过代理），因为此处的目的正是判断真实出口网络位置。
    /// </summary>
    public static class GeoService
    {
        private static bool? _cachedResult;
        private static readonly object _lock = new object();

        /// <summary>
        /// 判断当前网络环境是否很可能位于中国大陆。结果会缓存至进程退出，避免重复联网查询。
        /// </summary>
        public static async Task<bool> IsLikelyChinaAsync()
        {
            lock (_lock)
            {
                if (_cachedResult.HasValue)
                    return _cachedResult.Value;
            }

            bool result;
            var apiResult = await QueryCountryCodeAsync();
            if (apiResult != null)
            {
                result = string.Equals(apiResult, "CN", StringComparison.OrdinalIgnoreCase);
                Logger.Log($"GeoService: 通过外部接口检测到国家码 {apiResult}，判定为{(result ? "中国大陆" : "非中国大陆")}");
            }
            else
            {
                result = IsLikelyChinaByLocalHeuristic();
                Logger.Log($"GeoService: 外部接口不可用，回退本地判断结果={result}");
            }

            lock (_lock)
            {
                _cachedResult = result;
            }
            return result;
        }

        /// <summary>
        /// 依次尝试多个轻量 IP 地理接口，返回两位国家码（如 "CN"），全部失败返回 null。
        /// </summary>
        private static async Task<string?> QueryCountryCodeAsync()
        {
            // ip-api.com：返回 JSON，含 countryCode 字段（http 免鉴权）
            var code = await TryIpApiAsync();
            if (!string.IsNullOrEmpty(code)) return code;

            // ipapi.co：返回纯文本国家码
            code = await TryPlainTextCountryAsync("https://ipapi.co/country/");
            if (!string.IsNullOrEmpty(code)) return code;

            return null;
        }

        private static async Task<string?> TryIpApiAsync()
        {
            try
            {
                using var handler = new HttpClientHandler { UseProxy = false, Proxy = null };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                var json = await client.GetStringAsync("http://ip-api.com/json/?fields=status,countryCode");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("status", out var status)
                    && status.GetString() == "success"
                    && root.TryGetProperty("countryCode", out var cc))
                {
                    return cc.GetString();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"GeoService: ip-api.com 查询失败: {ex.Message}");
            }
            return null;
        }

        private static async Task<string?> TryPlainTextCountryAsync(string url)
        {
            try
            {
                using var handler = new HttpClientHandler { UseProxy = false, Proxy = null };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                var text = (await client.GetStringAsync(url))?.Trim();
                if (!string.IsNullOrEmpty(text) && text.Length == 2)
                    return text.ToUpperInvariant();
            }
            catch (Exception ex)
            {
                Logger.Log($"GeoService: {url} 查询失败: {ex.Message}");
            }
            return null;
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
