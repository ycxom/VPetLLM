using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace VPetLLM.Core.Services.Embedding
{
    /// <summary>
    /// Google Generative Language 嵌入端点（<c>text-embedding-004</c> 等）。
    ///
    /// 批量走 <c>models/{model}:batchEmbedContents?key=KEY</c>，
    /// 请求 <c>{ requests: [{ model, content: { parts: [{ text }] } }] }</c>，
    /// 响应 <c>{ embeddings: [{ values: [...] }] }</c>，顺序对应。
    /// 鉴权用 URL 上的 <c>?key=</c>，不是 Bearer 头。
    /// </summary>
    public sealed class GeminiEmbeddingProvider : IEmbeddingProvider
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly string _modelPath;

        public string ModelKey { get; }

        /// <param name="baseUrl">形如 https://generativelanguage.googleapis.com/v1beta，末尾斜杠可省。</param>
        public GeminiEmbeddingProvider(HttpClient http, string baseUrl, string? apiKey, string model)
        {
            _http = http;
            _apiKey = apiKey ?? "";
            _model = model;
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
                ? "https://generativelanguage.googleapis.com/v1beta"
                : baseUrl.TrimEnd('/');

            // Gemini 要求 model 形如 "models/text-embedding-004"
            _modelPath = model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? model
                : $"models/{model}";

            ModelKey = $"{model}@{new Uri(_baseUrl).Host}";
        }

        public async Task<IReadOnlyList<float[]?>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            var results = new float[]?[texts.Count];
            if (texts.Count == 0)
                return results;

            var requests = texts.Select(t => new
            {
                model = _modelPath,
                content = new { parts = new[] { new { text = t } } }
            }).ToArray();

            var body = new { requests };

            var url = $"{_baseUrl}/{_modelPath}:batchEmbedContents";
            if (!string.IsNullOrWhiteSpace(_apiKey))
                url += $"?key={Uri.EscapeDataString(_apiKey)}";

            using var content = new StringContent(
                JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, ct);

            var payload = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Gemini embed {(int)response.StatusCode}: {Truncate(payload, 300)}");

            if (JObject.Parse(payload)["embeddings"] is not JArray embeddings)
                throw new InvalidOperationException($"Gemini 响应缺少 embeddings 字段: {Truncate(payload, 300)}");

            for (int i = 0; i < embeddings.Count && i < results.Length; i++)
            {
                if (embeddings[i]?["values"] is not JArray vector || vector.Count == 0)
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
