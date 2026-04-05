using LinePutScript.Localization.WPF;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Core.Providers.Chat
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

        protected override Setting.ChannelProxyMode GetChannelProxyMode()
        {
            var node = _geminiSetting.GetCurrentGeminiSetting();
            if (node != null)
            {
                return node.ProxyMode;
            }
            return Setting.ChannelProxyMode.FollowDefault;
        }

        public override Task<string> Chat(string prompt)
        {
            return Chat(prompt, false);
        }

        public override async Task<string> ChatWithImage(string prompt, byte[] imageData)
        {
            OnConversationTurn();

            if (!Settings.KeepContext)
            {
                ClearContext();
            }

            var node = _geminiSetting.GetCurrentGeminiSetting("Chat");
            if (node is null)
            {
                var noNodeError = "没有启用的 Gemini 节点，请在设置中启用至少一个节点";
                Logger.Log($"Gemini ChatWithImage 错误: {noNodeError}");
                ResponseHandler?.Invoke(noNodeError);
                return "";
            }

            if (!node.EnableVision)
            {
                var visionError = "当前节点未启用视觉能力，请在设置中启用 EnableVision";
                Logger.Log($"Gemini ChatWithImage 错误: {visionError}");
                ResponseHandler?.Invoke(visionError);
                return "";
            }

            Logger.Log($"Gemini ChatWithImage: 发送多模态消息，图像大小: {imageData.Length} bytes");

            List<Message> history = GetCoreHistory();

            if (node.UseOpenAIAuth)
            {
                return await ChatWithImageOpenAI(prompt, imageData, history, node);
            }
            else
            {
                return await ChatWithImageGemini(prompt, imageData, history, node);
            }
        }

        private async Task<string> ChatWithImageOpenAI(string prompt, byte[] imageData, List<Message> history, Setting.GeminiNodeSetting node)
        {
            var base64Image = Convert.ToBase64String(imageData);
            var userContent = new object[]
            {
                new { type = "text", text = prompt },
                new { type = "image_url", image_url = new { url = $"data:image/png;base64,{base64Image}" } }
            };

            var requestMessages = new List<object>();
            foreach (var msg in history)
            {
                requestMessages.Add(new { role = msg.Role, content = msg.DisplayContent });
            }
            requestMessages.Add(new { role = "user", content = userContent });

            object data;
            if (node.EnableAdvanced)
            {
                data = new
                {
                    model = node.Model,
                    messages = requestMessages,
                    temperature = node.Temperature,
                    max_tokens = node.MaxTokens,
                    stream = node.EnableStreaming
                };
            }
            else
            {
                data = new
                {
                    model = node.Model,
                    messages = requestMessages,
                    max_tokens = 4096,
                    stream = node.EnableStreaming
                };
            }

            var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");

            var apiUrl = BuildOpenAIEndpoint(node.Url);

            string message;
            try
            {
                using (var client = GetClient())
                {
                    AddAuthHeaders(client, node);

                    if (node.EnableStreaming)
                    {
                        Logger.Log("Gemini (OpenAI兼容): 使用流式传输模式");
                        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl) { Content = content };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }

                        var fullMessage = new StringBuilder();
                        var streamProcessor = new Handlers.Core.StreamingCommandProcessor((cmd) =>
                        {
                            Logger.Log($"Gemini流式: 检测到完整命令: {cmd}");
                            ResponseHandler?.Invoke(cmd);
                        });

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new System.IO.StreamReader(stream))
                        {
                            string line;
                            while ((line = await reader.ReadLineAsync()) is not null)
                            {
                                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                                    continue;

                                var jsonData = line.Substring(6).Trim();
                                if (jsonData == "[DONE]")
                                    break;

                                try
                                {
                                    var chunk = JObject.Parse(jsonData);
                                    var delta = chunk["choices"]?[0]?["delta"]?["content"]?.ToString();
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
                    }
                    else
                    {
                        Logger.Log("Gemini (OpenAI兼容): 使用非流式传输模式");
                        var response = await client.PostAsync(apiUrl, content);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }

                        var responseString = await response.Content.ReadAsStringAsync();
                        var responseObject = JObject.Parse(responseString);
                        message = responseObject["choices"][0]["message"]["content"].ToString();
                        ResponseHandler?.Invoke(message);
                    }
                }
            }
            catch (Exception ex)
            {
                var errorMessage = ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Gemini");
                Logger.Log($"Gemini ChatWithImage 异常: {ex.Message}");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }

            if (Settings.KeepContext)
            {
                var userMessage = CreateUserMessage($"[图像] {prompt}");
                if (userMessage is not null)
                {
                    userMessage.ImageData = imageData;
                    await HistoryManager.AddMessage(userMessage);
                }
                await HistoryManager.AddMessage(new Message { Role = "assistant", Content = message });
                SaveHistory();
            }
            return "";
        }

        private async Task<string> ChatWithImageGemini(string prompt, byte[] imageData, List<Message> history, Setting.GeminiNodeSetting node)
        {
            var contents = new List<object>();
            foreach (var msg in history.Where(m => m.Role != "system"))
            {
                contents.Add(new { role = msg.Role == "assistant" ? "model" : msg.Role, parts = new[] { new { text = msg.DisplayContent } } });
            }

            var base64Image = Convert.ToBase64String(imageData);
            contents.Add(new
            {
                role = "user",
                parts = new object[]
                {
                    new { text = prompt },
                    new { inline_data = new { mime_type = "image/png", data = base64Image } }
                }
            });

            var requestData = new
            {
                contents = contents,
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

            var apiEndpoint = BuildGeminiEndpoint(node.Url, node.Model, node.EnableStreaming);

            string message;
            try
            {
                using (var client = GetClient())
                {
                    AddAuthHeaders(client, node);

                    if (node.EnableStreaming)
                    {
                        Logger.Log("Gemini ChatWithImage: 使用流式传输模式");
                        var request = new HttpRequestMessage(HttpMethod.Post, apiEndpoint) { Content = content };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }

                        var fullMessage = new StringBuilder();
                        var streamProcessor = new Handlers.Core.StreamingCommandProcessor((cmd) =>
                        {
                            Logger.Log($"Gemini流式: 检测到完整命令: {cmd}");
                            ResponseHandler?.Invoke(cmd);
                        });

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new System.IO.StreamReader(stream))
                        {
                            string line;
                            while ((line = await reader.ReadLineAsync()) is not null)
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
                    }
                    else
                    {
                        Logger.Log("Gemini ChatWithImage: 使用非流式传输模式");
                        var response = await client.PostAsync(apiEndpoint, content);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }

                        var responseString = await response.Content.ReadAsStringAsync();
                        var responseObject = JObject.Parse(responseString);
                        message = responseObject["candidates"][0]["content"]["parts"][0]["text"].ToString();
                        ResponseHandler?.Invoke(message);
                    }
                }
            }
            catch (Exception ex)
            {
                var errorMessage = ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Gemini");
                Logger.Log($"Gemini ChatWithImage 异常: {ex.Message}");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }

            if (Settings.KeepContext)
            {
                var userMessage = CreateUserMessage($"[图像] {prompt}");
                if (userMessage is not null)
                {
                    userMessage.ImageData = imageData;
                    await HistoryManager.AddMessage(userMessage);
                }
                await HistoryManager.AddMessage(new Message { Role = "assistant", Content = message });
                SaveHistory();
            }
            return "";
        }

        public override async Task<string> Chat(string prompt, bool isFunctionCall = false)
        {
            OnConversationTurn();

            if (!Settings.KeepContext)
            {
                ClearContext();
            }

            var tempUserMessage = CreateUserMessage(prompt);

            List<Message> history = GetCoreHistory();
            if (tempUserMessage is not null)
            {
                history.Add(tempUserMessage);
            }
            history = InjectRecordsIntoHistory(history);

            var node = _geminiSetting.GetCurrentGeminiSetting("Chat");
            if (node is null)
            {
                var noNodeError = "NoEnabledGeminiNodes".Translate();
                if (string.IsNullOrEmpty(noNodeError) || noNodeError == "NoEnabledGeminiNodes")
                {
                    noNodeError = "没有启用的 Gemini 节点，请在设置中启用至少一个节点";
                }
                Logger.Log($"Gemini Chat 错误: {noNodeError}");
                ResponseHandler?.Invoke(noNodeError);
                return "";
            }

            if (Settings?.Role?.Contains("VPetLLM_DeBug") == true)
            {
                Logger.Log($"[DEBUG] Gemini 当前调用节点: {node.Name}, URL: {node.Url}, Model: {node.Model}, UseOpenAIAuth: {node.UseOpenAIAuth}");
            }

            if (node.UseOpenAIAuth)
            {
                return await ChatOpenAI(prompt, history, node, tempUserMessage);
            }
            else
            {
                return await ChatGemini(prompt, history, node, tempUserMessage);
            }
        }

        private async Task<string> ChatOpenAI(string prompt, List<Message> history, Setting.GeminiNodeSetting node, Message? tempUserMessage)
        {
            object data;
            if (node.EnableAdvanced)
            {
                data = new
                {
                    model = node.Model,
                    messages = history.Select(m => new { role = m.Role, content = m.DisplayContent }),
                    temperature = node.Temperature,
                    max_tokens = node.MaxTokens,
                    stream = node.EnableStreaming
                };
            }
            else
            {
                data = new
                {
                    model = node.Model,
                    messages = history.Select(m => new { role = m.Role, content = m.DisplayContent }),
                    stream = node.EnableStreaming
                };
            }
            var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");

            var apiUrl = BuildOpenAIEndpoint(node.Url);

            string message;
            try
            {
                using (var client = GetClient())
                {
                    AddAuthHeaders(client, node);

                    if (node.EnableStreaming)
                    {
                        Logger.Log("Gemini (OpenAI兼容): 使用流式传输模式");
                        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl) { Content = content };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }

                        var fullMessage = new StringBuilder();
                        var streamProcessor = new StreamingCommandProcessor((cmd) =>
                        {
                            ResponseHandler?.Invoke(cmd);
                        }, VPetLLM.Instance);

                        bool useBatch = Settings?.EnableStreamingBatch ?? true;
                        int batchWindow = Settings?.StreamingBatchWindowMs ?? 100;
                        streamProcessor.SetBatchingConfig(useBatch, batchWindow);

                        var TotalUsage = 0;
                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new System.IO.StreamReader(stream))
                        {
                            string line;
                            while ((line = await reader.ReadLineAsync()) is not null)
                            {
                                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                                    continue;

                                var jsonData = line.Substring(6).Trim();
                                if (jsonData == "[DONE]")
                                    break;

                                try
                                {
                                    var chunk = JObject.Parse(jsonData);
                                    var delta = chunk["choices"]?[0]?["delta"]?["content"]?.ToString();
                                    if (!string.IsNullOrEmpty(delta))
                                    {
                                        fullMessage.Append(delta);
                                        streamProcessor.AddChunk(delta);
                                        StreamingChunkHandler?.Invoke(delta);
                                        var usage = chunk["usage"]?["total_tokens"]?.ToObject<int>() ?? 0;
                                        TotalUsage += usage;
                                    }
                                }
                                catch
                                {
                                }
                            }
                        }
                        message = fullMessage.ToString();
                        streamProcessor.FlushBatch();
                    }
                    else
                    {
                        Logger.Log("Gemini (OpenAI兼容): 使用非流式传输模式");
                        var response = await client.PostAsync(apiUrl, content);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }

                        var responseString = await response.Content.ReadAsStringAsync();
                        var responseObject = JObject.Parse(responseString);
                        message = responseObject["choices"][0]["message"]["content"].ToString();
                        ResponseHandler?.Invoke(message);
                    }
                }
            }
            catch (Exception ex)
            {
                var errorMessage = ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Gemini");
                Logger.Log($"Gemini Chat 异常: {ex.Message}");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }

            if (Settings.KeepContext)
            {
                if (tempUserMessage is not null)
                {
                    await HistoryManager.AddMessage(tempUserMessage);
                }
                await HistoryManager.AddMessage(new Message { Role = "assistant", Content = message });
                SaveHistory();
            }
            return "";
        }

        private async Task<string> ChatGemini(string prompt, List<Message> history, Setting.GeminiNodeSetting node, Message? tempUserMessage)
        {
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

            var apiEndpoint = BuildGeminiEndpoint(node.Url, node.Model, node.EnableStreaming);

            string message;
            try
            {
                using (var client = GetClient())
                {
                    AddAuthHeaders(client, node);

                    if (node.EnableStreaming)
                    {
                        Logger.Log("Gemini: 使用流式传输模式");
                        var request = new HttpRequestMessage(HttpMethod.Post, apiEndpoint) { Content = content };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }

                        var fullMessage = new StringBuilder();
                        var streamProcessor = new Handlers.Core.StreamingCommandProcessor((cmd) =>
                        {
                            Logger.Log($"Gemini流式: 检测到完整命令: {cmd}");
                            ResponseHandler?.Invoke(cmd);
                        });

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new System.IO.StreamReader(stream))
                        {
                            string line;
                            while ((line = await reader.ReadLineAsync()) is not null)
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
                        Logger.Log($"Gemini流式: 流式传输完成，总消息长度: {message.Length}");
                    }
                    else
                    {
                        Logger.Log("Gemini: 使用非流式传输模式");
                        var response = await client.PostAsync(apiEndpoint, content);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }

                        var responseString = await response.Content.ReadAsStringAsync();
                        var responseObject = JObject.Parse(responseString);
                        message = responseObject["candidates"][0]["content"]["parts"][0]["text"].ToString();
                        Logger.Log($"Gemini非流式: 收到完整消息，长度: {message.Length}");
                        ResponseHandler?.Invoke(message);
                    }
                }
            }
            catch (Exception ex)
            {
                var errorMessage = ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Gemini");
                Logger.Log($"Gemini Chat 异常: {ex.Message}");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }

            if (Settings.KeepContext)
            {
                if (tempUserMessage is not null)
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
                var node = _geminiSetting.GetCurrentGeminiSetting("Compression");
                if (node is null)
                {
                    var noNodeError = "NoEnabledGeminiNodes".Translate();
                    if (string.IsNullOrEmpty(noNodeError) || noNodeError == "NoEnabledGeminiNodes")
                    {
                        noNodeError = "没有启用的 Gemini 节点，请在设置中启用至少一个节点";
                    }
                    Logger.Log($"Gemini Summarize 错误: {noNodeError}");
                    return ErrorMessageHelper.IsDebugMode(Settings) ? noNodeError : (ErrorMessageHelper.GetSummarizeError(Settings) ?? "总结失败，请稍后再试。");
                }

                if (node.UseOpenAIAuth)
                {
                    return await SummarizeOpenAI(systemPrompt, userContent, node);
                }
                else
                {
                    return await SummarizeGemini(systemPrompt, userContent, node);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Gemini Summarize 异常: {ex.Message}");
                return ErrorMessageHelper.IsDebugMode(Settings)
                    ? $"Gemini Summarize 异常: {ex.Message}\n{ex.StackTrace}"
                    : (ErrorMessageHelper.GetSummarizeError(Settings) ?? "总结功能暂时不可用，请稍后再试。");
            }
        }

        private async Task<string> SummarizeOpenAI(string systemPrompt, string userContent, Setting.GeminiNodeSetting node)
        {
            var messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            };

            object data;
            if (node.EnableAdvanced)
            {
                data = new
                {
                    model = node.Model,
                    messages = messages,
                    temperature = node.Temperature,
                    max_tokens = node.MaxTokens
                };
            }
            else
            {
                data = new
                {
                    model = node.Model,
                    messages = messages
                };
            }

            var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");

            var apiUrl = BuildOpenAIEndpoint(node.Url);

            using (var client = GetClient())
            {
                AddAuthHeaders(client, node);
                var response = await client.PostAsync(apiUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini");
                    Logger.Log($"Gemini Summarize 错误: {errorMessage}");
                    return ErrorMessageHelper.IsDebugMode(Settings) ? errorMessage : (ErrorMessageHelper.GetSummarizeError(Settings) ?? "总结失败，请稍后再试");
                }

                var responseString = await response.Content.ReadAsStringAsync();
                var responseObject = JObject.Parse(responseString);
                return responseObject["choices"][0]["message"]["content"].ToString();
            }
        }

        private async Task<string> SummarizeGemini(string systemPrompt, string userContent, Setting.GeminiNodeSetting node)
        {
            var requestData = new
            {
                system_instruction = new { parts = new[] { new { text = systemPrompt } } },
                contents = new[] { new { parts = new[] { new { text = userContent } } } }
            };

            var content = new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json");

            var apiEndpoint = BuildGeminiEndpoint(node.Url, node.Model, false);

            using (var client = GetClient())
            {
                AddAuthHeaders(client, node);
                var response = await client.PostAsync(apiEndpoint, content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini");
                    Logger.Log($"Gemini Summarize 错误: {errorMessage}");
                    return ErrorMessageHelper.IsDebugMode(Settings) ? errorMessage : (ErrorMessageHelper.GetSummarizeError(Settings) ?? "总结失败，请稍后再试。");
                }

                var responseString = await response.Content.ReadAsStringAsync();
                var responseObject = JObject.Parse(responseString);
                return responseObject["candidates"][0]["content"]["parts"][0]["text"].ToString();
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
                if (node is null)
                {
                    var noNodeError = "NoEnabledGeminiNodes".Translate();
                    if (string.IsNullOrEmpty(noNodeError) || noNodeError == "NoEnabledGeminiNodes")
                    {
                        noNodeError = "没有启用的 Gemini 节点，请在设置中启用至少一个节点";
                    }
                    throw new System.Exception(noNodeError);
                }

                string requestUrl;
                if (node.UseOpenAIAuth)
                {
                    requestUrl = BuildOpenAIModelsEndpoint(node.Url);
                }
                else
                {
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
                }

                using (var client = GetClient())
                {
                    if (client.DefaultRequestHeaders.TryGetValues("User-Agent", out _))
                    {
                        client.DefaultRequestHeaders.Remove("User-Agent");
                    }
                    client.DefaultRequestHeaders.Add("User-Agent", "Lolisi_VPet_LLMAPI");
                    AddAuthHeaders(client, node);

                    var response = client.GetAsync(requestUrl).Result;

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorMessage = ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini").Result;
                        throw new System.Exception(errorMessage);
                    }

                    var responseString = response.Content.ReadAsStringAsync().Result;
                    var models = new List<string>();
                    try
                    {
                        var jsonToken = JToken.Parse(responseString);
                        if (jsonToken is JObject responseObject)
                        {
                            JArray? modelsArray = null;

                            if (node.UseOpenAIAuth)
                            {
                                if (responseObject["data"] is JArray openaiModelsArray)
                                {
                                    modelsArray = openaiModelsArray;
                                }
                            }
                            else
                            {
                                if (responseObject["models"] is JArray googleModelsArray)
                                {
                                    modelsArray = googleModelsArray;
                                }
                                else if (responseObject["data"] is JArray openaiModelsArray)
                                {
                                    modelsArray = openaiModelsArray;
                                }
                            }

                            if (modelsArray != null)
                            {
                                foreach (var model in modelsArray)
                                {
                                    var modelName = model["id"]?.ToString() ?? model["name"]?.ToString();
                                    if (!string.IsNullOrEmpty(modelName))
                                    {
                                        models.Add(modelName.Replace("models/", ""));
                                    }
                                }
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
                        var parseError = ErrorMessageHelper.IsDebugMode(Settings)
                            ? $"Failed to parse JSON response: {responseString.Substring(0, System.Math.Min(responseString.Length, 100))}"
                            : "获取模型列表失败，服务器返回了无效的响应格式。";
                        throw new System.Exception(parseError);
                    }
                    return models;
                }
            }
            catch (System.Exception ex) when (!(ex.Message.Contains("API") || ex.Message.Contains("获取模型") || ex.Message.Contains("没有启用")))
            {
                var errorMessage = ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Gemini");
                throw new System.Exception(errorMessage);
            }
        }

        public new List<string> GetModels()
        {
            return new List<string>();
        }

        private string BuildOpenAIEndpoint(string url)
        {
            string apiUrl = url;
            if (!apiUrl.Contains("/chat/completions"))
            {
                var baseUrl = apiUrl.TrimEnd('/');
                if (!baseUrl.EndsWith("/v1") && !baseUrl.EndsWith("/v1/"))
                {
                    baseUrl += "/v1";
                }
                apiUrl = baseUrl.TrimEnd('/') + "/chat/completions";
            }
            return apiUrl;
        }

        private string BuildOpenAIModelsEndpoint(string url)
        {
            string modelsUrl = url;
            if (modelsUrl.Contains("/chat/completions"))
            {
                modelsUrl = modelsUrl.Replace("/chat/completions", "/models");
            }
            else
            {
                var baseUrl = modelsUrl.TrimEnd('/');
                if (!baseUrl.EndsWith("/v1") && !baseUrl.EndsWith("/v1/"))
                {
                    baseUrl += "/v1";
                }
                modelsUrl = baseUrl.TrimEnd('/') + "/models";
            }
            return modelsUrl;
        }

        private string BuildGeminiEndpoint(string url, string modelName, bool enableStreaming)
        {
            var baseUrl = url.TrimEnd('/');
            if (!baseUrl.Contains("/v1") && !baseUrl.Contains("/v1beta"))
            {
                baseUrl += "/v1beta";
            }
            return enableStreaming
                ? $"{baseUrl}/models/{modelName}:streamGenerateContent?alt=sse"
                : $"{baseUrl}/models/{modelName}:generateContent";
        }

        private void AddAuthHeaders(HttpClient client, Setting.GeminiNodeSetting node)
        {
            var rotatedKey = GetCurrentApiKeyFromNode(node.ApiKey);
            if (!string.IsNullOrEmpty(rotatedKey))
            {
                if (node.UseOpenAIAuth)
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {rotatedKey}");
                }
                else
                {
                    client.DefaultRequestHeaders.Add("x-goog-api-key", rotatedKey);
                }
            }
        }
    }
}
