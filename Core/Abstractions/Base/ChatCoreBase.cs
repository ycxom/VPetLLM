using VPet_Simulator.Windows.Interface;
using System.Net;
using System.Net.Http;
using VPetLLM.Core.Data.Managers;
using VPetLLM.Core.Services.Embedding;

namespace VPetLLM.Core.Abstractions.Base
{
    public abstract class ChatCoreBase : IChatCore
    {
        public abstract string Name { get; }
        public HistoryManager HistoryManager { get; }
        public RecordManager RecordManager { get; }
        public SkillManager SkillManager { get; }
        public OverflowManager? OverflowManager { get; private set; }
        protected Setting? Settings { get; }
        protected IMainWindow? MainWindow { get; }
        protected ActionProcessor? ActionProcessor { get; }
        protected SystemMessageProvider SystemMessageProvider { get; }
        protected ContextFilter ContextFilter { get; }

        // 溢出检查缓存数据 — 在各 Provider API 成功时触发，而非 GetCoreHistoryCommonAsync 中火抛
        protected List<Message>? _pendingOverflowHistory;
        protected int _pendingOverflowSnapshotCount;
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
        /// 组装好的 prompt 达到预算的这个占比时开始裁剪，留出输出 token 的余量。
        /// </summary>
        private const double ContextBudgetTriggerRatio = 0.82;

        /// <summary>
        /// 裁剪循环的次数上限，防御性保护，正常情况下 1-2 轮就能收敛。
        /// </summary>
        private const int MaxBudgetTrimPasses = 8;

        /// <summary>
        /// 当前模型上下文窗口的 token 预算，&lt;= 0 表示不限制。
        /// 各 Provider 可覆盖以按模型给出不同预算。
        /// </summary>
        protected virtual int MaxContextTokens => Settings?.MaxContextTokens ?? 0;

        /// <summary>
        /// 单次请求 messages 数组的条数上限，&lt;= 0 表示不限制。
        /// 部分 API（如 Free 通道）限制的是条数而非 token
        /// （"Too many messages (1255), max 1000 allowed"）。
        /// 各 Provider 可覆盖。
        /// </summary>
        protected virtual int MaxContextMessages => Settings?.MaxContextMessages ?? 0;

