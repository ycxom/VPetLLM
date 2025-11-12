using Newtonsoft.Json;
using System.IO;
using System.Net;
using System.Net.Http;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers;

namespace VPetLLM.Core
{
    public abstract class ChatCoreBase : IChatCore
    {
        public abstract string Name { get; }
        public HistoryManager HistoryManager { get; }
        public RecordManager RecordManager { get; }
        protected Setting? Settings { get; }
        protected IMainWindow? MainWindow { get; }
        protected ActionProcessor? ActionProcessor { get; }
        protected SystemMessageProvider SystemMessageProvider { get; }
        protected Action<string> ResponseHandler;
        protected Action<string> StreamingChunkHandler;
        public abstract Task<string> Chat(string prompt);
        public abstract Task<string> Chat(string prompt, bool isFunctionCall);
        public abstract Task<string> Summarize(string systemPrompt, string userContent);

        protected string GetSystemMessage()
        {
            return SystemMessageProvider.GetSystemMessage();
        }

        /// <summary>
        /// Called at the start of each conversation turn to handle record weight decrement
        /// </summary>
        protected void OnConversationTurn()
        {
            try
            {
                if (RecordManager != null && Settings?.Records?.AutoDecrementWeights == true)
                {
                    RecordManager.OnConversationTurn();
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Error in OnConversationTurn: {ex.Message}");
            }
        }

        /// <summary>
        /// Inject important records into message history before sending to LLM
        /// </summary>
        protected List<Message> InjectRecordsIntoHistory(List<Message> history)
        {
            try
            {
                if (RecordManager != null && Settings?.Records?.EnableRecords == true)
                {
                    return RecordManager.InjectRecordsIntoHistory(history);
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Error injecting records into history: {ex.Message}");
            }
            
            return history;
        }

        protected ChatCoreBase(Setting? settings, IMainWindow? mainWindow, ActionProcessor? actionProcessor)
        {
            Settings = settings;
            MainWindow = mainWindow;
            ActionProcessor = actionProcessor;
            HistoryManager = new HistoryManager(settings, Name, this);
            SystemMessageProvider = new SystemMessageProvider(settings, mainWindow, actionProcessor);
            HistoryManager.SetSystemMessageProvider(SystemMessageProvider);
            
            // Initialize RecordManager
            try
            {
                RecordManager = new RecordManager(settings, Name);
                Utils.Logger.Log($"RecordManager initialized for {Name}");
                
                // Register RecordManager with ActionProcessor
                if (actionProcessor != null)
                {
                    actionProcessor.SetRecordManager(RecordManager);
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Failed to initialize RecordManager: {ex.Message}");
                // Create a dummy RecordManager to prevent null reference errors
                RecordManager = null;
            }
        }

        public virtual List<string> GetModels()
        {
            return new List<string>();
        }

        /// <summary>
        /// 清除聊天历史上下文
        /// </summary>
        public virtual void ClearContext()
        {
            HistoryManager.ClearHistory();
        }

        /// <summary>
        /// 获取聊天历史用于编辑
        /// </summary>
        public virtual List<Message> GetHistoryForEditing()
        {
            return HistoryManager.GetHistory();
        }

        public List<Message> GetChatHistory()
        {
            return HistoryManager.GetHistory();
        }

        /// <summary>
        /// 获取当前聊天历史的Token数量估算
        /// </summary>
        public virtual int GetCurrentTokenCount()
        {
            return HistoryManager.GetCurrentTokenCount();
        }

        /// <summary>
        /// 更新聊天历史（用户编辑后）
        /// </summary>
        public virtual void UpdateHistory(List<Message> editedHistory)
        {
            HistoryManager.GetHistory().Clear();
            HistoryManager.GetHistory().AddRange(editedHistory);
            
            // 更新到数据库
            try
            {
                var dbPath = GetDatabasePath();
                using var database = new ChatHistoryDatabase(dbPath);
                database.UpdateHistory(Name, editedHistory);
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"更新历史记录到数据库失败: {ex.Message}");
            }
        }

        private string GetDatabasePath()
        {
            var docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dataPath = Path.Combine(docPath, "VPetLLM", "Chat");
            if (!Directory.Exists(dataPath))
            {
                Directory.CreateDirectory(dataPath);
            }
            return Path.Combine(dataPath, "chat_history.db");
        }


        /// <summary>
        /// 设置聊天历史记录（用于切换提供商时恢复）
        /// </summary>
        public virtual void SetChatHistory(List<Message> history)
        {
            HistoryManager.GetHistory().Clear();
            HistoryManager.GetHistory().AddRange(history);
        }


        public void SaveHistory()
        {
            HistoryManager.SaveHistory();
        }

        public void LoadHistory()
        {
            HistoryManager.LoadHistory();
        }
        public void SetResponseHandler(Action<string> handler)
        {
            ResponseHandler = handler;
        }

        public void SetStreamingChunkHandler(Action<string> handler)
        {
            StreamingChunkHandler = handler;
        }

        public virtual void AddPlugin(IVPetLLMPlugin plugin)
        {
            SystemMessageProvider.AddPlugin(plugin);
        }

        public virtual void RemovePlugin(IVPetLLMPlugin plugin)
        {
            SystemMessageProvider.RemovePlugin(plugin);
        }

        public IWebProxy GetProxy(string? requestType = null)
        {
            // 如果Settings或Proxy为null，直接返回null
            if (Settings?.Proxy == null)
            {
                System.Diagnostics.Debug.WriteLine($"[ProxyDebug] Settings.Proxy is null, returning null proxy");
                return null;
            }

            // 如果代理未启用，直接返回null
            if (!Settings.Proxy.IsEnabled)
            {
                System.Diagnostics.Debug.WriteLine($"[ProxyDebug] Proxy not enabled, returning null proxy");
                return null;
            }

            bool useProxy = false;
            string type = requestType ?? Name;

            System.Diagnostics.Debug.WriteLine($"[ProxyDebug] Checking proxy for {type}");
            System.Diagnostics.Debug.WriteLine($"[ProxyDebug] ForAllAPI: {Settings.Proxy.ForAllAPI}");

            // 如果ForAllAPI为true，则对所有API使用代理
            if (Settings.Proxy.ForAllAPI)
            {
                useProxy = true;
                System.Diagnostics.Debug.WriteLine($"[ProxyDebug] Using proxy because ForAllAPI is true");
            }
            else
            {
                // 如果ForAllAPI为false，则根据具体的API设置决定
                switch (type)
                {
                    case "Ollama":
                        useProxy = Settings.Proxy.ForOllama;
                        System.Diagnostics.Debug.WriteLine($"[ProxyDebug] ForOllama: {Settings.Proxy.ForOllama}");
                        break;
                    case "OpenAI":
                        useProxy = Settings.Proxy.ForOpenAI;
                        System.Diagnostics.Debug.WriteLine($"[ProxyDebug] ForOpenAI: {Settings.Proxy.ForOpenAI}");
                        break;
                    case "Gemini":
                        useProxy = Settings.Proxy.ForGemini;
                        System.Diagnostics.Debug.WriteLine($"[ProxyDebug] ForGemini: {Settings.Proxy.ForGemini}");
                        break;
                    case "Free":
                        useProxy = Settings.Proxy.ForFree;
                        System.Diagnostics.Debug.WriteLine($"[ProxyDebug] ForFree: {Settings.Proxy.ForFree}");
                        break;
                    case "Plugin":
                        useProxy = Settings.Proxy.ForPlugin;
                        System.Diagnostics.Debug.WriteLine($"[ProxyDebug] ForPlugin: {Settings.Proxy.ForPlugin}");
                        break;
                    case "MCP":
                        useProxy = Settings.Proxy.ForMcp;
                        System.Diagnostics.Debug.WriteLine($"[ProxyDebug] ForMcp: {Settings.Proxy.ForMcp}");
                        break;
                }
            }

            System.Diagnostics.Debug.WriteLine($"[ProxyDebug] Final useProxy decision: {useProxy}");

            // 只有当useProxy为true时才返回代理
            if (useProxy)
            {
                if (Settings.Proxy.FollowSystemProxy)
                {
                    System.Diagnostics.Debug.WriteLine($"[ProxyDebug] Using system proxy");
                    return WebRequest.GetSystemWebProxy();
                }
                else
                {
                    if (string.IsNullOrEmpty(Settings.Proxy.Protocol))
                    {
                        Settings.Proxy.Protocol = "http";
                    }
                    var protocol = Settings.Proxy.Protocol.ToLower() == "socks" ? "socks5" : "http";
                    var proxyUri = $"{protocol}://{Settings.Proxy.Address}";
                    System.Diagnostics.Debug.WriteLine($"[ProxyDebug] Using custom proxy: {proxyUri}");
                    return new WebProxy(new Uri(proxyUri));
                }
            }

            // 如果不应该使用代理，返回null
            System.Diagnostics.Debug.WriteLine($"[ProxyDebug] Not using proxy, returning null");
            return null;
        }

        public HttpClientHandler CreateHttpClientHandler()
        {
            var handler = new HttpClientHandler();
            var proxy = GetProxy();

            if (proxy != null)
            {
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
            else
            {
                // 明确禁用代理，防止使用系统默认代理
                handler.UseProxy = false;
                handler.Proxy = null;
            }

            return handler;
        }

        protected HttpClient GetClient()
        {
            var handler = CreateHttpClientHandler();
            return new HttpClient(handler);
        }

        // 统一将内部 Message 映射为 OpenAI 风格的 { role, content }，避免额外字段
        protected IEnumerable<object> ShapeMessages(IEnumerable<Message> messages, bool useDisplayContent = true)
        {
            return messages.Select(m => new { role = m.Role, content = useDisplayContent ? m.DisplayContent : m.Content });
        }
    }

    public class Message
    {
        [JsonProperty(Order = 1)]
        public string? Role { get; set; }
        [JsonProperty(Order = 2)]
        public string? Content { get; set; }

        /// <summary>
        /// 标准化角色名称，确保不同提供商API的角色名称一致
        /// </summary>
        [JsonProperty(Order = 3)]
        public string NormalizedRole
        {
            get
            {
                return Role?.ToLower() switch
                {
                    "model" => "assistant",  // Gemini使用"model"，标准化为"assistant"
                    _ => Role ?? "user"
                };
            }
        }

        [JsonProperty(Order = 4)]
        public long? UnixTime { get; set; }

        /// <summary>
        /// 状态信息字符串（仅在启用ReduceInputTokenUsage时使用）
        /// </summary>
        [JsonProperty(Order = 5)]
        public string? StatusInfo { get; set; }

        [JsonIgnore]
        public string DisplayContent
        {
            get
            {
                // 基础文本：对assistant提取 say("...")，否则直接使用Content
                string baseText;
                if (Role == "assistant")
                {
                    var match = System.Text.RegularExpressions.Regex.Match(Content ?? "", @"(?<=say\("")(?:[^""\\]|\\.)*(?=""\))");
                    baseText = match.Success ? match.Value : (Content ?? "");
                }
                else
                {
                    baseText = Content ?? "";
                }

                // 时间前缀：若存在UnixTime则动态渲染为 [yyyy/MM/dd HH:mm:ss]
                string timePrefix = "";
                if (UnixTime.HasValue)
                {
                    try
                    {
                        var dt = DateTimeOffset.FromUnixTimeSeconds(UnixTime.Value).ToLocalTime().DateTime;
                        timePrefix = $"[{dt:yyyy/MM/dd HH:mm:ss}]";
                    }
                    catch
                    {
                        // 忽略解析异常，保持无前缀
                    }
                }

                // 状态信息前缀：若存在StatusInfo则动态添加
                string statusPrefix = "";
                if (!string.IsNullOrEmpty(StatusInfo))
                {
                    statusPrefix = $"[{StatusInfo}]";
                }

                // 组合前缀：时间和状态信息
                string prefix = "";
                if (!string.IsNullOrEmpty(timePrefix) && !string.IsNullOrEmpty(statusPrefix))
                {
                    // 如果同时有时间和状态，合并为一个方括号：[Time;Status]
                    prefix = $"[{timePrefix.Trim('[', ']')};{statusPrefix.Trim('[', ']')}] ";
                }
                else if (!string.IsNullOrEmpty(timePrefix))
                {
                    prefix = timePrefix + " ";
                }
                else if (!string.IsNullOrEmpty(statusPrefix))
                {
                    prefix = statusPrefix + " ";
                }

                return prefix + baseText;
            }
        }
    }
}

// 聊天历史兼容化处理器（移到ChatCoreBase类外部）
namespace VPetLLM.Core
{
    /// <summary>
    /// 聊天历史兼容化处理器
    /// </summary>
    public static class ChatHistoryCompatibility
    {
        /// <summary>
        /// 标准化消息角色，确保不同提供商API的角色名称一致
        /// </summary>
        public static List<Message> NormalizeRoles(List<Message> messages)
        {
            if (messages == null) return new List<Message>();

            return messages.Select(m => new Message
            {
                Role = m.Role?.ToLower() switch
                {
                    "model" => "assistant",  // Gemini使用"model"，标准化为"assistant"
                    _ => m.Role
                },
                Content = m.Content
            }).ToList();
        }

        /// <summary>
        /// 修复历史文件中的角色名称不一致问题
        /// </summary>
        public static void FixRoleInconsistencies(string historyFile)
        {
            if (!File.Exists(historyFile)) return;

            try
            {
                var json = File.ReadAllText(historyFile);
                var messages = JsonConvert.DeserializeObject<List<Message>>(json);

                if (messages != null && messages.Count > 0)
                {
                    var normalizedMessages = NormalizeRoles(messages);

                    // 只有在确实需要修复时才重写文件
                    if (HasRoleInconsistencies(messages))
                    {
                        var normalizedJson = JsonConvert.SerializeObject(normalizedMessages, Formatting.Indented);

                        // 使用临时文件确保写入完整性
                        var tempFile = historyFile + ".fix.tmp";
                        File.WriteAllText(tempFile, normalizedJson);

                        // 原子替换
                        File.Replace(tempFile, historyFile, null);

                        Console.WriteLine($"已修复聊天历史文件中的角色不一致问题: {Path.GetFileName(historyFile)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"修复角色不一致失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查是否存在角色名称不一致
        /// </summary>
        public static bool HasRoleInconsistencies(List<Message> messages)
        {
            return messages.Any(m => m.Role?.ToLower() == "model");
        }
    }
}