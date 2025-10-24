using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace VPetLLM.Core.ASRCore
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
                Utils.Logger.Log($"ASR (OpenAI): API URL: {url}");

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

                Utils.Logger.Log($"ASR (OpenAI): 发送请求，音频大小: {audioData.Length} bytes, 模型: {_openAISetting.Model}");
                
                var startTime = DateTime.Now;
                using var client = CreateHttpClient();
                var response = await client.SendAsync(request);
                var elapsed = (DateTime.Now - startTime).TotalSeconds;
                
                Utils.Logger.Log($"ASR (OpenAI): 响应接收完成，耗时 {elapsed:F2} 秒, 状态: {response.StatusCode}");
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Utils.Logger.Log($"ASR (OpenAI): API 错误: {response.StatusCode} - {responseContent}");
                    throw new Exception($"OpenAI API 错误: {response.StatusCode}");
                }

                Utils.Logger.Log($"ASR (OpenAI): 响应内容: {responseContent}");
                var result = JObject.Parse(responseContent);
                return result["text"]?.ToString() ?? "";
            }
            catch (TaskCanceledException ex)
            {
                Utils.Logger.Log($"ASR (OpenAI): 请求超时: {ex.Message}");
                throw new Exception("请求超时，请检查网络连接或尝试录制更短的音频");
            }
            catch (HttpRequestException ex)
            {
                Utils.Logger.Log($"ASR (OpenAI): 网络错误: {ex.Message}");
                throw new Exception($"网络错误: {ex.Message}");
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"ASR (OpenAI): 转录错误: {ex.Message}");
                throw;
            }
        }
    }
}