        /// <summary>
        /// 返回 <paramref name="start"/> 及其之后第一条 user 消息的下标。
        /// 找不到时返回 messages.Count。
        /// </summary>
        private static int FindRoundStart(IReadOnlyList<Message> messages, int start)
        {
            for (int i = Math.Max(0, start); i < messages.Count; i++)
            {
                if (string.Equals(messages[i].NormalizedRole, "user", StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return messages.Count;
        }

        /// <summary>
        /// 前导 system 消息的数量。这段前缀在裁剪时永不丢弃。
        /// </summary>
        private static int CountLeadingSystemMessages(IReadOnlyList<Message> messages)
        {
            int i = 0;
            while (i < messages.Count && string.Equals(messages[i].NormalizedRole, "system", StringComparison.OrdinalIgnoreCase))
                i++;
            return i;
        }

        /// <summary>
        /// 组装好的 prompt 的最后一道防线：先保证 messages 条数不超过
        /// <see cref="MaxContextMessages"/>，再保证 token 数不超过 <see cref="MaxContextTokens"/>。
        ///
        /// 独立于溢出总结机制 —— 即便 OverflowManager 初始化失败或总结一直失败导致
        /// 检查点不推进（窗口退化为全量历史），也能保证发出去的 prompt 收敛。
        /// 存储的历史不受影响。
        /// </summary>
        protected List<Message> EnforceContextBudget(List<Message> history)
        {
            if (history is null || history.Count == 0)
                return history;

            return EnforceTokenBudget(EnforceMessageCountLimit(history));
        }

        /// <summary>
        /// 把 messages 条数压到 <see cref="MaxContextMessages"/> 以内：保留 system 前缀和
        /// 最新的若干条，再把窗口对齐到轮边界。
        /// </summary>
        private List<Message> EnforceMessageCountLimit(List<Message> history)
        {
            var max = MaxContextMessages;
            if (max <= 0 || history.Count <= max)
                return history;

            var systemCount = CountLeadingSystemMessages(history);

            // system 前缀本身就撑满配额时无解，至少给窗口留一条消息（当前这轮提问）
            var keep = Math.Max(1, max - systemCount);
            var window = history.Skip(history.Count - keep).ToList();

            // 与 token 裁剪同样的轮边界规则：窗口必须以 user 开头；
            // 整段没有 user 说明这一截里没有提问，丢掉即可。
            var alignedStart = FindRoundStart(window, 0);
            window = alignedStart < window.Count
                ? window.Skip(alignedStart).ToList()
                : new List<Message>();

            var result = history.Take(systemCount).Concat(window).ToList();
            Logger.Log($"EnforceContextBudget: messages 条数 {history.Count} 超出上限 {max}，" +
                       $"已裁剪至 {result.Count} 条（system 前缀 {systemCount} 条）");
            return result;
        }

        /// <summary>
        /// 超出 token 预算时反复丢弃窗口（非 system 部分）中最旧的一半消息，
        /// 并把新窗口对齐到轮边界。
        /// </summary>
        private List<Message> EnforceTokenBudget(List<Message> history)
        {
            var budget = MaxContextTokens;
            if (budget <= 0 || history.Count == 0)
                return history;

            var limit = (int)(budget * ContextBudgetTriggerRatio);
            var tokensBefore = TokenCounter.EstimateMessagesTokenCount(history);
            if (tokensBefore <= limit)
                return history;

            var systemCount = CountLeadingSystemMessages(history);
            var result = history;
            var tokens = tokensBefore;

            for (int pass = 0; pass < MaxBudgetTrimPasses; pass++)
            {
                var windowSize = result.Count - systemCount;
                if (windowSize == 0)
                    break;

                // 窗口只剩一条 user 消息时停手：再裁就把当前这轮提问本身丢了，
                // 单条消息过长不是丢消息能解决的问题。
                if (windowSize == 1 &&
                    string.Equals(result[systemCount].NormalizedRole, "user", StringComparison.OrdinalIgnoreCase))
                    break;

                // 丢弃窗口中最旧的一半，保留较新的一半
                var dropCount = Math.Max(1, windowSize / 2);
                var window = result.Skip(systemCount + dropCount).ToList();

                // 对齐到轮边界：窗口必须以 user 消息开头（部分 API 拒绝 system 后紧跟 assistant）。
                // 剩下的全是 assistant 说明这半截里没有任何提问，整段丢弃即可 —— 此规则
                // 永远不会丢掉 user 消息，因为 user 消息一定会被 FindRoundStart 找到。
                var alignedStart = FindRoundStart(window, 0);
                window = alignedStart < window.Count
                    ? window.Skip(alignedStart).ToList()
                    : new List<Message>();

                result = result.Take(systemCount).Concat(window).ToList();

                tokens = TokenCounter.EstimateMessagesTokenCount(result);
                if (tokens <= limit)
                    break;
            }

            if (ReferenceEquals(result, history))
                Logger.Log($"EnforceContextBudget: prompt {tokensBefore} tokens 超出触发线 {limit}，但窗口已无可裁剪的消息");
            else if (tokens > limit)
                Logger.Log($"EnforceContextBudget: 裁剪 {history.Count}→{result.Count} 条消息后仍超标 " +
                           $"（{tokensBefore}→{tokens} tokens，触发线 {limit}），单条消息可能过长");
            else
                Logger.Log($"EnforceContextBudget: prompt 超出预算，已裁剪 {history.Count}→{result.Count} 条消息，" +
                           $"{tokensBefore}→{tokens} tokens（预算 {budget}，触发线 {limit}）");
            return result;
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

            if (messageType == "User" && SystemMessageProvider is not null)
            {
                var emphasis = SystemMessageProvider.GetEmphasis();
                if (!string.IsNullOrEmpty(emphasis))
                    content = content + $"[System: {emphasis}]";
            }

            var message = new Message { Role = "user", Content = content, MessageType = messageType };

            // 如果允许获取当前时间，设置Unix时间戳
            if (Settings?.EnableTime == true)
            {
                message.UnixTime = DateTimeOffset.Now.ToUnixTimeSeconds();
            }

            // 如果启用了减少输入token消耗，设置状态信息字段
            if (Settings?.ReduceInputTokenUsage == true && SystemMessageProvider is not null)
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
                if (RecordManager is not null && Settings?.Records?.AutoDecrementWeights == true)
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
        /// Inject important records and skills into message history before sending to LLM
        /// </summary>
        protected List<Message> InjectRecordsIntoHistory(List<Message> history)
        {
            try
            {
                // Inject records if enabled
                if (RecordManager is not null && Settings?.Records?.EnableRecords == true)
                {
                    history = RecordManager.InjectRecordsIntoHistory(history);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error injecting records into history: {ex.Message}");
            }

            try
            {
                // Inject skills context if SkillManager is available
                if (SkillManager is not null)
                {
                    history = SkillManager.InjectSkillsIntoHistory(history);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error injecting skills into history: {ex.Message}");
            }

            // 各 Provider 在追加当前用户消息后才调用本方法，因此这里是唯一能看到
            // 完整 prompt 的位置。裁剪是单调的，与 GetCoreHistoryCommonAsync 中的
            // 那次调用重复执行也无副作用。
            return EnforceContextBudget(history);
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
                if (actionProcessor is not null)
                {
                    actionProcessor.SetRecordManager(RecordManager);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to initialize RecordManager: {ex.Message}");
                RecordManager = null;
            }

            // Initialize SkillManager
            try
            {
                SkillManager = new SkillManager(Name);
                Logger.Log($"SkillManager initialized for {Name}");

                // Register SkillManager with ActionProcessor
                if (actionProcessor is not null)
                {
                    actionProcessor.SetSkillManager(SkillManager);
                }

                // Register SkillManager with SystemMessageProvider
                if (SystemMessageProvider is not null)
                {
                    SystemMessageProvider.SkillManager = SkillManager;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to initialize SkillManager: {ex.Message}");
                SkillManager = null;
            }

            // Initialize OverflowManager (always created; activates only in Overflow mode)
            try
            {
                OverflowManager = new OverflowManager(settings, Name, this, RecordManager);
                HistoryManager.SetOverflowManager(OverflowManager);
                Logger.Log($"OverflowManager initialized for {Name} (active={settings?.OverflowMode == Setting.ContextOverflowMode.Overflow})");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to initialize OverflowManager: {ex.Message}");
                OverflowManager = null;
            }

            // Initialize MemoryRetrievalService (always created; activates only when enabled)
            try
            {
                var retrievalService = new MemoryRetrievalService(
                    settings, this, HistoryManager, OverflowManager, RecordManager,
                    CreateEmbeddingService(settings));

                // Register MemoryRetrievalService with ActionProcessor for the retrieval tool handler
                if (actionProcessor is not null)
                {
                    actionProcessor.SetMemoryRetrievalService(retrievalService);
                }

                Logger.Log($"MemoryRetrievalService initialized for {Name} (active={settings?.EnableExpertMemoryRetrieval == true})");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to initialize MemoryRetrievalService: {ex.Message}");
            }
        }

        /// <summary>
        /// 构造向量检索后端。未启用、缺 URL、或构造失败时返回 null ——
        /// 检索会退化成 BM25 + 覆盖率两路，功能不受影响。
        /// </summary>
        private EmbeddingService? CreateEmbeddingService(Setting? settings)
        {
            var config = settings?.Embedding;
            if (config is null || !config.Enable || string.IsNullOrWhiteSpace(config.Url))
                return null;

            try
            {
                var http = new HttpClient(CreateHttpClientHandler())
                {
                    Timeout = TimeSpan.FromSeconds(Math.Max(1, config.TimeoutSeconds))
                };

                var provider = new OpenAiCompatibleEmbeddingProvider(
                    http, config.Url, config.ApiKey, config.Model);

                var store = new EmbeddingStore(HistoryManager.GetDatabasePath());

                Logger.Log($"EmbeddingService initialized for {Name} (model={provider.ModelKey})");
                return new EmbeddingService(
                    provider, store,
                    maxBatchSize: config.MaxBatchSize,
                    maxBackfillPerRound: config.MaxBackfillPerRound,
                    timeoutSeconds: config.TimeoutSeconds);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to initialize EmbeddingService: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Result of GetCoreHistoryCommonAsync.
        /// </summary>
        protected class CoreHistoryResult
        {
            public List<Message> History { get; set; } = new();
            /// <summary>
            /// Snapshot of full history for later overflow check. Set only in Overflow mode.
            /// Consumer (Provider) should call OverflowManager after successful API round.
            /// </summary>
            public List<Message>? OverflowCheckHistory { get; set; }
            public int OverflowCheckSnapshotCount { get; set; }
        }

        /// <summary>
        /// 从 CoreHistoryResult 中捕获溢出检查数据，供 API 成功后触发。
        /// 各 Provider 在 GetCoreHistoryAsync 中调用此方法代替直接返回 result.History。
        /// </summary>
        protected List<Message> CaptureOverflowCheckData(CoreHistoryResult result)
        {
            _pendingOverflowHistory = result.OverflowCheckHistory;
            _pendingOverflowSnapshotCount = result.OverflowCheckSnapshotCount;
            return result.History;
        }

        /// <summary>
        /// 在 API 调用成功且历史已保存后触发溢出检查。
        /// 替代旧的火抛方式（在 GetCoreHistoryCommonAsync 中 fire-and-forget），
        /// 确保只在 Chat 请求真正成功时才触发，避免失败重试时重复浪费。
        /// </summary>
        protected void TriggerOverflowCheckAfterSuccess()
        {
            if (OverflowManager is not null && _pendingOverflowHistory is not null)
            {
                OverflowManager.TriggerCheck(_pendingOverflowHistory, _pendingOverflowSnapshotCount);
                _pendingOverflowHistory = null;
            }
        }

        /// <summary>
        /// Builds the core history list for prompt construction.
        /// In overflow mode, uses summary + sliding window: the rolling summary
        /// covers messages before the overflow checkpoint, and only messages
        /// after the checkpoint are included verbatim.
        /// In compression mode, uses the legacy truncation approach.
        /// </summary>
        /// <param name="injectRecords">Whether to inject important records into the history.</param>
        /// <param name="userQuery">Optional user query for triggering memory retrieval.</param>
        protected async Task<CoreHistoryResult> GetCoreHistoryCommonAsync(bool injectRecords, string? userQuery = null)
        {
            var result = new CoreHistoryResult();

            // 记忆检索已移至 Tool/Handler 机制 (<|retrieve_memories_begin|> query <|retrieve_memories_end|>)
            // 由 AI 主动决定何时需要检索，不再自动匹配注入

            var history = new List<Message>
            {
                new Message { Role = "system", Content = GetSystemMessage() }
            };

            // 当 KeepContext = true 时才包含历史消息，false 时只使用系统消息
            if (Settings?.KeepContext ?? true)
            {
                if (Settings?.OverflowMode == Setting.ContextOverflowMode.Overflow)
                {
                    // Overflow mode（总结+滑动窗口）：已总结的消息不再进入 prompt，
                    // 由 [Previous Conversation Summary] 承载其信息
                    var fullHistory = HistoryManager.GetHistory();

                    // Inject overflow summary as a system message if it exists
                    if (OverflowManager?.LatestSummary is string summary && summary.Length > 0)
                    {
                        history.Add(new Message
                        {
                            Role = "system",
                            Content = $"[Previous Conversation Summary]\n{summary}\n[/Previous Conversation Summary]"
                        });
                    }

                    // 只发送检查点之后的消息（检查点可能因外部编辑超出当前历史，需钳制）
                    var windowStart = Math.Min(OverflowManager?.LastSummarizedIndex ?? 0, fullHistory.Count);

                    // 检查点落在一轮中间时，窗口会以 assistant 消息开头，部分 API（智谱、Gemini）
                    // 会拒绝 system 后紧跟 assistant。前推到下一条 user 消息。
                    var alignedStart = FindRoundStart(fullHistory, windowStart);
                    if (alignedStart < fullHistory.Count)
                        windowStart = alignedStart;

                    history.AddRange(fullHistory.Skip(windowStart));

                    // 记录快照拷贝用于后续溢出检查（由各 Provider 在 API 成功后显式触发）；
                    // 拷贝使火抛的检查任务不受活列表后续变更影响
                    var snapshot = fullHistory.ToList();
                    result.OverflowCheckHistory = snapshot;
                    result.OverflowCheckSnapshotCount = snapshot.Count;
                }
                else
                {
                    // Compression/legacy mode: use naive truncation
                    var threshold = Settings?.HistoryCompressionThreshold ?? 20;
                    var fullHistory = HistoryManager.GetHistory();
                    var windowStart = Math.Max(0, fullHistory.Count - threshold);

                    var alignedStart = FindRoundStart(fullHistory, windowStart);
                    if (alignedStart < fullHistory.Count)
                        windowStart = alignedStart;

                    history.AddRange(fullHistory.Skip(windowStart));
                }
            }

            // Inject important records into history (only when explicitly requested)
            if (injectRecords)
            {
                history = InjectRecordsIntoHistory(history);
            }
            else
            {
                // InjectRecordsIntoHistory 内部已含预算裁剪；未注入时在此补上，
                // 覆盖那些不调用注入方法的 Provider 路径。
                history = EnforceContextBudget(history);
            }

            result.History = history;
            return result;
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
            if (ContextFilter is null || Settings is null)
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
            OverflowManager?.NotifyHistoryReplaced(editedHistory.Count);

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
            OverflowManager?.NotifyHistoryReplaced(history.Count);

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

        protected virtual Setting.ChannelProxyMode GetChannelProxyMode()
        {
            return Setting.ChannelProxyMode.FollowDefault;
        }

        public IWebProxy GetProxy(string? requestType = null)
        {
            var channelProxyMode = GetChannelProxyMode();

            // 如果强制代理但全局代理未启用，则返回null（无法强制）
            if (channelProxyMode == Setting.ChannelProxyMode.ForceProxy && (Settings?.Proxy is null || !Settings.Proxy.IsEnabled))
            {
                System.Diagnostics.Debug.WriteLine($"[ProxyDebug] ForceProxy but global proxy not enabled, returning null proxy");
                return null;
            }

            // 如果直连，完全不使用代理
            if (channelProxyMode == Setting.ChannelProxyMode.Direct)
            {
                System.Diagnostics.Debug.WriteLine($"[ProxyDebug] Direct mode, returning null proxy");
                return null;
            }

            // 如果强制代理，跳过全局检查，直接使用代理
            if (channelProxyMode == Setting.ChannelProxyMode.ForceProxy)
            {
                System.Diagnostics.Debug.WriteLine($"[ProxyDebug] ForceProxy mode, using proxy");
                if (Settings?.Proxy != null && Settings.Proxy.FollowSystemProxy)
                {
                    return WebRequest.GetSystemWebProxy();
                }
                else if (Settings?.Proxy != null)
                {
                    var protocol = Settings.Proxy.Protocol?.ToLower() == "socks" ? "socks5" : "http";
                    var proxyUri = $"{protocol}://{Settings.Proxy.Address}";
                    return new WebProxy(new Uri(proxyUri));
                }
            }

            // FollowDefault 或默认行为：使用全局代理设置
            // 如果Settings或Proxy为null，直接返回null
            if (Settings?.Proxy is null)
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

            if (proxy is not null)
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
            var client = new HttpClient(handler);
            var timeoutSeconds = Settings?.LLMRequestTimeoutSeconds ?? 120;
            if (timeoutSeconds <= 0) timeoutSeconds = 120;
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            return client;
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
        public bool HasImage => ImageData is not null && ImageData.Length > 0;
    }
}

// 聊天历史兼容化处理器（移到ChatCoreBase类外部）
namespace VPetLLM.Core.Abstractions.Base
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
            if (messages is null) return new List<Message>();

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

                if (messages is not null && messages.Count > 0)
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
