using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VPetLLM.Core.Services
{
    /// <summary>ollama.com/library 上的一个模型家族。</summary>
    /// <param name="Capabilities">页面上的能力徽章，比如 tools、vision、embedding、cloud。</param>
    /// <param name="Sizes">规格徽章，比如 8b、e4b，可以直接当标签拉取。</param>
    /// <param name="Pulls">页面上写的下载量，比如 120.8M，只用来显示。</param>
    public sealed record OllamaLibraryModel(
        string Name,
        string Description,
        IReadOnlyList<string> Capabilities,
        IReadOnlyList<string> Sizes,
        string Pulls)
    {
        /// <summary>纯云端模型没有规格徽章，连 latest 都拉不到。云端加规格的（gemma4 这种）规格是本地的。</summary>
        [JsonIgnore]
        public bool IsCloudOnly => Capabilities.Contains("cloud") && Sizes.Count == 0;

        /// <summary>语音合成模型页面上没有徽章，只有说明里看得出来。</summary>
        [JsonIgnore]
        public bool IsChatModel => !Capabilities.Contains("embedding")
                                   && !Capabilities.Contains("decision")
                                   && !Description.Contains("text-to-speech", StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public IReadOnlyList<string> Tags => Sizes.Count > 0 ? Sizes : new[] { "latest" };

        public bool Matches(string keyword)
            => Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || Description.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    public sealed record OllamaLibrarySnapshot(IReadOnlyList<OllamaLibraryModel> Models, bool IsFresh);

    /// <summary>
    /// 读 ollama.com 的模型列表和各规格体积，只用来展示和拼模型名：真正的下载由 Ollama 进程
    /// 自己从官方仓库拉。官方没有列表接口，只能解析网页，所以页面改版解析不出来时不能冲掉
    /// 上次的结果，这份列表又不是必需品，取不到就退回内置名单。
    /// </summary>
    public sealed class OllamaLibrary
    {
        private const string LibraryUrl = "https://ollama.com/library";
        private const string ManifestUrl = "https://registry.ollama.ai/v2/library/{0}/manifests/{1}";

        // 正常约 900 KB，防的是地址被劫持后塞过来一个没完没了的响应
        private const int MaxResponseBytes = 8 * 1024 * 1024;

        // 正常 200 多个。解析出来太少说明页面改版了，这种结果不能当真
        private const int MinimumModels = 20;
        private const int MaxDescriptionLength = 300;
        private const int ConcurrentManifests = 4;

        private static readonly TimeSpan ListMaxAge = TimeSpan.FromHours(12);
        private static readonly TimeSpan SizeMaxAge = TimeSpan.FromDays(7);

        private static readonly TimeSpan RegexLimit = TimeSpan.FromSeconds(2);
        private static readonly Regex Card = new(@"<li\b[^>]*>\s*<a href=""/library/([^""]+)""", RegexOptions.None, RegexLimit);
        private static readonly Regex Description = new(@"<p class=""max-w-lg[^""]*"">(.*?)</p>", RegexOptions.Singleline, RegexLimit);
        private static readonly Regex Badge = new(@"<span\s+class=""inline-flex[^""]*"">([^<]+)</span>", RegexOptions.None, RegexLimit);
        private static readonly Regex PullCount = new(@"<span\s*>([\d.,]+[KMB]?)</span>\s*<span class=""hidden sm:flex"">&nbsp;Pulls", RegexOptions.None, RegexLimit);
        private static readonly Regex HtmlTag = new(@"<[^>]+>", RegexOptions.None, RegexLimit);
        private static readonly Regex Blank = new(@"[\s\p{Cc}]+", RegexOptions.None, RegexLimit);
        private static readonly Regex SizeBadge = new(@"^(?:e?\d+(?:\.\d+)?[mbt]|\d+x\d+(?:\.\d+)?[mb])$", RegexOptions.None, RegexLimit);
        private static readonly Regex SafeName = new(@"^[a-z0-9][a-z0-9._-]{0,99}$", RegexOptions.None, RegexLimit);
        private static readonly Regex SafeTag = new(@"^[A-Za-z0-9_][A-Za-z0-9._-]{0,127}$", RegexOptions.None, RegexLimit);

        private static readonly Lazy<OllamaLibrary> SharedInstance = new(() => new OllamaLibrary());

        private readonly HttpClient _http;
        private readonly string _listPath;
        private readonly string _sizePath;
        private readonly Func<DateTimeOffset> _now;
        private readonly SemaphoreSlim _manifestGate = new(ConcurrentManifests);
        private readonly Lazy<ConcurrentDictionary<string, SizeEntry>> _sizes;
        private readonly object _sizeFileGate = new();

        /// <summary>放在系统临时目录：卸载插件时不留东西，丢了也只是重新查一遍。</summary>
        public OllamaLibrary(HttpMessageHandler? handler = null, string? cacheDirectory = null, Func<DateTimeOffset>? now = null)
        {
            _http = new HttpClient(handler ?? new SocketsHttpHandler())
            {
                MaxResponseContentBufferSize = MaxResponseBytes,
                Timeout = TimeSpan.FromSeconds(30)
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("VPetLLM");

            var directory = cacheDirectory ?? Path.Combine(Path.GetTempPath(), "VPetLLM", "ollama");
            _listPath = Path.Combine(directory, "library.json");
            _sizePath = Path.Combine(directory, "library-sizes.json");
            _now = now ?? (() => DateTimeOffset.UtcNow);
            _sizes = new(LoadSizes);
        }

        /// <summary>整个进程共用一份，每次打开管理器不用重新读缓存。</summary>
        public static OllamaLibrary Shared => SharedInstance.Value;

        /// <summary>解析网页。认不出来的卡片跳过，名字只留能安全拼进模型名和地址的。</summary>
        public static IReadOnlyList<OllamaLibraryModel> Parse(string html)
        {
            var cards = Card.Matches(html);
            var models = new List<OllamaLibraryModel>();
            var seen = new HashSet<string>();

            for (var i = 0; i < cards.Count; i++)
            {
                var name = cards[i].Groups[1].Value;
                var start = cards[i].Index + cards[i].Length;
                var end = i + 1 < cards.Count ? cards[i + 1].Index : html.Length;
                var card = html[start..end];

                var badges = Badge.Matches(card).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value).Trim()).ToList();
                var model = new OllamaLibraryModel(
                    name,
                    CleanText(Description.Match(card).Groups[1].Value),
                    badges.Where(b => !SizeBadge.IsMatch(b)).Select(b => b.ToLowerInvariant()).ToList(),
                    badges.Where(b => SizeBadge.IsMatch(b)).ToList(),
                    PullCount.Match(card).Groups[1].Value);

                if (IsSafe(model) && seen.Add(name)) models.Add(model);
            }
            return models;
        }

        private static bool IsSafe(OllamaLibraryModel model)
            => SafeName.IsMatch(model.Name) && model.Sizes.All(SafeTag.IsMatch);

        private static string CleanText(string html)
        {
            var text = Blank.Replace(WebUtility.HtmlDecode(HtmlTag.Replace(html, " ")), " ").Trim();
            return text.Length > MaxDescriptionLength ? text[..MaxDescriptionLength] : text;
        }

        /// <summary>读上次存下的列表，没有或读不出来时返回 null。</summary>
        public Task<OllamaLibrarySnapshot?> LoadCachedAsync(CancellationToken token) => Task.Run(() =>
        {
            try
            {
                if (!File.Exists(_listPath)) return null;

                var cache = JsonConvert.DeserializeObject<ListCache>(File.ReadAllText(_listPath));
                var models = cache?.Models?.Where(IsSafe).ToList();
                if (models is null || models.Count < MinimumModels) return null;

                return new OllamaLibrarySnapshot(models, _now() - cache!.FetchedAt < ListMaxAge);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Logger.Log($"读取 Ollama 模型列表缓存失败: {ex.Message}");
                return null;
            }
        }, token);

        /// <summary>从网上取最新列表并存下来。取不到或解析不出来时返回 null，上次存的不动。</summary>
        public Task<IReadOnlyList<OllamaLibraryModel>?> RefreshAsync(CancellationToken token) => Task.Run(async () =>
        {
            try
            {
                var html = await _http.GetStringAsync(LibraryUrl, token).ConfigureAwait(false);
                var models = Parse(html);
                if (models.Count < MinimumModels)
                {
                    Logger.Log($"Ollama 模型库页面解析出 {models.Count} 个模型，页面可能改版了");
                    return null;
                }

                WriteFile(_listPath, JsonConvert.SerializeObject(new ListCache { FetchedAt = _now(), Models = models.ToList() }));
                return models;
            }
            catch (Exception ex) when (ex is HttpRequestException or RegexMatchTimeoutException
                                       || (ex is OperationCanceledException && !token.IsCancellationRequested))
            {
                Logger.Log($"获取 Ollama 模型列表失败: {ex.Message}");
                return null;
            }
        }, token);

        /// <summary>查一个家族各规格的下载体积，查不到的规格不在结果里。</summary>
        public Task<IReadOnlyDictionary<string, long>> GetSizesAsync(OllamaLibraryModel model, CancellationToken token) => Task.Run(async () =>
        {
            var tags = model.Tags;
            var sizes = await Task.WhenAll(tags.Select(tag => LookUpAsync(model.Name, tag, token))).ConfigureAwait(false);

            var result = new Dictionary<string, long>();
            for (var i = 0; i < tags.Count; i++)
            {
                if (sizes[i] is { } size) result[tags[i]] = size;
            }
            return (IReadOnlyDictionary<string, long>)result;
        }, token);

        /// <summary>按完整模型名查体积。别的仓库的模型（带斜杠）这里查不到。</summary>
        public Task<long?> GetSizeAsync(string model, CancellationToken token)
        {
            var parts = model.Split(':');
            return Task.Run(() => LookUpAsync(parts[0], parts.Length > 1 ? parts[1] : "latest", token), token);
        }

        /// <summary>manifest 里各层大小加起来就是下载量。云端模型的 manifest 没有层，当查不到。</summary>
        private async Task<long?> LookUpAsync(string name, string tag, CancellationToken token)
        {
            if (!SafeName.IsMatch(name) || !SafeTag.IsMatch(tag)) return null;

            var key = $"{name}:{tag}";
            if (_sizes.Value.TryGetValue(key, out var cached) && _now() - cached.CheckedAt < SizeMaxAge)
                return cached.Bytes;

            await _manifestGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var json = await _http.GetStringAsync(string.Format(ManifestUrl, name, tag), token).ConfigureAwait(false);
                var layers = JObject.Parse(json)["layers"]?.Select(layer => layer.Value<long>("size")).ToList();
                if (layers is null || layers.Count == 0 || layers.Any(size => size <= 0)) return null;

                var total = layers.Sum();
                _sizes.Value[key] = new SizeEntry { Bytes = total, CheckedAt = _now() };
                SaveSizes();
                return total;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidCastException or FormatException or OverflowException
                                       || (ex is OperationCanceledException && !token.IsCancellationRequested))
            {
                Logger.Log($"查询模型 {key} 体积失败: {ex.Message}");
                return null;
            }
            finally
            {
                _manifestGate.Release();
            }
        }

        private ConcurrentDictionary<string, SizeEntry> LoadSizes()
        {
            try
            {
                if (File.Exists(_sizePath))
                {
                    var saved = JsonConvert.DeserializeObject<Dictionary<string, SizeEntry>>(File.ReadAllText(_sizePath));
                    if (saved is not null) return new(saved);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Logger.Log($"读取模型体积缓存失败: {ex.Message}");
            }
            return new();
        }

        private void SaveSizes()
        {
            lock (_sizeFileGate)
            {
                WriteFile(_sizePath, JsonConvert.SerializeObject(_sizes.Value));
            }
        }

        /// <summary>先写临时文件再换过去，中途断电也不会留下半个文件。写不进去只是下次重查，不算失败。</summary>
        private static void WriteFile(string path, string content)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = $"{path}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temp, content);
                File.Move(temp, path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Log($"写入 Ollama 模型缓存失败: {ex.Message}");
            }
        }

        private sealed class ListCache
        {
            public DateTimeOffset FetchedAt { get; set; }
            public List<OllamaLibraryModel>? Models { get; set; }
        }

        private sealed class SizeEntry
        {
            public long Bytes { get; set; }
            public DateTimeOffset CheckedAt { get; set; }
        }
    }
}
