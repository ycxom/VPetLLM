using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Linq;
using VPet_Simulator.Windows.Interface;
using ErrorHelper = global::VPetLLM.Utils.System.ErrorMessageHelper;
using SystemLogger = global::VPetLLM.Utils.System.Logger;

namespace VPetLLM.Core.Providers.Chat
{
    public class LMStudioChatCore : ChatCoreBase
    {
        public override string Name => "LM Studio";
        private readonly Setting.LMStudioSetting _lmStudioSetting;
        private readonly Setting _setting;

        public LMStudioChatCore(Setting.LMStudioSetting lmStudioSetting, Setting setting, IMainWindow mainWindow, ActionProcessor actionProcessor)
            : base(setting, mainWindow, actionProcessor)
        {
            _lmStudioSetting = lmStudioSetting;
            _setting = setting;
        }

        private string GetCurrentApiUrl()
        {
            string apiUrl = _lmStudioSetting.Url;
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

        public override Task<string> Chat(string prompt)
        {
            return Chat(prompt, false);
        }

        private List<Message> GetCoreHistory(bool injectRecords = false)
        {
            var history = new List<Message>
            {
                new Message { Role = "system", Content = GetSystemMessage() }
            };
            history.AddRange(HistoryManager.GetHistory().Skip(Math.Max(0, HistoryManager.GetHistory().Count - _setting.HistoryCompressionThreshold)));

            if (injectRecords)
            {
                history = InjectRecordsIntoHistory(history);
            }

            return history;
        }

        public override async Task<string> Chat(string prompt, bool isFunctionCall = false)
        {
            try
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

                var messages = history.Select(m => new { role = m.Role, content = m.DisplayContent }).ToList();

                object data;
                if (_lmStudioSetting.EnableAdvanced)
                {
                    data = new
                    {
                        model = _lmStudioSetting.Model ?? "local-model",
                        messages = messages,
                        temperature = _lmStudioSetting.Temperature,
                        max_tokens = _lmStudioSetting.MaxTokens,
                        stream = _lmStudioSetting.EnableStreaming
                    };
                }
                else
                {
                    data = new
                    {
                        model = _lmStudioSetting.Model ?? "local-model",
                        messages = messages,
                        stream = _lmStudioSetting.EnableStreaming
                    };
                }

                var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
                string message;

                using (var client = GetClient())
                {
                    var apiUrl = GetCurrentApiUrl();
                    SystemLogger.Log($"LM Studio: 请求 URL = {apiUrl}");

                    if (_lmStudioSetting.EnableStreaming)
                    {
                        SystemLogger.Log("LM Studio: 使用流式传输模式");
                        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                        {
                            Content = content
                        };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await HandleHttpError(response, Settings, "LM Studio");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }

                        var fullMessage = new StringBuilder();
                        var streamProcessor = new StreamingCommandProcessor((cmd) =>
                        {
                            SystemLogger.Log($"LM Studio流式: 检测到完整命令: {cmd}");
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
                        SystemLogger.Log($"LM Studio流式: 流式传输完成，总消息长度: {message.Length}");
                    }
                    else
                    {
                        SystemLogger.Log("LM Studio: 使用非流式传输模式");
                        var response = await client.PostAsync(apiUrl, content);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await HandleHttpError(response, Settings, "LM Studio");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }

                        var responseString = await response.Content.ReadAsStringAsync();
                        var responseObject = JObject.Parse(responseString);
                        message = responseObject["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
                        SystemLogger.Log($"LM Studio非流式: 收到完整消息，长度: {message.Length}");
                        ResponseHandler?.Invoke(message);
                    }
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
            catch (TaskCanceledException tcEx)
            {
                SystemLogger.Log($"LM Studio Chat 请求超时: {tcEx.Message}");
                var errorMessage = ErrorHelper.GetOllamaTimeoutError(Settings)
                    ?? $"LM Studio 请求超时: {tcEx.Message}";
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
            catch (HttpRequestException httpEx)
            {
                SystemLogger.Log($"LM Studio Chat 网络异常: {httpEx.Message}");
                var errorMessage = ErrorHelper.GetOllamaConnectionError(Settings)
                    ?? $"LM Studio 网络异常: {httpEx.Message}";
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
            catch (Exception ex)
            {
                SystemLogger.Log($"LM Studio Chat 异常: {ex.Message}");
                var errorMessage = ErrorHelper.GetFriendlyExceptionError(ex, Settings, "LM Studio");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
        }

        public override async Task<string> ChatWithImage(string prompt, byte[] imageData)
        {
            if (!_lmStudioSetting.EnableVision)
            {
                var visionError = "LM Studio 未启用视觉能力，请在设置中启用 EnableVision";
                SystemLogger.Log($"LM Studio ChatWithImage 错误: {visionError}");
                ResponseHandler?.Invoke(visionError);
                return "";
            }

            try
            {
                OnConversationTurn();

                if (!Settings.KeepContext)
                {
                    ClearContext();
                }

                var base64Image = Convert.ToBase64String(imageData);
                var userContent = new object[]
                {
                    new { type = "text", text = prompt },
                    new { type = "image_url", image_url = new { url = $"data:image/png;base64,{base64Image}" } }
                };

                List<Message> history = GetCoreHistory();
                var requestMessages = new List<object>();
                foreach (var msg in history)
                {
                    requestMessages.Add(new { role = msg.Role, content = msg.DisplayContent });
                }
                requestMessages.Add(new { role = "user", content = userContent });

                var tempUserMessage = CreateUserMessage($"[图像] {prompt}");
                if (tempUserMessage is not null)
                {
                    tempUserMessage.ImageData = imageData;
                }

                object data;
                if (_lmStudioSetting.EnableAdvanced)
                {
                    data = new
                    {
                        model = _lmStudioSetting.Model ?? "local-model",
                        messages = requestMessages,
                        temperature = _lmStudioSetting.Temperature,
                        max_tokens = _lmStudioSetting.MaxTokens,
                        stream = _lmStudioSetting.EnableStreaming
                    };
                }
                else
                {
                    data = new
                    {
                        model = _lmStudioSetting.Model ?? "local-model",
                        messages = requestMessages,
                        stream = _lmStudioSetting.EnableStreaming
                    };
                }

                var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
                string message;

                using (var client = GetClient())
                {
                    var apiUrl = GetCurrentApiUrl();
                    SystemLogger.Log($"LM Studio ChatWithImage: 请求 URL = {apiUrl}");

                    if (_lmStudioSetting.EnableStreaming)
                    {
                        SystemLogger.Log("LM Studio ChatWithImage: 使用流式传输模式");
                        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                        {
                            Content = content
                        };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await HandleHttpError(response, Settings, "LM Studio");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }

                        var fullMessage = new StringBuilder();
                        var streamProcessor = new StreamingCommandProcessor((cmd) =>
                        {
                            SystemLogger.Log($"LM Studio流式: 检测到完整命令: {cmd}");
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
                        SystemLogger.Log($"LM Studio ChatWithImage 流式: 传输完成，长度: {message.Length}");
                    }
                    else
                    {
                        SystemLogger.Log("LM Studio ChatWithImage: 使用非流式传输模式");
                        var response = await client.PostAsync(apiUrl, content);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = await HandleHttpError(response, Settings, "LM Studio");
                            ResponseHandler?.Invoke(errorMessage);
                            return "";
                        }

                        var responseString = await response.Content.ReadAsStringAsync();
                        var responseObject = JObject.Parse(responseString);
                        message = responseObject["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
                        SystemLogger.Log($"LM Studio ChatWithImage 非流式: 收到完整消息，长度: {message.Length}");
                        ResponseHandler?.Invoke(message);
                    }
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
            catch (TaskCanceledException tcEx)
            {
                SystemLogger.Log($"LM Studio ChatWithImage 请求超时: {tcEx.Message}");
                var errorMessage = ErrorHelper.GetOllamaTimeoutError(Settings)
                    ?? $"LM Studio 请求超时: {tcEx.Message}";
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
            catch (HttpRequestException httpEx)
            {
                SystemLogger.Log($"LM Studio ChatWithImage 网络异常: {httpEx.Message}");
                var errorMessage = ErrorHelper.GetOllamaConnectionError(Settings)
                    ?? $"LM Studio 网络异常: {httpEx.Message}";
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
            catch (Exception ex)
            {
                SystemLogger.Log($"LM Studio ChatWithImage 异常: {ex.Message}");
                var errorMessage = ErrorHelper.GetFriendlyExceptionError(ex, Settings, "LM Studio");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
        }

        public override async Task<string> Summarize(string systemPrompt, string userContent)
        {
            try
            {
                var messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userContent }
                };

                var data = new
                {
                    model = _lmStudioSetting.Model ?? "local-model",
                    messages = messages,
                    stream = false
                };

                var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
                using (var client = GetClient())
                {
                    var apiUrl = GetCurrentApiUrl();
                    var response = await client.PostAsync(apiUrl, content);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorMessage = await HandleHttpError(response, Settings, "LM Studio");
                        SystemLogger.Log($"LM Studio Summarize 错误: {errorMessage}");
                        return ErrorHelper.IsDebugMode(Settings) ? errorMessage : (ErrorHelper.GetSummarizeError(Settings) ?? "总结失败");
                    }

                    var responseString = await response.Content.ReadAsStringAsync();
                    var responseObject = JObject.Parse(responseString);
                    return responseObject["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
                }
            }
            catch (TaskCanceledException tcEx)
            {
                SystemLogger.Log($"LM Studio Summarize 请求超时: {tcEx.Message}");
                return ErrorHelper.GetOllamaTimeoutError(Settings)
                    ?? $"LM Studio Summarize 请求超时: {tcEx.Message}";
            }
            catch (HttpRequestException httpEx)
            {
                SystemLogger.Log($"LM Studio Summarize 网络异常: {httpEx.Message}");
                return ErrorHelper.GetOllamaConnectionError(Settings)
                    ?? $"LM Studio Summarize 网络异常: {httpEx.Message}";
            }
            catch (Exception ex)
            {
                SystemLogger.Log($"LM Studio Summarize 异常: {ex.Message}");
                return ErrorHelper.GetFriendlyExceptionError(ex, Settings, "LM Studio");
            }
        }

        public List<string> RefreshModels()
        {
            try
            {
                using (var client = GetClient())
                {
                    var baseUrl = _lmStudioSetting.Url.TrimEnd('/');
                    client.BaseAddress = new System.Uri(baseUrl);
                    var response = client.GetAsync("/v1/models").Result;

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorMessage = HandleHttpError(response, Settings, "LM Studio").Result;
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
                            : "获取模型列表失败，服务器返回了无效的响应格式。";
                        throw new System.Exception(parseError);
                    }
                    var models = new List<string>();
                    if (responseObject["data"] != null)
                    {
                        foreach (var model in responseObject["data"])
                        {
                            if (model["id"] != null)
                            {
                                models.Add(model["id"].ToString());
                            }
                        }
                    }
                    return models;
                }
            }
            catch (System.Exception ex) when (!(ex.Message.Contains("API") || ex.Message.Contains("获取模型")))
            {
                SystemLogger.Log($"LM Studio RefreshModels 异常: {ex.Message}");
                throw;
            }
        }

        private async Task<string> HandleHttpError(HttpResponseMessage response, Setting settings, string providerName)
        {
            var statusCode = response.StatusCode;
            var rawError = await response.Content.ReadAsStringAsync();

            SystemLogger.Log($"{providerName} API 错误: {(int)statusCode} {statusCode} - {rawError}");

            if (ErrorHelper.IsDebugMode(settings))
            {
                return $"{providerName} API 错误 [{(int)statusCode} {statusCode}]: {rawError}";
            }

            return ErrorHelper.GetFriendlyHttpError(statusCode, rawError, settings);
        }
    }
}