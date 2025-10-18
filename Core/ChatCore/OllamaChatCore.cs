using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Text;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers;

namespace VPetLLM.Core.ChatCore
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



        public override Task<string> Chat(string prompt)
        {
            return Chat(prompt, false);
        }
        public override async Task<string> Chat(string prompt, bool isFunctionCall = false)
        {
            try
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
                    client.Timeout = TimeSpan.FromSeconds(120); // 设置2分钟超时
                    
                    if (_ollamaSetting.EnableStreaming)
                    {
                        // 流式传输模式
                        Utils.Logger.Log("Ollama: 使用流式传输模式");
                        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
                        {
                            Content = content
                        };
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                        response.EnsureSuccessStatusCode();
                        
                        var fullMessage = new StringBuilder();
                        var streamProcessor = new Handlers.StreamingCommandProcessor((cmd) =>
                        {
                            // 当检测到完整命令时，立即处理（流式模式下逐个命令处理）
                            Utils.Logger.Log($"Ollama流式: 检测到完整命令: {cmd}");
                            ResponseHandler?.Invoke(cmd);
                        });
                        
                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new System.IO.StreamReader(stream))
                        {
                            string line;
                            while ((line = await reader.ReadLineAsync()) != null)
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
                        Utils.Logger.Log($"Ollama流式: 流式传输完成，总消息长度: {message.Length}");
                        // 注意：流式模式下不再调用 ResponseHandler，因为已经通过 streamProcessor 逐个处理了
                    }
                    else
                    {
                        // 非流式传输模式
                        Utils.Logger.Log("Ollama: 使用非流式传输模式");
                        var response = await client.PostAsync("/api/chat", content);
                        response.EnsureSuccessStatusCode();
                        var responseString = await response.Content.ReadAsStringAsync();
                        var responseObject = JObject.Parse(responseString);
                        message = responseObject["message"]["content"].ToString();
                        Utils.Logger.Log($"Ollama非流式: 收到完整消息，长度: {message.Length}");
                        // 非流式模式下，一次性处理完整消息
                        ResponseHandler?.Invoke(message);
                    }
                }
                
                // 根据上下文设置决定是否保留历史（使用基类的统一状态）
                if (Settings.KeepContext)
                {
                    await HistoryManager.AddMessage(new Message { Role = "assistant", Content = message });
                }
                // 只有在保持上下文模式时才保存历史记录
                if (Settings.KeepContext)
                {
                    SaveHistory();
                }
                return "";
            }
            catch (TaskCanceledException tcEx)
            {
                var errorMessage = "Ollama请求超时，请检查：\n1. Ollama服务是否正常运行\n2. 模型是否已下载\n3. 网络连接是否正常\n4. URL配置是否正确";
                Utils.Logger.Log($"Ollama Chat 请求超时: {tcEx.Message}");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
            catch (HttpRequestException httpEx)
            {
                var errorMessage = "Ollama连接失败，请检查：\n1. Ollama服务是否启动\n2. URL配置是否正确\n3. 防火墙设置";
                Utils.Logger.Log($"Ollama Chat 网络异常: {httpEx.Message}");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
            catch (Exception ex)
            {
                var errorMessage = $"Ollama聊天异常: {ex.Message}";
                Utils.Logger.Log($"Ollama Chat 异常: {ex.Message}");
                ResponseHandler?.Invoke(errorMessage);
                return "";
            }
        }

        public override async Task<string> Summarize(string text)
        {
            try
            {
                var data = new
                {
                    model = _ollamaSetting.Model,
                    prompt = text,
                    stream = false
                };
                var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
                using (var client = GetClient())
                {
                    client.BaseAddress = new System.Uri(_ollamaSetting.Url);
                    client.Timeout = TimeSpan.FromSeconds(120); // 设置2分钟超时
                    var response = await client.PostAsync("/api/generate", content);
                    response.EnsureSuccessStatusCode();
                    var responseString = await response.Content.ReadAsStringAsync();
                    var responseObject = JObject.Parse(responseString);
                    return responseObject["response"].ToString();
                }
            }
            catch (TaskCanceledException tcEx)
            {
                Utils.Logger.Log($"Ollama Summarize 请求超时: {tcEx.Message}");
                return "总结请求超时，请检查Ollama服务状态";
            }
            catch (HttpRequestException httpEx)
            {
                Utils.Logger.Log($"Ollama Summarize 网络异常: {httpEx.Message}");
                return "Ollama连接失败，无法完成总结";
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Ollama Summarize 异常: {ex.Message}");
                return $"总结异常: {ex.Message}";
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
        /// <summary>
        /// 手动刷新可用模型列表
        /// </summary>
        public List<string> RefreshModels()
        {
            using (var client = GetClient())
            {
                client.BaseAddress = new System.Uri(_ollamaSetting.Url);
                client.Timeout = TimeSpan.FromSeconds(30); // 获取模型列表用较短超时
                var response = client.GetAsync("/api/tags").Result;
                response.EnsureSuccessStatusCode();
                var responseString = response.Content.ReadAsStringAsync().Result;
                JObject responseObject;
                try
                {
                    responseObject = JObject.Parse(responseString);
                }
                catch (JsonReaderException)
                {
                    throw new System.Exception($"Failed to parse JSON response: {responseString.Substring(0, System.Math.Min(responseString.Length, 100))}");
                }
                var models = new List<string>();
                foreach (var model in responseObject["models"])
                {
                    models.Add(model["name"].ToString());
                }
                return models;
            }
        }

        public new List<string> GetModels()
        {
            // 返回空列表，避免启动时自动扫描
            return new List<string>();
        }
    }
}