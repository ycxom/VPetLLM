using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Http;
using VPet_Simulator.Windows.Interface;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using VPetLLM.Handlers;

namespace VPetLLM.Core.ChatCore
{
    public class GeminiChatCore : ChatCoreBase
    {
        public override string Name => "Gemini";
        private readonly Setting.GeminiSetting _geminiSetting;
        private readonly Setting _setting;
        public GeminiChatCore(Setting.GeminiSetting geminiSetting, Setting setting, IMainWindow mainWindow, ActionProcessor actionProcessor)
            : base(setting, mainWindow, actionProcessor)
        {
            _geminiSetting = geminiSetting;
            _setting = setting;
        }

        public override Task<string> Chat(string prompt)
        {
            return Chat(prompt, false);
        }
        public override async Task<string> Chat(string prompt, bool isFunctionCall = false)
        {
            if (!_keepContext)
            {
                ClearContext();
            }
            else
            {
                if (!string.IsNullOrEmpty(prompt))
                {
                    if (!string.IsNullOrEmpty(prompt))
                    {
                        //无论是用户输入还是插件返回，都作为user角色
                        await HistoryManager.AddMessage(new Message { Role = "user", Content = prompt });
                    }
                }
            }
            
            List<Message> history = GetCoreHistory();
            var requestData = new
            {
                contents = history.Where(m => m.Role != "system")
                                  .Select(m => new { role = m.Role == "assistant" ? "model" : m.Role, parts = new[] { new { text = m.Content } } }),
                generationConfig = new
                {
                    maxOutputTokens = _geminiSetting.EnableAdvanced ? _geminiSetting.MaxTokens : 4096,
                    temperature = _geminiSetting.EnableAdvanced ? _geminiSetting.Temperature : 0.8
                },
                systemInstruction = new
                {
                    parts = new[] { new { text = GetSystemMessage() } }
                }
            };
            
            var content = new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json");
            
            var baseUrl = _geminiSetting.Url.TrimEnd('/');
            if (!baseUrl.Contains("/v1") && !baseUrl.Contains("/v1beta"))
            {
                baseUrl += "/v1beta";
            }
            var modelName = _geminiSetting.Model;
            var apiEndpoint = $"{baseUrl}/models/{modelName}:generateContent";
            
            string message;
            using (var client = GetClient())
            {
                if (client.DefaultRequestHeaders.TryGetValues("User-Agent", out _))
                {
                    client.DefaultRequestHeaders.Remove("User-Agent");
                }
                client.DefaultRequestHeaders.Add("User-Agent", "Lolisi_VPet_LLMAPI");
                if (!string.IsNullOrEmpty(_geminiSetting.ApiKey))
                {
                    client.DefaultRequestHeaders.Add("x-goog-api-key", _geminiSetting.ApiKey);
                }
                var response = await client.PostAsync(apiEndpoint, content);
                response.EnsureSuccessStatusCode();
                var responseString = await response.Content.ReadAsStringAsync();
                var responseObject = JObject.Parse(responseString);
                message = responseObject["candidates"][0]["content"]["parts"][0]["text"].ToString();
            }
            
            if (_keepContext)
            {
               await HistoryManager.AddMessage(new Message { Role = "assistant", Content = message });
            }
            if (_keepContext)
            {
                SaveHistory();
            }
            ResponseHandler?.Invoke(message);
            return "";
        }


