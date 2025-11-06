using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VPetLLM.Core.TTSCore
{
    /// <summary>
    /// Free TTS 实现 (POST API 格式)
    /// 使用固定参数，减少服务器压力
    /// </summary>
    public class FreeTTSCore : TTSCoreBase
    {
        public override string Name => "Free";

        // 编码的 API Key 和 URL（使用 Hex + Base64 双重编码，与 FreeChatCore 统一）
        private const string ENCODED_API_KEY = "566c426c6445784d545639436556395a5131685054513d3d";
        private const string ENCODED_API_URL = "6148523063484d364c793968615335355933687662533530623341364f5467344d43393064484d3d";

        public FreeTTSCore(Setting settings) : base(settings)
        {
        }

        public override async Task<byte[]> GenerateAudioAsync(string text)
        {
            try
            {
                var apiUrl = DecodeString(ENCODED_API_URL);
                var apiKey = DecodeString(ENCODED_API_KEY);

                Utils.Logger.Log($"TTS (Free): 发送请求，文本长度: {text.Length}");

                // 构建请求体
                var requestBody = new
                {
                    text = text,
                    text_lang = "auto",
                    api_key = apiKey,
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var startTime = DateTime.Now;
                using var client = CreateHttpClient();
                var response = await client.PostAsync(apiUrl, content);
                var elapsed = (DateTime.Now - startTime).TotalSeconds;

                Utils.Logger.Log($"TTS (Free): 响应接收完成，耗时 {elapsed:F2} 秒, 状态: {response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Utils.Logger.Log($"TTS (Free): API 错误: {response.StatusCode} - {errorContent}");

                    // 尝试解析错误消息（符合文档中的错误响应格式）
                    try
                    {
                        var errorObj = JObject.Parse(errorContent);
                        var errorMessage = errorObj["message"]?.ToString() ?? "未知错误";
                        OnAudioGenerationError($"Free TTS 服务错误: {errorMessage}");
                    }
                    catch
                    {
                        OnAudioGenerationError($"Free TTS 服务错误: {response.StatusCode}");
                    }

                    return Array.Empty<byte>();
                }

                // 成功响应：二进制音频数据（Content-Type: audio/wav）
                var audioData = await response.Content.ReadAsByteArrayAsync();
                Utils.Logger.Log($"TTS (Free): 音频生成成功，大小: {audioData.Length} bytes");

                OnAudioGenerated(audioData);
                return audioData;
            }
            catch (TaskCanceledException ex)
            {
                Utils.Logger.Log($"TTS (Free): 请求超时: {ex.Message}");
                OnAudioGenerationError("请求超时，请检查网络连接");
                return Array.Empty<byte>();
            }
            catch (HttpRequestException ex)
            {
                Utils.Logger.Log($"TTS (Free): 网络错误: {ex.Message}");
                OnAudioGenerationError($"网络错误: {ex.Message}");
                return Array.Empty<byte>();
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"TTS (Free): 生成音频异常: {ex.Message}");
                OnAudioGenerationError($"生成音频异常: {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        public override string GetAudioFormat()
        {
            return "wav"; // 固定返回 WAV 格式
        }

        // 该API服务由 @ycxom 提供，我们无法支持大量请求，若您拿到并且正确响应，还请不要泄露与滥用，作为 VPetLLM 为 VPet 大量AI对话Mod的其中一个免费提供AI对话服务的Mod，还请您善待，谢谢！
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
