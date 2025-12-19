using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Text;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers;

namespace VPetLLM.Core.ChatCore
{
    public class FreeChatCore : ChatCoreBase
    {
        public override string Name => "Free";
        private readonly Setting.FreeSetting _freeSetting;
        private readonly HttpClient _httpClient;

        private string _apiKey;
        private string _apiUrl;
        private string _model;
        
        // 保留硬编码的User-Agent
        private const string ENCODED_UA = "566c426c6445784d54563947636d566c58304a3558304a5a54513d3d";

        public FreeChatCore(Setting.FreeSetting freeSetting, Setting setting, IMainWindow mainWindow, ActionProcessor actionProcessor)
            : base(setting, mainWindow, actionProcessor)
        {
            _freeSetting = freeSetting;

            LoadConfig();

            // 使用基类的代理设置逻辑
            var handler = CreateHttpClientHandler();
            _httpClient = new HttpClient(handler);

            // 设置超时时间为30秒，避免长时间等待
            _httpClient.Timeout = TimeSpan.FromSeconds(30);

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
                var config = Utils.FreeConfigManager.GetChatConfig();
                if (config != null)
                {
                    _apiKey = DecodeString(config["API_KEY"]?.ToString() ?? "");
                    _apiUrl = DecodeString(config["API_URL"]?.ToString() ?? "");
                    _model = config["Model"]?.ToString() ?? "";
                    Utils.Logger.Log("FreeChatCore: 配置加载成功");
                }
                else
                {
                    Utils.Logger.Log("FreeChatCore: 配置文件不存在，请等待配置下载完成后重启程序");
                    _apiKey = "";
                    _apiUrl = "";
                    _model = "";
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"FreeChatCore: 加载配置失败: {ex.Message}");
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
        public override async Task<string> ChatWithImage(string prompt, byte[] imageData)
        {
            try
            {
                // Handle conversation turn for record weight decrement
                OnConversationTurn();
                
                if (string.IsNullOrEmpty(_apiUrl) || string.IsNullOrEmpty(_apiKey))
                {
                    var errorMessage = Utils.ErrorMessageHelper.GetFreeApiError(Settings, "ConfigNotLoaded") 
                        ?? "Free Chat 配置未加载，请等待配置下载完成后重启程序";
                    Utils.Logger.Log(errorMessage);
                    ResponseHandler?.Invoke(errorMessage);
                    return "";
                }

                // 检查视觉能力是否启用
                if (!_freeSetting.EnableVision)
                {
                    var visionError = "Free 接口未启用视觉能力，请在设置中启用 EnableVision";
                    Utils.Logger.Log($"Free ChatWithImage 错误: {visionError}");
                    ResponseHandler?.Invoke(visionError);
                    return "";
                }

                if (!Settings.KeepContext)
                {
                    ClearContext();
                }

                Utils.Logger.Log($"Free ChatWithImage: 发送多模态消息，图像大小: {imageData.Length} bytes");

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

                var requestBody = new
                {
                    model = _model,
                    messages = requestMessages,
                    temperature = _freeSetting.Temperature,
                    max_tokens = _freeSetting.MaxTokens,
                    stream = _freeSetting.EnableStreaming
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                string message;
                if (_freeSetting.EnableStreaming)
                {
                    // 流式传输模式
                    Utils.Logger.Log("Free ChatWithImage: 使用流式传输模式");
                    var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl)
                    {
                        Content = content
                    };
                    var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                    if (response.IsSuccessStatusCode)
                    {
                        var fullMessage = new StringBuilder();
                        var streamProcessor = new Handlers.StreamingCommandProcessor((cmd) =>
                        {
                            Utils.Logger.Log($"Free流式: 检测到完整命令: {cmd}");
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
                        Utils.Logger.Log($"Free流式: 流式传输完成，总消息长度: {message.Length}");
                    }
                    else
                    {
                        var responseContent = await response.Content.ReadAsStringAsync();
                        Utils.Logger.Log($"Free ChatWithImage API 错误: {response.StatusCode} - {responseContent}");
                        message = Utils.ErrorMessageHelper.IsDebugMode(Settings)
                            ? $"API调用失败: {response.StatusCode} - {responseContent}"
                            : await Utils.ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Free");
                        ResponseHandler?.Invoke(message);
                        return "";
                    }
                }
                else
                {
                    // 非流式传输模式
                    Utils.Logger.Log("Free ChatWithImage: 使用非流式传输模式");
                    var response = await _httpClient.PostAsync(_apiUrl, content);
                    var responseContent = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        var responseObj = JsonConvert.DeserializeObject<JObject>(responseContent);
                        message = responseObj?["choices"]?[0]?["message"]?["content"]?.ToString() ?? "无回复";
                        Utils.Logger.Log($"Free非流式: 收到完整消息，长度: {message.Length}");
                        ResponseHandler?.Invoke(message);
                    }
                    else
                    {
                        Utils.Logger.Log($"Free ChatWithImage API 错误: {response.StatusCode} - {responseContent}");
                        message = Utils.ErrorMessageHelper.IsDebugMode(Settings)
                            ? $"API调用失败: {response.StatusCode} - {responseContent}"
                            : Utils.ErrorMessageHelper.GetFriendlyHttpError(response.StatusCode, responseContent, Settings);
                        ResponseHandler?.Invoke(message);
                        return "";
                    }
                }

                // 保存历史记录（不保存图像数据，只保存文本）
                if (Settings.KeepContext)
                {
                    var userMessage = CreateUserMessage($"[图像] {prompt}");
                    if (userMessage != null)
                    {
                        await HistoryManager.AddMessage(userMessage);
                    }
                    await HistoryManager.AddMessage(new Message { Role = "assistant", Content = message });
                    SaveHistory();
                }

                return "";
            }
            catch (HttpRequestException httpEx)
            {
                Utils.Logger.Log($"Free ChatWithImage 网络异常: {httpEx.Message}");
                var errorMessage = Utils.ErrorMessageHelper.IsDebugMode(Settings)
                    ? $"Free ChatWithImage 网络异常: {httpEx.Message}\n{httpEx.StackTrace}"
                    : Utils.ErrorMessageHelper.GetFriendlyExceptionError(httpEx, Settings, "Free");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
            catch (TaskCanceledException tcEx)
            {
                Utils.Logger.Log($"Free ChatWithImage 请求超时: {tcEx.Message}");
                var errorMessage = Utils.ErrorMessageHelper.IsDebugMode(Settings)
                    ? $"Free ChatWithImage 请求超时: {tcEx.Message}\n{tcEx.StackTrace}"
                    : Utils.ErrorMessageHelper.GetFriendlyExceptionError(tcEx, Settings, "Free");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Free ChatWithImage 异常: {ex.Message}");
                var errorMessage = Utils.ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Free");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
        }

        public override async Task<string> Chat(string prompt, bool isRetry)
        {
            try
            {
                // Handle conversation turn for record weight decrement
                OnConversationTurn();
                
                if (string.IsNullOrEmpty(_apiUrl) || string.IsNullOrEmpty(_apiKey))
                {
                    var errorMessage = Utils.ErrorMessageHelper.GetFreeApiError(Settings, "ConfigNotLoaded") 
                        ?? "Free Chat 配置未加载，请等待配置下载完成后重启程序";
                    Utils.Logger.Log(errorMessage);
                    ResponseHandler?.Invoke(errorMessage);
                    return "";
                }

                if (!Settings.KeepContext)
                {
                    ClearContext();
                }

                // 临时构建包含当前用户消息的历史记录（用于API请求），但不立即保存到数据库
                // 使用 CreateUserMessage 自动设置时间戳和状态信息
                var tempUserMessage = CreateUserMessage(prompt);

                // 构建请求数据，使用和OpenAI相同的逻辑
                List<Message> history = GetCoreHistory();
                // 如果有临时用户消息，添加到历史末尾用于API请求
                if (tempUserMessage != null)
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
                    max_tokens = _freeSetting.MaxTokens,
                    stream = _freeSetting.EnableStreaming
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                string message;
                if (_freeSetting.EnableStreaming)
                {
                    // 流式传输模式
                    Utils.Logger.Log("Free: 使用流式传输模式");
                    var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl)
                    {
                        Content = content
                    };
                    var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var fullMessage = new StringBuilder();
                        var streamProcessor = new Handlers.StreamingCommandProcessor((cmd) =>
                        {
                            // 当检测到完整命令时，立即处理（流式模式下逐个命令处理）
                            Utils.Logger.Log($"Free流式: 检测到完整命令: {cmd}");
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
                        Utils.Logger.Log($"Free流式: 流式传输完成，总消息长度: {message.Length}");
                        // 注意：流式模式下不再调用 ResponseHandler，因为已经通过 streamProcessor 逐个处理了
                        if (string.IsNullOrEmpty(message))
                        {
                            message = "无回复";
                        }
                        
                        // API调用成功后，才将用户消息和助手回复保存到历史记录
                        if (Settings.KeepContext)
                        {
                            if (tempUserMessage != null)
                            {
                                await HistoryManager.AddMessage(tempUserMessage);
                            }
                            await HistoryManager.AddMessage(new Message { Role = "assistant", Content = message });
                            SaveHistory();
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
                            Utils.Logger.Log($"Free API 服务器错误: {response.StatusCode} - {responseContent}");

                            // 如果不是重试，尝试重试一次
                            if (!isRetry)
                            {
                                Utils.Logger.Log("尝试重试 Free API 请求...");
                                await Task.Delay(2000); // 等待2秒后重试
                                return await Chat(prompt, true);
                            }

                            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                message = Utils.ErrorMessageHelper.GetFreeApiError(Settings, "ServiceMaintenance") 
                                    ?? "Free API 服务正在维护中，请稍后再试。如果问题持续存在，请联系开发者。";
                            }
                            else
                            {
                                message = Utils.ErrorMessageHelper.GetFreeApiError(Settings, "ServiceUnavailable") 
                                    ?? "Free API 服务暂时不可用，请稍后再试。这可能是由于服务器负载过高或维护导致的。";
                            }
                        }
                        else
                        {
                            Utils.Logger.Log($"Free API 错误: {response.StatusCode} - {responseContent}");
                            message = Utils.ErrorMessageHelper.IsDebugMode(Settings)
                                ? $"API调用失败: {response.StatusCode} - {responseContent}"
                                : await Utils.ErrorMessageHelper.HandleHttpResponseError(response, Settings, "Free");
                        }
                    }
                }
                else
                {
                    // 非流式传输模式
                    Utils.Logger.Log("Free: 使用非流式传输模式");
                    var response = await _httpClient.PostAsync(_apiUrl, content);
                    var responseContent = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        var responseObj = JsonConvert.DeserializeObject<JObject>(responseContent);
                        message = responseObj?["choices"]?[0]?["message"]?["content"]?.ToString() ?? "无回复";
                        Utils.Logger.Log($"Free非流式: 收到完整消息，长度: {message.Length}");
                        // 非流式模式下，一次性处理完整消息
                        ResponseHandler?.Invoke(message);
                        
                        // API调用成功后，才将用户消息和助手回复保存到历史记录
                        if (Settings.KeepContext)
                        {
                            if (tempUserMessage != null)
                            {
                                await HistoryManager.AddMessage(tempUserMessage);
                            }
                            await HistoryManager.AddMessage(new Message { Role = "assistant", Content = message });
                            SaveHistory();
                        }
                    }
                    else
                    {
                        // 检查是否是服务器错误
                        if (responseContent.Contains("Failed to retrieve proxy group") ||
                            responseContent.Contains("INTERNAL_SERVER_ERROR") ||
                            response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            Utils.Logger.Log($"Free API 服务器错误: {response.StatusCode} - {responseContent}");

                            // 如果不是重试，尝试重试一次
                            if (!isRetry)
                            {
                                Utils.Logger.Log("尝试重试 Free API 请求...");
                                await Task.Delay(2000); // 等待2秒后重试
                                return await Chat(prompt, true);
                            }

                            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                message = Utils.ErrorMessageHelper.GetFreeApiError(Settings, "ServiceMaintenance") 
                                    ?? "Free API 服务正在维护中，请稍后再试。如果问题持续存在，请联系开发者。";
                            }
                            else
                            {
                                message = Utils.ErrorMessageHelper.GetFreeApiError(Settings, "ServiceUnavailable") 
                                    ?? "Free API 服务暂时不可用，请稍后再试。这可能是由于服务器负载过高或维护导致的。";
                            }
                        }
                        else
                        {
                            Utils.Logger.Log($"Free API 错误: {response.StatusCode} - {responseContent}");
                            message = Utils.ErrorMessageHelper.IsDebugMode(Settings)
                                ? $"API调用失败: {response.StatusCode} - {responseContent}"
                                : Utils.ErrorMessageHelper.GetFriendlyHttpError(response.StatusCode, responseContent, Settings);
                        }
                    }
                }

                // 注意：ResponseHandler 已经在流式/非流式模式的各自分支中调用过了，这里不需要再次调用
                return "";
            }
            catch (HttpRequestException httpEx)
            {
                Utils.Logger.Log($"Free Chat 网络异常: {httpEx.Message}");

                // 如果不是重试，尝试重试一次
                if (!isRetry)
                {
                    Utils.Logger.Log("网络异常，尝试重试 Free API 请求...");
                    await Task.Delay(2000); // 等待2秒后重试
                    return await Chat(prompt, true);
                }

                var errorMessage = Utils.ErrorMessageHelper.IsDebugMode(Settings)
                    ? $"Free Chat 网络异常: {httpEx.Message}\n{httpEx.StackTrace}"
                    : Utils.ErrorMessageHelper.GetFriendlyExceptionError(httpEx, Settings, "Free");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
            catch (TaskCanceledException tcEx)
            {
                Utils.Logger.Log($"Free Chat 请求超时: {tcEx.Message}");
                var errorMessage = Utils.ErrorMessageHelper.IsDebugMode(Settings)
                    ? $"Free Chat 请求超时: {tcEx.Message}\n{tcEx.StackTrace}"
                    : Utils.ErrorMessageHelper.GetFriendlyExceptionError(tcEx, Settings, "Free");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Free Chat 异常: {ex.Message}");
                var errorMessage = Utils.ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Free");
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
                    Utils.Logger.Log("Free Chat 配置未加载，总结功能不可用");
                    return Utils.ErrorMessageHelper.GetFreeApiError(Settings, "ConfigNotLoaded") 
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
                    max_tokens = _freeSetting.MaxTokens
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
                        Utils.Logger.Log($"Free Summarize 服务器内部错误: {responseContent}");
                        return Utils.ErrorMessageHelper.GetFreeApiError(Settings, "ServiceUnavailable") 
                            ?? "Free API 服务暂时不可用，总结功能无法使用。";
                    }

                    Utils.Logger.Log($"Free Summarize 错误: {response.StatusCode} - {responseContent}");
                    return Utils.ErrorMessageHelper.GetSummarizeError(Settings) ?? "总结失败";
                }
            }
            catch (HttpRequestException httpEx)
            {
                Utils.Logger.Log($"Free Summarize 网络异常: {httpEx.Message}");
                return Utils.ErrorMessageHelper.IsDebugMode(Settings)
                    ? $"Free Summarize 网络异常: {httpEx.Message}"
                    : Utils.ErrorMessageHelper.GetFriendlyExceptionError(httpEx, Settings, "Free");
            }
            catch (TaskCanceledException tcEx)
            {
                Utils.Logger.Log($"Free Summarize 请求超时: {tcEx.Message}");
                return Utils.ErrorMessageHelper.IsDebugMode(Settings)
                    ? $"Free Summarize 请求超时: {tcEx.Message}"
                    : Utils.ErrorMessageHelper.GetFriendlyExceptionError(tcEx, Settings, "Free");
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Free Summarize 异常: {ex.Message}");
                return Utils.ErrorMessageHelper.GetFriendlyExceptionError(ex, Settings, "Free");
            }
        }

        private List<Message> GetCoreHistory(bool injectRecords = false)
        {
            var history = new List<Message>
            {
                new Message { Role = "system", Content = GetSystemMessage() }
            };
            history.AddRange(HistoryManager.GetHistory().Skip(Math.Max(0, HistoryManager.GetHistory().Count - Settings.HistoryCompressionThreshold)));
            
            // Inject important records into history (only when explicitly requested, after user message is added)
            if (injectRecords)
            {
                history = InjectRecordsIntoHistory(history);
            }
            
            return history;
        }

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