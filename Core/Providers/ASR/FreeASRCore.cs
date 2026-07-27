using LinePutScript.Localization.WPF;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using VPetLLM.Utils.Data;

namespace VPetLLM.Core.Providers.ASR
{
    /// <summary>
    /// Free ASR 实现 (OpenAI 格式)
    /// 配置从服务器动态获取
    /// </summary>
    public class FreeASRCore : ASRCoreBase
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

        public FreeASRCore(Setting settings) : base(settings)
        {
            LoadConfig();
        }

        private void LoadConfig()
        {
            try
            {
                var config = FreeConfigManager.GetASRConfig();
                if (config is not null)
                {
                    _apiKey = DecodeString(config["API_KEY"]?.ToString() ?? "");
                    _apiUrl = DecodeString(config["API_URL"]?.ToString() ?? "");
                    _model = config["Model"]?.ToString() ?? "";
                    Logger.Log("FreeASRCore: 配置加载成功");
                }
                else
                {
                    Logger.Log("FreeASRCore: 配置文件不存在，请等待配置下载完成后重启程序");
                    _apiKey = "";
                    _apiUrl = "";
                    _model = "";
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"FreeASRCore: 加载配置失败: {ex.Message}");
                _apiKey = "";
                _apiUrl = "";
                _model = "";
            }
        }

        public override async Task<string> TranscribeAsync(byte[] audioData)
        {
            try
            {
                if (string.IsNullOrEmpty(_apiUrl) || string.IsNullOrEmpty(_apiKey))
                {
                    throw new Exception("Free ASR 配置未加载，请等待配置下载完成后重启程序".Translate());
                }

                var url = $"{_apiUrl}/audio/transcriptions";

                // 与 TTS 共用同一份主机级健康状态：任一方刚探过或刚失败过，这里直接复用结论。
                var (healthy, healthReason) = await Utils.Network.FreeServiceHealthCheck
                    .IsHealthyAsync(url, GetProxy(), m => Logger.Log($"ASR (Free): {m}"))
                    .ConfigureAwait(false);
                if (!healthy)
                {
                    Logger.Log($"ASR (Free): 预检判定服务不可用（{healthReason}），跳过本次请求");
                    throw new Exception($"Free ASR {healthReason}");
                }

                Logger.Log("{1}: 发送请求，音频大小: {0} bytes".Translate(audioData.Length, "ASR (Free)"));

                using var content = new MultipartFormDataContent();

                var audioContent = new ByteArrayContent(audioData);
                audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                content.Add(audioContent, "file", "audio.wav");

                content.Add(new StringContent(_model), "model");

                if (!string.IsNullOrWhiteSpace(Settings?.ASR?.Language))
                {
                    content.Add(new StringContent(Settings.ASR.Language), "language");
                }

                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = content
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

                // 添加签名头
                await RequestSignatureHelper.AddSignatureAsync(request);

                var startTime = DateTime.Now;
                using var client = CreateHttpClient();
                var response = await client.SendAsync(request);
                var elapsed = (DateTime.Now - startTime).TotalSeconds;

                Logger.Log("{2}: 响应接收完成，耗时 {0: F2} 秒，状态 {1}".Translate(elapsed, response.StatusCode, "ASR (Free)"));
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log("{2}: API 错误: {0} - {1}".Translate(response.StatusCode, responseContent, "ASR (Free)"));
                    throw new Exception("Free ASR 服务暂时不可用 {0}".Translate(response.StatusCode));
                }

                Logger.Log("{1}: 响应内容: {0}".Translate(responseContent, "ASR (Free)"));

                // 成功即刷新共享 TTL，TTS 那边也能立刻受益。
                Utils.Network.FreeServiceHealthCheck.ReportOutcome(url, true, "服务正常");

                var result = JObject.Parse(responseContent);
                return result["text"]?.ToString() ?? "";
            }
            catch (TaskCanceledException ex)
            {
                Logger.Log("{1}: 请求超时: {0}".Translate(ex.Message, "ASR (Free)"));
                Utils.Network.FreeServiceHealthCheck.ReportOutcome($"{_apiUrl}/audio/transcriptions", false, "服务无响应");
                throw new Exception("请求超时，请检查网络连接或尝试录制更短的音频".Translate());
            }
            catch (HttpRequestException ex)
            {
                Logger.Log("{1}: 网络错误: {0}".Translate(ex.Message, "ASR (Free)"));
                Utils.Network.FreeServiceHealthCheck.ReportOutcome($"{_apiUrl}/audio/transcriptions", false, "无法连接到服务");
                throw new Exception("网络错误: {0}".Translate(ex.Message));
            }
            catch (Exception ex)
            {
                Logger.Log("{1}: 转录错误: {0}".Translate(ex.Message, "ASR (Free)"));
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