        public override async Task<string> Summarize(string text)
        {
            var requestData = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = text } } }
                }
            };

            var content = new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json");

            var baseUrl = _geminiSetting.Url.TrimEnd('/');
            if (!baseUrl.Contains("/v1") && !baseUrl.Contains("/v1beta"))
            {
                baseUrl += "/v1beta";
            }
            var modelName = _geminiSetting.Model;
            var apiEndpoint = $"{baseUrl}/models/{modelName}:generateContent";
            using (var client = GetClient())
            {
                if (client.DefaultRequestHeaders.TryGetValues("User-Agent", out _))
                {
                    client.DefaultRequestHeaders.Remove("User-Agent");
                }
                client.DefaultRequestHeaders.Add("User-Agent", "Lolisi_VPet_LLMAPI");
                if (!string.IsNullOrEmpty(_geminiSetting.ApiKey))
                {
                    client.DefaultRequestHeaders.Add("x-goog-api-key", _geminiSetting.ApiKey);
                }
                var response = await client.PostAsync(apiEndpoint, content);
                response.EnsureSuccessStatusCode();
                var responseString = await response.Content.ReadAsStringAsync();
                var responseObject = JObject.Parse(responseString);
                return responseObject["candidates"][0]["content"]["parts"][0]["text"].ToString();
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
            string requestUrl;
            if (_geminiSetting.Url.Contains("/models"))
            {
                requestUrl = _geminiSetting.Url;
            }
            else
            {
                var baseUrl = _geminiSetting.Url.TrimEnd('/');
                
                if (!baseUrl.Contains("/v1") && !baseUrl.Contains("/v1beta"))
                {
                    baseUrl += "/v1beta";
                }
                
                requestUrl = baseUrl.EndsWith("/") ? $"{baseUrl}models/" : $"{baseUrl}/models/";
            }
            
            System.Diagnostics.Debug.WriteLine($"[GeminiDebug] Request URL: {requestUrl}");
            System.Diagnostics.Debug.WriteLine($"[GeminiDebug] API Key present: {!string.IsNullOrEmpty(_geminiSetting.ApiKey)}");

            using (var client = GetClient())
            {
                if (client.DefaultRequestHeaders.TryGetValues("User-Agent", out _))
                {
                    client.DefaultRequestHeaders.Remove("User-Agent");
                }
                client.DefaultRequestHeaders.Add("User-Agent", "Lolisi_VPet_LLMAPI");
                if (!string.IsNullOrEmpty(_geminiSetting.ApiKey))
                {
                    client.DefaultRequestHeaders.Add("x-goog-api-key", _geminiSetting.ApiKey);
                }

                var response = client.GetAsync(requestUrl).Result;

                System.Diagnostics.Debug.WriteLine($"[GeminiDebug] Response Status: {(int)response.StatusCode} {response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = response.Content.ReadAsStringAsync().Result;
                    System.Diagnostics.Debug.WriteLine($"[GeminiDebug] Error Response: {errorContent}");
                    throw new System.Exception($"Failed to refresh Gemini models: {response.StatusCode}. URL: {requestUrl}, Response: {errorContent}");
                }

                var responseString = response.Content.ReadAsStringAsync().Result;
                var models = new List<string>();
                try
                {
                    var jsonToken = JToken.Parse(responseString);

                    // Handle official Gemini format: { "models": [...] }
                    if (jsonToken is JObject responseObject && responseObject["models"] is JArray modelsArray)
                    {
                        foreach (var model in modelsArray)
                        {
                            models.Add(model["name"].ToString().Replace("models/", ""));
                        }
                    }
                    // Handle old format: [...]
                    else if (jsonToken is JArray responseArray)
                    {
                        foreach (var model in responseArray)
                        {
                            // The user's old format returns an array of objects with an 'id' or 'name'
                            var modelName = model["id"]?.ToString() ?? model["name"]?.ToString();
                            if (!string.IsNullOrEmpty(modelName))
                            {
                                models.Add(modelName.Replace("models/", ""));
                            }
                        }
                    }
                }
                catch (JsonReaderException)
                {
                    System.Diagnostics.Debug.WriteLine($"[GeminiDebug] JSON Parse Error: {responseString}");
                    throw new System.Exception($"Failed to parse JSON response: {responseString.Substring(0, System.Math.Min(responseString.Length, 100))}");
                }

                System.Diagnostics.Debug.WriteLine($"[GeminiDebug] Models found: {models.Count}");
                return models;
            }
        }

        public new List<string> GetModels()
        {
            return new List<string>();
        }
    }
}