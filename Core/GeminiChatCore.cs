using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace VPetLLM.Core
{
    public class GeminiChatCore : ChatCoreBase
    {
        public override string Name => "Gemini";
        private readonly HttpClient _httpClient;
        private readonly Setting.GeminiSetting _geminiSetting;
        private readonly Setting _setting;
        private bool _keepContext = true; // 默认保持上下文

        public GeminiChatCore(Setting.GeminiSetting geminiSetting, Setting setting)
        {
            _geminiSetting = geminiSetting;
            _setting = setting;
            _httpClient = new HttpClient();
        }

        /// <summary>
        /// 设置是否保持上下文
        /// </summary>
        public void SetContextMode(bool keepContext)
        {
            _keepContext = keepContext;
        }



        public override async Task<string> Chat(string prompt)
        {
            // 根据上下文设置决定是否保留历史
            if (!_keepContext)
            {
                ClearContext();
            }
            History.Add(new Message { Role = "user", Content = prompt });
            
            // 使用dynamic类型来构建请求数据，避免匿名类型转换问题
            dynamic requestData;
            
            if (!string.IsNullOrEmpty(_setting.Role))
            {
                // 有角色设置时，包含systemInstruction字段
                requestData = new
                {
                    contents = History
                        .Where(m => m.Role != "system")
                        .Select(m => new { role = m.Role, parts = new[] { new { text = m.Content } } }),
                    generationConfig = new
                    {
                        maxOutputTokens = _geminiSetting.EnableAdvanced ? _geminiSetting.MaxTokens : 4096,
                        temperature = _geminiSetting.EnableAdvanced ? _geminiSetting.Temperature : 0.8
                    },
                    systemInstruction = new
                    {
                        parts = new[] { new { text = _setting.Role } }
                    }
                };
            }
            else
            {
                // 没有角色设置时，不包含systemInstruction字段
                requestData = new
                {
                    contents = History
                        .Where(m => m.Role != "system")
                        .Select(m => new { role = m.Role, parts = new[] { new { text = m.Content } } }),
                    generationConfig = new
                    {
                        maxOutputTokens = _geminiSetting.EnableAdvanced ? _geminiSetting.MaxTokens : 4096,
                        temperature = _geminiSetting.EnableAdvanced ? _geminiSetting.Temperature : 0.8
                    }
                };
            }
            
            var content = new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json");
            
            // 构建正确的Gemini API端点：使用模型名称和generateContent方法
            var baseUrl = _geminiSetting.Url.TrimEnd('/');
            
            // 如果URL不包含v1或v1beta后缀，自动添加/v1beta
            if (!baseUrl.Contains("/v1") && !baseUrl.Contains("/v1beta"))
            {
                baseUrl += "/v1beta";
            }
            
            // 构建完整的API端点：/v1beta/models/{modelName}:generateContent
            var modelName = _geminiSetting.Model; // 使用用户配置的模型名称
            var apiEndpoint = $"{baseUrl}/models/{modelName}:generateContent";
            
            // 创建HTTP请求并添加必要的头信息
            var request = new HttpRequestMessage(HttpMethod.Post, apiEndpoint);
            request.Content = content;
            request.Headers.Add("User-Agent", "Lolisi_VPet_LLMAPI");
            request.Headers.Add("Connection", "keep-alive");
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Accept-Encoding", "gzip, deflate, br");
            if (!string.IsNullOrEmpty(_geminiSetting.ApiKey))
            {
                request.Headers.Add("x-goog-api-key", _geminiSetting.ApiKey);
            }
            
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var responseString = await response.Content.ReadAsStringAsync();
            var responseObject = JObject.Parse(responseString);
            var message = responseObject["candidates"][0]["content"]["parts"][0]["text"].ToString();
            
            // 根据上下文设置决定是否保留历史
            if (_keepContext)
            {
                History.Add(new Message { Role = "model", Content = message });
            }
            // 始终保存历史记录，无论上下文设置如何
            SaveHistory();
            return message;
        }

        /// <summary>
        /// 手动刷新可用模型列表
        /// </summary>
        public List<string> RefreshModels()
        {
            // 处理反向代理地址：如果URL已经是完整的端点，直接使用；否则添加标准路径
            string requestUrl;
            if (_geminiSetting.Url.Contains("/models"))
            {
                // URL已经是完整的请求地址
                requestUrl = _geminiSetting.Url;
            }
            else
            {
                // 标准Google Gemini API路径
                var baseUrl = _geminiSetting.Url.TrimEnd('/');
                
                // 如果URL不包含v1或v1beta后缀，自动添加/v1beta
                if (!baseUrl.Contains("/v1") && !baseUrl.Contains("/v1beta"))
                {
                    baseUrl += "/v1beta";
                }
                
                // 确保路径拼接正确，URL以斜杠结尾
                requestUrl = baseUrl.EndsWith("/") ? $"{baseUrl}models/" : $"{baseUrl}/models/";
            }
            
            // 添加详细日志输出用于调试
            System.Diagnostics.Debug.WriteLine($"[GeminiDebug] Request URL: {requestUrl}");
            System.Diagnostics.Debug.WriteLine($"[GeminiDebug] API Key present: {!string.IsNullOrEmpty(_geminiSetting.ApiKey)}");
            
            // 创建HTTP请求并添加必要的头信息
            var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.Add("User-Agent", "Lolisi_VPet_LLMAPI");
            request.Headers.Add("Connection", "keep-alive");
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Accept-Encoding", "gzip, deflate, br");
            if (!string.IsNullOrEmpty(_geminiSetting.ApiKey))
            {
                request.Headers.Add("x-goog-api-key", _geminiSetting.ApiKey);
            }
            
            var response = _httpClient.SendAsync(request).Result;
            
            // 添加响应状态码日志
            System.Diagnostics.Debug.WriteLine($"[GeminiDebug] Response Status: {(int)response.StatusCode} {response.StatusCode}");
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = response.Content.ReadAsStringAsync().Result;
                System.Diagnostics.Debug.WriteLine($"[GeminiDebug] Error Response: {errorContent}");
                throw new System.Exception($"Failed to refresh Gemini models: {response.StatusCode}. URL: {requestUrl}, Response: {errorContent}");
            }
            
            var responseString = response.Content.ReadAsStringAsync().Result;
            JObject responseObject;
            try
            {
                responseObject = JObject.Parse(responseString);
            }
            catch (JsonReaderException)
            {
                System.Diagnostics.Debug.WriteLine($"[GeminiDebug] JSON Parse Error: {responseString}");
                throw new System.Exception($"Failed to parse JSON response: {responseString.Substring(0, System.Math.Min(responseString.Length, 100))}");
            }
            var models = new List<string>();
            foreach (var model in responseObject["models"])
            {
                models.Add(model["name"].ToString().Replace("models/", ""));
            }
            
            System.Diagnostics.Debug.WriteLine($"[GeminiDebug] Models found: {models.Count}");
            return models;
        }

        public override List<string> GetModels()
        {
            // 返回空列表，避免启动时自动扫描
            return new List<string>();
        }
    }
}