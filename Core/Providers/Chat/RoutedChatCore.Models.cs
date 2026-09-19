using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace VPetLLM.Core.Providers.Chat
{
    public sealed partial class RoutedChatCore
    {
        /// <summary>
        /// 拉取某个渠道可用的模型列表。代理规则和真实请求一致（借该类型工作 core 的 handler）。
        /// 失败抛异常，异常消息可以直接给用户看。
        /// </summary>
        public async Task<List<string>> FetchModelsAsync(Setting.ChannelNodeBase node, CancellationToken cancellationToken = default)
        {
            if (node is Setting.FreeNodeSetting)
                return new List<string> { "auto" };

            // 临时工作 core 只为拿到按渠道代理模式配好的 handler，不发请求、不缓存
            var probe = (Setting.ChannelNodeBase)JsonConvert.DeserializeObject(
                JsonConvert.SerializeObject(node), node.GetType())!;
            probe.Enabled = true; // 停用的渠道也要能拉列表；各 core 选节点时会跳过停用节点
            var worker = CreateWorker(probe, this);

            using var client = new HttpClient(worker.CreateHttpClientHandler(), disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };

            string url;
            switch (node)
            {
                case Setting.OpenAINodeSetting oa:
                {
                    // 聊天地址换掉结尾就是 /models，与请求地址同一套规则
                    var chat = OpenAIChatCore.BuildEndpointUrl(oa);
                    url = chat.Substring(0, chat.LastIndexOf('/')) + "/models";
                    var key = FirstKey(oa.ApiKey);
                    if (!string.IsNullOrEmpty(key))
                        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                    break;
                }
                case Setting.GeminiNodeSetting gm:
                {
                    var key = FirstKey(gm.ApiKey);
                    var baseUrl = string.IsNullOrWhiteSpace(gm.Url)
                        ? "https://generativelanguage.googleapis.com/v1beta"
                        : gm.Url.Trim().TrimEnd('/');
                    if (gm.UseOpenAIAuth)
                    {
                        url = baseUrl + "/models";
                        if (!string.IsNullOrEmpty(key))
                            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                    }
                    else
                    {
                        url = baseUrl + "/models?key=" + Uri.EscapeDataString(key ?? "");
                    }
                    break;
                }
                case Setting.OllamaNodeSetting ol:
                    url = (ol.Url ?? "").Trim().TrimEnd('/') + "/api/tags";
                    break;
                case Setting.LMStudioNodeSetting lm:
                {
                    var baseUrl = (lm.Url ?? "").Trim().TrimEnd('/');
                    if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                        baseUrl = baseUrl.Substring(0, baseUrl.Length - 3);
                    url = baseUrl + "/v1/models";
                    break;
                }
                default:
                    return new List<string>();
            }

            var response = await client.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = body.Length > 200 ? body.Substring(0, 200) + "…" : body;
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {snippet}");
            }

            var json = JObject.Parse(body);
            var models = new List<string>();

            // OpenAI / LM Studio / Gemini(OpenAI 兼容)：data[].id
            if (json["data"] is JArray data)
                models.AddRange(data.Select(m => m["id"]?.ToString()).OfType<string>());

            // Ollama：models[].name；Gemini 原生：models[].name = "models/gemini-xxx"
            if (json["models"] is JArray list)
            {
                foreach (var m in list)
                {
                    var name = m["name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;
                    models.Add(name.StartsWith("models/", StringComparison.Ordinal) ? name.Substring(7) : name);
                }
            }

            return models.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>渠道内可以填多个 Key（换行/逗号分隔），拉模型列表只用第一个。</summary>
        private static string? FirstKey(string? keys)
            => keys?.Split(new[] { ',', ';', '|', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(k => k.Trim())
                    .FirstOrDefault(k => k.Length > 0);
    }
}
