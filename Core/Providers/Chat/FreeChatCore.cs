using Newtonsoft.Json.Linq;
using System.Net.Http;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils.Data;

namespace VPetLLM.Core.Providers.Chat
{
    public class FreeChatCore : ChatCoreBase
    {
        public override string Name => "Free";
        private readonly Setting.FreeSetting _freeSetting;
        private readonly HttpClient _httpClient;

        private string _apiKey;
        private string _apiUrl;
        private string _model;
        private int _maxTokensLimit = 3000;

        /// <summary>
        /// Free 通道对 messages 数组条数的硬限制（API 返回 "Too many messages (N), max 1000 allowed"）。
        /// 云端可通过 MaxMessagesLimit 字段修正。缓存配置陈旧或离线时回落到这个保守值 ——
        /// 这里绝不能像全局 MaxContextMessages 那样以 0（不限制）兜底，否则「云端修正」
        /// 恰好在最需要它的老客户端上失效。
        /// </summary>
        private const int DEFAULT_MESSAGES_LIMIT = 1000;

        /// <summary>
        /// 预算裁剪之后仍会追加的消息条数余量（ChatWithImage 在裁剪后才追加图像消息）。
        /// </summary>
        private const int MESSAGES_LIMIT_RESERVE = 4;

        private int _maxMessagesLimit = DEFAULT_MESSAGES_LIMIT;

        /// <inheritdoc />
        protected override int MaxContextMessages => Math.Max(1, _maxMessagesLimit - MESSAGES_LIMIT_RESERVE);

        // 保留硬编码的User-Agent
        private const string ENCODED_UA = "566c426c6445784d54563947636d566c58304a3558304a5a54513d3d";



        public FreeChatCore(Setting.FreeSetting freeSetting, Setting setting, IMainWindow mainWindow, ActionProcessor actionProcessor)
            : base(setting, mainWindow, actionProcessor)
        {
            _freeSetting = freeSetting;

            LoadConfig();

            var timeoutSeconds = setting?.LLMRequestTimeoutSeconds ?? 30;
            if (timeoutSeconds <= 0) timeoutSeconds = 30;
            _httpClient = Utils.Network.HttpHandlerPool.CreateClient(
                CreateHttpClientHandler, TimeSpan.FromSeconds(timeoutSeconds));

            // 设置API密钥
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

            // 设置解码后的User-Agent头部
            var decodedUA = DecodeString(ENCODED_UA);
            if (!string.IsNullOrEmpty(decodedUA))
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(decodedUA);
            }
        }

