using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Text;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers;

namespace VPetLLM.Core.ChatCore
{
    public class OpenAIChatCore : ChatCoreBase
    {
        public override string Name => "OpenAI";
        private readonly Setting.OpenAISetting _openAISetting;
        private readonly Setting _setting;
        public OpenAIChatCore(Setting.OpenAISetting openAISetting, Setting setting, IMainWindow mainWindow, ActionProcessor actionProcessor)
            : base(setting, mainWindow, actionProcessor)
        {
            _openAISetting = openAISetting;
            _setting = setting;
        }

        public override Task<string> Chat(string prompt)
        {
            return Chat(prompt, false);
        }
        public override async Task<string> Chat(string prompt, bool isFunctionCall = false)
        {
            if (!Settings.KeepContext)
            {
                ClearContext();
            }
            if (!string.IsNullOrEmpty(prompt))
            {
                //无论是用户输入还是插件返回，都作为user角色
                await HistoryManager.AddMessage(new Message { Role = "user", Content = prompt });
            }
            // 构建请求数据，根据启用开关决定是否包含高级参数
            List<Message> history = GetCoreHistory();
            object data;
            if (_openAISetting.EnableAdvanced)
            {
                data = new
                {
                    model = _openAISetting.Model,
                    messages = history.Select(m => new { role = m.Role, content = m.DisplayContent }),
                    temperature = _openAISetting.Temperature,
                    max_tokens = _openAISetting.MaxTokens
                };
            }
            else
            {
                data = new
                {
                    model = _openAISetting.Model,
                    messages = history.Select(m => new { role = m.Role, content = m.DisplayContent })
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

            string message;
            using (var client = GetClient())
            {
                if (!string.IsNullOrEmpty(_openAISetting.ApiKey))
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_openAISetting.ApiKey}");
                }
                var response = await client.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                var responseString = await response.Content.ReadAsStringAsync();
                var responseObject = JObject.Parse(responseString);
                message = responseObject["choices"][0]["message"]["content"].ToString();
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

        public override async Task<string> Summarize(string text)
        {
            var messages = new[]
            {
                new { role = "user", content = text }
            };

            object data;
            if (_openAISetting.EnableAdvanced)
            {
                data = new
                {
                    model = _openAISetting.Model,
                    messages = messages,
                    temperature = _openAISetting.Temperature,
                    max_tokens = _openAISetting.MaxTokens
                };
            }
            else
            {
                data = new
                {
                    model = _openAISetting.Model,
                    messages = messages
                };
            }

            var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");

            string apiUrl = _openAISetting.Url;
            if (!apiUrl.Contains("/chat/completions"))
            {
                var baseUrl = apiUrl.TrimEnd('/');
                if (!baseUrl.EndsWith("/v1") && !baseUrl.EndsWith("/v1/"))
                {
                    baseUrl += "/v1";
                }
                apiUrl = baseUrl.TrimEnd('/') + "/chat/completions";
            }
            using (var client = GetClient())
            {
                if (!string.IsNullOrEmpty(_openAISetting.ApiKey))
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_openAISetting.ApiKey}");
                }
                var response = await client.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                var responseString = await response.Content.ReadAsStringAsync();
                var responseObject = JObject.Parse(responseString);
                return responseObject["choices"][0]["message"]["content"].ToString();
            }
        }

        private List<Message> GetCoreHistory()
        {
            var history = new List<Message>
            {
                new Message { Role = "system", Content = GetSystemMessage() }
            };
            history.AddRange(HistoryManager.GetHistory().Skip(Math.Max(0, HistoryManager.GetHistory().Count - _setting.HistoryCompressionThreshold)));
            return history;
        }
        public List<string> RefreshModels()
        {
            string apiUrl = _openAISetting.Url;
            if (apiUrl.Contains("/chat/completions"))
            {
                apiUrl = apiUrl.Replace("/chat/completions", "/models");
            }
            else
            {
                var baseUrl = apiUrl.TrimEnd('/');
                if (!baseUrl.EndsWith("/v1") && !baseUrl.EndsWith("/v1/"))
                {
                    baseUrl += "/v1";
                }
                apiUrl = baseUrl.TrimEnd('/') + "/models";
            }

            var url = new System.Uri(new System.Uri(apiUrl), "");
            using (var client = GetClient())
            {
                if (!string.IsNullOrEmpty(_openAISetting.ApiKey))
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_openAISetting.ApiKey}");
                }
                var response = client.GetAsync(url).Result;
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
        }

        public new List<string> GetModels()
        {
            return new List<string>();
        }
    }
}