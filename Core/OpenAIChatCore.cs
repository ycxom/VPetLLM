using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using VPet_Simulator.Windows.Interface;
using System.Text;
using System.Threading.Tasks;

namespace VPetLLM.Core
{
    public class OpenAIChatCore : ChatCoreBase
    {
        public override string Name => "OpenAI";
        private readonly HttpClient _httpClient;
        private readonly Setting.OpenAISetting _openAISetting;
        private readonly Setting _setting;
        public OpenAIChatCore(Setting.OpenAISetting openAISetting, Setting setting, IMainWindow mainWindow)
            : base(setting, mainWindow)
        {
            _openAISetting = openAISetting;
            _setting = setting;
            _httpClient = new HttpClient();
            if (!string.IsNullOrEmpty(openAISetting.ApiKey))
            {
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {openAISetting.ApiKey}");
            }
        }



        public override async Task<string> Chat(string prompt)
        {
            // 检查并更新系统消息（确保Role设置生效）
            var systemMessage = History.FirstOrDefault(m => m.Role == "system");
            var currentSystemMessage = GetSystemMessage();
            
            if (systemMessage == null)
            {
                // 如果没有系统消息，添加新的
                History.Insert(0, new Message { Role = "system", Content = currentSystemMessage });
            }
            else if (systemMessage.Content != currentSystemMessage)
            {
                // 如果系统消息内容已更改，更新它
                systemMessage.Content = currentSystemMessage;
            }
            
            // 根据上下文设置决定是否保留历史（使用基类的统一状态）
            if (!_keepContext)
            {
                ClearContext();
            }
            else
            {
                History.Add(new Message { Role = "user", Content = prompt });
            }
            // 构建请求数据，根据启用开关决定是否包含高级参数
            object data;
            if (_openAISetting.EnableAdvanced)
            {
                data = new
                {
                    model = _openAISetting.Model,
                    messages = History,
                    temperature = _openAISetting.Temperature,
                    max_tokens = _openAISetting.MaxTokens
                };
            }
            else
            {
                data = new
                {
                    model = _openAISetting.Model,
                    messages = History
                };
            }
            var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
            
            // 处理OpenAI URL兼容性：自动补全后缀
            string apiUrl = _openAISetting.Url;
            if (!apiUrl.Contains("/chat/completions"))
            {
                // 如果URL不包含完整端点，自动补全
                var baseUrl = apiUrl.TrimEnd('/');
                if (!baseUrl.EndsWith("/v1") && !baseUrl.EndsWith("/v1/"))
                {
                    baseUrl += "/v1";
                }
                apiUrl = baseUrl.TrimEnd('/') + "/chat/completions";
            }
            
            var response = await _httpClient.PostAsync(apiUrl, content);
            response.EnsureSuccessStatusCode();
            var responseString = await response.Content.ReadAsStringAsync();
            var responseObject = JObject.Parse(responseString);
            var message = responseObject["choices"][0]["message"]["content"].ToString();
            // 根据上下文设置决定是否保留历史（使用基类的统一状态）
            if (_keepContext)
            {
                History.Add(new Message { Role = "assistant", Content = message });
            }
            // 只有在保持上下文模式时才保存历史记录
            if (_keepContext)
            {
                SaveHistory();
            }
            return message;
        }

        /// <summary>
        /// 手动刷新可用模型列表
        /// </summary>
        public List<string> RefreshModels()
        {
            // 处理OpenAI URL兼容性：自动补全到/models端点
            string apiUrl = _openAISetting.Url;
            if (apiUrl.Contains("/chat/completions"))
            {
                // 如果URL包含完整端点，替换为/models
                apiUrl = apiUrl.Replace("/chat/completions", "/models");
            }
            else
            {
                // 如果URL不包含完整端点，自动补全
                var baseUrl = apiUrl.TrimEnd('/');
                if (!baseUrl.EndsWith("/v1") && !baseUrl.EndsWith("/v1/"))
                {
                    baseUrl += "/v1";
                }
                apiUrl = baseUrl.TrimEnd('/') + "/models";
            }
            
            var url = new System.Uri(new System.Uri(apiUrl), "");
            var response = _httpClient.GetAsync(url).Result;
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
            foreach (var model in responseObject["data"])
            {
                models.Add(model["id"].ToString());
            }
            return models;
        }

        public new List<string> GetModels()
        {
            // 返回空列表，避免启动时自动扫描
            return new List<string>();
        }
    }
}