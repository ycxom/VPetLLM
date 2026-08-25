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

        /// <summary>
        /// 指定单个节点构造（与 OpenAIChatCore / LMStudioChatCore 同形）。
        /// 前置多模态要把图发给用户挑定的那个视觉节点，若传整份 GeminiSetting，
        /// GetCurrentGeminiSetting() 会按主聊天的轮换规则另选一个，用户的选择就失效了。
        /// </summary>
        public GeminiChatCore(Setting.GeminiNodeSetting geminiNodeSetting, Setting setting, IMainWindow mainWindow, ActionProcessor actionProcessor)
            : base(setting, mainWindow, actionProcessor)
        {
            _geminiSetting = new Setting.GeminiSetting
            {
                ApiKey = geminiNodeSetting.ApiKey,
                Model = geminiNodeSetting.Model,
                Url = geminiNodeSetting.Url,
                Temperature = geminiNodeSetting.Temperature,
                MaxTokens = geminiNodeSetting.MaxTokens,
                EnableAdvanced = geminiNodeSetting.EnableAdvanced,
                EnableStreaming = geminiNodeSetting.EnableStreaming,
                GeminiNodes = new List<Setting.GeminiNodeSetting> { geminiNodeSetting }
            };
            _setting = setting;
        }

        protected override Setting.ChannelProxyMode GetChannelProxyMode()
        {
            // 注意：本方法可能在**基类构造函数**执行期间被调到
            // （ChatCoreBase ctor → CreateEmbeddingService → NewEmbeddingHttpClient →
            //  CreateHttpClientHandler → GetProxy → 本虚方法），
            // 那时派生类的 _xxxSetting 还没赋值（派生 ctor 体在 base(...) 之后才跑）。
            // 所以这里必须容忍字段为 null，否则整个 EmbeddingService 会被一个 NRE 静默干掉。
            var node = _geminiSetting?.GetCurrentGeminiSetting();
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

        public override async Task<string> ChatWithImages(string prompt, IReadOnlyList<byte[]> images)
        {
            LastCallFailed = false;
            // 钳制尺寸，避免过大的图片被目标服务端拒收
            // 逐张钳制尺寸，避免过大的图片被目标服务端拒收；空图直接剔除
            images = images.Select(i => Utils.Common.ImageDownscaler.ClampToMaxDimension(i)!)
                           .Where(i => i is not null && i.Length > 0).ToList();
            if (images.Count == 0) return "";
            // 历史只有一个图像槽位，先留第一张（见 README 已知限制）
            var imageData = images[0];

            OnConversationTurn();

            var node = _geminiSetting.GetCurrentGeminiSetting("Chat");
            if (node is null)
            {
                var noNodeError = "没有启用的 Gemini 节点，请在设置中启用至少一个节点";
                Logger.Log($"Gemini ChatWithImage 错误: {noNodeError}");
                ReportFailure(noNodeError);
                return "";
            }

            if (!node.EnableVision)
            {
                var visionError = "当前节点未启用视觉能力，请在设置中启用 EnableVision";
                Logger.Log($"Gemini ChatWithImage 错误: {visionError}");
                ReportFailure(visionError);
                return "";
            }

            Logger.Log($"Gemini ChatWithImage: 发送多模态消息，图像大小: {DescribeImages(images)}");

            // 提示词要说"本节点是否开启工具"，判断必须跟着这一轮的节点走
            CurrentNodeToolsEnabled = global::VPetLLM.Core.Tools.NativeToolSession.WillAttachTools(Settings, node.EnableToolCall);
            List<Message> history = await GetCoreHistoryAsync(userQuery: prompt);

            if (node.UseOpenAIAuth)
            {
                return await ChatWithImageOpenAI(prompt, images, history, node);
            }
            else
            {
                return await ChatWithImageGemini(prompt, images, history, node);
            }
        }

        private async Task<string> ChatWithImageOpenAI(string prompt, IReadOnlyList<byte[]> images, List<Message> history, Setting.GeminiNodeSetting node)
        {
            LastCallFailed = false;
            var imageData = images[0];
            var userContent = BuildMultimodalContent(prompt, images);

            var requestMessages = new List<object>();
            foreach (var msg in history)
            {
                requestMessages.Add(new { role = msg.Role, content = msg.DisplayContent });
            }
            requestMessages.Add(new { role = "user", content = userContent });

            var useStreaming = UseStreaming(node.EnableStreaming);

            object data;
            if (node.EnableAdvanced)
            {
                data = new
                {
                    model = node.Model,
                    messages = requestMessages,
                    temperature = node.Temperature,
                    max_tokens = node.MaxTokens,
                    stream = useStreaming
                };
            }
            else
            {
                data = new
                {
                    model = node.Model,
                    messages = requestMessages,
                    max_tokens = 4096,
                    stream = useStreaming
                };
            }


            // 挂原生工具（OpenAI 兼容格式）
            var toolSession = global::VPetLLM.Core.Tools.NativeToolSession.TryCreate(Settings, node.EnableToolCall);
            var toolPayload = JObject.FromObject(data);
            if (toolSession is not null)
            {
                toolSession.AttachOpenAiTools(toolPayload);
                // 工具循环强制非流式，见 NativeToolLoop 的说明
                toolPayload["stream"] = false;
            }
            var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");

            var apiUrl = BuildOpenAIEndpoint(node.Url);
            global::VPetLLM.Core.Tools.NativeToolLoopResult? toolLoop = null;

            string message;
            try
            {
                using (var client = GetClient())
                {
                    AddAuthHeaders(client, node);

                    if (toolSession is not null)
                    {
                        var loop = await global::VPetLLM.Core.Tools.NativeToolLoop.RunOpenAiAsync(
                            toolPayload, toolSession,
                            async body =>
                            {
                                var roundContent = new StringContent(
                                    body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                                var roundResponse = await client.PostAsync(apiUrl, roundContent, InterruptManager.Token);
                                if (!roundResponse.IsSuccessStatusCode)
                                {
                                    var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(roundResponse, Settings, "Gemini");
                                    ResponseHandler?.Invoke(errorMessage);
                                    return null;
                                }
                                return JObject.Parse(await roundResponse.Content.ReadAsStringAsync());
                            });

                        if (!loop.Success) return "";
                        toolLoop = loop;

                        message = loop.Message;
                        if (loop.HitLimit)
                        {
                            Logger.Log("Gemini ChatWithImage: 工具调用达到轮次上限，本轮不再继续");
                        }
                        ResponseHandler?.Invoke(message);
                    }
                    else if (useStreaming)
                    {
                        Logger.Log("Gemini (OpenAI兼容): 使用流式传输模式");
                        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl) { Content = content };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, InterruptManager.Token);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini");
                            ReportFailure(errorMessage);
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
                            while ((line = await ReadStreamLineAsync(reader)) is not null && !InterruptManager.IsInterrupted)
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
                        var response = await client.PostAsync(apiUrl, content, InterruptManager.Token);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini");
                            ReportFailure(errorMessage);
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
                // 用户中断：取消引发的异常不是故障，既不重试也不弹错误提示
                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("Chat 请求已被用户中断");
                    return "";
                }

                var errorMessage = ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Gemini");
                Logger.Log($"Gemini ChatWithImage 异常: {ex.Message}");
                ReportFailure(errorMessage);
                return "";
            }

            if (Settings?.KeepContext ?? true)
            {
                var userMessage = CreateUserMessage($"[图像] {prompt}");
                if (userMessage is not null)
                {
                    userMessage.ImageData = imageData;
                    await HistoryManager.AddMessage(userMessage);
                }
                await PersistToolCallTraceAsync(toolLoop);
                await HistoryManager.AddMessage(new Message { Role = "assistant", Content = AppendInterruptMarker(message) });
                SaveHistory();
                TriggerOverflowCheckAfterSuccess();
            }

            return "";
        }

        private async Task<string> ChatWithImageGemini(string prompt, IReadOnlyList<byte[]> images, List<Message> history, Setting.GeminiNodeSetting node)
        {
            LastCallFailed = false;
            var contents = new List<object>();
            foreach (var msg in history.Where(m => m.Role != "system"))
            {
                contents.Add(new { role = msg.Role == "assistant" ? "model" : msg.Role, parts = new[] { new { text = msg.DisplayContent } } });
            }

            var imageData = images[0];

            // Gemini 原生格式：一个 parts 数组里放一段文本 + N 个 inline_data
            var parts = new List<object> { new { text = prompt } };
            foreach (var image in images)
            {
                parts.Add(new { inline_data = new { mime_type = "image/png", data = Convert.ToBase64String(image) } });
            }

            contents.Add(new { role = "user", parts = parts.ToArray() });

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

            // 挂原生工具（Gemini 原生格式）
            var toolSession = global::VPetLLM.Core.Tools.NativeToolSession.TryCreate(Settings, node.EnableToolCall);
            var toolPayload = JObject.FromObject(requestData);
            toolSession?.AttachGeminiTools(toolPayload);

            var content = new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json");

            var useStreaming = UseStreaming(node.EnableStreaming);
            // 工具循环强制非流式，端点也要跟着按非流式选，否则会打到 streamGenerateContent
            var apiEndpoint = BuildGeminiEndpoint(node.Url, node.Model, useStreaming && toolSession is null);
            global::VPetLLM.Core.Tools.NativeToolLoopResult? toolLoop = null;

            string message;
            try
            {
                using (var client = GetClient())
                {
                    AddAuthHeaders(client, node);

                    if (toolSession is not null)
                    {
                        var loop = await global::VPetLLM.Core.Tools.NativeToolLoop.RunGeminiAsync(
                            toolPayload, toolSession,
                            async body =>
                            {
                                var roundContent = new StringContent(
                                    body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                                var roundResponse = await client.PostAsync(apiEndpoint, roundContent, InterruptManager.Token);
                                if (!roundResponse.IsSuccessStatusCode)
                                {
                                    var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(roundResponse, Settings, "Gemini");
                                    ResponseHandler?.Invoke(errorMessage);
                                    return null;
                                }
                                return JObject.Parse(await roundResponse.Content.ReadAsStringAsync());
                            });

                        if (!loop.Success) return "";
                        toolLoop = loop;

                        message = loop.Message;
                        if (loop.HitLimit)
                        {
                            Logger.Log("Gemini ChatWithImage: 工具调用达到轮次上限，本轮不再继续");
                        }
                        ResponseHandler?.Invoke(message);
                    }
                    else if (useStreaming)
                    {
                        Logger.Log("Gemini ChatWithImage: 使用流式传输模式");
                        var request = new HttpRequestMessage(HttpMethod.Post, apiEndpoint) { Content = content };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, InterruptManager.Token);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini");
                            ReportFailure(errorMessage);
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
                            while ((line = await ReadStreamLineAsync(reader)) is not null && !InterruptManager.IsInterrupted)
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
                        var response = await client.PostAsync(apiEndpoint, content, InterruptManager.Token);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Gemini");
                            ReportFailure(errorMessage);
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
                // 用户中断：取消引发的异常不是故障，既不重试也不弹错误提示
                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("Chat 请求已被用户中断");
                    return "";
                }

                var errorMessage = ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Gemini");
                Logger.Log($"Gemini ChatWithImage 异常: {ex.Message}");
                ReportFailure(errorMessage);
                return "";
            }

            if (Settings?.KeepContext ?? true)
            {
                var userMessage = CreateUserMessage($"[图像] {prompt}");
                if (userMessage is not null)
                {
                    userMessage.ImageData = imageData;
                    await HistoryManager.AddMessage(userMessage);
                }
                await PersistToolCallTraceAsync(toolLoop);
                await HistoryManager.AddMessage(new Message { Role = "assistant", Content = AppendInterruptMarker(message) });
                SaveHistory();
                TriggerOverflowCheckAfterSuccess();
            }

            return "";
        }

        public override async Task<string> Chat(string prompt, bool isRetry = false)
        {
            OnConversationTurn();

            var tempUserMessage = CreateUserMessage(prompt);

            // 节点必须在构建历史**之前**选好：系统提示词里那句"本节点已开启原生工具调用"
            // 得按这一轮真正要发往的节点来写。GetCurrentGeminiSetting 会推进负载均衡的
            // 轮换下标，所以整轮只能调这一次。
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

            CurrentNodeToolsEnabled = global::VPetLLM.Core.Tools.NativeToolSession.WillAttachTools(Settings, node.EnableToolCall);
            List<Message> history = await GetCoreHistoryAsync(userQuery: prompt);
            if (tempUserMessage is not null)
            {
                history.Add(tempUserMessage);
            }
            history = InjectRecordsIntoHistory(history);

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

            // 挂原生工具（OpenAI 兼容格式）
            var toolSession = global::VPetLLM.Core.Tools.NativeToolSession.TryCreate(Settings, node.EnableToolCall);
            var toolPayload = JObject.FromObject(data);
            if (toolSession is not null)
            {
                toolSession.AttachOpenAiTools(toolPayload);
                // 工具循环强制非流式，见 NativeToolLoop 的说明
                toolPayload["stream"] = false;
            }
            var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");

            var apiUrl = BuildOpenAIEndpoint(node.Url);
            global::VPetLLM.Core.Tools.NativeToolLoopResult? toolLoop = null;

            string message;
            try
            {
                using (var client = GetClient())
                {
                    AddAuthHeaders(client, node);

                    if (toolSession is not null)
                    {
                        var loop = await global::VPetLLM.Core.Tools.NativeToolLoop.RunOpenAiAsync(
                            toolPayload, toolSession,
                            async body =>
                            {
                                var roundContent = new StringContent(
                                    body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                                var roundResponse = await client.PostAsync(apiUrl, roundContent, InterruptManager.Token);
                                if (!roundResponse.IsSuccessStatusCode)
                                {
                                    var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(roundResponse, Settings, "Gemini");
                                    ResponseHandler?.Invoke(errorMessage);
                                    return null;
                                }
                                return JObject.Parse(await roundResponse.Content.ReadAsStringAsync());
                            });

                        if (!loop.Success) return "";
                        toolLoop = loop;

                        message = loop.Message;
                        if (loop.HitLimit)
                        {
                            Logger.Log("Gemini (OpenAI兼容): 工具调用达到轮次上限，本轮不再继续");
                        }
                        ResponseHandler?.Invoke(message);
                    }
                    else if (node.EnableStreaming)
                    {
                        Logger.Log("Gemini (OpenAI兼容): 使用流式传输模式");
                        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl) { Content = content };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, InterruptManager.Token);

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
                            while ((line = await ReadStreamLineAsync(reader)) is not null && !InterruptManager.IsInterrupted)
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
                        var response = await client.PostAsync(apiUrl, content, InterruptManager.Token);

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
                // 用户中断：取消引发的异常不是故障，既不重试也不弹错误提示
                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("Chat 请求已被用户中断");
                    return "";
                }

                var errorMessage = ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Gemini");
                Logger.Log($"Gemini Chat 异常: {ex.Message}");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }

            if (Settings?.KeepContext ?? true)
            {
                if (tempUserMessage is not null)
                {
                    await HistoryManager.AddMessage(tempUserMessage);
                }
                await PersistToolCallTraceAsync(toolLoop);
                await HistoryManager.AddMessage(new Message { Role = "assistant", Content = AppendInterruptMarker(message) });
                SaveHistory();
                TriggerOverflowCheckAfterSuccess();
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

            var toolSession = global::VPetLLM.Core.Tools.NativeToolSession.TryCreate(Settings, node.EnableToolCall);

            var payload = JObject.FromObject(requestData);
            toolSession?.AttachGeminiTools(payload);

            var content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");

            // 工具循环强制非流式，所以端点也要按非流式来选
            var apiEndpoint = BuildGeminiEndpoint(node.Url, node.Model,
                node.EnableStreaming && toolSession is null);
            global::VPetLLM.Core.Tools.NativeToolLoopResult? toolLoop = null;

            string message;
            try
            {
                using (var client = GetClient())
                {
                    AddAuthHeaders(client, node);

                    if (toolSession is not null)
                    {
                        var loop = await global::VPetLLM.Core.Tools.NativeToolLoop.RunGeminiAsync(
                            payload, toolSession,
                            async body =>
                            {
                                var roundContent = new StringContent(
                                    body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                                var roundResponse = await client.PostAsync(apiEndpoint, roundContent, InterruptManager.Token);
                                if (!roundResponse.IsSuccessStatusCode)
                                {
                                    var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(roundResponse, Settings, "Gemini");
                                    ResponseHandler?.Invoke(errorMessage);
                                    return null;
                                }
                                return JObject.Parse(await roundResponse.Content.ReadAsStringAsync());
                            });

                        if (!loop.Success) return "";
                        toolLoop = loop;

                        message = loop.Message;
                        if (loop.HitLimit)
                        {
                            Logger.Log("Gemini: 工具调用达到轮次上限，本轮不再继续");
                        }
                        ResponseHandler?.Invoke(message);
                    }
                    else if (node.EnableStreaming)
                    {
                        Logger.Log("Gemini: 使用流式传输模式");
                        var request = new HttpRequestMessage(HttpMethod.Post, apiEndpoint) { Content = content };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, InterruptManager.Token);

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
                            while ((line = await ReadStreamLineAsync(reader)) is not null && !InterruptManager.IsInterrupted)
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
                        var response = await client.PostAsync(apiEndpoint, content, InterruptManager.Token);

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
                // 用户中断：取消引发的异常不是故障，既不重试也不弹错误提示
                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("Chat 请求已被用户中断");
                    return "";
                }

                var errorMessage = ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Gemini");
                Logger.Log($"Gemini Chat 异常: {ex.Message}");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }

            if (Settings?.KeepContext ?? true)
            {
                if (tempUserMessage is not null)
                {
                    await HistoryManager.AddMessage(tempUserMessage);
                }
                await PersistToolCallTraceAsync(toolLoop);
                await HistoryManager.AddMessage(new Message { Role = "assistant", Content = AppendInterruptMarker(message) });
                SaveHistory();
                TriggerOverflowCheckAfterSuccess();
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

        private async Task<List<Message>> GetCoreHistoryAsync(bool injectRecords = false, string? userQuery = null)
        {
            var result = await GetCoreHistoryCommonAsync(injectRecords, userQuery);
            return CaptureOverflowCheckData(result);
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
