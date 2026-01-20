using Newtonsoft.Json;
using System.IO;
using System.Net;
using System.Net.Http;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers.Core;
using VPetLLM.Utils.System;

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
        protected ContextFilter ContextFilter { get; }
        protected Action<string> ResponseHandler;
        protected Action<string> StreamingChunkHandler;
        public abstract Task<string> Chat(string prompt);
        public abstract Task<string> Chat(string prompt, bool isFunctionCall);
        public abstract Task<string> Summarize(string systemPrompt, string userContent);

        /// <summary>
        /// 发送带图像的多模态消息（默认实现：不支持）
        /// </summary>
        public virtual Task<string> ChatWithImage(string prompt, byte[] imageData)
        {
            Logger.Log($"{Name} 不支持多模态消息");
            ResponseHandler?.Invoke("当前模型不支持图像输入");
            return Task.FromResult("");
        }

        protected string GetSystemMessage()
        {
            return SystemMessageProvider.GetSystemMessage();
        }

        /// <summary>
        /// 创建用户消息，自动设置时间戳和状态信息
        /// </summary>
        /// <param name="content">消息内容</param>
        /// <param name="messageType">消息类型：User（默认）、System、Plugin</param>
        protected Message CreateUserMessage(string content, string messageType = "User")
        {
            if (string.IsNullOrEmpty(content))
                return null;

            var message = new Message { Role = "user", Content = content, MessageType = messageType };

            // 如果允许获取当前时间，设置Unix时间戳
            if (Settings?.EnableTime == true)
            {
                message.UnixTime = DateTimeOffset.Now.ToUnixTimeSeconds();
            }

            // 如果启用了减少输入token消耗，设置状态信息字段
            if (Settings?.ReduceInputTokenUsage == true && SystemMessageProvider != null)
            {
                var statusString = SystemMessageProvider.GetStatusString();
                if (!string.IsNullOrEmpty(statusString))
                {
                    message.StatusInfo = statusString;
                }
            }

            return message;
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
                Logger.Log($"Error in OnConversationTurn: {ex.Message}");
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
                Logger.Log($"Error injecting records into history: {ex.Message}");
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
            ContextFilter = new ContextFilter();
            HistoryManager.SetSystemMessageProvider(SystemMessageProvider);

            // Initialize RecordManager
            try
            {
                RecordManager = new RecordManager(settings, Name);
                Logger.Log($"RecordManager initialized for {Name}");

                // Register RecordManager with ActionProcessor
                if (actionProcessor != null)
                {
                    actionProcessor.SetRecordManager(RecordManager);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to initialize RecordManager: {ex.Message}");
                // Create a dummy RecordManager to prevent null reference errors
                RecordManager = null;
            }
        }

        /// <summary>
        /// 检查当前模型是否支持视觉
        /// </summary>
        protected bool CheckVisionSupport()
        {
            return ContextFilter?.CheckVisionSupport(Settings) ?? false;
        }

        /// <summary>
        /// 根据当前模型的视觉能力过滤消息历史
        /// </summary>
        protected List<Message> FilterMessagesForModel(List<Message> messages)
        {
            if (ContextFilter == null || Settings == null)
            {
                return messages;
            }

            var supportsVision = CheckVisionSupport();
            return ContextFilter.FilterForModel(messages, supportsVision);
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
                Logger.Log($"更新历史记录到数据库失败: {ex.Message}");
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

            // 更新到数据库
            try
            {
                var dbPath = GetDatabasePath();
                using var database = new ChatHistoryDatabase(dbPath);
                database.UpdateHistory(Name, history);
            }
            catch (Exception ex)
            {
                Logger.Log($"更新历史记录到数据库失败: {ex.Message}");
            }
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

        /// <summary>
        /// 消息类型：User（用户输入）、System（系统消息）、Plugin（插件消息）
        /// </summary>
        [JsonProperty(Order = 6)]
        public string? MessageType { get; set; }

        [JsonIgnore]
        public string DisplayContent
        {
            get
            {
                // 对于 assistant 角色，直接返回原始内容
                if (Role == "assistant")
                {
                    return Content ?? "";
                }

                // 对于 user 角色，构建 JSON 格式
                var baseText = Content ?? "";

                // 构建时间字符串（ISO 8601 格式，使用本地时区）
                string nowTime = "";
                if (UnixTime.HasValue)
                {
                    try
                    {
                        // 从Unix时间戳转换为DateTimeOffset，然后转换为本地时间
                        var utcTime = DateTimeOffset.FromUnixTimeSeconds(UnixTime.Value);
                        var localTime = utcTime.ToLocalTime();
                        // 使用带时区偏移的格式，如 "2025-12-07T15:30:00+08:00"
                        nowTime = localTime.ToString("yyyy-MM-ddTHH:mm:sszzz");
                    }
                    catch
                    {
                        // 忽略解析异常
                    }
                }

                // 提取状态信息（兼容旧版本，去掉可能存在的前缀）
                string vpetStatus = "";
                if (!string.IsNullOrEmpty(StatusInfo))
                {
                    vpetStatus = StatusInfo
                        .Replace("[桌宠状态] ", "")
                        .Replace("[Pet Status] ", "")
                        .TrimStart();
                }

                // 如果没有时间和状态信息，直接返回原始内容
                if (string.IsNullOrEmpty(nowTime) && string.IsNullOrEmpty(vpetStatus))
                {
                    return baseText;
                }

                // 构建 JSON 格式输出
                var parts = new List<string>();

                if (!string.IsNullOrEmpty(nowTime))
                {
                    parts.Add($"\"NowTime\": \"{nowTime}\"");
                }

                if (!string.IsNullOrEmpty(vpetStatus))
                {
                    parts.Add($"\"VPetStatus\": \"{vpetStatus}\"");
                }

                // 检测是否为插件消息（格式：[Plugin Result: XXX] 内容）
                var pluginMatch = System.Text.RegularExpressions.Regex.Match(baseText, @"^\[Plugin Result:\s*([^\]]+)\]\s*(.*)$", System.Text.RegularExpressions.RegexOptions.Singleline);
                if (pluginMatch.Success)
                {
                    var pluginName = pluginMatch.Groups[1].Value.Trim();
                    var pluginContent = pluginMatch.Groups[2].Value.Trim();
                    parts.Add($"\"Plugin\": \"[{EscapeJsonString(pluginName)}] {EscapeJsonString(pluginContent)}\"");
                }
                else
                {
                    // 根据消息类型选择对应的字段名
                    var msgType = MessageType ?? "User";
                    switch (msgType)
                    {
                        case "System":
                            parts.Add($"\"System\": \"{EscapeJsonString(baseText)}\"");
                            break;
                        case "Plugin":
                            parts.Add($"\"Plugin\": \"{EscapeJsonString(baseText)}\"");
                            break;
                        default: // User
                            parts.Add($"\"UserSay\": \"{EscapeJsonString(baseText)}\"");
                            break;
                    }
                }

                return "{" + string.Join(", ", parts) + "}";
            }
        }

        /// <summary>
        /// 转义 JSON 字符串中的特殊字符
        /// </summary>
        private static string EscapeJsonString(string str)
        {
            if (string.IsNullOrEmpty(str)) return "";
            return str
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        /// <summary>
        /// 图像数据（用于多模态消息）
        /// </summary>
        [JsonIgnore]
        public byte[]? ImageData { get; set; }

        /// <summary>
        /// 图像 MIME 类型
        /// </summary>
        [JsonIgnore]
        public string ImageMimeType { get; set; } = "image/png";

        /// <summary>
        /// 是否包含图像
        /// </summary>
        [JsonIgnore]
        public bool HasImage => ImageData != null && ImageData.Length > 0;
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