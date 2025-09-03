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

        public GeminiChatCore(Setting.GeminiSetting geminiSetting)
        {
            _geminiSetting = geminiSetting;
            _httpClient = new HttpClient();
        }

        public override async Task<string> Chat(string prompt)
        {
            History.Add(new Message { Role = "user", Content = prompt });
            var data = new
            {
                contents = History.Select(m => new { role = m.Role, parts = new[] { new { text = m.Content } } })
            };
            var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
            var url = _geminiSetting.Url;
            var response = await _httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();
            var responseString = await response.Content.ReadAsStringAsync();
            var responseObject = JObject.Parse(responseString);
            var message = responseObject["candidates"][0]["content"]["parts"][0]["text"].ToString();
            History.Add(new Message { Role = "model", Content = message });
            return message;
        }

        public override List<string> GetModels()
        {
            // 处理反向代理地址：如果URL已经是完整的端点，直接使用；否则添加标准路径
            string requestUrl;
            if (_geminiSetting.Url.Contains("/models") || _geminiSetting.Url.Contains("key="))
            {
                // URL已经是完整的请求地址
                requestUrl = _geminiSetting.Url;
                if (!requestUrl.Contains("key=") && !string.IsNullOrEmpty(_geminiSetting.ApiKey))
                {
                    requestUrl += requestUrl.Contains("?") ? "&" : "?";
                    requestUrl += $"key={_geminiSetting.ApiKey}";
                }
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
                
                requestUrl = $"{baseUrl}/models";
                if (!string.IsNullOrEmpty(_geminiSetting.ApiKey))
                {
                    requestUrl += $"?key={_geminiSetting.ApiKey}";
                }
            }
            
            var response = _httpClient.GetAsync(requestUrl).Result;
            response.EnsureSuccessStatusCode();
            var responseString = response.Content.ReadAsStringAsync().Result;
            JObject responseObject;
            try
            {
                responseObject = JObject.Parse(responseString);
            }
            catch (JsonReaderException)
            {
                throw new System.Exception($"Failed to parse JSON response: {responseString.Substring(0, System.Math.Min(responseString.Length, 100))}");
            }
            var models = new List<string>();
            foreach (var model in responseObject["models"])
            {
                models.Add(model["name"].ToString().Replace("models/", ""));
            }
            return models;
        }
    }
}