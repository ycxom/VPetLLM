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

        /// <inheritdoc/>
        public async Task<string> RecognizeText(byte[] imageData)
        {
            var provider = _settings.Screenshot.OCR.Provider;
            Logger.Log($"Performing OCR with provider: {provider}");

            return provider switch
            {
                "OpenAI" => await RecognizeWithOpenAI(imageData),
                "Free" => await RecognizeWithFree(imageData),
                _ => throw new NotSupportedException($"OCR provider not supported: {provider}")
            };
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

            using var client = new HttpClient();
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
