using LinePutScript.Localization.WPF;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Net.Http.Headers;

namespace VPetLLM.Core.Providers.ASR
{
    /// <summary>
    /// OpenAI Whisper ASR 实现
    /// </summary>
    public class OpenAIASRCore : ASRCoreBase
    {
        public override string Name => "OpenAI";
        private readonly Setting.OpenAIASRSetting _openAISetting;

        public OpenAIASRCore(Setting settings) : base(settings)
        {
            _openAISetting = settings?.ASR?.OpenAI ?? new Setting.OpenAIASRSetting();
        }

        public override async Task<string> TranscribeAsync(byte[] audioData)
        {
            try
            {
                var baseUrl = _openAISetting.BaseUrl.TrimEnd('/');
                if (!baseUrl.EndsWith("/v1"))
                {
                    baseUrl += "/v1";
                }
                var url = $"{baseUrl}/audio/transcriptions";
                Logger.Log("{1}: API URL: {0}".Translate(url, "ASR (OpenAI)"));

                using var content = new MultipartFormDataContent();

                var audioContent = new ByteArrayContent(audioData);
                audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                content.Add(audioContent, "file", "audio.wav");

                content.Add(new StringContent(_openAISetting.Model), "model");

                if (!string.IsNullOrWhiteSpace(Settings?.ASR?.Language))
                {
                    content.Add(new StringContent(Settings.ASR.Language), "language");
                }

                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = content
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _openAISetting.ApiKey);

                Logger.Log("{2}: 发送请求，音频大小: {0} bytes, 模型: {1}".Translate(audioData.Length, _openAISetting.Model, "ASR (OpenAI)"));

                var startTime = DateTime.Now;
                using var client = CreateHttpClient();
                var response = await client.SendAsync(request);
                var elapsed = (DateTime.Now - startTime).TotalSeconds;

                Logger.Log("{2}: 响应接收完成，耗时 {0:F2} 秒，状态 {1}".Translate(elapsed, response.StatusCode, "ASR (OpenAI)"));
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log("{2}: API 错误: {0} - {1}".Translate(response.StatusCode, responseContent, "ASR (OpenAI)"));
                    throw new Exception("OpenAI API 错误: {0}".Translate(response.StatusCode));
                }

                Logger.Log("{1}: 响应内容: {0}".Translate(responseContent, "ASR (OpenAI)"));
                var result = JObject.Parse(responseContent);
                return result["text"]?.ToString() ?? "";
            }
            catch (TaskCanceledException ex)
            {
                Logger.Log("{1}: 请求超时: {0}".Translate(ex.Message, "ASR (OpenAI)"));
                throw new Exception("请求超时，请检查网络连接或尝试录制更短的语音".Translate());
            }
            catch (HttpRequestException ex)
            {
                Logger.Log("{1}: 网络错误: {0}".Translate(ex.Message, "ASR (OpenAI)"));
                throw new Exception("网络错误: {0}".Translate(ex.Message));
            }
            catch (Exception ex)
            {
                Logger.Log("{1}: 转录错误: {0}".Translate(ex.Message, "ASR (OpenAI)"));
                throw;
            }
        }
    }
}
