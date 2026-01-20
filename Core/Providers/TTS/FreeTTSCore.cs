using Newtonsoft.Json.Linq;
using System.Net.Http;
using VPetLLM.Utils.Data;

namespace VPetLLM.Core.Providers.TTS
{
    /// <summary>
    /// Free TTS 实现 (POST API 格式)
    /// 配置从服务器动态获取
    /// </summary>
    public class FreeTTSCore : TTSCoreBase
    {
        public override string Name => "Free";

        private string _apiKey;
        private string _apiUrl;
        private string _model;

        /// <summary>
        /// 设置认证信息获取委托（由 VPetLLM 主类在初始化时调用）
        /// </summary>
        public static void SetAuthProviders(Func<ulong> getSteamId, Func<Task<int>> getAuthKey, Func<string>? getModId = null)
        {
            RequestSignatureHelper.Init(getSteamId, getAuthKey, getModId);
        }

        public FreeTTSCore(Setting settings) : base(settings)
        {
            LoadConfig();
        }

        private void LoadConfig()
        {
            try
            {
                var config = FreeConfigManager.GetTTSConfig();
                if (config is not null)
                {
                    _apiKey = DecodeString(config["API_KEY"]?.ToString() ?? "");
                    _apiUrl = DecodeString(config["API_URL"]?.ToString() ?? "");
                    _model = config["Model"]?.ToString() ?? "";
                    Logger.Log("FreeTTSCore: 配置加载成功");
                }
                else
                {
                    Logger.Log("FreeTTSCore: 配置文件不存在，请等待配置下载完成后重启程序");
                    _apiKey = "";
                    _apiUrl = "";
                    _model = "";
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"FreeTTSCore: 加载配置失败: {ex.Message}");
                _apiKey = "";
                _apiUrl = "";
                _model = "";
            }
        }

        public override async Task<byte[]> GenerateAudioAsync(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(_apiUrl) || string.IsNullOrEmpty(_apiKey))
                {
                    Logger.Log("TTS (Free): 配置未加载，TTS功能不可用");
                    OnAudioGenerationError("Free TTS 配置未加载，请等待配置下载完成后重启程序");
                    return Array.Empty<byte>();
                }

                Logger.Log($"TTS (Free): 发送请求，文本长度: {text.Length}");

                var requestBody = new
                {
                    text = text,
                    text_lang = "auto",
                    api_key = _apiKey,
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var startTime = DateTime.Now;
                using var client = CreateHttpClient();

                using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl)
                {
                    Content = content
                };
                await RequestSignatureHelper.AddSignatureAsync(request);

                var response = await client.SendAsync(request);
                var elapsed = (DateTime.Now - startTime).TotalSeconds;

                Logger.Log($"TTS (Free): 响应接收完成，耗时 {elapsed:F2} 秒，状态 {response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Logger.Log($"TTS (Free): API 错误: {response.StatusCode} - {errorContent}");

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

                var audioData = await response.Content.ReadAsByteArrayAsync();
                Logger.Log($"TTS (Free): 音频生成成功，大小 {audioData.Length} bytes");

                OnAudioGenerated(audioData);
                return audioData;
            }
            catch (TaskCanceledException ex)
            {
                Logger.Log($"TTS (Free): 请求超时: {ex.Message}");
                OnAudioGenerationError("请求超时，请检查网络连接");
                return Array.Empty<byte>();
            }
            catch (HttpRequestException ex)
            {
                Logger.Log($"TTS (Free): 网络错误: {ex.Message}");
                OnAudioGenerationError($"网络错误: {ex.Message}");
                return Array.Empty<byte>();
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS (Free): 生成音频异常: {ex.Message}");
                OnAudioGenerationError($"生成音频异常: {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        public override string GetAudioFormat()
        {
            return "wav";
        }

        private string DecodeString(string encodedString)
        {
            try
            {
                if (string.IsNullOrEmpty(encodedString))
                {
                    return "";
                }

                var hexBytes = new byte[encodedString.Length / 2];
                for (int i = 0; i < hexBytes.Length; i++)
                {
                    hexBytes[i] = Convert.ToByte(encodedString.Substring(i * 2, 2), 16);
                }

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
