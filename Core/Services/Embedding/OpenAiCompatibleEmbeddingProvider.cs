using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace VPetLLM.Core.Services.Embedding
{
    /// <summary>
    /// 走 OpenAI 兼容 <c>/embeddings</c> 端点的向量化后端。
    ///
    /// 同一份实现覆盖三种来源：用户自配的 OpenAI/兼容网关、本地 Ollama/LM Studio、
    /// 以及后续要接的免费 Qwen embedding 端点 —— 后者只是换一组 url/key/model。
    /// </summary>
    public sealed class OpenAiCompatibleEmbeddingProvider : IEmbeddingProvider
    {
        private readonly HttpClient _http;
        private readonly string _endpoint;
        private readonly string _model;
		private readonly bool _useSecureTransport;

        public string ModelKey { get; }

        /// <param name="baseUrl">形如 https://host/v1，末尾有无斜杠均可。</param>
		/// <param name="useSecureTransport">Free 通道启用签名和应用层端到端加密；自配通道保持普通 HTTP 行为。</param>
        public OpenAiCompatibleEmbeddingProvider(
            HttpClient http, string baseUrl, string? apiKey, string model,
			bool useSecureTransport = false)
        {
            _http = http;
            _model = model;
            _endpoint = BuildEndpoint(baseUrl);
			_useSecureTransport = useSecureTransport;

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                _http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            // 端点也进 key：同名模型部署在不同服务上，向量空间未必一致
            ModelKey = $"{model}@{new Uri(_endpoint).Host}";
        }

        /// <summary>
        /// 补全 <c>/embeddings</c> 路径。用户填的 baseUrl 可能已经带上了它，
        /// 也可能只到 /v1，两种都要能用。
        /// </summary>
        private static string BuildEndpoint(string baseUrl)
        {
            var trimmed = (baseUrl ?? "").TrimEnd('/');
            if (trimmed.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
                return trimmed;
            return trimmed + "/embeddings";
        }

        public async Task<IReadOnlyList<float[]?>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            var results = new float[]?[texts.Count];
            if (texts.Count == 0)
                return results;

            var body = new
            {
                model = _model,
                input = texts,
                encoding_format = "float"
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json")
            };

            // Free 通道在此挂签名头；自配通道无钩子
			using var response = _useSecureTransport
				? await Utils.Common.SecureCommunicationBridge.SendAsync(
					_http, request, HttpCompletionOption.ResponseContentRead, ct)
				: await _http.SendAsync(request, ct);

            var payload = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Embedding API {(int)response.StatusCode}: {Truncate(payload, 300)}");

            var data = JObject.Parse(payload)["data"] as JArray;
            if (data is null)
                throw new InvalidOperationException($"Embedding 响应缺少 data 字段: {Truncate(payload, 300)}");

            // data[i].index 指明对应的输入下标；多数实现按序返回，但不保证
            foreach (var item in data)
            {
                var index = item["index"]?.ToObject<int>() ?? -1;
                if (index < 0 || index >= results.Length)
                    continue;

                if (item["embedding"] is not JArray vector || vector.Count == 0)
                    continue;

                var floats = new float[vector.Count];
                for (int i = 0; i < vector.Count; i++)
                    floats[i] = vector[i].ToObject<float>();

                results[index] = floats;
            }

            return results;
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s.Substring(0, max) + "...";
    }
}
