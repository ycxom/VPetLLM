using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Text;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers;

namespace VPetLLM.Core.ChatCore
{
    public class GeminiChatCore : ChatCoreBase
    {
        private int _currentApiKeyIndex = 0;
        public override string Name => "Gemini";
        private readonly Setting.GeminiSetting _geminiSetting;
        
        private string GetCurrentApiKey()
        {
            var apiKey = _geminiSetting.ApiKey ?? string.Empty;
            if (string.IsNullOrWhiteSpace(apiKey))
                return string.Empty;
            var keys = apiKey.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (keys.Length == 0)
                return string.Empty;
            if (keys.Length == 1)
                return keys[0];
            _currentApiKeyIndex = (_currentApiKeyIndex + 1) % keys.Length;
            return keys[_currentApiKeyIndex];
        }
        private readonly Setting _setting;
        private string GetCurrentApiKeyFromNode(string? apiKey)
        {
            var keyText = apiKey ?? string.Empty;
            if (string.IsNullOrWhiteSpace(keyText))
                return string.Empty;
            var keys = keyText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (keys.Length == 0)
                return string.Empty;
            if (keys.Length == 1)
                return keys[0];
            _currentApiKeyIndex = (_currentApiKeyIndex + 1) % keys.Length;
            return keys[_currentApiKeyIndex];
        }
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
            if (!Settings.KeepContext)
            {
                ClearContext();
            }
            if (!string.IsNullOrEmpty(prompt))
            {
                //无论是用户输入还是插件返回，都作为user角色
                await HistoryManager.AddMessage(new Message { Role = "user", Content = prompt });
            }

            List<Message> history = GetCoreHistory();
            var node = _geminiSetting.GetCurrentGeminiSetting();
            var requestData = new
            {
                contents = history.Where(m => m.Role != "system")
                                  .Select(m => new { role = m.Role == "assistant" ? "model" : m.Role, parts = new[] { new { text = m.DisplayContent } } }),
                generationConfig = new
                {
                    maxOutputTokens = node.EnableAdvanced ? node.MaxTokens : 4096,
                    temperature = node.EnableAdvanced ? node.Temperature : 0.8
                },
                systemInstruction = new
                {
                    parts = new[] { new { text = GetSystemMessage() } }
                }
            };

            var content = new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json");

            var baseUrl = node.Url.TrimEnd('/');
            if (!baseUrl.Contains("/v1") && !baseUrl.Contains("/v1beta"))
            {
                baseUrl += "/v1beta";
            }
            var modelName = node.Model;
            var apiEndpoint = node.EnableStreaming 
                ? $"{baseUrl}/models/{modelName}:streamGenerateContent?alt=sse"
                : $"{baseUrl}/models/{modelName}:generateContent";

