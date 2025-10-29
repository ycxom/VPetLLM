using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace VPetLLM.Core.ASRCore
{
    /// <summary>
    /// Free ASR 实现 (OpenAI 格式)
    /// </summary>
    public class FreeASRCore : ASRCoreBase
    {
        public override string Name => "Free";

        // 编码的 API Key 和 URL
        private const string ENCODED_API_KEY = "59534e45575370594c69394f515878495855686b4b43386e4932465a51537045666e5a7a55326c4e4e455a51596d3946504645304f4367384e566c615a4834344b5474744b793036546a5635533170555379597056673d3d";
        private const string ENCODED_API_URL = "6148523063484d364c793968633349755a586873596935755a585176646a453d";
        private const string Model = "LBGAME";

        public FreeASRCore(Setting settings) : base(settings)
        {
        }

        public override async Task<string> TranscribeAsync(byte[] audioData)
        {
            try
            {
                var apiUrl = DecodeString(ENCODED_API_URL);
                var apiKey = DecodeString(ENCODED_API_KEY);
                
                var url = $"{apiUrl}/audio/transcriptions";
                Utils.Logger.Log($"ASR (Free): 发送请求，音频大小: {audioData.Length} bytes");

                using var content = new MultipartFormDataContent();
                
                var audioContent = new ByteArrayContent(audioData);
                audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                content.Add(audioContent, "file", "audio.wav");
                
                content.Add(new StringContent(Model), "model");
                
                if (!string.IsNullOrWhiteSpace(Settings?.ASR?.Language))
                {
                    content.Add(new StringContent(Settings.ASR.Language), "language");
                }

                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = content
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var startTime = DateTime.Now;
                using var client = CreateHttpClient();
                var response = await client.SendAsync(request);
                var elapsed = (DateTime.Now - startTime).TotalSeconds;
                
                Utils.Logger.Log($"ASR (Free): 响应接收完成，耗时 {elapsed:F2} 秒, 状态: {response.StatusCode}");
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Utils.Logger.Log($"ASR (Free): API 错误: {response.StatusCode} - {responseContent}");
                    throw new Exception($"Free ASR 服务暂时不可用: {response.StatusCode}");
                }

                Utils.Logger.Log($"ASR (Free): 响应内容: {responseContent}");
                var result = JObject.Parse(responseContent);
                return result["text"]?.ToString() ?? "";
            }
            catch (TaskCanceledException ex)
            {
                Utils.Logger.Log($"ASR (Free): 请求超时: {ex.Message}");
                throw new Exception("请求超时，请检查网络连接或尝试录制更短的音频");
            }
            catch (HttpRequestException ex)
            {
                Utils.Logger.Log($"ASR (Free): 网络错误: {ex.Message}");
                throw new Exception($"网络错误: {ex.Message}");
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"ASR (Free): 转录错误: {ex.Message}");
                throw;
            }
        }

        private string DecodeString(string encodedString)
        {
            try
            {
                if (string.IsNullOrEmpty(encodedString))
                {
                    return "";
                }

                // 第一步：Hex解码
                var hexBytes = new byte[encodedString.Length / 2];
                for (int i = 0; i < hexBytes.Length; i++)
                {
                    hexBytes[i] = Convert.ToByte(encodedString.Substring(i * 2, 2), 16);
                }

                // 第二步：Base64解码
                var base64String = Encoding.UTF8.GetString(hexBytes);
                var finalBytes = Convert.FromBase64String(base64String);
                var result = Encoding.UTF8.GetString(finalBytes);

                return result;
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