        private void LoadConfig()
        {
            try
            {
                var config = FreeConfigManager.GetChatConfig();
                if (config is not null)
                {
                    _apiKey = DecodeString(config["API_KEY"]?.ToString() ?? "");
                    _apiUrl = DecodeString(config["API_URL"]?.ToString() ?? "");
                    _model = config["Model"]?.ToString() ?? "";
                    // 读取云端下发的 MaxTokensLimit，未设置则默认 10000
                    if (config["MaxTokensLimit"] is not null && int.TryParse(config["MaxTokensLimit"]?.ToString(), out int cloudLimit) && cloudLimit > 0)
                    {
                        _maxTokensLimit = cloudLimit;
                    }
                    // 读取云端下发的 messages 条数上限，未下发则保持保守默认值
                    if (config["MaxMessagesLimit"] is not null && int.TryParse(config["MaxMessagesLimit"]?.ToString(), out int cloudMsgLimit) && cloudMsgLimit > 0)
                    {
                        _maxMessagesLimit = cloudMsgLimit;
                    }
                    Logger.Log($"FreeChatCore: messages 条数上限={_maxMessagesLimit}（实际裁剪至 {MaxContextMessages}）");
                    // 读取云端下发的记忆检索开关
                    if (config["EnableMemoryRetrieval"] is not null && bool.TryParse(config["EnableMemoryRetrieval"]?.ToString(), out bool enableRetrieval))
                    {
                        // 同步到 handler 的全局开关
                        Handlers.Actions.MemoryRetrievalHandler.IsEnabled = enableRetrieval;
                        Logger.Log($"FreeChatCore: 记忆检索工具模式={(enableRetrieval ? "启用" : "禁用")}");
                    }
                    else
                    {
                        Handlers.Actions.MemoryRetrievalHandler.IsEnabled = false;
                    }
                    // 读取云端下发的原生工具调用策略（Auto/On/Off，缺省 Auto）。
                    // Free 的模型由云端决定且随时可换，所以这里只收策略，具体支不支持由客户端探测。
                    Tools.FreeToolCapability.ApplyCloudConfig(config["EnableToolCall"]);
                    Logger.Log("FreeChatCore: 配置加载成功");
                }
                else
                {
                    Logger.Log("FreeChatCore: 配置文件不存在，请等待配置下载完成后重启程序");
                    _apiKey = "";
                    _apiUrl = "";
                    _model = "";
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"FreeChatCore: 加载配置失败: {ex.Message}");
                _apiKey = "";
                _apiUrl = "";
                _model = "";
            }
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

            try
            {
                // Handle conversation turn for record weight decrement
                OnConversationTurn();

                if (string.IsNullOrEmpty(_apiUrl) || string.IsNullOrEmpty(_apiKey))
                {
                    var errorMessage = ErrorMessageHelper.GetFreeApiError(Settings, "ConfigNotLoaded")
                        ?? "Free Chat 配置未加载，请等待配置下载完成后重启程序";
                    Logger.Log(errorMessage);
                    ReportFailure(errorMessage);
                    return "";
                }

                // 检查视觉能力是否启用
                if (!_freeSetting.EnableVision)
                {
                    var visionError = "Free 接口未启用视觉能力，请在设置中启用EnableVision";
                    Logger.Log($"Free ChatWithImage 错误: {visionError}");
                    ReportFailure(visionError);
                    return "";
                }

                // 检查 MaxTokens 限制，超过云端限制则自动启用上下文压缩模式
                if (_freeSetting.MaxTokens > _maxTokensLimit)
                {
                    if (!Settings.EnableHistoryCompression)
                    {
                        Logger.Log($"Free Chat: MaxTokens ({_freeSetting.MaxTokens}) 超过限制 ({_maxTokensLimit})，自动启用上下文压缩模式");
                        Settings.EnableHistoryCompression = true;
                    }
                }

                Logger.Log($"Free ChatWithImage: 发送多模态消息，图像大小: {DescribeImages(images)}");

                // 构建多模态消息内容
                var userContent = BuildMultimodalContent(prompt, images);

                // 构建历史消息（不包含图像）
                List<Message> history = await GetCoreHistoryAsync(userQuery: prompt);

                // 构建请求消息列表
                var requestMessages = new List<object>();
                foreach (var msg in history)
                {
                    requestMessages.Add(new { role = msg.Role, content = msg.DisplayContent });
                }
                // 添加带图像的用户消息
                requestMessages.Add(new { role = "user", content = userContent });

                var useStreaming = UseStreaming(_freeSetting.EnableStreaming);

                var requestBody = new
                {
                    model = _model,
                    messages = requestMessages,
                    temperature = _freeSetting.Temperature,
                    max_tokens = Math.Min(_freeSetting.MaxTokens, _maxTokensLimit),
                    stream = useStreaming
                };

                var requestPayload = JObject.FromObject(requestBody);
                Utils.Common.ReasoningEffortHelper.Apply(requestPayload, _freeSetting.ThinkingEffort, Utils.Common.ReasoningApiStyle.OpenAIChat);
                var json = requestPayload.ToString(Formatting.None);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                string message = "";
                var handledByTools = false;
                Tools.NativeToolLoopResult? imageToolLoop = null;

                // 多模态请求同样可以带工具。但这一路**不记录**能力判定：
                // 有的端点单独支持视觉、单独支持工具，两者同时用才报错；
                // 把那种失败写成"该模型不支持工具"会连纯文本对话的工具也一起误关。
                var imageToolSession = Tools.NativeToolSession.TryCreate(
                    Settings, Tools.FreeToolCapability.ShouldAttachTools());
                if (imageToolSession is not null)
                {
                    var attempt = await TryChatWithNativeToolsAsync(requestPayload, imageToolSession, recordVerdict: false);
                    if (attempt.Completed)
                    {
                        message = attempt.Message;
                        ResponseHandler?.Invoke(message);
                        imageToolLoop = attempt.Loop;
                        handledByTools = true;
                    }
                    else if (!attempt.FallBack)
                    {
                        ReportFailure(attempt.Message);
                        return "";
                    }
                    else
                    {
                        Logger.Log("Free ChatWithImage: 工具路径不可用，本轮改用标记协议重发");
                    }
                }

                if (handledByTools)
                {
                    // 工具路径已经拿到最终回复，直接走下面的落库
                }
                else if (useStreaming)
                {
                    // 流式传输模式
                    Logger.Log("Free ChatWithImage: 使用流式传输模式");
                    var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl)
                    {
                        Content = content
                    };
                    var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, InterruptManager.Token);

                    if (response.IsSuccessStatusCode)
                    {
                        var fullMessage = new StringBuilder();
                        var streamProcessor = new StreamingCommandProcessor((cmd) =>
                        {
                            Logger.Log($"Free流式: 检测到完整命令: {cmd}");
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
                        Logger.Log($"Free流式: 流式传输完成，总消息长度 {message.Length}");
                    }
                    else
                    {
                        var responseContent = await response.Content.ReadAsStringAsync();
                        Logger.Log($"Free ChatWithImage API 错误: {response.StatusCode} - {responseContent}");
                        message = ErrorMessageHelper.IsDebugMode(Settings)
                            ? $"API调用失败: {response.StatusCode} - {responseContent}"
                            : ErrorMessageHelper.GetFriendlyHttpError(response.StatusCode, responseContent, Settings);
                        ReportFailure(message);
                        return "";
                    }
                }
                else
                {
                    // 非流式传输模式
                    Logger.Log("Free ChatWithImage: 使用非流式传输模式");
                    var response = await _httpClient.PostAsync(_apiUrl, content, InterruptManager.Token);
                    var responseContent = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        var responseObj = JsonConvert.DeserializeObject<JObject>(responseContent);
                        message = responseObj?["choices"]?[0]?["message"]?["content"]?.ToString() ?? "无回复";
                        Logger.Log($"Free非流式: 收到完整消息，长度 {message.Length}");
                        ResponseHandler?.Invoke(message);
                    }
                    else
                    {
                        Logger.Log($"Free ChatWithImage API 错误: {response.StatusCode} - {responseContent}");
                        message = ErrorMessageHelper.IsDebugMode(Settings)
                            ? $"API调用失败: {response.StatusCode} - {responseContent}"
                            : ErrorMessageHelper.GetFriendlyHttpError(response.StatusCode, responseContent, Settings);
                        ReportFailure(message);
                        return "";
                    }
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
                    await PersistToolCallTraceAsync(imageToolLoop);
                    await HistoryManager.AddMessage(new Message { Role = "assistant", Content = PrepareAssistantHistoryContent(message) });
                    SaveHistory();
                    TriggerOverflowCheckAfterSuccess();
                }

                return "";
            }
            catch (HttpRequestException httpEx)
            {
                Logger.Log($"Free ChatWithImage 网络异常: {httpEx.Message}");
                var errorMessage = ErrorMessageHelper.IsDebugMode(Settings)
                    ? $"Free ChatWithImage 网络异常: {httpEx.Message}\n{httpEx.StackTrace}"
                    : ErrorMessageHelper.GetFriendlyExceptionError(httpEx, Settings, "Free");
                ReportFailure(errorMessage);
                return "";
            }
            catch (TaskCanceledException tcEx)
            {
                // 用户中断时 HttpClient 抛的就是 TaskCanceledException，不是超时
                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("Free 请求已被用户中断");
                    return "";
                }

