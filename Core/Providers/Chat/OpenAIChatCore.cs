using LinePutScript.Localization.WPF;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using VPet_Simulator.Windows.Interface;
using ErrorHelper = global::VPetLLM.Utils.System.ErrorMessageHelper;
using SystemLogger = global::VPetLLM.Utils.System.Logger;

namespace VPetLLM.Core.Providers.Chat
{
    public class OpenAIChatCore : ChatCoreBase
    {
        public override string Name => "OpenAI";
        private readonly Setting.OpenAISetting _openAISetting;
        private readonly Setting _setting;

        private readonly Random _random = new Random();
        // 单次请求上下文缓存：避免同一请求中多次随机选择不同节点
        private Setting.OpenAINodeSetting? _currentNodeContext;

        public OpenAIChatCore(Setting.OpenAINodeSetting openAINodeSetting, Setting setting, IMainWindow mainWindow, ActionProcessor actionProcessor)
            : base(setting, mainWindow, actionProcessor)
        {
            // 将OpenAINodeSetting转换为OpenAISetting
            _openAISetting = new Setting.OpenAISetting
            {
                ApiKey = openAINodeSetting.ApiKey,
                Model = openAINodeSetting.Model,
                Url = openAINodeSetting.Url,
                Temperature = openAINodeSetting.Temperature,
                MaxTokens = openAINodeSetting.MaxTokens,
                EnableAdvanced = openAINodeSetting.EnableAdvanced,
                Enabled = openAINodeSetting.Enabled,
                Name = openAINodeSetting.Name,
                OpenAINodes = new List<Setting.OpenAINodeSetting> { openAINodeSetting }
            };
            _setting = setting;
        }

        public OpenAIChatCore(Setting.OpenAISetting openAISetting, Setting setting, IMainWindow mainWindow, ActionProcessor actionProcessor)
            : base(setting, mainWindow, actionProcessor)
        {
            _openAISetting = openAISetting;
            _setting = setting;
        }

        /// <summary>
        /// 包装ErrorMessageHelper.HandleHttpResponseError调用以避免类型冲突
        /// </summary>
        private static async Task<string> HandleHttpError(HttpResponseMessage response, Setting settings, string providerName)
        {
            var statusCode = response.StatusCode;
            var rawError = await response.Content.ReadAsStringAsync();

            SystemLogger.Log($"{providerName} API 错误: {(int)statusCode} {statusCode} - {rawError}");

            // 如果是调试模式，返回详细的原始错误
            if (ErrorHelper.IsDebugMode(settings))
            {
                return $"{providerName} API 错误 [{(int)statusCode} {statusCode}]: {rawError}";
            }

            return ErrorHelper.GetFriendlyHttpError(statusCode, rawError, settings);
        }

        /// <summary>
        /// 包装ErrorMessageHelper.HandleHttpResponseError的同步调用以避免类型冲突
        /// </summary>
        private static string HandleHttpErrorSync(HttpResponseMessage response, Setting settings, string providerName)
        {
            var statusCode = response.StatusCode;
            var rawError = response.Content.ReadAsStringAsync().Result;

            SystemLogger.Log($"{providerName} API 错误: {(int)statusCode} {statusCode} - {rawError}");

            // 如果是调试模式，返回详细的原始错误
            if (ErrorHelper.IsDebugMode(settings))
            {
                return $"{providerName} API 错误 [{(int)statusCode} {statusCode}]: {rawError}";
            }

            return ErrorHelper.GetFriendlyHttpError(statusCode, rawError, settings);
        }

        /// <summary>
        /// 获取当前节点，使用集中式节点选择逻辑
        /// </summary>
        /// <returns>当前选中的节点，如果没有启用的节点则返回 null</returns>
        private Setting.OpenAINodeSetting? GetCurrentNode()
        {
            // 若存在单次请求的缓存节点，则优先返回（不清空，保持请求一致性）
            if (_currentNodeContext is not null)
            {
                return _currentNodeContext;
            }

            // 使用集中式节点选择逻辑
            var node = _openAISetting.GetCurrentOpenAISetting();
            if (node is not null)
            {
                // 缓存本次选中的节点，供同一请求中后续调用复用
                _currentNodeContext = node;
            }
            return node;
        }

