using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Text;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers;

namespace VPetLLM.Core.ChatCore
{
    public class FreeChatCore : ChatCoreBase
    {
        public override string Name => "Free";
        private readonly Setting.FreeSetting _freeSetting;
        private readonly HttpClient _httpClient;

        private const string ENCODED_API_KEY = "633273745233704955327875626b46775879314f64584e66513051744e3070714d544e49625855776331424d536b644d6344424254306c575545394a5954557956555a49";
        private const string ENCODED_API_URL = "6148523063484d364c793932634756304c6e706c59574a31636935686348417663484a7665486b76646e426c644639766347567559576b76646a46695a5852684c324e6f59585176593239746347786c64476c76626e4d3d";
        private const string ENCODED_UA = "566c426c6445784d54563947636d566c58304a3558304a5a54513d3d";
        private const string Model = "bymbymbym";

        public FreeChatCore(Setting.FreeSetting freeSetting, Setting setting, IMainWindow mainWindow, ActionProcessor actionProcessor)
            : base(setting, mainWindow, actionProcessor)
        {
            _freeSetting = freeSetting;

            // 使用基类的代理设置逻辑
            var handler = CreateHttpClientHandler();
            _httpClient = new HttpClient(handler);

            // 设置超时时间为30秒，避免长时间等待
            _httpClient.Timeout = TimeSpan.FromSeconds(30);

            // 设置API密钥
            var decodedApiKey = DecodeString(ENCODED_API_KEY);
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", decodedApiKey);

            // 设置解码后的User-Agent头部
            var decodedUA = DecodeString(ENCODED_UA);
            if (!string.IsNullOrEmpty(decodedUA))
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(decodedUA);
            }
        }

        public override Task<string> Chat(string prompt)
        {
            return Chat(prompt, false);
        }

