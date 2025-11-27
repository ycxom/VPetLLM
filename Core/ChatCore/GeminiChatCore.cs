using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Text;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers;
using LinePutScript.Localization.WPF;

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
            OnConversationTurn();
            
            if (!Settings.KeepContext)
            {
                ClearContext();
            }
            
            var tempUserMessage = !string.IsNullOrEmpty(prompt) ? new Message { Role = "user", Content = prompt } : null;

            List<Message> history = GetCoreHistory();
            if (tempUserMessage != null)
            {
                history.Add(tempUserMessage);
            }
            history = InjectRecordsIntoHistory(history);
            
            var node = _geminiSetting.GetCurrentGeminiSetting();
            if (node == null)
            {
                var noNodeError = "NoEnabledGeminiNodes".Translate();
                if (string.IsNullOrEmpty(noNodeError) || noNodeError == "NoEnabledGeminiNodes")
                {
                    noNodeError = "没有启用的 Gemini 节点，请在设置中启用至少一个节点";
                }
                Utils.Logger.Log($"Gemini Chat 错误: {noNodeError}");
                ResponseHandler?.Invoke(noNodeError);
                return "";
            }
            
            // 调试模式：当角色设定包含 VPetLLM_DeBug 时，记录当前调用的节点信息
            if (Settings?.Role?.Contains("VPetLLM_DeBug") == true)
            {
                Utils.Logger.Log($"[DEBUG] Gemini 当前调用节点: {node.Name}, URL: {node.Url}, Model: {node.Model}");
            }
            
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
            try
            {
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
                        Utils.Logger.Log("Gemini: 使用流式传输模式");
                        var request = new HttpRequestMessage(HttpMethod.Post, apiEndpoint) { Content = content };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                        
                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await Utils.ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }
                        
                        var fullMessage = new StringBuilder();
                        var streamProcessor = new Handlers.StreamingCommandProcessor((cmd) =>
                        {
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
                                        streamProcessor.AddChunk(delta);
                                        StreamingChunkHandler?.Invoke(delta);
                                    }
                                }
                                catch { }
                            }
                        }
                        message = fullMessage.ToString();
                        Utils.Logger.Log($"Gemini流式: 流式传输完成，总消息长度: {message.Length}");
                    }
                    else
                    {
                        Utils.Logger.Log("Gemini: 使用非流式传输模式");
                        var response = await client.PostAsync(apiEndpoint, content);
                        
                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await Utils.ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }
                        
                        var responseString = await response.Content.ReadAsStringAsync();
                        var responseObject = JObject.Parse(responseString);
                        message = responseObject["candidates"][0]["content"]["parts"][0]["text"].ToString();
                        Utils.Logger.Log($"Gemini非流式: 收到完整消息，长度: {message.Length}");
                        ResponseHandler?.Invoke(message);
                    }
                }
            }
            catch (Exception ex)
            {
                var errorMessage = Utils.ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Gemini");
                Utils.Logger.Log($"Gemini Chat 异常: {ex.Message}");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }

            if (Settings.KeepContext)
            {
                if (tempUserMessage != null)
                {
                    await HistoryManager.AddMessage(tempUserMessage);
                }
                await HistoryManager.AddMessage(new Message { Role = "assistant", Content = message });
                SaveHistory();
            }
            return "";
        }


        public override async Task<string> Summarize(string systemPrompt, string userContent)
        {
            try
            {
                var node = _geminiSetting.GetCurrentGeminiSetting();
                if (node == null)
                {
                    var noNodeError = "NoEnabledGeminiNodes".Translate();
                    if (string.IsNullOrEmpty(noNodeError) || noNodeError == "NoEnabledGeminiNodes")
                    {
                        noNodeError = "没有启用的 Gemini 节点，请在设置中启用至少一个节点";
                    }
                    Utils.Logger.Log($"Gemini Summarize 错误: {noNodeError}");
                    return Utils.ErrorMessageHelper.IsDebugMode(Settings) ? noNodeError : (Utils.ErrorMessageHelper.GetSummarizeError(Settings) ?? "总结失败，请稍后再试。");
                }

                var requestData = new
                {
                    system_instruction = new { parts = new[] { new { text = systemPrompt } } },
                    contents = new[] { new { parts = new[] { new { text = userContent } } } }
                };

                var content = new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json");

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
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        var errorMessage = await Utils.ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini");
                        Utils.Logger.Log($"Gemini Summarize 错误: {errorMessage}");
                        return Utils.ErrorMessageHelper.IsDebugMode(Settings) ? errorMessage : (Utils.ErrorMessageHelper.GetSummarizeError(Settings) ?? "总结失败，请稍后再试。");
                    }
                    
                    var responseString = await response.Content.ReadAsStringAsync();
                    var responseObject = JObject.Parse(responseString);
                    return responseObject["candidates"][0]["content"]["parts"][0]["text"].ToString();
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Gemini Summarize 异常: {ex.Message}");
                return Utils.ErrorMessageHelper.IsDebugMode(Settings) 
                    ? $"Gemini Summarize 异常: {ex.Message}\n{ex.StackTrace}" 
                    : (Utils.ErrorMessageHelper.GetSummarizeError(Settings) ?? "总结功能暂时不可用，请稍后再试。");
            }
        }

        private List<Message> GetCoreHistory(bool injectRecords = false)
        {
            var history = new List<Message> { new Message { Role = "system", Content = GetSystemMessage() } };
            history.AddRange(HistoryManager.GetHistory().Skip(Math.Max(0, HistoryManager.GetHistory().Count - _setting.HistoryCompressionThreshold)));
            if (injectRecords)
            {
                history = InjectRecordsIntoHistory(history);
            }
            return history;
        }

        public List<string> RefreshModels()
        {
            try
            {
                var node = _geminiSetting.GetCurrentGeminiSetting();
                if (node == null)
                {
                    var noNodeError = "NoEnabledGeminiNodes".Translate();
                    if (string.IsNullOrEmpty(noNodeError) || noNodeError == "NoEnabledGeminiNodes")
                    {
                        noNodeError = "没有启用的 Gemini 节点，请在设置中启用至少一个节点";
                    }
                    throw new System.Exception(noNodeError);
                }

                string requestUrl;
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

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorMessage = Utils.ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini").Result;
                        throw new System.Exception(errorMessage);
                    }

                    var responseString = response.Content.ReadAsStringAsync().Result;
                    var models = new List<string>();
                    try
                    {
                        var jsonToken = JToken.Parse(responseString);
                        if (jsonToken is JObject responseObject && responseObject["models"] is JArray modelsArray)
                        {
                            foreach (var model in modelsArray)
                            {
                                models.Add(model["name"].ToString().Replace("models/", ""));
                            }
                        }
                        else if (jsonToken is JArray responseArray)
                        {
                            foreach (var model in responseArray)
                            {
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
                        var parseError = Utils.ErrorMessageHelper.IsDebugMode(Settings)
                            ? $"Failed to parse JSON response: {responseString.Substring(0, System.Math.Min(responseString.Length, 100))}"
                            : "获取模型列表失败，服务器返回了无效的响应格式。";
                        throw new System.Exception(parseError);
                    }
                    return models;
                }
            }
            catch (System.Exception ex) when (!(ex.Message.Contains("API") || ex.Message.Contains("获取模型") || ex.Message.Contains("没有启用")))
            {
                var errorMessage = Utils.ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Gemini");
                throw new System.Exception(errorMessage);
            }
        }

        public new List<string> GetModels()
        {
            return new List<string>();
        }
    }
}
