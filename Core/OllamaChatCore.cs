using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace VPetLLM.Core
{
    public class OllamaChatCore : ChatCoreBase
    {
        public override string Name => "Ollama";
        private readonly HttpClient _httpClient;
        private readonly Setting.OllamaSetting _ollamaSetting;
        private readonly Setting _setting;
        private bool _keepContext = true; // 默认保持上下文

        public OllamaChatCore(Setting.OllamaSetting ollamaSetting, Setting setting)
        {
            _ollamaSetting = ollamaSetting;
            _setting = setting;
            _httpClient = new HttpClient()
            {
                BaseAddress = new System.Uri(ollamaSetting.Url)
            };
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
            // 检查并添加系统角色消息
            if (!string.IsNullOrEmpty(_setting.Role) && !History.Any(m => m.Role == "system"))
            {
                History.Insert(0, new Message { Role = "system", Content = _setting.Role });
            }
            
            // 根据上下文设置决定是否保留历史
            if (!_keepContext)
            {
                ClearContext();
            }
            History.Add(new Message { Role = "user", Content = prompt });
            var data = new
            {
                model = _ollamaSetting.Model,
                messages = History,
                stream = false,
                options = _ollamaSetting.EnableAdvanced ? new
                {
                    temperature = _ollamaSetting.Temperature,
                    num_predict = _ollamaSetting.MaxTokens
                } : null
            };
            var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("/api/chat", content);
            response.EnsureSuccessStatusCode();
            var responseString = await response.Content.ReadAsStringAsync();
            var responseObject = JObject.Parse(responseString);
            var message = responseObject["message"]["content"].ToString();
            // 根据上下文设置决定是否保留历史
            if (_keepContext)
            {
                History.Add(new Message { Role = "assistant", Content = message });
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
            var response = _httpClient.GetAsync("/api/tags").Result;
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
                models.Add(model["name"].ToString());
            }
            return models;
        }

        public override List<string> GetModels()
        {
            // 返回空列表，避免启动时自动扫描
            return new List<string>();
        }
    }
}