        public override async Task<string> Chat(string prompt, bool isRetry)
        {
            try
            {
                if (!Settings.KeepContext)
                {
                    ClearContext();
                }

                if (!string.IsNullOrEmpty(prompt))
                {
                    // 无论是用户输入还是插件返回，都作为user角色
                    await HistoryManager.AddMessage(new Message { Role = "user", Content = prompt });
                }

                // 构建请求数据，使用和OpenAI相同的逻辑
                List<Message> history = GetCoreHistory();
                var requestBody = new
                {
                    model = Model,
                    messages = history.Select(m => new { role = m.Role, content = m.Content }),
                    temperature = _freeSetting.Temperature,
                    max_tokens = _freeSetting.MaxTokens
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var apiUrl = DecodeString(ENCODED_API_URL);
                var response = await _httpClient.PostAsync(apiUrl, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                string message;
                if (response.IsSuccessStatusCode)
                {
                    var responseObj = JsonConvert.DeserializeObject<JObject>(responseContent);
                    message = responseObj?["choices"]?[0]?["message"]?["content"]?.ToString() ?? "无回复";
                }
                else
                {
                    // 检查是否是服务器错误
                    if (responseContent.Contains("Failed to retrieve proxy group") ||
                        responseContent.Contains("INTERNAL_SERVER_ERROR") ||
                        response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        Utils.Logger.Log($"Free API 服务器错误: {response.StatusCode} - {responseContent}");

                        // 如果不是重试，尝试重试一次
                        if (!isRetry)
                        {
                            Utils.Logger.Log("尝试重试 Free API 请求...");
                            await Task.Delay(2000); // 等待2秒后重试
                            return await Chat(prompt, true);
                        }

                        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            message = "Free API 服务正在维护中，请稍后再试。如果问题持续存在，请联系开发者。";
                        }
                        else
                        {
                            message = "Free API 服务暂时不可用，请稍后再试。这可能是由于服务器负载过高或维护导致的。";
                        }
                    }
                    else
                    {
                        Utils.Logger.Log($"Free API 错误: {response.StatusCode} - {responseContent}");
                        message = $"API调用失败: {response.StatusCode}";
                    }
                }

                // 根据上下文设置决定是否保留历史（使用基类的统一状态）
                if (Settings.KeepContext)
                {
                    await HistoryManager.AddMessage(new Message { Role = "assistant", Content = message });
                }

                // 只有在保持上下文模式时才保存历史记录
                if (Settings.KeepContext)
                {
                    SaveHistory();
                }

                ResponseHandler?.Invoke(message);
                return "";
            }
            catch (HttpRequestException httpEx)
            {
                Utils.Logger.Log($"Free Chat 网络异常: {httpEx.Message}");

                // 如果不是重试，尝试重试一次
                if (!isRetry)
                {
                    Utils.Logger.Log("网络异常，尝试重试 Free API 请求...");
                    await Task.Delay(2000); // 等待2秒后重试
                    return await Chat(prompt, true);
                }

                var errorMessage = "网络连接异常，请检查网络设置或稍后再试。";
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
            catch (TaskCanceledException tcEx)
            {
                Utils.Logger.Log($"Free Chat 请求超时: {tcEx.Message}");
                var errorMessage = "请求超时，请稍后再试。";
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Free Chat 异常: {ex.Message}");
                var errorMessage = $"聊天异常: {ex.Message}";
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
        }



        public override async Task<string> Summarize(string text)
        {
            try
            {
                var messages = new List<object>
                {
                    new { role = "system", content = "请简洁地总结以下内容，保持关键信息。" },
                    new { role = "user", content = text }
                };

                var requestBody = new
                {
                    model = Model,
                    messages = messages,
                    temperature = 0.3,
                    max_tokens = 500
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var apiUrl = DecodeString(ENCODED_API_URL);
                var response = await _httpClient.PostAsync(apiUrl, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var responseObj = JsonConvert.DeserializeObject<JObject>(responseContent);
                    return responseObj?["choices"]?[0]?["message"]?["content"]?.ToString() ?? "总结失败";
                }
                else
                {
                    // 检查是否是服务器内部错误
                    if (responseContent.Contains("Failed to retrieve proxy group") ||
                        responseContent.Contains("INTERNAL_SERVER_ERROR"))
                    {
                        Utils.Logger.Log($"Free Summarize 服务器内部错误: {responseContent}");
                        return "Free API 服务暂时不可用，总结功能无法使用。";
                    }

                    Utils.Logger.Log($"Free Summarize 错误: {response.StatusCode} - {responseContent}");
                    return "总结失败";
                }
            }
            catch (HttpRequestException httpEx)
            {
                Utils.Logger.Log($"Free Summarize 网络异常: {httpEx.Message}");
                return "网络连接异常，总结功能暂时不可用。";
            }
            catch (TaskCanceledException tcEx)
            {
                Utils.Logger.Log($"Free Summarize 请求超时: {tcEx.Message}");
                return "请求超时，总结功能暂时不可用。";
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Free Summarize 异常: {ex.Message}");
                return "总结异常";
            }
        }

        private List<Message> GetCoreHistory()
        {
            var history = new List<Message>
            {
                new Message { Role = "system", Content = GetSystemMessage() }
            };
            history.AddRange(HistoryManager.GetHistory().Skip(Math.Max(0, HistoryManager.GetHistory().Count - Settings.HistoryCompressionThreshold)));
            return history;
        }

        /// <summary>
        /// 检查API服务状态
        /// </summary>
        public async Task<bool> CheckApiHealthAsync()
        {
            try
            {
                // 使用一个简单的测试请求来检查API状态
                var messages = new List<object>
                {
                    new { role = "user", content = "test" }
                };

                var requestBody = new
                {
                    model = Model,
                    messages = messages,
                    max_tokens = 1
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var apiUrl = DecodeString(ENCODED_API_URL);
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                var response = await _httpClient.PostAsync(apiUrl, content, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    return !responseContent.Contains("Failed to retrieve proxy group") &&
                           !responseContent.Contains("INTERNAL_SERVER_ERROR");
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        // 该API服务由 QQ：790132463 提供，我们可使用的Key有限，我们无法支持大量请求，若您拿到并且正确响应，还请不要泄露与滥用，作为 VPetLLM 为 VPet 大量AI对话Mod的其中一个免费提供AI对话服务的Mod，还请您善待，谢谢！
        // This API service is provided by QQ: 790132463. We have a limited number of available keys and are unable to support a large volume of requests. If you receive a successful response, please do not leak or misuse the key. As one of the free AI conversation services for VPet, specifically for the VPet Large AI Dialogue Mod, we kindly ask for your considerate use. Thank you!
        // このAPIサービスは、QQ：790132463によって提供されています。利用できるキーの数には限りがあり、大量のリクエストには対応できません。もしこのキーを入手し、正常なレスポンスを受け取れた場合でも、漏洩や悪用は厳禁です。VPetLLMは、VPetのAI対話Modの多くの中で、無料のAI対話サービスを提供するModの一つです。どうか大切にご利用ください。よろしくお願いいたします！
        // 這個 API 服務由 QQ：790132463 提供。我們所能使用的金鑰數量有限，因此無法支援大量的請求。如果您已拿到金鑰並成功獲得回應，請不要外洩或濫用。本服務作為 VPetLLM，是為 VPet 大量 AI 對話模組免費提供 AI 對話服務的其中之一，請您珍惜使用，謝謝！
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