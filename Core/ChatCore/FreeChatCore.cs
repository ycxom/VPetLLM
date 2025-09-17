using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using VPet_Simulator.Windows.Interface;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using VPetLLM.Handlers;
using VPetLLM.Core;
using System;

namespace VPetLLM.Core.ChatCore
{
    public class FreeChatCore : ChatCoreBase
    {
        public override string Name => "Free";
        private readonly Setting.FreeSetting _freeSetting;
        private readonly Setting _setting;
        private readonly HttpClient _httpClient;
        
        private const string ENCODED_API_KEY = "633273745233704955327875626b46775879314f64584e66513051744e3070714d544e49625855776331424d536b644d6344424254306c575545394a5954557956555a49";
        private const string ENCODED_API_URL = "6148523063484d364c793932634756304c6e706c59574a31636935686348417663484a7665486b76646e426c644339324d574a6c64474576593268686443396a62323177624756306157397563773d3d";
        private const string ENCODED_UA = "566c426c6445784d54563947636d566c58304a3558304a5a54513d3d";
        
        public FreeChatCore(Setting.FreeSetting freeSetting, Setting setting, IMainWindow mainWindow, ActionProcessor actionProcessor)
            : base(setting, mainWindow, actionProcessor)
        {
            _freeSetting = freeSetting;
            _setting = setting;
            
            // 创建HttpClient并设置内置配置
            _httpClient = new HttpClient();
            var apiKey = DecodeString(ENCODED_API_KEY);
            _httpClient.DefaultRequestHeaders.Authorization = 
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            
            // 设置解码后的User-Agent头部
            var decodedUA = DecodeString(ENCODED_UA);
            if (!string.IsNullOrEmpty(decodedUA))
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(decodedUA);
            }
            
            // 设置代理
            if (setting.Proxy.ForFree)
            {
                var proxy = GetProxy(setting.Proxy.Address);
                if (proxy != null)
                {
                    var handler = new HttpClientHandler()
                    {
                        Proxy = proxy,
                        UseProxy = true
                    };
                    _httpClient.Dispose();
                    _httpClient = new HttpClient(handler);
                    var decodedApiKey = DecodeString(ENCODED_API_KEY);
                    _httpClient.DefaultRequestHeaders.Authorization = 
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", decodedApiKey);
                    
                    // 重新设置User-Agent
                    if (!string.IsNullOrEmpty(decodedUA))
                    {
                        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(decodedUA);
                    }
                }
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
                var messages = new List<object>();
                
                // 添加系统消息
                var systemMessage = GetSystemMessage();
                if (!string.IsNullOrEmpty(systemMessage))
                {
                    messages.Add(new { role = "system", content = systemMessage });
                }
                
                // 添加历史消息
                foreach (var msg in HistoryManager.GetHistory())
                {
                    messages.Add(new { role = msg.Role, content = msg.Content });
                }
                
                // 添加当前用户消息
                messages.Add(new { role = "user", content = prompt });
                
                var requestBody = new
                {
                    model = _freeSetting.Model ?? "gpt-3.5-turbo",
                    messages = messages,
                    temperature = _freeSetting.Temperature,
                    max_tokens = _freeSetting.MaxTokens
                };
                
                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                var apiUrl = DecodeString(ENCODED_API_URL);
                var response = await _httpClient.PostAsync(apiUrl, content);
                var responseContent = await response.Content.ReadAsStringAsync();
                
                if (response.IsSuccessStatusCode)
                {
                    var responseObj = JsonConvert.DeserializeObject<JObject>(responseContent);
                    var reply = responseObj?["choices"]?[0]?["message"]?["content"]?.ToString() ?? "无回复";
                    
                    // 添加到历史记录
                    await HistoryManager.AddMessage(new Message { Role = "user", Content = prompt });
                    await HistoryManager.AddMessage(new Message { Role = "assistant", Content = reply });
                    
                    return reply;
                }
                else
                {
                    Utils.Logger.Log($"Free API 错误: {response.StatusCode} - {responseContent}");
                    return $"API调用失败: {response.StatusCode}";
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Free Chat 异常: {ex.Message}");
                return $"聊天异常: {ex.Message}";
            }
        }

        public override List<string> GetModels()
        {
            try
            {
                // 使用标准OpenAI API获取模型列表
                var apiUrl = DecodeString(ENCODED_API_URL);
                var modelsUrl = apiUrl.Replace("/chat/completions", "/models");
                var response = _httpClient.GetAsync(modelsUrl).Result;
                
                if (response.IsSuccessStatusCode)
                {
                    var responseContent = response.Content.ReadAsStringAsync().Result;
                    var responseObj = JsonConvert.DeserializeObject<JObject>(responseContent);
                    var models = responseObj?["data"]?.ToObject<JArray>();
                    
                    if (models != null)
                    {
                        return models.Select(m => m["id"]?.ToString()).Where(id => !string.IsNullOrEmpty(id)).ToList()!;
                    }
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"获取Free模型列表失败: {ex.Message}");
            }
            
            // 如果API调用失败，返回空列表而不是硬编码列表
            return new List<string>();
        }

        public List<string> RefreshModels()
        {
            // 直接调用GetModels方法，避免重复代码
            return GetModels();
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
                    model = _freeSetting.Model ?? "gpt-3.5-turbo",
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
                    Utils.Logger.Log($"Free Summarize 错误: {response.StatusCode} - {responseContent}");
                    return "总结失败";
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Free Summarize 异常: {ex.Message}");
                return "总结异常";
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