                Logger.Log($"Free ChatWithImage 请求超时: {tcEx.Message}");
                var errorMessage = ErrorMessageHelper.IsDebugMode(Settings)
                    ? $"Free ChatWithImage 请求超时: {tcEx.Message}\n{tcEx.StackTrace}"
                    : ErrorMessageHelper.GetFriendlyExceptionError(tcEx, Settings, "Free");
                ReportFailure(errorMessage);
                return "";
            }
            catch (Exception ex)
            {
                // 用户中断：取消引发的异常不是故障，既不重试也不弹错误提示
                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("Chat 请求已被用户中断");
                    return "";
                }

                Logger.Log($"Free ChatWithImage 异常: {ex.Message}");
                var errorMessage = ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Free");
                ReportFailure(errorMessage);
                return "";
            }
        }

        public override async Task<string> Chat(string prompt, bool isRetry)
        {
            LastCallFailed = false;
            try
            {
                // Handle conversation turn for record weight decrement
                OnConversationTurn();

                if (string.IsNullOrEmpty(_apiUrl) || string.IsNullOrEmpty(_apiKey))
                {
                    var errorMessage = ErrorMessageHelper.GetFreeApiError(Settings, "ConfigNotLoaded")
                        ?? "Free Chat 配置未加载，请等待配置下载完成后重启程序";
                    Logger.Log(errorMessage);
                    ResponseHandler?.Invoke(errorMessage);
                    return "";
                }

                // 检查 MaxTokens 限制，超过云端限制则自动启用上下文压缩模式
                if (_freeSetting.MaxTokens > _maxTokensLimit)
                {
                    if (!Settings.EnableHistoryCompression)
                    {
                        Logger.Log($"Free Chat: MaxTokens ({_freeSetting.MaxTokens}) 超过限制 ({_maxTokensLimit})，自动启用上下文压缩模式");
                        Settings.EnableHistoryCompression = true;
                    }
                }

                // 临时构建包含当前用户消息的历史记录（用于API请求），但不立即保存到数据库
                // 使用 CreateUserMessage 自动设置时间戳和状态信息
                var tempUserMessage = CreateUserMessage(prompt);

                List<Message> history = await GetCoreHistoryAsync(userQuery: prompt);
                // 如果有临时用户消息，添加到历史末尾用于API请求
                if (tempUserMessage is not null)
                {
                    history.Add(tempUserMessage);
                }
                // 在添加用户消息后注入重要记录
                history = InjectRecordsIntoHistory(history);
                var requestBody = new
                {
                    model = _model,
                    messages = ShapeMessages(history),
                    temperature = _freeSetting.Temperature,
                    max_tokens = Math.Min(_freeSetting.MaxTokens, _maxTokensLimit),
                    stream = _freeSetting.EnableStreaming
                };

                var requestPayload = JObject.FromObject(requestBody);
                Utils.Common.ReasoningEffortHelper.Apply(requestPayload, _freeSetting.ThinkingEffort, Utils.Common.ReasoningApiStyle.OpenAIChat);
                var json = requestPayload.ToString(Formatting.None);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                string message;

                // 原生工具调用：Free 没有"用户勾选"这一说（模型是云端下发的），
                // 由云端策略 + 本地探测共同决定这一轮要不要挂 tools。
                var toolSession = Tools.NativeToolSession.TryCreate(
                    Settings, Tools.FreeToolCapability.ShouldAttachTools());
                if (toolSession is not null)
                {
                    var attempt = await TryChatWithNativeToolsAsync(requestPayload, toolSession);
                    if (attempt.Completed)
                    {
                        message = attempt.Message;
                        ResponseHandler?.Invoke(message);
                        await PersistTurnAsync(tempUserMessage, message, attempt.Loop);
                        return "";
                    }
                    if (!attempt.FallBack)
                    {
                        // 与工具能力无关的失败（限流、维护、网络），按普通错误处理
                        ReportFailure(attempt.Message);
                        return "";
                    }
                    // FallBack：判定当前模型不支持原生工具，落到下面用标记协议把这一轮重发一遍。
                    // 探测请求没有产生任何用户可见输出，所以重发是干净的。
                    Logger.Log("Free: 本轮改用标记协议重发");
                }

                if (_freeSetting.EnableStreaming)
                {
                    // 流式传输模式
                    Logger.Log("Free: 使用流式传输模式");
                    var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl)
                    {
                        Content = content
                    };
                    var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, InterruptManager.Token);

                    if (response.IsSuccessStatusCode)
                    {
                        var fullMessage = new StringBuilder();
                        var streamProcessor = new StreamingCommandProcessor((cmd) =>
                        {
                            // 当检测到完整命令时，立即处理（流式模式下逐个命令处理）
                            Logger.Log($"Free流式: 检测到完整命令: {cmd}");
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
                        Logger.Log($"Free流式: 流式传输完成，总消息长度 {message.Length}");
                        // 注意：流式模式下不再调用 ResponseHandler，因为已经通过 streamProcessor 逐个处理了
                        if (string.IsNullOrEmpty(message))
                        {
                            message = "无回复";
                        }

                        // API调用成功后，才将用户消息和助手回复保存到历史记录
                        if (Settings?.KeepContext ?? true)
                        {
                            if (tempUserMessage is not null)
                            {
                                await HistoryManager.AddMessage(tempUserMessage);
                            }
                            await HistoryManager.AddMessage(new Message { Role = "assistant", Content = PrepareAssistantHistoryContent(message) });
                            SaveHistory();

                            TriggerOverflowCheckAfterSuccess();
                        }
                    }
                    else
                    {
                        // 读取错误响应内容
                        var responseContent = await response.Content.ReadAsStringAsync();

                        // 检查是否是服务器错误
                        if (responseContent.Contains("Failed to retrieve proxy group") ||
                            responseContent.Contains("INTERNAL_SERVER_ERROR") ||
                            response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            Logger.Log($"Free API 服务器错误: {response.StatusCode} - {responseContent}");

                            // 如果不是重试，尝试重试一次
                            if (!isRetry)
                            {
                                Logger.Log("尝试重试 Free API 请求...");
                                await Task.Delay(2000); // 等待2秒后重试
                                return await Chat(prompt, true);
                            }

                            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                message = ErrorMessageHelper.GetFreeApiError(Settings, "ServiceMaintenance")
                                    ?? "Free API 服务正在维护中，请稍后再试。如果问题持续存在，请联系开发者。";
                            }
                            else
                            {
                                message = ErrorMessageHelper.GetFreeApiError(Settings, "ServiceUnavailable")
                                    ?? "Free API 服务暂时不可用，请稍后再试。这可能是由于服务器负载过高或维护导致的。";
                            }
                        }
                        else
                        {
                            Logger.Log($"Free API 错误: {response.StatusCode} - {responseContent}");
                            message = ErrorMessageHelper.IsDebugMode(Settings)
                                ? $"API调用失败: {response.StatusCode} - {responseContent}"
                                : ErrorMessageHelper.GetFriendlyHttpError(response.StatusCode, responseContent, Settings);
                        }
                        // 错误分支必须自己投递：底部那句"已在各分支调用过"只对成功分支成立
                        ReportFailure(message);
                    }
                }
                else
                {
                    // 非流式传输模式
                    Logger.Log("Free: 使用非流式传输模式");
                    var response = await _httpClient.PostAsync(_apiUrl, content, InterruptManager.Token);
                    var responseContent = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        var responseObj = JsonConvert.DeserializeObject<JObject>(responseContent);
                        message = responseObj?["choices"]?[0]?["message"]?["content"]?.ToString() ?? "无回复";
                        Logger.Log($"Free非流式: 收到完整消息，长度 {message.Length}");
                        // 非流式模式下，一次性处理完整消息
                        ResponseHandler?.Invoke(message);

                        // API调用成功后，才将用户消息和助手回复保存到历史记录
                        if (Settings?.KeepContext ?? true)
                        {
                            if (tempUserMessage is not null)
                            {
                                await HistoryManager.AddMessage(tempUserMessage);
                            }
                            await HistoryManager.AddMessage(new Message { Role = "assistant", Content = PrepareAssistantHistoryContent(message) });
                            SaveHistory();

                            TriggerOverflowCheckAfterSuccess();
                        }
                    }
                    else
                    {
                        // 检查是否是服务器错误
                        if (responseContent.Contains("Failed to retrieve proxy group") ||
                            responseContent.Contains("INTERNAL_SERVER_ERROR") ||
                            response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            Logger.Log($"Free API 服务器错误: {response.StatusCode} - {responseContent}");

                            // 如果不是重试，尝试重试一次
                            if (!isRetry)
                            {
                                Logger.Log("尝试重试 Free API 请求...");
                                await Task.Delay(2000); // 等待2秒后重试
                                return await Chat(prompt, true);
                            }

                            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                message = ErrorMessageHelper.GetFreeApiError(Settings, "ServiceMaintenance")
                                    ?? "Free API 服务正在维护中，请稍后再试。如果问题持续存在，请联系开发者。";
                            }
                            else
                            {
                                message = ErrorMessageHelper.GetFreeApiError(Settings, "ServiceUnavailable")
                                    ?? "Free API 服务暂时不可用，请稍后再试。这可能是由于服务器负载过高或维护导致的。";
                            }
                        }
                        else
                        {
                            Logger.Log($"Free API 错误: {response.StatusCode} - {responseContent}");
                            message = ErrorMessageHelper.IsDebugMode(Settings)
                                ? $"API调用失败: {response.StatusCode} - {responseContent}"
                                : ErrorMessageHelper.GetFriendlyHttpError(response.StatusCode, responseContent, Settings);
                        }
                        // 错误分支必须自己投递：底部那句"已在各分支调用过"只对成功分支成立
                        ReportFailure(message);
                    }
                }

                // 注意：ResponseHandler 已经在流式/非流式模式的各自分支中调用过了，这里不需要再次调用
                return "";
            }
            catch (HttpRequestException httpEx)
            {
                Logger.Log($"Free Chat 网络异常: {httpEx.Message}");

                // 如果不是重试，尝试重试一次
                if (!isRetry)
                {
                    Logger.Log("网络异常，尝试重试 Free API 请求...");
                    await Task.Delay(2000); // 等待2秒后重试
                    return await Chat(prompt, true);
                }

                var errorMessage = ErrorMessageHelper.IsDebugMode(Settings)
                    ? $"Free Chat 网络异常: {httpEx.Message}\n{httpEx.StackTrace}"
                    : ErrorMessageHelper.GetFriendlyExceptionError(httpEx, Settings, "Free");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
            catch (TaskCanceledException tcEx)
            {
                // 用户中断时 HttpClient 抛的就是 TaskCanceledException，不是超时
                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("Free 请求已被用户中断");
                    return "";
                }

                Logger.Log($"Free Chat 请求超时: {tcEx.Message}");
                var errorMessage = ErrorMessageHelper.IsDebugMode(Settings)
                    ? $"Free Chat 请求超时: {tcEx.Message}\n{tcEx.StackTrace}"
                    : ErrorMessageHelper.GetFriendlyExceptionError(tcEx, Settings, "Free");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
            catch (Exception ex)
            {
                // 用户中断：取消引发的异常不是故障，既不重试也不弹错误提示
                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("Chat 请求已被用户中断");
                    return "";
                }

                Logger.Log($"Free Chat 异常: {ex.Message}");
                var errorMessage = ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Free");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
        }



        public override async Task<string> Summarize(string systemPrompt, string userContent)
        {
            try
            {
                if (string.IsNullOrEmpty(_apiUrl) || string.IsNullOrEmpty(_apiKey))
                {
                    Logger.Log("Free Chat 配置未加载，总结功能不可用");
                    return ErrorMessageHelper.GetFreeApiError(Settings, "ConfigNotLoaded")
                        ?? "配置未加载，总结功能暂时不可用";
                }

                var messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userContent }
                };

                var requestBody = new
                {
                    model = _model,
                    messages = messages,
                    temperature = _freeSetting.Temperature,
                    max_tokens = Math.Min(_freeSetting.MaxTokens, _maxTokensLimit)
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(_apiUrl, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var responseObj = JsonConvert.DeserializeObject<JObject>(responseContent);
                    return responseObj?["choices"]?[0]?["message"]?["content"]?.ToString() ?? "总结失败";
                }
                else
                {
                    // 检查是否是服务器内部错误
                    if (responseContent.Contains("Failed to retrieve proxy group") ||
                        responseContent.Contains("INTERNAL_SERVER_ERROR"))
                    {
                        Logger.Log($"Free Summarize 服务器内部错误: {responseContent}");
                        return ErrorMessageHelper.GetFreeApiError(Settings, "ServiceUnavailable")
                            ?? "Free API 服务暂时不可用，总结功能无法使用。";
                    }

                    Logger.Log($"Free Summarize 错误: {response.StatusCode} - {responseContent}");
                    return ErrorMessageHelper.GetSummarizeError(Settings) ?? "总结失败";
                }
            }
            catch (HttpRequestException httpEx)
            {
                Logger.Log($"Free Summarize 网络异常: {httpEx.Message}");
                return ErrorMessageHelper.IsDebugMode(Settings)
                    ? $"Free Summarize 网络异常: {httpEx.Message}"
                    : ErrorMessageHelper.GetFriendlyExceptionError(httpEx, Settings, "Free");
            }
            catch (TaskCanceledException tcEx)
            {
                Logger.Log($"Free Summarize 请求超时: {tcEx.Message}");
                return ErrorMessageHelper.IsDebugMode(Settings)
                    ? $"Free Summarize 请求超时: {tcEx.Message}"
                    : ErrorMessageHelper.GetFriendlyExceptionError(tcEx, Settings, "Free");
            }
            catch (Exception ex)
            {
                Logger.Log($"Free Summarize 异常: {ex.Message}");
                return ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Free");
            }
        }

        private async Task<List<Message>> GetCoreHistoryAsync(bool injectRecords = false, string? userQuery = null)
        {
            var result = await GetCoreHistoryCommonAsync(injectRecords, userQuery);
            return CaptureOverflowCheckData(result);
        }

        #region 原生工具调用

        /// <summary>一次带工具的请求尝试的三种去向。</summary>
        private sealed class ToolAttempt
        {
            /// <summary>拿到了最终回复，<see cref="Message"/> 即正文。</summary>
            public bool Completed;
            /// <summary>判定当前模型不支持原生工具，调用方应用标记协议重发本轮。</summary>
            public bool FallBack;
            /// <summary>Completed 时是正文；两者都为 false 时是要展示给用户的错误文案。</summary>
            public string Message = "";

            /// <summary>本轮工具调用还原成标记后的消息序列，Completed 时随正文一起落库。</summary>
            public Tools.NativeToolLoopResult? Loop;

            public static ToolAttempt Done(string message, Tools.NativeToolLoopResult? loop = null)
                => new() { Completed = true, Message = message, Loop = loop };
            public static ToolAttempt Retry() => new() { FallBack = true };
            public static ToolAttempt Error(string message) => new() { Message = message };
        }

        /// <summary>
        /// 带 tools 跑一轮对话，并顺带完成"当前 Free 模型到底支不支持工具"的判定。
        ///
        /// 判定只认两类硬证据：端点因为 tools 字段本身报错，或者模型把工具调用当正文吐出来。
        /// 「这一轮没调工具」不是证据 —— 大多数闲聊本来就不需要工具，据此关掉工具模式是错的，
        /// 所以那种情况保持"未知"，下一轮继续探测。
        /// </summary>
        /// <param name="recordVerdict">
        /// 是否把本轮的观察写进全局能力判定。纯文本对话传 true；多模态传 false ——
        /// "图 + 工具不能同时用"是端点组合能力的问题，不该被记成"该模型不支持工具"。
        /// 为 false 时依然会按需退回标记协议，只是不留下结论。
        /// </param>
        private async Task<ToolAttempt> TryChatWithNativeToolsAsync(
            object requestBody, Tools.NativeToolSession toolSession, bool recordVerdict = true)
        {
            var payload = JObject.FromObject(requestBody);
            toolSession.AttachOpenAiTools(payload);
            // 工具循环强制非流式，见 NativeToolLoop 的说明
            payload["stream"] = false;

            var policy = Tools.FreeToolCapability.CurrentPolicy;
            string? rejectReason = null;    // 端点明确拒绝 tools
            string? hardError = null;       // 与工具无关的失败
            bool sawToolCalls = false;

            var loop = await Tools.NativeToolLoop.RunOpenAiAsync(payload, toolSession, async body =>
            {
                var roundContent = new StringContent(
                    body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                var roundResponse = await _httpClient.PostAsync(_apiUrl, roundContent, InterruptManager.Token);
                var roundText = await roundResponse.Content.ReadAsStringAsync();

                if (!roundResponse.IsSuccessStatusCode)
                {
                    var status = (int)roundResponse.StatusCode;
                    if (Tools.FreeToolCapability.LooksLikeToolRejection(status, roundText))
                    {
                        rejectReason = $"HTTP {status}: {Truncate(roundText, 200)}";
                    }
                    else
                    {
                        Logger.Log($"Free 工具请求错误: {roundResponse.StatusCode} - {roundText}");
                        hardError = ErrorMessageHelper.IsDebugMode(Settings)
                            ? $"API调用失败: {roundResponse.StatusCode} - {roundText}"
                            : ErrorMessageHelper.GetFriendlyHttpError(roundResponse.StatusCode, roundText, Settings);
                    }
                    return null;
                }

                JObject parsed;
                try
                {
                    parsed = JObject.Parse(roundText);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Free 工具响应解析失败: {ex.Message}");
                    hardError = ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Free");
                    return null;
                }

                if (parsed["choices"]?[0]?["message"]?["tool_calls"] is JArray { Count: > 0 })
                {
                    sawToolCalls = true;
                }
                return parsed;
            });

            if (loop.Success)
            {
                if (sawToolCalls)
                {
                    if (recordVerdict) Tools.FreeToolCapability.MarkSupported();
                }
                else if (Tools.FreeToolCapability.LooksLikeToolCallLeakage(
                             loop.Message, toolSession.Definitions.Select(d => d.Name)))
                {
                    // 模型想调工具但吐成了正文。这段文本直接念给用户听是最糟的结果，
                    // 所以无论云端策略如何，本轮都退回标记协议重发。
                    if (!recordVerdict)
                        Logger.Log("Free: 多模态轮次出现正文泄漏，本轮退回标记协议（不记录判定）");
                    else if (policy == Tools.FreeToolCapability.Policy.On)
                        Logger.Log("Free: 云端策略为 On，但模型把工具调用写进了正文；本轮退回标记协议（不改变策略）");
                    else
                        Tools.FreeToolCapability.MarkUnsupported("模型把工具调用写进了正文");
                    return ToolAttempt.Retry();
                }
                // 其余情况说明不了任何事（这轮本来就不需要工具），保持现状，下轮继续观察


                var message = string.IsNullOrWhiteSpace(loop.Message) ? "无回复" : loop.Message;
                return ToolAttempt.Done(message, loop);
            }

            if (rejectReason is not null)
            {
                if (!recordVerdict)
                    Logger.Log($"Free: 多模态轮次被端点拒绝 tools（{rejectReason}）；本轮退回标记协议（不记录判定）");
                else if (policy == Tools.FreeToolCapability.Policy.On)
                    Logger.Log($"Free: 云端策略为 On，但端点拒绝了 tools 字段（{rejectReason}）；本轮退回标记协议（不改变策略）");
                else
                    Tools.FreeToolCapability.MarkUnsupported(rejectReason);
                return ToolAttempt.Retry();
            }

            if (hardError is not null)
            {
                return ToolAttempt.Error(hardError);
            }

            // 循环自己放弃了（payload 结构异常等），没有证据说明模型不支持，按重发处理
            Logger.Log("Free: 工具循环未能完成且无明确原因，本轮退回标记协议");
            return ToolAttempt.Retry();
        }

        /// <summary>成功一轮之后落库。与流式/非流式两条分支里的保存逻辑保持一致。</summary>
        private async Task PersistTurnAsync(
            Message? tempUserMessage, string message, Tools.NativeToolLoopResult? loop = null)
        {
            if (!(Settings?.KeepContext ?? true)) return;

            if (tempUserMessage is not null)
            {
                await HistoryManager.AddMessage(tempUserMessage);
            }

            // 工具调用留下的痕迹（已还原成标记）夹在用户消息和最终回复之间，
            // 顺序与标记模式一致
            await PersistToolCallTraceAsync(loop);

            await HistoryManager.AddMessage(new Message
            {
                Role = "assistant",
                Content = PrepareAssistantHistoryContent(message)
            });
            SaveHistory();

            TriggerOverflowCheckAfterSuccess();
        }

        private static string Truncate(string? text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        #endregion

        /// <summary>
        /// 检查API服务状态
        /// </summary>
        public async Task<bool> CheckApiHealthAsync()
        {
            try
            {
                // 使用一个简单的测试请求来检查API状态
                var messages = new List<object>
                {
                    new { role = "user", content = "test" }
                };

                var requestBody = new
                {
                    model = _model,
                    messages = messages,
                    max_tokens = 1
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                var response = await _httpClient.PostAsync(_apiUrl, content, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    return !responseContent.Contains("Failed to retrieve proxy group") &&
                           !responseContent.Contains("INTERNAL_SERVER_ERROR");
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        // 该API服务由 QQ：790132463 提供，我们可使用的Key有限，我们无法支持大量请求，若您拿到并且正确响应，还请不要泄露与滥用，作为 VPetLLM 为 VPet 大量AI对话Mod的其中一个免费提供AI对话服务的Mod，还请您善待，谢谢！
        // This API service is provided by QQ: 790132463. We have a limited number of available keys and are unable to support a large volume of requests. If you receive a successful response, please do not leak or misuse the key. As one of the free AI conversation services for VPet, specifically for the VPet Large AI Dialogue Mod, we kindly ask for your considerate use. Thank you!
        // このAPIサービスは、QQ：790132463によって提供されています。利用できるキーの数には限りがあり、大量のリクエストには対応できません。もしこのキーを入手し、正常なレスポンスを受け取れた場合でも、漏洩や悪用は厳禁です。VPetLLMは、VPetのAI対話Modの多くの中で、無料のAI対話サービスを提供するModの一つです。どうか大切にご利用ください。よろしくお願いいたします！
        // 這個 API 服務由 QQ：790132463 提供。我們所能使用的金鑰數量有限，因此無法支援大量的請求。如果您已拿到金鑰並成功獲得回應，請不要外洩或濫用。本服務作為 VPetLLM，是為 VPet 大量 AI 對話模組免費提供 AI 對話服務的其中之一，請您珍惜使用，謝謝！
        private string DecodeString(string encodedString)
        {
            try
            {
                if (string.IsNullOrEmpty(encodedString))
                {
                    return "";
                }

                // 第一步：Hex解码
                var hexBytes = new byte[encodedString.Length / 2];
                for (int i = 0; i < hexBytes.Length; i++)
                {
                    hexBytes[i] = Convert.ToByte(encodedString.Substring(i * 2, 2), 16);
                }

                // 第二步：Base64解码
                var base64String = Encoding.UTF8.GetString(hexBytes);
                var finalBytes = Convert.FromBase64String(base64String);
                var result = Encoding.UTF8.GetString(finalBytes);

                return result;
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}