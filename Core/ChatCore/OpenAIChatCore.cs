using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers;

namespace VPetLLM.Core.ChatCore
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

        private Setting.OpenAINodeSetting GetCurrentNode()
        {
            // 若存在单次请求的缓存节点，则优先返回并清空，确保同一请求的一致性
            if (_currentNodeContext != null)
            {
                var ctx = _currentNodeContext;
                _currentNodeContext = null;
                return ctx;
            }
            // 每次调用都按当前设置进行“渠道随机”
            var nodes = _openAISetting.OpenAINodes ?? new List<Setting.OpenAINodeSetting>();
            if (nodes.Count == 0)
            {
                // 兼容无节点：从全局 OpenAISetting 构造一个临时节点
                return new Setting.OpenAINodeSetting
                {
                    ApiKey = _openAISetting.ApiKey,
                    Model = _openAISetting.Model,
                    Url = _openAISetting.Url,
                    Temperature = _openAISetting.Temperature,
                    MaxTokens = _openAISetting.MaxTokens,
                    EnableAdvanced = _openAISetting.EnableAdvanced,
                    Enabled = _openAISetting.Enabled,
                    Name = _openAISetting.Name
                };
            }

            var enabled = nodes.Where(n => n.Enabled).ToList();
            if (enabled.Count == 0)
            {
                // 无启用节点时，回退到第一个
                return nodes.First();
            }

            if (_openAISetting.EnableLoadBalancing)
            {
                // 负载均衡开启：在启用的节点中随机选择一个
                return enabled[_random.Next(enabled.Count)];
            }
            else
            {
                // 未开启负载均衡：固定索引，否则使用第一个
                if (_openAISetting.CurrentNodeIndex >= 0 && _openAISetting.CurrentNodeIndex < nodes.Count)
                    return nodes[_openAISetting.CurrentNodeIndex];
                return nodes.First();
            }
        }

        private string GetCurrentApiKey(Setting.OpenAINodeSetting node)
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

        private (string apiUrl, string apiKey) GetCurrentEndpoint()
        {
            var currentNode = GetCurrentNode();
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

            // 将本次选中的节点写入上下文缓存，供同一请求中后续调用复用
            _currentNodeContext = currentNode;
            return (apiUrl, currentApiKey);
        }

        public override Task<string> Chat(string prompt)
        {
            return Chat(prompt, false);
        }
        public override async Task<string> Chat(string prompt, bool isFunctionCall = false)
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
            
            // 获取当前节点和API Key
            var (apiUrl, apiKey) = GetCurrentEndpoint();
            var currentNode = GetCurrentNode();
            
            // 构建请求数据，根据启用开关决定是否包含高级参数
            List<Message> history = GetCoreHistory();
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
            using (var client = GetClient())
            {
                if (!string.IsNullOrEmpty(apiKey))
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                }
                
                if (currentNode.EnableStreaming)
                {
                    // 流式传输模式
                    Utils.Logger.Log("OpenAI: 使用流式传输模式");
                    var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                    {
                        Content = content
                    };
                    var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    
                    var fullMessage = new StringBuilder();
                    var streamProcessor = new Handlers.StreamingCommandProcessor((cmd) =>
                    {
                        // 当检测到完整命令时，立即处理（流式模式下逐个命令处理）
                        Utils.Logger.Log($"OpenAI流式: 检测到完整命令: {cmd}");
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
                    Utils.Logger.Log($"OpenAI流式: 流式传输完成，总消息长度: {message.Length}");
                    // 注意：流式模式下不再调用 ResponseHandler，因为已经通过 streamProcessor 逐个处理了
                }
                else
                {
                    // 非流式传输模式
                    Utils.Logger.Log("OpenAI: 使用非流式传输模式");
                    var response = await client.PostAsync(apiUrl, content);
                    response.EnsureSuccessStatusCode();
                    var responseString = await response.Content.ReadAsStringAsync();
                    var responseObject = JObject.Parse(responseString);
                    message = responseObject["choices"][0]["message"]["content"].ToString();
                    Utils.Logger.Log($"OpenAI非流式: 收到完整消息，长度: {message.Length}");
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

        public override async Task<string> Summarize(string systemPrompt, string userContent)
        {
            var messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            };

            // 获取当前节点和API Key
            var (apiUrl, apiKey) = GetCurrentEndpoint();
            var currentNode = GetCurrentNode();

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
                response.EnsureSuccessStatusCode();
                var responseString = await response.Content.ReadAsStringAsync();
                var responseObject = JObject.Parse(responseString);
                return responseObject["choices"][0]["message"]["content"].ToString();
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
        public List<string> RefreshModels()
        {
            // 获取当前节点和API Key
            var (apiUrl, apiKey) = GetCurrentEndpoint();
            var currentNode = GetCurrentNode();
            
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
                foreach (var model in responseObject["data"])
                {
                    models.Add(model["id"].ToString());
                }
                return models;
            }
        }

        public new List<string> GetModels()
        {
            return new List<string>();
        }
    }
}