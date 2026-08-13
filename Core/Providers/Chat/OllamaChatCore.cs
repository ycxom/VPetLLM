using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Net.Http;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Core.Providers.Chat
{
    public class OllamaChatCore : ChatCoreBase
    {
        public override string Name => "Ollama";
        private readonly Setting.OllamaSetting _ollamaSetting;
        private readonly Setting _setting;
        public OllamaChatCore(Setting.OllamaSetting ollamaSetting, Setting setting, IMainWindow mainWindow, ActionProcessor actionProcessor)
            : base(setting, mainWindow, actionProcessor)
        {
            _ollamaSetting = ollamaSetting;
            _setting = setting;
        }

        public OllamaChatCore(Setting.OllamaNodeSetting ollamaNodeSetting, Setting setting, IMainWindow mainWindow, ActionProcessor actionProcessor)
            : base(setting, mainWindow, actionProcessor)
        {
            _ollamaSetting = new Setting.OllamaSetting
            {
                Url = ollamaNodeSetting.Url,
                Model = ollamaNodeSetting.Model,
                Temperature = ollamaNodeSetting.Temperature,
                MaxTokens = ollamaNodeSetting.MaxTokens,
                EnableAdvanced = ollamaNodeSetting.EnableAdvanced,
                EnableStreaming = ollamaNodeSetting.EnableStreaming,
                OllamaNodes = new List<Setting.OllamaNodeSetting> { ollamaNodeSetting }
            };
            _setting = setting;
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

                // 检查视觉能力是否启用
                if (!_ollamaSetting.EnableVision)
                {
                    var visionError = "Ollama 未启用视觉能力，请在设置中启用 EnableVision";
                    Logger.Log($"Ollama ChatWithImage 错误: {visionError}");
                    ReportFailure(visionError);
                    return "";
                }

                // 验证图像数据
                if (images.Count == 0)
                {
                    var invalidImageError = "图像数据无效";
                    Logger.Log($"Ollama ChatWithImage 错误: {invalidImageError}");
                    ReportFailure(invalidImageError);
                    return "";
                }

                Logger.Log($"Ollama ChatWithImage: 发送多模态消息，图像大小: {DescribeImages(images)}");

                // Ollama 的 images 字段本来就是数组，多图天然支持
                var base64Images = images.Select(Convert.ToBase64String).ToArray();

                // 创建用户消息
                var tempUserMessage = CreateUserMessage($"[图像] {prompt}");
                if (tempUserMessage is not null)
                {
                    // 保存图像数据到消息对象（用于历史记录）
                    tempUserMessage.ImageData = imageData;
                }

                // 构建历史记录
                List<Message> history = await GetCoreHistoryAsync(userQuery: prompt);
                // 如果有临时用户消息，添加到历史末尾用于API请求
                if (tempUserMessage is not null)
                {
                    history.Add(tempUserMessage);
                }
                // 在添加用户消息后注入重要记录
                history = InjectRecordsIntoHistory(history);

                // 构建请求负载 - Ollama 格式需要在消息级别添加 images 数组
                var messages = new List<object>();
                foreach (var msg in history)
                {
                    var messageObj = new Dictionary<string, object>
                    {
                        ["role"] = msg.Role,
                        ["content"] = msg.DisplayContent
                    };

                    // 本轮这条消息带的是本次截的全部图；Message 只有一个图像槽位，
                    // 所以多图不能靠 msg.ImageData 取，得直接用手上的 base64Images。
                    // 历史图不再回放：和其它通道一致，只发当轮图，避免每轮把旧截图读盘+Base64。
                    if (ReferenceEquals(msg, tempUserMessage))
                    {
                        messageObj["images"] = base64Images;
                    }

                    messages.Add(messageObj);
                }

                var data = new
                {
                    model = _ollamaSetting.Model,
                    messages = messages,
                    stream = _ollamaSetting.EnableStreaming,
                    options = _ollamaSetting.EnableAdvanced ? new
                    {
                        temperature = _ollamaSetting.Temperature,
                        num_predict = _ollamaSetting.MaxTokens
                    } : null
                };
                var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");

                string message;
                using (var client = GetClient())
                {
                    client.BaseAddress = new System.Uri(_ollamaSetting.Url);

                    if (_ollamaSetting.EnableStreaming)
                    {
                        // 流式传输模式
                        Logger.Log("Ollama ChatWithImage: 使用流式传输模式");
                        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
                        {
                            Content = content
                        };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, InterruptManager.Token);
                        response.EnsureSuccessStatusCode();

                        var fullMessage = new StringBuilder();
                        var streamProcessor = new StreamingCommandProcessor((cmd) =>
                        {
                            // 当检测到完整命令时，立即处理（流式模式下逐个命令处理）
                            Logger.Log($"Ollama流式: 检测到完整命令: {cmd}");
                            ResponseHandler?.Invoke(cmd);
                        });

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new System.IO.StreamReader(stream))
                        {
                            string line;
                            while ((line = await ReadStreamLineAsync(reader)) is not null && !InterruptManager.IsInterrupted)
                            {
                                if (string.IsNullOrWhiteSpace(line))
                                    continue;

                                try
                                {
                                    var chunk = JObject.Parse(line);
                                    var delta = chunk["message"]?["content"]?.ToString();
                                    if (!string.IsNullOrEmpty(delta))
                                    {
                                        fullMessage.Append(delta);
                                        // 将新片段传递给流式处理器，检测完整命令
                                        streamProcessor.AddChunk(delta);
                                        // 通知流式文本更新（用于显示）
                                        StreamingChunkHandler?.Invoke(delta);
                                    }

                                    // 检查是否完成
                                    if (chunk["done"]?.Value<bool>() == true)
                                        break;
                                }
                                catch
                                {
                                    // 忽略解析错误，继续处理下一行
                                }
                            }
                        }
                        message = fullMessage.ToString();
                        Logger.Log($"Ollama流式: 流式传输完成，总消息长度: {message.Length}");
                        // 注意：流式模式下不再调用 ResponseHandler，因为已经通过 streamProcessor 逐个处理了
                    }
                    else
                    {
                        // 非流式传输模式
                        Logger.Log("Ollama ChatWithImage: 使用非流式传输模式");
                        var response = await client.PostAsync("/api/chat", content, InterruptManager.Token);
                        response.EnsureSuccessStatusCode();
                        var responseString = await response.Content.ReadAsStringAsync();
                        var responseObject = JObject.Parse(responseString);
                        message = responseObject["message"]["content"].ToString();
                        Logger.Log($"Ollama非流式: 收到完整消息，长度: {message.Length}");
                        // 非流式模式下，一次性处理完整消息
                        ResponseHandler?.Invoke(message);
                    }
                }

                // API调用成功后，才将用户消息和助手回复保存到历史记录
                if (Settings?.KeepContext ?? true)
                {
                    // 先保存用户消息（包含图像数据）
                    if (tempUserMessage is not null)
                    {
                        await HistoryManager.AddMessage(tempUserMessage);
                    }
                    // 再保存助手回复
                    await HistoryManager.AddMessage(new Message { Role = "assistant", Content = AppendInterruptMarker(message) });
                    // 保存历史记录
                    SaveHistory();
                    TriggerOverflowCheckAfterSuccess();
                }
                return "";
            }
            catch (TaskCanceledException tcEx)
            {
                // 用户中断时 HttpClient 抛的就是 TaskCanceledException，不是超时
                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("Ollama 请求已被用户中断");
                    return "";
                }

                Logger.Log($"Ollama ChatWithImage 请求超时: {tcEx.Message}");
                var errorMessage = ErrorMessageHelper.GetOllamaTimeoutError(Settings)
                    ?? $"Ollama 请求超时: {tcEx.Message}";
                ReportFailure(errorMessage);
                return "";
            }
            catch (HttpRequestException httpEx)
            {
                Logger.Log($"Ollama ChatWithImage 网络异常: {httpEx.Message}");
                var errorMessage = ErrorMessageHelper.GetOllamaConnectionError(Settings)
                    ?? $"Ollama 网络异常: {httpEx.Message}";
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

                Logger.Log($"Ollama ChatWithImage 异常: {ex.Message}");
                var errorMessage = ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Ollama");
                ReportFailure(errorMessage);
                return "";
            }
        }

        public override async Task<string> Chat(string prompt, bool isRetry = false)
        {
            try
            {
                // Handle conversation turn for record weight decrement
                OnConversationTurn();

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

                var data = new
                {
                    model = _ollamaSetting.Model,
                    messages = history.Select(m => new { role = m.Role, content = m.DisplayContent }),
                    stream = _ollamaSetting.EnableStreaming,
                    options = _ollamaSetting.EnableAdvanced ? new
                    {
                        temperature = _ollamaSetting.Temperature,
                        num_predict = _ollamaSetting.MaxTokens
                    } : null
                };
                var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");

                string message;
                using (var client = GetClient())
                {
                    client.BaseAddress = new System.Uri(_ollamaSetting.Url);

                    if (_ollamaSetting.EnableStreaming)
                    {
                        // 流式传输模式
                        Logger.Log("Ollama: 使用流式传输模式");
                        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
                        {
                            Content = content
                        };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, InterruptManager.Token);
                        response.EnsureSuccessStatusCode();

                        var fullMessage = new StringBuilder();
                        var streamProcessor = new StreamingCommandProcessor((cmd) =>
                        {
                            // 当检测到完整命令时，立即处理（流式模式下逐个命令处理）
                            Logger.Log($"Ollama流式: 检测到完整命令: {cmd}");
                            ResponseHandler?.Invoke(cmd);
                        });

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new System.IO.StreamReader(stream))
                        {
                            string line;
                            while ((line = await ReadStreamLineAsync(reader)) is not null && !InterruptManager.IsInterrupted)
                            {
                                if (string.IsNullOrWhiteSpace(line))
                                    continue;

                                try
                                {
                                    var chunk = JObject.Parse(line);
                                    var delta = chunk["message"]?["content"]?.ToString();
                                    if (!string.IsNullOrEmpty(delta))
                                    {
                                        fullMessage.Append(delta);
                                        // 将新片段传递给流式处理器，检测完整命令
                                        streamProcessor.AddChunk(delta);
                                        // 通知流式文本更新（用于显示）
                                        StreamingChunkHandler?.Invoke(delta);
                                    }

                                    // 检查是否完成
                                    if (chunk["done"]?.Value<bool>() == true)
                                        break;
                                }
                                catch
                                {
                                    // 忽略解析错误，继续处理下一行
                                }
                            }
                        }
                        message = fullMessage.ToString();
                        Logger.Log($"Ollama流式: 流式传输完成，总消息长度: {message.Length}");
                        // 注意：流式模式下不再调用 ResponseHandler，因为已经通过 streamProcessor 逐个处理了
                    }
                    else
                    {
                        // 非流式传输模式
                        Logger.Log("Ollama: 使用非流式传输模式");
                        var response = await client.PostAsync("/api/chat", content, InterruptManager.Token);
                        response.EnsureSuccessStatusCode();
                        var responseString = await response.Content.ReadAsStringAsync();
                        var responseObject = JObject.Parse(responseString);
                        message = responseObject["message"]["content"].ToString();
                        Logger.Log($"Ollama非流式: 收到完整消息，长度: {message.Length}");
                        // 非流式模式下，一次性处理完整消息
                        ResponseHandler?.Invoke(message);
                    }
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
                    await HistoryManager.AddMessage(new Message { Role = "assistant", Content = AppendInterruptMarker(message) });
                    // 保存历史记录
                    SaveHistory();
                    TriggerOverflowCheckAfterSuccess();
                }
                return "";
            }
            catch (TaskCanceledException tcEx)
            {
                // 用户中断时 HttpClient 抛的就是 TaskCanceledException，不是超时
                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("Ollama 请求已被用户中断");
                    return "";
                }

                Logger.Log($"Ollama Chat 请求超时: {tcEx.Message}");
                var errorMessage = ErrorMessageHelper.GetOllamaTimeoutError(Settings)
                    ?? $"Ollama 请求超时: {tcEx.Message}\n{tcEx.StackTrace}";
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
            catch (HttpRequestException httpEx)
            {
                Logger.Log($"Ollama Chat 网络异常: {httpEx.Message}");
                var errorMessage = ErrorMessageHelper.GetOllamaConnectionError(Settings)
                    ?? $"Ollama 网络异常: {httpEx.Message}\n{httpEx.StackTrace}";
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

                Logger.Log($"Ollama Chat 异常: {ex.Message}");
                var errorMessage = ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Ollama");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
        }

        public override async Task<string> Summarize(string systemPrompt, string userContent)
        {
            try
            {
                var combinedPrompt = $"{systemPrompt}\n\n{userContent}";
                var data = new
                {
                    model = _ollamaSetting.Model,
                    prompt = combinedPrompt,
                    stream = false
                };
                var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
                using (var client = GetClient())
                {
                    client.BaseAddress = new System.Uri(_ollamaSetting.Url);
                    var response = await client.PostAsync("/api/generate", content);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorMessage = await ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Ollama");
                        Logger.Log($"Ollama Summarize 错误: {errorMessage}");
                        return ErrorMessageHelper.IsDebugMode(Settings) ? errorMessage : (ErrorMessageHelper.GetSummarizeError(Settings) ?? "总结失败");
                    }

                    var responseString = await response.Content.ReadAsStringAsync();
                    var responseObject = JObject.Parse(responseString);
                    return responseObject["response"].ToString();
                }
            }
            catch (TaskCanceledException tcEx)
            {
                Logger.Log($"Ollama Summarize 请求超时: {tcEx.Message}");
                return ErrorMessageHelper.GetOllamaTimeoutError(Settings)
                    ?? $"Ollama Summarize 请求超时: {tcEx.Message}";
            }
            catch (HttpRequestException httpEx)
            {
                Logger.Log($"Ollama Summarize 网络异常: {httpEx.Message}");
                return ErrorMessageHelper.GetOllamaConnectionError(Settings)
                    ?? $"Ollama Summarize 网络异常: {httpEx.Message}";
            }
            catch (Exception ex)
            {
                Logger.Log($"Ollama Summarize 异常: {ex.Message}");
                return ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Ollama");
            }
        }

        private async Task<List<Message>> GetCoreHistoryAsync(bool injectRecords = false, string? userQuery = null)
        {
            var result = await GetCoreHistoryCommonAsync(injectRecords, userQuery);
            return CaptureOverflowCheckData(result);
        }
        /// <summary>
        /// 手动刷新可用模型列表
        /// </summary>
        public List<string> RefreshModels()
        {
            try
            {
                using (var client = GetClient())
                {
                    client.BaseAddress = new System.Uri(_ollamaSetting.Url);
                    var response = client.GetAsync("/api/tags").Result;

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorMessage = ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Ollama").Result;
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
                        var parseError = ErrorMessageHelper.IsDebugMode(Settings)
                            ? $"Failed to parse JSON response: {responseString.Substring(0, System.Math.Min(responseString.Length, 100))}"
                            : "获取模型列表失败，服务器返回了无效的响应格式。";
                        throw new System.Exception(parseError);
                    }
                    var models = new List<string>();
                    foreach (var model in responseObject["models"])
                    {
                        models.Add(model["name"].ToString());
                    }
                    return models;
                }
            }
            catch (System.Exception ex) when (!(ex.Message.Contains("API") || ex.Message.Contains("获取模型")))
            {
                var errorMessage = ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Ollama");
                throw new System.Exception(errorMessage);
            }
        }

        public new List<string> GetModels()
        {
            // 返回空列表，避免启动时自动扫描
            return new List<string>();
        }
    }
}