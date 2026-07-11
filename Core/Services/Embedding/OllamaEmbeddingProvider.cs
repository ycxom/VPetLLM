using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace VPetLLM.Core.Services.Embedding
{
    /// <summary>
    /// Ollama 原生嵌入端点 <c>/api/embed</c>（批量版，非 OpenAI 兼容的 <c>/v1/embeddings</c>）。
    ///
    /// 请求体 <c>{ model, input: [...] }</c>，响应 <c>{ embeddings: [[...], ...] }</c>，
    /// 顺序与输入一一对应。Ollama 通常无鉴权。
    /// </summary>
    public sealed class OllamaEmbeddingProvider : IEmbeddingProvider
    {
        private readonly HttpClient _http;
        private readonly string _endpoint;
        private readonly string _model;

        public string ModelKey { get; }

        /// <param name="baseUrl">形如 http://localhost:11434，末尾斜杠可有可无。</param>
        public OllamaEmbeddingProvider(HttpClient http, string baseUrl, string? apiKey, string model)
        {
            _http = http;
            _model = model;
            _endpoint = BuildEndpoint(baseUrl);

            // Ollama 一般不需要 key，但反代网关可能要
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                _http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            ModelKey = $"{model}@{new Uri(_endpoint).Host}";
        }

        private static string BuildEndpoint(string baseUrl)
        {
            var trimmed = (baseUrl ?? "").TrimEnd('/');
            if (trimmed.EndsWith("/api/embed", StringComparison.OrdinalIgnoreCase))
                return trimmed;
            // 用户可能填到 /api，也可能只填主机
            if (trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
                return trimmed + "/embed";
            return trimmed + "/api/embed";
        }

        public async Task<IReadOnlyList<float[]?>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            var results = new float[]?[texts.Count];
            if (texts.Count == 0)
                return results;

            var body = new { model = _model, input = texts };

            using var content = new StringContent(
                JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_endpoint, content, ct);

            var payload = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Ollama embed {(int)response.StatusCode}: {Truncate(payload, 300)}");

            if (JObject.Parse(payload)["embeddings"] is not JArray embeddings)
                throw new InvalidOperationException($"Ollama 响应缺少 embeddings 字段: {Truncate(payload, 300)}");

            // 按序对应输入，不足的位置留 null
            for (int i = 0; i < embeddings.Count && i < results.Length; i++)
            {
                if (embeddings[i] is not JArray vector || vector.Count == 0)
                    continue;

                var floats = new float[vector.Count];
                for (int j = 0; j < vector.Count; j++)
                    floats[j] = vector[j].ToObject<float>();

                results[i] = floats;
            }

            return results;
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s.Substring(0, max) + "...";
    }
}
