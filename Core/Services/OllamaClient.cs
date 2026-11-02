using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;

namespace VPetLLM.Core.Services
{
    public enum OllamaTaskPhase
    {
        DownloadingSetup,
        VerifyingSetup,
        InstallingSetup,
        StartingServer,
        PullingManifest,
        PullingModel,
        VerifyingModel,
        WritingModel
    }

    public sealed record OllamaTaskProgress(OllamaTaskPhase Phase, long Completed = 0, long Total = 0);

    public sealed record OllamaLocalModel(string Name, long SizeBytes, string Family)
    {
        /// <summary>嵌入模型只能算向量，选它当聊天模型每次都会报错。</summary>
        public bool IsChatModel => !Name.Contains("embed", StringComparison.OrdinalIgnoreCase)
                                   && !Family.Contains("bert", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ollama 本身的 HTTP 接口：查版本、列模型、下载和删除模型。
    /// 只连设置里填的地址，不走任何代理；模型由 Ollama 进程自己从官方仓库下载。
    /// </summary>
    public sealed class OllamaClient
    {
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(15);

        // 冒号前可带命名空间，也允许 hf.co/作者/仓库 这种写法
        private static readonly Regex ModelName = new(
            @"^[A-Za-z0-9][A-Za-z0-9._-]*(/[A-Za-z0-9][A-Za-z0-9._-]*)*(:[A-Za-z0-9][A-Za-z0-9._-]*)?$",
            RegexOptions.Compiled);

        private readonly HttpClient _http;
        private readonly Uri _baseUri;

        public OllamaClient(Uri baseUri, HttpMessageHandler? handler = null)
        {
            _baseUri = baseUri;
            // 下载模型的流要开很久，超时交给各调用自己控制
            _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false })
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        public static bool IsValidModelName(string? name)
            => !string.IsNullOrEmpty(name) && name.Length <= 200 && ModelName.IsMatch(name);

        /// <summary>服务的版本号；连不上或端口上不是 Ollama 时返回 null。</summary>
        public async Task<string?> GetVersionAsync(CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ProbeTimeout);
            try
            {
                using var response = await _http.GetAsync(Endpoint("/api/version"), timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                return (string?)JObject.Parse(json)["version"];
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                return null;
            }
        }

        public async Task<IReadOnlyList<OllamaLocalModel>> ListModelsAsync(CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ListTimeout);

            JObject json;
            try
            {
                using var response = await SendAsync(HttpMethod.Get, "/api/tags", null, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                json = JObject.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new OllamaOperationException(OllamaFailure.Api, "timeout");
            }

            return (json["models"] as JArray ?? new JArray())
                .Select(m => new OllamaLocalModel(
                    (string?)m["name"] ?? (string?)m["model"] ?? "",
                    (long?)m["size"] ?? 0,
                    (string?)m["details"]?["family"] ?? ""))
                .Where(m => m.Name.Length > 0)
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 下载模型，收到 success 才算完成。取消即断开连接，已下载的部分 Ollama 会留着，下次接着下。
        /// 各层的进度按 digest 累加：层是一层层出现的，单看某一层进度条会来回跳。
        /// </summary>
        public async Task PullAsync(string model, long expectedSize, IProgress<OllamaTaskProgress>? progress, CancellationToken token)
        {
            var body = new JObject { ["model"] = model, ["stream"] = true };
            using var response = await SendAsync(HttpMethod.Post, "/api/pull", body, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var layers = new Dictionary<string, (long Completed, long Total)>();
            string? line;
            while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0) continue;

                JObject update;
                try
                {
                    update = JObject.Parse(line);
                }
                catch (JsonException ex)
                {
                    // 新版本可能加字段但不会改成别的格式，读不懂的行留个记录就过
                    Logger.Log($"Ollama 下载进度解析失败: {ex.Message}");
                    continue;
                }

                // 流已经开始后状态码不会再变，这时的失败只出现在内容里
                var error = (string?)update["error"];
                if (!string.IsNullOrEmpty(error))
                    throw new OllamaOperationException(OllamaFailure.Api, error);

                var status = (string?)update["status"] ?? "";
                if (status == "success") return;

                var digest = (string?)update["digest"];
                if (!string.IsNullOrEmpty(digest))
                    layers[digest] = ((long?)update["completed"] ?? layers.GetValueOrDefault(digest).Completed,
                        (long?)update["total"] ?? layers.GetValueOrDefault(digest).Total);

                var completed = layers.Values.Sum(l => l.Completed);
                var total = Math.Max(layers.Values.Sum(l => l.Total), expectedSize);
                progress?.Report(new OllamaTaskProgress(ClassifyPull(status), completed, total));
            }

            token.ThrowIfCancellationRequested();
            throw new OllamaOperationException(OllamaFailure.Api, "pull stream ended before success");
        }

        public async Task DeleteAsync(string model, CancellationToken token)
        {
            var body = new JObject { ["model"] = model };
            using var response = await SendAsync(HttpMethod.Delete, "/api/delete", body, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
        }

        private static OllamaTaskPhase ClassifyPull(string status)
        {
            if (status.StartsWith("pulling manifest", StringComparison.Ordinal)) return OllamaTaskPhase.PullingManifest;
            if (status.StartsWith("verifying", StringComparison.Ordinal)) return OllamaTaskPhase.VerifyingModel;
            if (status.StartsWith("writing", StringComparison.Ordinal) || status.StartsWith("removing", StringComparison.Ordinal))
                return OllamaTaskPhase.WritingModel;
            return OllamaTaskPhase.PullingModel;
        }

        /// <summary>发请求并把非 2xx 转成带 Ollama 原话的异常。</summary>
        private async Task<HttpResponseMessage> SendAsync(
            HttpMethod method, string path, JObject? body, HttpCompletionOption completion, CancellationToken token)
        {
            using var request = new HttpRequestMessage(method, Endpoint(path));
            if (body is not null)
                request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, completion, token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new OllamaOperationException(OllamaFailure.Api, ex.Message, ex);
            }

            if (response.IsSuccessStatusCode) return response;

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                throw new OllamaOperationException(OllamaFailure.Api, ExtractError(text) ?? $"HTTP {(int)response.StatusCode}");
            }
        }

        // 和 OllamaChatCore 一样按绝对路径解析，否则管理器和聊天可能连到不同的地方
        private Uri Endpoint(string path) => new(_baseUri, path);

        private static string? ExtractError(string text)
        {
            try
            {
                return (string?)JObject.Parse(text)["error"];
            }
            catch (JsonException)
            {
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
        }
    }
}
