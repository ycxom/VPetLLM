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
        // 节点级错误转移：记录本次请求已尝试失败的节点索引
        private HashSet<int> _triedNodeIndices = new HashSet<int>();
        // 标记当前 Chat 调用是否为容灾重试（此时应固定使用已选定的转移节点，而非重新轮选）
        private bool _isFailoverRetry;

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
        /// <param name="purpose">用途标识（如 "Chat"、"Compression"），为 null 时不过滤</param>
        /// <returns>当前选中的节点，如果没有启用的节点则返回 null</returns>
        private Setting.OpenAINodeSetting? GetCurrentNode(string? purpose = null)
        {
            // 若存在单次请求的缓存节点，则优先返回（不清空，保持请求一致性）
            if (_currentNodeContext is not null)
            {
                return _currentNodeContext;
            }

            // 使用集中式节点选择逻辑。
            // _openAISetting 可能为 null：基类构造函数会经 GetChannelProxyMode 调到这里，
            // 而派生 ctor 体在 base(...) 之后才跑。不容忍 null 会让 EmbeddingService
            // 被一个 NRE 静默干掉（向量检索整条路就此失效，且日志只有一句"未将对象引用..."）。
            var node = _openAISetting?.GetCurrentOpenAISetting(purpose);
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

        /// <summary>
        /// 获取下一个未尝试的节点用于错误转移（仅在启用负载均衡时）
        /// </summary>
        private Setting.OpenAINodeSetting? GetNextNodeForFailover(string? purpose = null)
        {
            if (!_openAISetting.EnableLoadBalancing)
                return null; // 负载均衡禁用，不进行错误转移

            // 获取当前节点的原始索引
            if (_currentNodeContext == null)
                return null;

            var currentIndex = _openAISetting.OpenAINodes.IndexOf(_currentNodeContext);
            if (currentIndex >= 0)
            {
                _triedNodeIndices.Add(currentIndex);
            }

            // 尝试获取下一个未尝试的节点
            return _openAISetting.GetCurrentOpenAISetting(purpose, _triedNodeIndices);
        }

        /// <summary>
        /// 检测是否使用 Responses API（新 API）格式
        /// 根据 URL 是否包含 /responses 自动判断
        /// </summary>
        private static bool IsResponsesApi(string url)
        {
            return url.IndexOf("/responses", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 从 Responses API 的响应中提取文本内容
        /// Responses API 返回 output[] 数组，需遍历 type:"message" 项的 content[].text
        /// </summary>
        private static string ExtractTextFromResponsesOutput(JObject responseObject)
        {
            var output = responseObject["output"] as JArray;
            if (output == null) return "";

            foreach (var item in output)
            {
                if (item["type"]?.ToString() == "message")
                {
                    var content = item["content"] as JArray;
                    if (content != null)
                    {
                        foreach (var part in content)
                        {
                            if (part["type"]?.ToString() == "output_text")
                            {
                                return part["text"]?.ToString() ?? "";
                            }
                        }
                    }
                }
            }
            return "";
        }

        protected override Setting.ChannelProxyMode GetChannelProxyMode()
        {
            var node = GetCurrentNode();
            if (node != null)
            {
                return node.ProxyMode;
            }
            return Setting.ChannelProxyMode.FollowDefault;
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

        private (string? apiUrl, string apiKey, Setting.OpenAINodeSetting? node) GetCurrentEndpoint(string? purpose = null)
        {
            var currentNode = GetCurrentNode(purpose);
            if (currentNode is null)
            {
                return (null, string.Empty, null);
            }

            var currentApiKey = GetCurrentApiKey(currentNode);

            string apiUrl = currentNode.Url;
            // 如果 URL 已包含具体端点路径（/chat/completions 或 /responses），直接使用
            if (apiUrl.Contains("/chat/completions") || apiUrl.Contains("/responses"))
            {
                // 已是完整端点 URL，无需拼接
            }
            else
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

            // Handle conversation turn for record weight decrement
            OnConversationTurn();

            // 清除上一次请求的节点缓存
            ClearNodeContext();

            // 获取当前节点和API Key
            var (apiUrl, apiKey, currentNode) = GetCurrentEndpoint("Chat");

            // 检查是否有可用节点
            if (currentNode is null || apiUrl is null)
            {
                var noNodeError = "没有启用的OpenAI 节点，请在设置中启用至少一个节点";
                SystemLogger.Log($"OpenAI ChatWithImage 错误: {noNodeError}");
                ReportFailure(noNodeError);
                return "";
            }

            // 检查视觉能力是否启用
            if (!currentNode.EnableVision)
            {
                var visionError = "当前节点未启用视觉能力，请在设置中启用EnableVision";
                SystemLogger.Log($"OpenAI ChatWithImage 错误: {visionError}");
                ReportFailure(visionError);
                return "";
            }



            // 构建多模态消息内容
            var userContent = BuildMultimodalContent(prompt, images);

            // 构建历史消息（不包含图像）
            // 提示词要说"本节点是否开启工具"，判断必须跟着这一轮的节点走
            CurrentNodeToolsEnabled = global::VPetLLM.Core.Tools.NativeToolSession.WillAttachTools(Settings, currentNode.EnableToolCall);
            List<Message> history = await GetCoreHistoryAsync(userQuery: prompt);

            // 构建请求消息列表
            var requestMessages = new List<object>();
            foreach (var msg in history)
            {
                requestMessages.Add(new { role = msg.Role, content = msg.DisplayContent });
            }
            // 添加带图像的用户消息
            requestMessages.Add(new { role = "user", content = userContent });

            var useStreaming = UseStreaming(currentNode.EnableStreaming);

            object data;
            bool useResponses = IsResponsesApi(apiUrl);
            if (_openAISetting.EnableAdvanced)
            {
                if (useResponses)
                {
                    data = new
                    {
                        model = currentNode.Model,
                        input = requestMessages,
                        temperature = _openAISetting.Temperature,
                        max_output_tokens = _openAISetting.MaxTokens,
                        stream = useStreaming
                    };
                }
                else
                {
                    data = new
                    {
                        model = currentNode.Model,
                        messages = requestMessages,
                        temperature = _openAISetting.Temperature,
                        max_tokens = _openAISetting.MaxTokens,
                        stream = useStreaming
                    };
                }
            }
            else
            {
                if (useResponses)
                {
                    data = new
                    {
                        model = currentNode.Model,
                        input = requestMessages,
                        max_output_tokens = 4096,
                        stream = useStreaming
                    };
                }
                else
                {
                    data = new
                    {
                        model = currentNode.Model,
                        messages = requestMessages,
                        max_tokens = 4096,
                        stream = useStreaming
                    };
                }
            }

            // 多模态请求同样可以带工具：模型看完图之后往往正需要调插件（查天气、开应用、搜网页）。
            var toolSession = global::VPetLLM.Core.Tools.NativeToolSession.TryCreate(Settings, currentNode.EnableToolCall);
            var toolPayload = JObject.FromObject(data);
            if (toolSession is not null)
            {
                if (useResponses)
                    toolSession.AttachOpenAiResponsesTools(toolPayload);
                else
                    toolSession.AttachOpenAiTools(toolPayload);
                // 工具循环强制非流式，见 NativeToolLoop 的说明
                toolPayload["stream"] = false;
            }

            var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
            global::VPetLLM.Core.Tools.NativeToolLoopResult? toolLoop = null;
            string message;

            try
            {
                using (var client = GetClient())
                {
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                    }

                    if (toolSession is not null)
                    {
                        Func<JObject, Task<JObject?>> roundSend = async body =>
                        {
                            var roundContent = new StringContent(
                                body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                            var roundResponse = await client.PostAsync(apiUrl, roundContent, InterruptManager.Token);
                            if (!roundResponse.IsSuccessStatusCode)
                            {
                                var errorMessage = await HandleHttpError(roundResponse, Settings, "OpenAI");
                                ReportFailure(errorMessage);
                                return null;
                            }
                            return JObject.Parse(await roundResponse.Content.ReadAsStringAsync());
                        };

                        var loop = useResponses
                            ? await global::VPetLLM.Core.Tools.NativeToolLoop.RunOpenAiResponsesAsync(
                                toolPayload, toolSession, roundSend)
                            : await global::VPetLLM.Core.Tools.NativeToolLoop.RunOpenAiAsync(
                                toolPayload, toolSession, roundSend);

                        if (!loop.Success) return "";
                        toolLoop = loop;

                        message = loop.Message;
                        if (loop.HitLimit)
                        {
                            SystemLogger.Log("OpenAI ChatWithImage: 工具调用达到轮次上限，本轮不再继续");
                        }
                        ResponseHandler?.Invoke(message);
                    }
                    else if (useStreaming)
                    {
                        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                        {
                            Content = content
                        };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, InterruptManager.Token);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await HandleHttpError(response, Settings, "OpenAI");
                            ReportFailure(errorMessage);
                            return "";
                        }

                        var fullMessage = new StringBuilder();
                        var streamProcessor = new StreamingCommandProcessor((cmd) =>
                        {
                            ResponseHandler?.Invoke(cmd);
                        }, VPetLLM.Instance);

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
                                    string? delta = null;
                                    if (useResponses)
                                    {
                                        // Responses API: event type "response.output_text.delta", delta in "delta" field
                                        if (chunk["type"]?.ToString() == "response.output_text.delta")
                                        {
                                            delta = chunk["delta"]?.ToString();
                                        }
                                    }
                                    else
                                    {
                                        // Chat Completions API: delta in choices[0].delta.content
                                        delta = chunk["choices"]?[0]?["delta"]?["content"]?.ToString();
                                    }
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
                        var response = await client.PostAsync(apiUrl, content, InterruptManager.Token);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await HandleHttpError(response, Settings, "OpenAI");
                            ReportFailure(errorMessage);
                            return "";
                        }

                        var responseString = await response.Content.ReadAsStringAsync();
                        var responseObject = JObject.Parse(responseString);
                        if (useResponses)
                        {
                            message = ExtractTextFromResponsesOutput(responseObject);
                        }
                        else
                        {
                            message = responseObject["choices"][0]["message"]["content"].ToString();
                        }
                        ResponseHandler?.Invoke(message);
                    }
                }
            }
            catch (Exception ex)
            {
                // 用户中断：取消引发的异常不是故障，既不重试也不弹错误提示
                if (InterruptManager.IsInterrupted)
                {
                    SystemLogger.Log("OpenAI 请求已被用户中断");
                    return "";
                }

                var errorMessage = ErrorHelper.GetFriendlyExceptionError(ex, Settings, "OpenAI");
                SystemLogger.Log($"OpenAI ChatWithImage 异常: {ex.Message}");
                ReportFailure(errorMessage);
                return "";
            }

            // 保存历史记录（包含图像数据用于上下文编辑器显示）
            if (Settings?.KeepContext ?? true)
            {
                var userMessage = CreateUserMessage($"[图像] {prompt}");
                if (userMessage is not null)
                {
                    // 保存图像数据到消息对象（用于上下文编辑器显示）
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
            // Handle conversation turn for record weight decrement
            OnConversationTurn();

            // 容灾重试时保持已选定的转移节点与已尝试节点列表，避免重新轮选导致再次命中失败节点（死循环）
            if (_isFailoverRetry)
            {
                _isFailoverRetry = false; // 消费标记，本次请求内后续逻辑按正常流程走
            }
            else
            {
                // 首次调用：重置已尝试节点列表并清除上一次请求的节点缓存，确保重新选择节点
                _triedNodeIndices.Clear();
                ClearNodeContext();
            }

            // 临时构建包含当前用户消息的历史记录（用于API请求），但不立即保存到数据库
            // 使用 CreateUserMessage 自动设置时间戳和状态信息
            var tempUserMessage = CreateUserMessage(prompt);

            // 获取当前节点和API Key
            var (apiUrl, apiKey, currentNode) = GetCurrentEndpoint("Chat");

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
            // 提示词要说"本节点是否开启工具"，判断必须跟着这一轮的节点走
            CurrentNodeToolsEnabled = global::VPetLLM.Core.Tools.NativeToolSession.WillAttachTools(Settings, currentNode.EnableToolCall);
            List<Message> history = await GetCoreHistoryAsync(userQuery: prompt);
            // 如果有临时用户消息，添加到历史末尾用于API请求
            if (tempUserMessage is not null)
            {
                history.Add(tempUserMessage);
            }
            // 在添加用户消息后注入重要记录
            history = InjectRecordsIntoHistory(history);

            object data;
            bool useResponses = IsResponsesApi(apiUrl);
            if (_openAISetting.EnableAdvanced)
            {
                if (useResponses)
                {
                    data = new
                    {
                        model = currentNode.Model,
                        input = history.Select(m => new { role = m.Role, content = m.DisplayContent }),
                        temperature = _openAISetting.Temperature,
                        max_output_tokens = _openAISetting.MaxTokens,
                        stream = currentNode.EnableStreaming
                    };
                }
                else
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
            }
            else
            {
                if (useResponses)
                {
                    data = new
                    {
                        model = currentNode.Model,
                        input = history.Select(m => new { role = m.Role, content = m.DisplayContent }),
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
            }
            // 原生工具调用。Responses API 和 chat completions 的工具协议完全不同
            // （声明扁平、调用散在 output[] 里、靠 call_id 配对），所以分两条路挂。
            var toolSession = global::VPetLLM.Core.Tools.NativeToolSession.TryCreate(Settings, currentNode.EnableToolCall);

            var payload = JObject.FromObject(data);
            if (toolSession is not null)
            {
                if (useResponses)
                    toolSession.AttachOpenAiResponsesTools(payload);
                else
                    toolSession.AttachOpenAiTools(payload);
                // 工具循环强制非流式，见 NativeToolLoop 的说明
                payload["stream"] = false;
            }

            var content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            global::VPetLLM.Core.Tools.NativeToolLoopResult? toolLoop = null;

            string message;
            try
            {
                using (var client = GetClient())
                {
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                    }

                    if (toolSession is not null)
                    {
                        Func<JObject, Task<JObject?>> roundSend = async body =>
                        {
                            var roundContent = new StringContent(
                                body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                            var roundResponse = await client.PostAsync(apiUrl, roundContent, InterruptManager.Token);
                            if (!roundResponse.IsSuccessStatusCode)
                            {
                                var errorMessage = await HandleHttpError(roundResponse, Settings, "OpenAI");
                                ResponseHandler?.Invoke(errorMessage);
                                return null;
                            }
                            return JObject.Parse(await roundResponse.Content.ReadAsStringAsync());
                        };

                        var loop = useResponses
                            ? await global::VPetLLM.Core.Tools.NativeToolLoop.RunOpenAiResponsesAsync(
                                payload, toolSession, roundSend)
                            : await global::VPetLLM.Core.Tools.NativeToolLoop.RunOpenAiAsync(
                                payload, toolSession, roundSend);

                        if (!loop.Success) return "";
                        toolLoop = loop;

                        message = loop.Message;
                        if (loop.HitLimit)
                        {
                            SystemLogger.Log("OpenAI: 工具调用达到轮次上限，本轮不再继续");
                        }
                        ResponseHandler?.Invoke(message);
                    }
                    else if (currentNode.EnableStreaming)
                    {
                        // 流式传输模式
                        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                        {
                            Content = content
                        };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, InterruptManager.Token);

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
                                    string? delta = null;
                                    if (useResponses)
                                    {
                                        // Responses API: event type "response.output_text.delta"
                                        if (chunk["type"]?.ToString() == "response.output_text.delta")
                                        {
                                            delta = chunk["delta"]?.ToString();
                                        }
                                    }
                                    else
                                    {
                                        // Chat Completions API: delta in choices[0].delta.content
                                        delta = chunk["choices"]?[0]?["delta"]?["content"]?.ToString();
                                    }
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

                        // 注意：流式模式下不再调用 ResponseHandler，因为已经通过 streamProcessor 逐个处理了
                    }
                    else
                    {
                        // 非流式传输模式
                        var response = await client.PostAsync(apiUrl, content, InterruptManager.Token);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await HandleHttpError(response, Settings, "OpenAI");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }

                        var responseString = await response.Content.ReadAsStringAsync();
                        var responseObject = JObject.Parse(responseString);
                        if (useResponses)
                        {
                            message = ExtractTextFromResponsesOutput(responseObject);
                            var tokenUsage = responseObject["usage"]?["total_tokens"]?.ToString() ?? "0";
                        }
                        else
                        {
                            message = responseObject["choices"][0]["message"]["content"].ToString();
                            var tokenUsage = responseObject["usage"]["total_tokens"].ToString();
                        }
                        // 非流式模式下，一次性处理完整消息
                        ResponseHandler?.Invoke(message);
                    }
                }
            }
            catch (Exception ex)
            {
                // 用户中断：取消引发的异常不是故障，绝不能走下面的节点转移重试
                if (InterruptManager.IsInterrupted)
                {
                    SystemLogger.Log("OpenAI Chat 已被用户中断");
                    _triedNodeIndices.Clear();
                    return "";
                }

                // 错误转移：当启用负载均衡时，尝试下一个节点
                var nextNode = GetNextNodeForFailover("Chat");
                if (nextNode is not null)
                {
                    SystemLogger.Log($"OpenAI 节点 {_currentNodeContext?.Name} 失败，正在转移到 {nextNode.Name}...");
                    // 固定使用已选定的转移节点（而非清空后重新轮选，否则可能再次命中已失败节点造成死循环）
                    _currentNodeContext = nextNode;
                    _isFailoverRetry = true;

                    // 重新调用 Chat 方法（会递归重试）
                    // 注意：_triedNodeIndices 已记录失败节点，且 _isFailoverRetry 保证不会重新轮选
                    return await Chat(prompt, isRetry);
                }

                var errorMessage = ErrorHelper.GetFriendlyExceptionError(ex, Settings, "OpenAI");
                SystemLogger.Log($"OpenAI Chat 异常: {ex.Message}");
                ResponseHandler?.Invoke(errorMessage);

                // 重置已尝试节点列表
                _triedNodeIndices.Clear();
                return "";
            }

            // API调用成功后，才将用户消息和助手回复保存到历史记录
            if (Settings?.KeepContext ?? true)
            {
                // 先保存用户消息
                if (tempUserMessage is not null)
                {
                    await HistoryManager.AddMessage(tempUserMessage);
                }
                // 再保存助手回复
                await PersistToolCallTraceAsync(toolLoop);
                await HistoryManager.AddMessage(new Message { Role = "assistant", Content = AppendInterruptMarker(message) });
                // 保存历史记录
                SaveHistory();
                TriggerOverflowCheckAfterSuccess();
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
                var (apiUrl, apiKey, currentNode) = GetCurrentEndpoint("Compression");

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
                bool useResponses = IsResponsesApi(apiUrl);
                if (_openAISetting.EnableAdvanced)
                {
                    if (useResponses)
                    {
                        data = new
                        {
                            model = currentNode.Model,
                            input = messages,
                            temperature = _openAISetting.Temperature,
                            max_output_tokens = _openAISetting.MaxTokens
                        };
                    }
                    else
                    {
                        data = new
                        {
                            model = currentNode.Model,
                            messages = messages,
                            temperature = _openAISetting.Temperature,
                            max_tokens = _openAISetting.MaxTokens
                        };
                    }
                }
                else
                {
                    if (useResponses)
                    {
                        data = new
                        {
                            model = currentNode.Model,
                            input = messages
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
                    if (useResponses)
                    {
                        return ExtractTextFromResponsesOutput(responseObject);
                    }
                    else
                    {
                        return responseObject["choices"][0]["message"]["content"].ToString();
                    }
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

        private async Task<List<Message>> GetCoreHistoryAsync(bool injectRecords = false, string? userQuery = null)
        {
            var result = await GetCoreHistoryCommonAsync(injectRecords, userQuery);
            return CaptureOverflowCheckData(result);
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
                else if (modelsUrl.Contains("/responses"))
                {
                    modelsUrl = modelsUrl.Replace("/responses", "/models");
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