        /// <summary>
        /// 清除当前请求的节点缓存，用于容灾切换到下一个节点
        /// </summary>
        private void ClearNodeContext()
        {
            _currentNodeContext = null;
        }

        private string GetCurrentApiKey(Setting.OpenAINodeSetting? node)
        {
            if (string.IsNullOrWhiteSpace(node?.ApiKey))
                return string.Empty;

            var apiKeys = node.ApiKey
                .Split(new[] { ',', ';', '|', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct()
                .ToList();

            if (apiKeys.Count == 0)
                return string.Empty;
            if (apiKeys.Count == 1)
                return apiKeys[0];

            // 渠道内多 key 自动随机（不受负载均衡开关影响）
            return apiKeys[_random.Next(apiKeys.Count)];
        }

        private (string? apiUrl, string apiKey, Setting.OpenAINodeSetting? node) GetCurrentEndpoint()
        {
            var currentNode = GetCurrentNode();
            if (currentNode is null)
            {
                return (null, string.Empty, null);
            }

            var currentApiKey = GetCurrentApiKey(currentNode);

            string apiUrl = currentNode.Url;
            if (!apiUrl.Contains("/chat/completions"))
            {
                var baseUrl = apiUrl.TrimEnd('/');
                if (!baseUrl.EndsWith("/v1") && !baseUrl.EndsWith("/v1/"))
                {
                    baseUrl += "/v1";
                }
                apiUrl = baseUrl.TrimEnd('/') + "/chat/completions";
            }

            return (apiUrl, currentApiKey, currentNode);
        }

        public override Task<string> Chat(string prompt)
        {
            return Chat(prompt, false);
        }

        /// <summary>
        /// 发送带图像的多模态消息
        /// </summary>
        /// <param name="prompt">文本提示</param>
        /// <param name="imageData">图像数据</param>
        /// <returns>响应内容</returns>
        public override async Task<string> ChatWithImage(string prompt, byte[] imageData)
        {
            // Handle conversation turn for record weight decrement
            OnConversationTurn();

            if (!Settings.KeepContext)
            {
                ClearContext();
            }

            // 清除上一次请求的节点缓存
            ClearNodeContext();

            // 获取当前节点和API Key
            var (apiUrl, apiKey, currentNode) = GetCurrentEndpoint();

            // 检查是否有可用节点
            if (currentNode is null || apiUrl is null)
            {
                var noNodeError = "没有启用的OpenAI 节点，请在设置中启用至少一个节点";
                SystemLogger.Log($"OpenAI ChatWithImage 错误: {noNodeError}");
                ResponseHandler?.Invoke(noNodeError);
                return "";
            }

            // 检查视觉能力是否启用
            if (!currentNode.EnableVision)
            {
                var visionError = "当前节点未启用视觉能力，请在设置中启用EnableVision";
                SystemLogger.Log($"OpenAI ChatWithImage 错误: {visionError}");
                ResponseHandler?.Invoke(visionError);
                return "";
            }

            SystemLogger.Log($"OpenAI ChatWithImage: 发送多模态消息，图像大小: {imageData.Length} bytes");

            // 构建多模态消息内容
            var base64Image = Convert.ToBase64String(imageData);
            var userContent = new object[]
            {
                new { type = "text", text = prompt },
                new { type = "image_url", image_url = new { url = $"data:image/png;base64,{base64Image}" } }
            };

            // 构建历史消息（不包含图像）
            List<Message> history = GetCoreHistory();

            // 构建请求消息列表
            var requestMessages = new List<object>();
            foreach (var msg in history)
            {
                requestMessages.Add(new { role = msg.Role, content = msg.DisplayContent });
            }
            // 添加带图像的用户消息
            requestMessages.Add(new { role = "user", content = userContent });

            object data;
            if (_openAISetting.EnableAdvanced)
            {
                data = new
                {
                    model = currentNode.Model,
                    messages = requestMessages,
                    temperature = _openAISetting.Temperature,
                    max_tokens = _openAISetting.MaxTokens,
                    stream = currentNode.EnableStreaming
                };
            }
            else
            {
                data = new
                {
                    model = currentNode.Model,
                    messages = requestMessages,
                    max_tokens = 4096,
                    stream = currentNode.EnableStreaming
                };
            }

            var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
            string message;

            try
            {
                using (var client = GetClient())
                {
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                    }

                    if (currentNode.EnableStreaming)
                    {
                        SystemLogger.Log("OpenAI ChatWithImage: 使用流式传输模式");
                        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                        {
                            Content = content
                        };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await HandleHttpError(response, Settings, "OpenAI");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }

                        var fullMessage = new StringBuilder();
                        var streamProcessor = new StreamingCommandProcessor((cmd) =>
                        {
                            SystemLogger.Log($"OpenAI流式: 检测到完整命令: {cmd}");
                            ResponseHandler?.Invoke(cmd);
                        }, VPetLLM.Instance);

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
                        streamProcessor.FlushBatch();
                    }
                    else
                    {
                        SystemLogger.Log("OpenAI ChatWithImage: 使用非流式传输模式");
                        var response = await client.PostAsync(apiUrl, content);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await HandleHttpError(response, Settings, "OpenAI");
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
                var errorMessage = ErrorHelper.GetFriendlyExceptionError(ex, Settings, "OpenAI");
                SystemLogger.Log($"OpenAI ChatWithImage 异常: {ex.Message}");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }

            // 保存历史记录（包含图像数据用于上下文编辑器显示）
            if (Settings.KeepContext)
            {
                var userMessage = CreateUserMessage($"[图像] {prompt}");
                if (userMessage is not null)
                {
                    // 保存图像数据到消息对象（用于上下文编辑器显示）
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
            // Handle conversation turn for record weight decrement
            OnConversationTurn();

            if (!Settings.KeepContext)
            {
                ClearContext();
            }

            // 清除上一次请求的节点缓存，确保每次新请求都重新选择节点
            ClearNodeContext();

            // 临时构建包含当前用户消息的历史记录（用于API请求），但不立即保存到数据库
            // 使用 CreateUserMessage 自动设置时间戳和状态信息
            var tempUserMessage = CreateUserMessage(prompt);

            // 获取当前节点和API Key
            var (apiUrl, apiKey, currentNode) = GetCurrentEndpoint();

            // 检查是否有可用节点
            if (currentNode is null || apiUrl is null)
            {
                var noNodeError = "NoEnabledOpenAINodes".Translate();
                if (string.IsNullOrEmpty(noNodeError) || noNodeError == "NoEnabledOpenAINodes")
                {
                    noNodeError = "没有启用的OpenAI 节点，请在设置中启用至少一个节点";
                }
                SystemLogger.Log($"OpenAI Chat 错误: {noNodeError}");
                ResponseHandler?.Invoke(noNodeError);
                return "";
            }

            // 调试模式：当角色设定包含 VPetLLM_DeBug 时，记录当前调用的节点信息
            if (Settings?.Role?.Contains("VPetLLM_DeBug") == true)
            {
                SystemLogger.Log($"[DEBUG] OpenAI 当前调用节点: {currentNode.Name}, URL: {currentNode.Url}, Model: {currentNode.Model}");
            }

            // 构建请求数据，根据启用开关决定是否包含高级参数
            List<Message> history = GetCoreHistory();
            // 如果有临时用户消息，添加到历史末尾用于API请求
            if (tempUserMessage is not null)
            {
                history.Add(tempUserMessage);
            }
            // 在添加用户消息后注入重要记录
            history = InjectRecordsIntoHistory(history);

            object data;
            if (_openAISetting.EnableAdvanced)
            {
                data = new
                {
                    model = currentNode.Model,
                    messages = history.Select(m => new { role = m.Role, content = m.DisplayContent }),
                    temperature = _openAISetting.Temperature,
                    max_tokens = _openAISetting.MaxTokens,
                    stream = currentNode.EnableStreaming
                };
            }
            else
            {
                data = new
                {
                    model = currentNode.Model,
                    messages = history.Select(m => new { role = m.Role, content = m.DisplayContent }),
                    stream = currentNode.EnableStreaming
                };
            }
            var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");

            string message;
            try
            {
                using (var client = GetClient())
                {
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                    }

                    if (currentNode.EnableStreaming)
                    {
                        // 流式传输模式
                        SystemLogger.Log("OpenAI: 使用流式传输模式");
                        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                        {
                            Content = content
                        };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await HandleHttpError(response, Settings, "OpenAI");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }

                        var fullMessage = new StringBuilder();
                        var streamProcessor = new StreamingCommandProcessor((cmd) =>
                        {
                            // 当检测到完整命令时，立即处理（流式模式下逐个命令处理）
                            SystemLogger.Log($"OpenAI流式: 检测到完整命令: {cmd}");
                            ResponseHandler?.Invoke(cmd);
                        }, VPetLLM.Instance);

                        // 配置批处理设置
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
                                        // 将新片段传递给流式处理器，检测完整命令
                                        streamProcessor.AddChunk(delta);
                                        // 通知流式文本更新（用于显示）
                                        StreamingChunkHandler?.Invoke(delta);
                                        var usage = chunk["usage"]?["total_tokens"]?.ToObject<int>() ?? 0;
                                        TotalUsage += usage;
                                    }
                                }
                                catch
                                {
                                    // 忽略解析错误，继续处理下一行
                                }
                            }
                        }
                        message = fullMessage.ToString();

                        // 刷新批处理器，确保所有待处理命令都被处理
                        streamProcessor.FlushBatch();

                        SystemLogger.Log("OpenAI流式: 流式传输完成，总消息长度 {0},总Token用量：{1}".Translate(message.Length, TotalUsage));
                        // 注意：流式模式下不再调用 ResponseHandler，因为已经通过 streamProcessor 逐个处理了
                    }
                    else
                    {
                        // 非流式传输模式
                        SystemLogger.Log("OpenAI: 使用非流式传输模式");
                        var response = await client.PostAsync(apiUrl, content);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await HandleHttpError(response, Settings, "OpenAI");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }

                        var responseString = await response.Content.ReadAsStringAsync();
                        var responseObject = JObject.Parse(responseString);
                        message = responseObject["choices"][0]["message"]["content"].ToString();
                        var tokenUsage = responseObject["usage"]["total_tokens"].ToString();
                        SystemLogger.Log("OpenAI非流式: 收到完整消息，长度 {0}，Token用量：{1}".Translate(message.Length, tokenUsage));
                        // 非流式模式下，一次性处理完整消息
                        ResponseHandler?.Invoke(message);
                    }
                }
            }
            catch (Exception ex)
            {
                var errorMessage = ErrorHelper.GetFriendlyExceptionError(ex, Settings, "OpenAI");
                SystemLogger.Log($"OpenAI Chat 异常: {ex.Message}");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }

            // API调用成功后，才将用户消息和助手回复保存到历史记录
            if (Settings.KeepContext)
            {
                // 先保存用户消息
                if (tempUserMessage is not null)
                {
                    await HistoryManager.AddMessage(tempUserMessage);
                }
                // 再保存助手回复
                await HistoryManager.AddMessage(new Message { Role = "assistant", Content = message });
                // 保存历史记录
                SaveHistory();
            }
            return "";
        }

        public override async Task<string> Summarize(string systemPrompt, string userContent)
        {
            try
            {
                var messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userContent }
                };

                // 清除节点缓存，确保重新选择节点
                ClearNodeContext();

                // 获取当前节点和API Key
                var (apiUrl, apiKey, currentNode) = GetCurrentEndpoint();

                // 检查是否有可用节点
                if (currentNode is null || apiUrl is null)
                {
                    var noNodeError = "NoEnabledOpenAINodes".Translate();
                    if (string.IsNullOrEmpty(noNodeError) || noNodeError == "NoEnabledOpenAINodes")
                    {
                        noNodeError = "没有启用的OpenAI 节点，请在设置中启用至少一个节点";
                    }
                    SystemLogger.Log($"OpenAI Summarize 错误: {noNodeError}");
                    return ErrorHelper.IsDebugMode(Settings) ? noNodeError : (ErrorHelper.GetSummarizeError(Settings) ?? "总结失败，请稍后再试");
                }

                object data;
                if (_openAISetting.EnableAdvanced)
                {
                    data = new
                    {
                        model = currentNode.Model,
                        messages = messages,
                        temperature = _openAISetting.Temperature,
                        max_tokens = _openAISetting.MaxTokens
                    };
                }
                else
                {
                    data = new
                    {
                        model = currentNode.Model,
                        messages = messages
                    };
                }

                var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");

                using (var client = GetClient())
                {
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                    }
                    var response = await client.PostAsync(apiUrl, content);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorMessage = await HandleHttpError(response, Settings, "OpenAI");
                        SystemLogger.Log($"OpenAI Summarize 错误: {errorMessage}");
                        return ErrorHelper.IsDebugMode(Settings) ? errorMessage : (ErrorHelper.GetSummarizeError(Settings) ?? "总结失败，请稍后再试");
                    }

                    var responseString = await response.Content.ReadAsStringAsync();
                    var responseObject = JObject.Parse(responseString);
                    return responseObject["choices"][0]["message"]["content"].ToString();
                }
            }
            catch (Exception ex)
            {
                SystemLogger.Log($"OpenAI Summarize 异常: {ex.Message}");
                return ErrorHelper.IsDebugMode(Settings)
                    ? $"OpenAI Summarize 异常: {ex.Message}\n{ex.StackTrace}"
                    : (ErrorHelper.GetSummarizeError(Settings) ?? "总结功能暂时不可用，请稍后再试");
            }
        }

        private List<Message> GetCoreHistory(bool injectRecords = false)
        {
            var history = new List<Message>
            {
                new Message { Role = "system", Content = GetSystemMessage() }
            };
            history.AddRange(HistoryManager.GetHistory().Skip(Math.Max(0, HistoryManager.GetHistory().Count - _setting.HistoryCompressionThreshold)));

            // Inject important records into history (only when explicitly requested, after user message is added)
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
                // 清除节点缓存，确保重新选择节点
                ClearNodeContext();

                // 获取当前节点和API Key
                var (apiUrl, apiKey, currentNode) = GetCurrentEndpoint();

                // 检查是否有可用节点
                if (currentNode is null || apiUrl is null)
                {
                    var noNodeError = "NoEnabledOpenAINodes".Translate();
                    if (string.IsNullOrEmpty(noNodeError) || noNodeError == "NoEnabledOpenAINodes")
                    {
                        noNodeError = "没有启用的OpenAI 节点，请在设置中启用至少一个节点";
                    }
                    throw new System.Exception(noNodeError);
                }

                string modelsUrl = apiUrl;
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

                var url = new System.Uri(new System.Uri(modelsUrl), "");
                using (var client = GetClient())
                {
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                    }
                    var response = client.GetAsync(url).Result;

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorMessage = HandleHttpErrorSync(response, Settings, "OpenAI");
                        throw new System.Exception(errorMessage);
                    }

                    var responseString = response.Content.ReadAsStringAsync().Result;
                    JObject responseObject;
                    try
                    {
                        responseObject = JObject.Parse(responseString);
                    }
                    catch (JsonReaderException)
                    {
                        var parseError = ErrorHelper.IsDebugMode(Settings)
                            ? $"Failed to parse JSON response: {responseString.Substring(0, System.Math.Min(responseString.Length, 100))}"
                            : "获取模型列表失败，服务器返回了无效的响应格式";
                        throw new System.Exception(parseError);
                    }
                    var models = new List<string>();
                    foreach (var model in responseObject["data"])
                    {
                        models.Add(model["id"].ToString());
                    }
                    return models;
                }
            }
            catch (System.Exception ex) when (!(ex.Message.Contains("API") || ex.Message.Contains("获取模型") || ex.Message.Contains("没有启用")))
            {
                var errorMessage = ErrorHelper.GetFriendlyExceptionError(ex, Settings, "OpenAI");
                throw new System.Exception(errorMessage);
            }
        }

        public new List<string> GetModels()
        {
            return new List<string>();
        }
    }
}