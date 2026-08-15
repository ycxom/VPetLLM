using Newtonsoft.Json.Linq;
using System.Net.Http;

namespace VPetLLM.Core.Engine
{
    /// <summary>
    /// OCR 引擎实现
    /// </summary>
    public class OCREngine : IOCREngine
    {
        private readonly Setting _settings;
        private readonly VPetLLM _plugin;

        public OCREngine(Setting settings, VPetLLM plugin)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        }

        /// <summary>
        /// OCR 是否走独立端点（用户单独填了 key/base_url）。
        ///
        /// 为 false 时，OCR 复用的是截图视觉那套节点——也就是说它与视觉调用
        /// 共享端点和凭据，**不构成独立的失败域**。调用方若想拿 OCR 当视觉失败后的兜底，
        /// 必须先看这个属性：同一套节点刚失败过，再打一次只是白费往返。
        /// </summary>
        public bool UsesDedicatedEndpoint =>
            _settings.Screenshot?.OCR?.Provider == "OpenAI"
            && !string.IsNullOrWhiteSpace(_settings.Screenshot?.OCR?.ApiKey);

        /// <inheritdoc/>
        public async Task<string> RecognizeText(byte[] imageData)
        {
            var ocrSettings = _settings.Screenshot.OCR;
            var provider = ocrSettings.Provider;

            // 只有当用户确实单独填了 OCR 专用端点时，才走那条独立链路。
            // 否则一律复用截图视觉那套节点配置——OCR 本质就是「带识字提示词的视觉调用」，
            // 没道理让用户在节点系统之外再维护一份 key/base_url。
            if (UsesDedicatedEndpoint)
            {
                Logger.Log("OCREngine: 使用独立配置的 OpenAI OCR 端点");
                return await RecognizeWithOpenAI(imageData);
            }

            Logger.Log($"OCREngine: 未配置独立 OCR 端点（provider={provider}），复用视觉节点识字");
            return await RecognizeWithConfiguredNodes(imageData);
        }

        /// <summary>
        /// 复用截图视觉的节点配置做文字识别。
        /// 好处：与截图共用同一份节点（模型、密钥、地址都不必重复填），
        /// 并且自动获得多节点容灾；也顺带补上了 Free 渠道的 OCR
        /// （原本 RecognizeWithFree 只是个返回占位文案的桩）。
        /// </summary>
        private async Task<string> RecognizeWithConfiguredNodes(byte[] imageData)
        {
            var lang = _settings.PromptLanguage ?? "zh";
            var prompt = PromptHelper.Get("OCR_Recognize_Prompt", lang);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                prompt = lang == "zh"
                    ? "请识别这张图片中的所有文字，只返回识别到的文字内容，不要添加任何解释或格式。"
                    : "Extract all text from this image. Return only the recognized text, without any explanation or formatting.";
            }

            var preprocessing = new global::VPetLLM.Services.PreprocessingMultimodal(_settings, _plugin);

            global::VPetLLM.Services.PreprocessingResult result;

            if (preprocessing.HasAvailableProvider())
            {
                // 用户显式选过视觉节点，用那些
                result = await preprocessing.AnalyzeImageAsync(imageData, prompt);
            }
            else if (_settings.Screenshot?.ProcessingMode == ScreenshotProcessingMode.PreprocessingMultimodal)
            {
                // 前置多模态的语义就是「先把图翻译成文字，别把图本身交给主模型」。
                // 此时若退回主聊天渠道，等于绕过用户的选择偷偷做了原生多模态——
                // 主渠道恰好开着视觉时还会「成功」，用户根本发现不了图已经发出去了。
                // 宁可失败也不能走这条路。
                Logger.Log("OCREngine: 前置多模态未配置可用视觉渠道，拒绝回退到主聊天渠道");
                throw new InvalidOperationException(
                    "OCR 失败: 前置多模态没有可用的视觉渠道，请在「截图与模型视觉」里配置多模态提供商");
            }
            else
            {
                // OCR / 原生多模态模式下没选节点，退回主聊天渠道是合理的降级
                result = await preprocessing.AnalyzeWithMainProviderAsync(imageData, prompt);
            }

