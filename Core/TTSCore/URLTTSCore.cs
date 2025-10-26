using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VPetLLM.Core.TTSCore
{
    /// <summary>
    /// URL TTS 实现
    /// </summary>
    public class URLTTSCore : TTSCoreBase
    {
        public override string Name => "URL";
        private readonly Setting.URLTTSSetting _urlSetting;

        public URLTTSCore(Setting settings) : base(settings)
        {
            _urlSetting = settings?.TTS?.URL ?? new Setting.URLTTSSetting();
        }

        public override async Task<byte[]> GenerateAudioAsync(string text)
        {
            try
            {
                var baseUrl = _urlSetting.BaseUrl?.TrimEnd('/');
                var voice = _urlSetting.Voice;
                var requestMethod = _urlSetting.Method?.ToUpper() ?? "GET";

                // 验证URL格式
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
                {
                    Utils.Logger.Log($"TTS (URL): 错误 - URL格式无效: {baseUrl}");
                    throw new ArgumentException($"无效的URL格式: {baseUrl}");
                }

                HttpResponseMessage response;

                if (requestMethod == "POST")
                {
                    // POST请求
                    var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);

                    // 设置请求头
                    request.Headers.Add("Connection", "keep-alive");
                    request.Headers.Add("Accept", "*/*");
                    request.Headers.Add("Accept-Encoding", "gzip, deflate, br");

                    // 构建请求体参数
                    var requestBody = new
                    {
                        text = text,
                        @void = voice  // 使用voice作为void参数
                    };

                    var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { WriteIndented = true });
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    Utils.Logger.Log($"TTS (URL): 发送POST请求到: {baseUrl}");

                    using var client = CreateHttpClient();
                    response = await client.SendAsync(request);
                }
                else
                {
                    // GET请求
                    var encodedText = Uri.EscapeDataString(text);
                    var url = $"{baseUrl}/?text={encodedText}&voice={voice}";

                    Utils.Logger.Log($"TTS (URL): 发送GET请求到: {url}");

                    using var client = CreateHttpClient();
                    response = await client.GetAsync(url);
                }

                Utils.Logger.Log($"TTS (URL): 响应状态码: {response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Utils.Logger.Log($"TTS (URL): 错误响应内容: {errorContent}");
                    throw new Exception($"URL TTS API 错误: {response.StatusCode}");
                }

                var responseData = await response.Content.ReadAsByteArrayAsync();
                Utils.Logger.Log($"TTS (URL): 响应数据大小: {responseData.Length} 字节");

                return responseData;
            }
            catch (TaskCanceledException ex)
            {
                Utils.Logger.Log($"TTS (URL): 请求超时: {ex.Message}");
                throw new Exception("请求超时，请检查网络连接");
            }
            catch (HttpRequestException ex)
            {
                Utils.Logger.Log($"TTS (URL): 网络错误: {ex.Message}");
                throw new Exception($"网络错误: {ex.Message}");
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"TTS (URL): 生成音频错误: {ex.Message}");
                throw;
            }
        }

        public override string GetAudioFormat()
        {
            return "mp3";
        }
    }
}