            string message;
            using (var client = GetClient())
            {
                if (client.DefaultRequestHeaders.TryGetValues("User-Agent", out _))
                {
                    client.DefaultRequestHeaders.Remove("User-Agent");
                }
                client.DefaultRequestHeaders.Add("User-Agent", "Lolisi_VPet_LLMAPI");
                var rotatedKey = GetCurrentApiKeyFromNode(node.ApiKey);
                if (!string.IsNullOrEmpty(rotatedKey))
                {
                    client.DefaultRequestHeaders.Add("x-goog-api-key", rotatedKey);
                }
                
                if (node.EnableStreaming)
                {
                    // 流式传输模式
                    Utils.Logger.Log("Gemini: 使用流式传输模式");
                    var request = new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                    {
                        Content = content
                    };
                    var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    
                    var fullMessage = new StringBuilder();
                    var streamProcessor = new Handlers.StreamingCommandProcessor((cmd) =>
                    {
                        // 当检测到完整命令时，立即处理（流式模式下逐个命令处理）
                        Utils.Logger.Log($"Gemini流式: 检测到完整命令: {cmd}");
                        ResponseHandler?.Invoke(cmd);
                    });
                    
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var reader = new System.IO.StreamReader(stream))
                    {
                        string line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                                continue;
                            
                            var jsonData = line.Substring(6).Trim();
                            
                            try
                            {
                                var chunk = JObject.Parse(jsonData);
                                var delta = chunk["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();
                                if (!string.IsNullOrEmpty(delta))
                                {
                                    fullMessage.Append(delta);
                                    // 将新片段传递给流式处理器，检测完整命令
                                    streamProcessor.AddChunk(delta);
                                    // 通知流式文本更新（用于显示）
                                    StreamingChunkHandler?.Invoke(delta);
                                }
                            }
                            catch
                            {
                                // 忽略解析错误，继续处理下一行
                            }
                        }
                    }
                    message = fullMessage.ToString();
                    Utils.Logger.Log($"Gemini流式: 流式传输完成，总消息长度: {message.Length}");
                    // 注意：流式模式下不再调用 ResponseHandler，因为已经通过 streamProcessor 逐个处理了
                }
                else
                {
                    // 非流式传输模式
                    Utils.Logger.Log("Gemini: 使用非流式传输模式");
                    var response = await client.PostAsync(apiEndpoint, content);
                    response.EnsureSuccessStatusCode();
                    var responseString = await response.Content.ReadAsStringAsync();
                    var responseObject = JObject.Parse(responseString);
                    message = responseObject["candidates"][0]["content"]["parts"][0]["text"].ToString();
                    Utils.Logger.Log($"Gemini非流式: 收到完整消息，长度: {message.Length}");
                    // 非流式模式下，一次性处理完整消息
                    ResponseHandler?.Invoke(message);
                }
            }

            if (Settings.KeepContext)
            {
                await HistoryManager.AddMessage(new Message { Role = "assistant", Content = message });
            }
            if (Settings.KeepContext)
            {
                SaveHistory();
            }
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

            var node = _geminiSetting.GetCurrentGeminiSetting();
            var baseUrl = node.Url.TrimEnd('/');
            if (!baseUrl.Contains("/v1") && !baseUrl.Contains("/v1beta"))
            {
                baseUrl += "/v1beta";
            }
            var modelName = node.Model;
            var apiEndpoint = $"{baseUrl}/models/{modelName}:generateContent";
            using (var client = GetClient())
            {
                if (client.DefaultRequestHeaders.TryGetValues("User-Agent", out _))
                {
                    client.DefaultRequestHeaders.Remove("User-Agent");
                }
                client.DefaultRequestHeaders.Add("User-Agent", "Lolisi_VPet_LLMAPI");
                var rotatedKey2 = GetCurrentApiKeyFromNode(node.ApiKey);
                if (!string.IsNullOrEmpty(rotatedKey2))
                {
                    client.DefaultRequestHeaders.Add("x-goog-api-key", rotatedKey2);
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
            var node = _geminiSetting.GetCurrentGeminiSetting();
            if (node.Url.Contains("/models"))
            {
                requestUrl = node.Url;
            }
            else
            {
                var baseUrl = node.Url.TrimEnd('/');

                if (!baseUrl.Contains("/v1") && !baseUrl.Contains("/v1beta"))
                {
                    baseUrl += "/v1beta";
                }

                requestUrl = baseUrl.EndsWith("/") ? $"{baseUrl}models/" : $"{baseUrl}/models/";
            }

            System.Diagnostics.Debug.WriteLine($"[GeminiDebug] Request URL: {requestUrl}");
            System.Diagnostics.Debug.WriteLine($"[GeminiDebug] API Key present: {!string.IsNullOrEmpty(node.ApiKey)}");

            using (var client = GetClient())
            {
                if (client.DefaultRequestHeaders.TryGetValues("User-Agent", out _))
                {
                    client.DefaultRequestHeaders.Remove("User-Agent");
                }
                client.DefaultRequestHeaders.Add("User-Agent", "Lolisi_VPet_LLMAPI");
                var rotatedKey3 = GetCurrentApiKeyFromNode(node.ApiKey);
                if (!string.IsNullOrEmpty(rotatedKey3))
                {
                    client.DefaultRequestHeaders.Add("x-goog-api-key", rotatedKey3);
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
