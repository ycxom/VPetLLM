using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using VPet_Simulator.Windows.Interface;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;

using VPetLLM.Handlers;

namespace VPetLLM.Core
{
    public class OllamaChatCore : ChatCoreBase
    {
        public override string Name => "Ollama";
        private readonly HttpClient _httpClient;
        private readonly Setting.OllamaSetting _ollamaSetting;
        private readonly Setting _setting;
        public OllamaChatCore(Setting.OllamaSetting ollamaSetting, Setting setting, IMainWindow mainWindow, ActionProcessor actionProcessor)
            : base(setting, mainWindow, actionProcessor)
        {
            _ollamaSetting = ollamaSetting;
            _setting = setting;
            _httpClient = new HttpClient()
            {
                BaseAddress = new System.Uri(ollamaSetting.Url)
            };
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

        public new List<string> GetModels()
        {
            // 返回空列表，避免启动时自动扫描
            return new List<string>();
        }
    }
}