            if (!result.Success || string.IsNullOrWhiteSpace(result.ImageDescription))
            {
                var reason = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "未返回内容" : result.ErrorMessage;
                Logger.Log($"OCREngine: 节点识字失败: {reason}");
                throw new InvalidOperationException($"OCR 失败: {reason}");
            }

            Logger.Log($"OCREngine: 节点识字成功（{result.UsedProvider}），{result.ImageDescription.Length} 字");
            return result.ImageDescription;
        }

        /// <summary>
        /// 创建 OCR 请求用的 HttpClient：仅在全局代理启用且 ForAllAPI 时走代理，
        /// 否则显式禁用（HttpClientHandler 默认会静默使用系统代理）
        /// </summary>
        private HttpClient CreateOcrHttpClient()
        {
            // handler（连接池）按代理配置共享；超时保持 HttpClient 默认的 100 秒，
            // 各调用点会按需覆盖。
            return Utils.Network.HttpHandlerPool.CreateClient(CreateOcrHandler, TimeSpan.FromSeconds(100));
        }

        private HttpClientHandler CreateOcrHandler()
        {
            var handler = new HttpClientHandler { UseProxy = false, Proxy = null };
            var proxy = _settings.Proxy;
            if (proxy?.IsEnabled == true && proxy.ForAllAPI)
            {
                if (proxy.FollowSystemProxy)
                {
                    handler.Proxy = System.Net.WebRequest.GetSystemWebProxy();
                }
                else if (!string.IsNullOrWhiteSpace(proxy.Address))
                {
                    var protocol = proxy.Protocol?.ToLower() == "socks" ? "socks5" : "http";
                    handler.Proxy = new System.Net.WebProxy(new Uri($"{protocol}://{proxy.Address}"));
                }
                handler.UseProxy = handler.Proxy is not null;
            }
            return handler;
        }

        private async Task<string> RecognizeWithOpenAI(byte[] imageData)
        {
            var ocrSettings = _settings.Screenshot.OCR;

            if (string.IsNullOrWhiteSpace(ocrSettings.ApiKey))
            {
                throw new InvalidOperationException("OpenAI OCR requires an API key");
            }

            var base64Image = Convert.ToBase64String(imageData);
            var baseUrl = ocrSettings.BaseUrl.TrimEnd('/');
            if (!baseUrl.EndsWith("/v1"))
            {
                baseUrl += "/v1";
            }
            var apiUrl = $"{baseUrl}/chat/completions";

            var requestData = new
            {
                model = "gpt-4o-mini",
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = "请识别这张图片中的所有文字，只返回识别到的文字内容，不要添加任何解释或格式。" },
                            new { type = "image_url", image_url = new { url = $"data:image/png;base64,{base64Image}" } }
                        }
                    }
                },
                max_tokens = 4096
            };

            var content = new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json");

            using var client = CreateOcrHttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {ocrSettings.ApiKey}");
            client.Timeout = TimeSpan.FromSeconds(60);

            var response = await client.PostAsync(apiUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Logger.Log($"OpenAI OCR error: {response.StatusCode} - {errorContent}");
                throw new HttpRequestException($"OCR request failed: {response.StatusCode}");
            }

            var responseString = await response.Content.ReadAsStringAsync();
            var responseObject = JObject.Parse(responseString);
            var text = responseObject["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";

            Logger.Log($"OpenAI OCR completed, text length: {text.Length}");
            return text;
        }

        private async Task<string> RecognizeWithFree(byte[] imageData)
        {
            // Free OCR 使用固定的免费服务
            // 这里作为占位符，实际实现可以接入免费的 OCR 服务
            Logger.Log("Free OCR is not fully implemented yet");
            await Task.Delay(100); // 模拟异步操作
            return "Free OCR 功能尚未完全实现，请使用 OpenAI OCR 或原生多模态模式。";
        }
    }
}
