using VPetLLM.Core.Data.Database;
using VPetLLM.Core.Services.Embedding;
using VPetLLM.Core.Services.MemoryGraph;
using VPetLLM.Infrastructure.Exceptions;

namespace VPetLLM.Core.Data.Managers
{
    /// <summary>
    /// Manages the overflow-based context handling for long-context mode
    /// (summary + sliding window).
    ///
    /// Logic (example with message threshold = 10):
    ///   - The prompt window always keeps the messages after the committed
    ///     checkpoint (at most ~2x threshold messages).
    ///   - Messages beyond the keep window (threshold) count as "overflowed".
    ///   - Once the overflowed batch itself reaches the threshold (i.e. around
    ///     2x threshold total messages since the checkpoint), an incremental
    ///     summary is triggered and the checkpoint advances.
    ///
    /// 总结是分段制（参考 astrbot_plugin_livingmemory）：每片只总结检查点之后的
    /// 新消息，各自落成一条，与已有条目逐行去重后再存；注入时按先后拼起来。
    /// 超出预算时只把最旧的几条合并压缩成一条 consolidated，近期分段原样保留。
    ///
    /// 为什么不再用替换制滚动总结（把旧总结喂回去让模型「EXTEND」并输出完整版）：
    /// 模型每次都把旧总结整段抄一遍再往后续写，真实库里相邻两版相似度 0.86~1.00，
    /// 旧内容被一遍遍重复计入，表现就是在上次总结后面莫名其妙又「补充」一段。
    ///
    /// The prompt only contains messages after the checkpoint (sliding window).
    /// Messages are NEVER deleted from stored history.
    /// </summary>
    public class OverflowManager
    {
        private readonly OverflowDatabase _database;
        private readonly RecordManager? _recordManager;
        private readonly Setting _settings;
        private readonly string _providerName;
        private readonly ChatCoreBase _chatCore;

        /// <summary>
        /// Index (in full history) up to which messages have been summarized.
        /// 0 = no summary yet. Messages [0.._lastSummarizedIndex) are covered.
        /// </summary>
        private int _lastSummarizedIndex;

        /// <summary>
        /// 有效条目按先后拼成的文本，注入 prompt 用。每次落库后从库里重算。
        /// </summary>
        private string? _currentSummary;

        /// <summary>当前有效的总结条目（未被合并掉），按覆盖的消息先后排列。</summary>
        private List<OverflowSummaryRecord> _activeEntries = new();

        /// <summary>
        /// Incremented on ClearAll so an in-flight summarization started before
        /// the clear discards its result instead of resurrecting stale state.
        /// </summary>
        private int _generation;

        private volatile int _isTriggering;  // 0 = idle, 1 = running — 防止并发触发多个 Summarize

        /// <summary>合并连续失败的次数，成功后清零。</summary>
        private int _consolidationFailures;

        /// <summary>
        /// 合并失败后还要跳过几次合并机会。合并一直失败（上游拒绝、输出被过滤空）时，
        /// 不退避的话每落一片新分段就再发一次注定失败的合并请求。
        /// </summary>
        private int _consolidationSkipsLeft;

        /// <summary>
        /// 落库前语义去重用的向量服务，由 ChatCoreBase 在建好检索服务后注入。
        /// 为 null 或暂不可用时只做字面去重。
        /// </summary>
        public EmbeddingService? EmbeddingService { get; set; }

        /// <summary>
        /// 记忆图谱（事实原子 + 人物/话题实体），总结落库时顺带写入。
        /// 建库失败时为 null，总结流程照常、只是不出图。
        /// </summary>
        public MemoryGraphService? MemoryGraph { get; }

        /// <summary>
        /// 余弦相似度达到这个值、且数字和否定词一致，才把一行当成已有事实的换个说法。
        /// 0.92 是保守取值，还没有拿真实语料校准过。
        /// </summary>
        public const double SemanticDuplicateThreshold = 0.92;

        /// <summary>当前注入 prompt 的条目 id。</summary>
        public IReadOnlyCollection<int> ActiveSummaryIds => _activeEntries.Select(e => e.Id).ToHashSet();

        /// <summary>
        /// The current rolling summary, or null if no summary exists.
        /// </summary>
        public string? LatestSummary => string.IsNullOrEmpty(_currentSummary) ? null : _currentSummary;

        /// <summary>
        /// Whether any summary has been created (for UI display).
        /// </summary>
        public bool HasSummary => !string.IsNullOrEmpty(_currentSummary);

        /// <summary>
        /// Committed checkpoint: messages [0..LastSummarizedIndex) are covered by
        /// the summary and should be excluded from the prompt window.
        /// </summary>
        public int LastSummarizedIndex => _lastSummarizedIndex;

        /// <summary>
        /// Number of messages overflowed since last summary (for UI).
        /// </summary>
        public int AccumulatedOverflowMessageCount { get; private set; }

        /// <summary>
        /// Estimated tokens overflowed since last summary (for UI).
        /// </summary>
        public int AccumulatedOverflowTokens { get; private set; }

        /// <summary>
        /// Overflow data key: history is stored per provider only when
        /// SeparateChatByProvider is on; mirror that so overflow indexes always
        /// align with the history list they were computed against.
        /// Legacy rows (no provider column) carry '' and thus map to merged mode.
        /// </summary>
        private string EffectiveProvider => _settings?.SeparateChatByProvider == true ? _providerName : "";

        /// <summary>
        /// 滚动总结自身的 token 预算，0 表示不限制。
        /// 未显式配置时取上下文预算的 15%，与 prompt 里保留给近期消息的窗口大致平衡。
        /// </summary>
        private int SummaryTokenBudget
        {
            get
            {
                if (_settings is null) return 0;
                if (_settings.MaxSummaryTokens > 0) return _settings.MaxSummaryTokens;

                // 用**生效**预算而不是 _settings.MaxContextTokens。
                //
                // 后者默认是 0（不限制），于是这里也返回 0 = 总结不设上限 —— 而总结是
                // 拼进 system 消息的，system 又永远不参与窗口裁剪。结果就是：本地模型
                // n_ctx 只有 8192，滚动总结却越滚越大，最后光它一个就把窗口占满，
                // 后面裁掉多少条历史都没用，每一轮照样 400。
                //
                // EffectiveContextTokenBudget 里含了从 400 错误中撞出来的真实窗口，
                // 所以哪怕用户从没手工配过 MaxContextTokens，这里也能拿到一个靠谱的数。
                var ctx = _chatCore?.EffectiveContextTokenBudget ?? 0;
                if (ctx <= 0) ctx = _settings.MaxContextTokens;
                return ctx > 0 ? Math.Max(256, (int)(ctx * 0.15)) : 0;
            }
        }

        /// <param name="databasePath">
        /// 覆盖数据库位置。生产代码不传，走「我的文档」下的固定路径；
        /// 回归检查传一个临时库，免得测试写进使用者的真实聊天记录。
        /// </param>
        public OverflowManager(Setting settings, string providerName, ChatCoreBase chatCore,
            RecordManager? recordManager, string? databasePath = null)
        {
            _settings = settings;
            _providerName = providerName;
            _chatCore = chatCore;
            _recordManager = recordManager;
            var path = databasePath ?? GetDatabasePath();
            _database = new OverflowDatabase(path);

            try
            {
                MemoryGraph = new MemoryGraphService(new MemoryGraphDatabase(path),
                    () => _settings?.UserName, () => _settings?.AiName);
            }
            catch (Exception ex)
            {
                Logger.Log($"OverflowManager: 记忆图谱初始化失败，本次不建图: {ex.Message}");
            }

            // 从数据库恢复上次的溢出状态，避免重启后重复处理全部历史
            RestoreFromDatabase();
        }

        /// <summary>
        /// Restore overflow state from database after restart.
        /// </summary>
        private void RestoreFromDatabase()
        {
            try
            {
                var provider = EffectiveProvider;
                var endIndex = _database.GetSummarizedEndExclusive(provider);
                if (endIndex > 0)
                {
                    _lastSummarizedIndex = endIndex;
                    ReloadActiveEntries();
                    Logger.Log($"OverflowManager: 从数据库恢复状态 - provider='{provider}' lastSummarizedIndex={_lastSummarizedIndex} hasSummary={HasSummary}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"OverflowManager: 恢复数据库状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Trigger overflow check with explicit snapshot data.
        /// Should be called by the Provider AFTER a successful API round and history save.
        /// The caller must pass a stable snapshot copy of the history (not the live list).
        /// </summary>
        public void TriggerCheck(List<Message> fullHistory, int snapshotCount)
        {
            // Fire-and-forget the async check (it runs on thread pool)
            _ = CheckAndTriggerAsync(fullHistory, snapshotCount);
        }

        /// <summary>
        /// Called when history is externally replaced/edited so the checkpoint
        /// never points beyond the new history.
        /// </summary>
        public void NotifyHistoryReplaced(int newCount)
        {
            if (_lastSummarizedIndex > newCount)
            {
                Logger.Log($"OverflowManager: 历史被外部修改，检查点 {_lastSummarizedIndex}→{newCount}");
                _lastSummarizedIndex = Math.Max(0, newCount);
            }
        }

        /// <summary>
        /// Check if any overflow has occurred and trigger summaries as needed.
        ///
        /// State machine:
        ///   1. Caller captures a snapshot copy of history + count
        ///   2. Count messages since the committed checkpoint (_lastSummarizedIndex)
        ///   3. If accumulated >= threshold → trigger summary
        ///   4. After summary completes → advance checkpoint (committed to DB)
        /// </summary>
        /// <param name="fullHistory">Snapshot copy of the message list.</param>
        /// <param name="snapshotCount">The number of messages at snapshot time.</param>
        public async Task CheckAndTriggerAsync(List<Message> fullHistory, int snapshotCount)
        {
            try
            {
                if (fullHistory is null || snapshotCount == 0)
                    return;

                var msgThreshold = _settings.HistoryCompressionThreshold;
                if (msgThreshold <= 0) msgThreshold = 20;

                // 历史被删减（编辑器删除消息等）时钳制检查点，避免溢出量永远为负
                if (_lastSummarizedIndex > snapshotCount)
                    _lastSummarizedIndex = snapshotCount;

                // 步骤2：从已提交的检查点开始计算溢出量
                var keepCount = Math.Min(msgThreshold, snapshotCount);
                var checkpoint = _lastSummarizedIndex; // 已提交完成态，来自上一次成功总结或数据库恢复

                // 检查点之后累积了"需要保留"之外的溢出消息数量
                // snapshotCount - checkpoint = 总消息中检查点之后的部分
                // snapshotCount - keepCount = 需要溢出处理的部分（超过保留量的部分）
                var overflowedCount = snapshotCount - checkpoint - keepCount;

                // 溢出消息的起始索引 = checkpoint
                // 结束索引 = snapshotCount - keepCount
                var overflowStart = checkpoint;
                var overflowEnd = snapshotCount - keepCount;

                if (overflowedCount <= 0)
                {
                    // 没有新溢出消息
                    AccumulatedOverflowMessageCount = 0;
                    AccumulatedOverflowTokens = 0;
                    return;
                }

                var overflowed = fullHistory.Skip(overflowStart).Take(overflowedCount).ToList();
                var ovTokens = TokenCounter.EstimateMessagesTokenCount(overflowed);

                AccumulatedOverflowMessageCount = overflowed.Count;
                AccumulatedOverflowTokens = ovTokens;

                Logger.Log($"OverflowManager: checkpoint={checkpoint} snapshotCount={snapshotCount} keep={keepCount} overflowed={overflowed.Count} tokens={ovTokens}");

                // 步骤3：检查是否达到阈值
                var shouldTrigger = false;
                switch (_settings.CompressionMode)
                {
                    case Setting.CompressionTriggerMode.MessageCount:
                        shouldTrigger = overflowed.Count >= msgThreshold;
                        break;
                    case Setting.CompressionTriggerMode.TokenCount:
                        shouldTrigger = ovTokens >= _settings.HistoryCompressionTokenThreshold;
                        break;
                    case Setting.CompressionTriggerMode.Both:
                    default:
                        shouldTrigger = overflowed.Count >= msgThreshold || ovTokens >= _settings.HistoryCompressionTokenThreshold;
                        break;
                }

                if (shouldTrigger && overflowed.Count > 0)
                {
                    // 防止并发触发：如果已有 TriggerSummaryAsync 在运行，跳过本次触发
                    if (Interlocked.CompareExchange(ref _isTriggering, 1, 0) != 0)
                    {
                        Logger.Log("OverflowManager: 跳过触发，已有总结任务正在运行");
                        return;
                    }
                    try
                    {
                        // 步骤4：触发总结。成功后 TriggerSummaryAsync 会提交新检查点
                        await TriggerSummaryAsync(overflowed, overflowStart, overflowEnd, msgThreshold);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isTriggering, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                // 本方法以火抛方式调用，异常必须就地记录，否则会被静默丢弃
                Logger.Log($"OverflowManager: 溢出检查失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 总结新溢出的消息：按预算切片，每片单独总结、单独落一条。
        /// </summary>
        private async Task TriggerSummaryAsync(List<Message> overflowedMessages, int segmentStart, int segmentEnd, int msgThreshold)
        {
            try
            {
                var generation = _generation;

                Logger.Log($"OverflowManager: Triggering incremental summary for messages [{segmentStart}..{segmentEnd}) ({overflowedMessages.Count} msgs)");

                // 积压可能远超单次请求能装下的量（回滚后、或长时间没总结过），
                // 所以这里不再一次性发全部，而是按预算切片、逐片提交、逐片推进检查点。
                // 每片成功即落库，中途失败也不会丢掉已经做完的部分。
                var cursor = 0;
                var committed = 0;
                var skippedOversize = 0;
                var consecutiveApiFailures = 0;

                // 退让条数只属于本轮：上一轮因故障中止时它可能已经退到 1，
                // 留着它会让下一轮一上来就逐条发、逐条跳。
                _retreatBatchSize = 0;

                while (cursor < overflowedMessages.Count)
                {
                    if (generation != _generation)
                    {
                        Logger.Log("OverflowManager: 总结期间上下文已被清空，停止后续分批");
                        return;
                    }

                    var budget = ResolveRequestTokenBudget();
                    var batchSize = TakeBatchSize(overflowedMessages, cursor, budget);
                    var batch = overflowedMessages.GetRange(cursor, batchSize);
                    var batchStart = segmentStart + cursor;

                    // 单条就超预算：再切也切不小了，发出去必定失败。直接跳过这一条，
                    // 推进检查点让它不再挡住后面的消息。原文仍在 chat_history 里。
                    if (batchSize == 1 && ExceedsBudget(batch[0], budget))
                    {
                        Logger.Log($"OverflowManager: 第 {batchStart} 条单条即超预算" +
                                   $"（{TokenCounter.EstimateTokenCount(batch[0].Content ?? "")} tokens > {budget}），跳过该条");
                        cursor += 1;
                        _lastSummarizedIndex = segmentStart + cursor;
                        skippedOversize++;
                        consecutiveApiFailures = 0;
                        continue;
                    }

                    SummarizeOutcome outcome;
                    try
                    {
                        outcome = await SummarizeBatchAsync(batch, batchStart, msgThreshold, generation);
                    }
                    catch (SummarizeFailedException ex)
                    {
                        // 切小只对「这一片太大」有用。连不上、超时、网关/CDN 自己回的错误页、
                        // 限流、鉴权、5xx……切多小都一样失败，照旧对半退让会一路切到单条，
                        // 再把消息逐条跳过，检查点越过从没总结过的消息。真实日志：EdgeOne
                        // 15 秒回源超时把 175 条积压二分到单条，几轮下来连跳了 6 条。
                        if (!MayBeCausedByInputSize(ex))
                        {
                            Logger.Log($"OverflowManager: [{batchStart}..{batchStart + batchSize}) 总结失败且与内容长度无关，" +
                                       $"整轮放弃；检查点停在 {_lastSummarizedIndex}，" +
                                       $"[{segmentStart + cursor}..{segmentEnd}) 留待下次重试: {ex.Message}");
                            break;
                        }

                        if (batchSize > 1)
                        {
                            // 还能再切：对半退让后重试，不推进游标。
                            // 这条路同时兜住了「我们估的 token 数比模型实际算的小」。
                            _retreatBatchSize = Math.Max(1, batchSize / 2);
                            Logger.Log($"OverflowManager: [{batchStart}..{batchStart + batchSize}) 总结失败，" +
                                       $"批量退让至 {_retreatBatchSize} 重试: {ex.Message}");
                            continue;
                        }

                        // 已经只剩一条还被判太大。只有它自己确实占了预算的大头，跳过它才
                        // 解决问题；一条小消息都发不出去，毛病在滚动总结/提示词或别的 4xx，
                        // 跳过它只会白丢一条，下一条照样失败。
                        if (TokenCounter.EstimateTokenCount(batch[0].Content ?? "") * 2 < budget)
                        {
                            Logger.Log($"OverflowManager: 第 {batchStart} 条单条总结失败，但它远小于预算 {budget}，" +
                                       $"不是它太长；整轮放弃，检查点停在 {_lastSummarizedIndex}: {ex.Message}");
                            break;
                        }

                        // 连续失败说明多半是服务不可用而不是消息太长——那样一条条跳下去
                        // 会把整段积压静默吞掉，所以到阈值就整轮放弃，留给下次触发重试。
                        consecutiveApiFailures++;
                        if (consecutiveApiFailures >= MaxConsecutiveSingleFailures)
                        {
                            Logger.Log($"OverflowManager: 单条总结连续失败 {consecutiveApiFailures} 次，" +
                                       $"判定为服务不可用而非内容过长；检查点停在 {_lastSummarizedIndex}，" +
                                       $"[{segmentStart + cursor}..{segmentEnd}) 留待下次重试: {ex.Message}");
                            break;
                        }

                        Logger.Log($"OverflowManager: 第 {batchStart} 条单条总结失败，跳过该条: {ex.Message}");
                        cursor += 1;
                        _lastSummarizedIndex = segmentStart + cursor;
                        continue;
                    }

                    if (outcome == SummarizeOutcome.Stale)
                        return;

                    // 空结果不算失败，也没东西可提交，照常跳过这一片
                    if (outcome == SummarizeOutcome.Empty)
                        Logger.Log($"OverflowManager: [{batchStart}..{batchStart + batchSize}) 总结为空，跳过该片");
                    else
                        committed++;

                    cursor += batchSize;
                    _lastSummarizedIndex = segmentStart + cursor;
                    _retreatBatchSize = 0;
                    consecutiveApiFailures = 0;
                }

                AccumulatedOverflowMessageCount = 0;
                AccumulatedOverflowTokens = 0;

                Logger.Log($"OverflowManager: Incremental summary completed. 提交 {committed} 片，" +
                           $"跳过超长 {skippedOversize} 条，lastSummarizedIndex={_lastSummarizedIndex}");
            }
            catch (Exception ex)
            {
                Logger.Log($"OverflowManager: Failed to trigger overflow summary: {ex.Message}");
            }
        }

        private enum SummarizeOutcome
        {
            Committed,
            Empty,
            Stale
        }

        /// <summary>单条消息都总结不了时，最多连着跳过几条就判定为服务故障、整轮放弃。</summary>
        private const int MaxConsecutiveSingleFailures = 3;

        /// <summary>预算未知时单次请求携带的历史文本上限。</summary>
        private const int FallbackRequestTokenBudget = 24000;

        /// <summary>失败退让后指定的下一批条数；0 表示按预算自由计算。</summary>
        private int _retreatBatchSize;

        /// <summary>
        /// 单次总结请求能携带多少 token 的历史文本。
        ///
        /// 从生效上下文预算里扣掉提示词本身和留给模型写总结的输出空间。
        /// 分段制下请求里不带已有总结，不用再扣它。
        /// </summary>
        private int ResolveRequestTokenBudget()
        {
            var context = _chatCore?.EffectiveContextTokenBudget ?? 0;
            if (context <= 0) context = _settings?.MaxContextTokens ?? 0;
            if (context <= 0) return FallbackRequestTokenBudget;

            // 输出空间：至多 SummaryTokenBudget，没配就按上下文的 15% 估
            var output = SummaryTokenBudget;
            if (output <= 0) output = Math.Max(256, (int)(context * 0.15));

            // 提示词模板 + 估算误差的余量
            const int promptOverhead = 512;

            var available = context - output - promptOverhead;

            // 上下文很小时也要留一条能走的路：给一个下限，让流程继续推进，
            // 真发不出去会由失败退让那条分支接住。
            return Math.Max(MinRequestTokenBudget, available);
        }

        private const int MinRequestTokenBudget = 512;

        /// <summary>
        /// 从 <paramref name="start"/> 起最多能放进预算的条数，至少 1 条。
        /// 上一轮失败退让指定了条数时以它为准。
        /// </summary>
        private int TakeBatchSize(List<Message> messages, int start, int budget)
        {
            var available = messages.Count - start;
            if (_retreatBatchSize > 0)
                return Math.Min(_retreatBatchSize, available);

            var used = 0;
            var count = 0;
            while (count < available)
            {
                var tokens = TokenCounter.EstimateTokenCount(messages[start + count].Content ?? "");
                if (count > 0 && used + tokens > budget)
                    break;
                used += tokens;
                count++;
            }

            return Math.Max(1, count);
        }

        private static bool ExceedsBudget(Message message, int budget)
            => TokenCounter.EstimateTokenCount(message.Content ?? "") > budget;

        /// <summary>
        /// 这次失败有没有可能是因为这一片太大。只有这种失败值得切小重试或跳过单条。
        /// </summary>
        private static bool MayBeCausedByInputSize(SummarizeFailedException ex)
        {
            // 底层是传输异常 = 根本没拿到模型的判定。加密通道把网关/CDN 自己回的
            // 错误页也报成 HttpRequestException，归在这里。
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            {
                if (inner is System.Net.Http.HttpRequestException or OperationCanceledException or TimeoutException or IOException)
                    return false;
            }

            // 只有这几个码可能是在说「输入太长」。没有状态码的失败（无可用渠道、
            // 配置未加载、响应解析失败）同样与长度无关。
            return ex.StatusCode is System.Net.HttpStatusCode.BadRequest
                or System.Net.HttpStatusCode.RequestEntityTooLarge
                or System.Net.HttpStatusCode.UnprocessableEntity;
        }

        /// <summary>
        /// 只追加在 Overflow_Summary_Prefix 后面：Free 网关按名单里提示词的首段放行，
        /// 首段原样保留，追加的说明不影响匹配。
        /// </summary>
        private const string SegmentInstruction =
            "Summarize ONLY the conversation segment below. It is a new part of an ongoing conversation; " +
            "earlier parts are already recorded separately, so do not restate them, do not continue or append to " +
            "any earlier summary, and do not add headings such as \"Supplement\" or \"Update\". " +
            "Output one fact per line, each line starting with \"- \". Messages carry their send time; turn relative " +
            "times (today, tomorrow, last week) into concrete dates. If nothing in the segment is worth remembering, output nothing.";

        /// <summary>
        /// 总结一片消息并提交。请求里只有这一片消息，不带已有总结——
        /// 带了模型就会把旧总结抄一遍再续写，旧内容被重复计入。
        /// 失败抛 <see cref="SummarizeFailedException"/> 由调用方决定退让还是跳过。
        /// </summary>
        private async Task<SummarizeOutcome> SummarizeBatchAsync(
            List<Message> batch, int batchStart, int msgThreshold, int generation)
        {
            var batchEnd = batchStart + batch.Count;
            var tokenCount = TokenCounter.EstimateMessagesTokenCount(batch);

            var historyText = string.Join("\n", batch
                .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                .Select(FormatForSummary));

            if (string.IsNullOrWhiteSpace(historyText))
            {
                CommitEmptySegment(batchStart, batchEnd, tokenCount, msgThreshold);
                return SummarizeOutcome.Empty;
            }

            var systemPrompt = PromptHelper.Get("Overflow_Summary_Prefix", _settings.PromptLanguage);
            if (systemPrompt.StartsWith("[Prompt Missing"))
                systemPrompt = PromptHelper.Get("Context_Summary_Prefix", _settings.PromptLanguage);

            systemPrompt += "\n\n" + SegmentInstruction;
            if (MemoryGraph is not null)
                systemPrompt += "\n" + MemoryFactParser.MarkerInstruction;

            // 一片只是十几二十条消息，给它总预算的四分之一足够；分段多了由合并兜住
            var budget = SummaryTokenBudget;
            if (budget > 0)
                systemPrompt += $"\nKeep it under roughly {Math.Max(128, budget / 4)} tokens.";

            if (_settings.EnableCompressionRecords && _recordManager is not null)
                systemPrompt += "\n" + PromptHelper.Get("Context_Summary_RecordHint", _settings.PromptLanguage);

            var raw = await _chatCore.Summarize(systemPrompt, historyText);

            // 先判过期再判空：清空之后回来的空结果也不能落空片，否则检查点又被它推回去
            if (generation != _generation)
            {
                Logger.Log("OverflowManager: 总结期间上下文已被清空，丢弃本次结果");
                return SummarizeOutcome.Stale;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                CommitEmptySegment(batchStart, batchEnd, tokenCount, msgThreshold);
                return SummarizeOutcome.Empty;
            }

            // 实体标记只用来建图，落库、注入、去重用的都是去掉标记的文本
            var markers = MemoryFactParser.IndexMarkers(raw);
            var summary = MemoryFactParser.StripMarkers(raw);

            // Extract record commands
            if (_settings.EnableCompressionRecords && _recordManager is not null)
                summary = ExtractAndExecuteRecordCommands(summary);

            // 模型仍可能复述早先说过的事实（名字、偏好之类每片都提一次），
            // 和已有条目逐行比对，重复的不再存第二遍。先字面，再拿向量补一刀换了说法的
            var known = _activeEntries.Select(e => e.SummaryText).ToList();
            var filtered = FactSimilarity.Filter(summary, known);
            if (filtered.Kept.Count > 0 && known.Count > 0)
            {
                var semantic = await FindSemanticDuplicatesAsync(filtered.Kept, known);
                if (generation != _generation)
                {
                    Logger.Log("OverflowManager: 总结期间上下文已被清空，丢弃本次结果");
                    return SummarizeOutcome.Stale;
                }
                if (semantic.Count > 0)
                {
                    filtered = FactSimilarity.Filter(summary, known,
                        line => semantic.TryGetValue(line.Trim(), out var matched) ? matched : null);
                    Logger.Log($"OverflowManager: [{batchStart}..{batchEnd}) 语义去重去掉 {semantic.Count} 行");
                }
            }

            if (string.IsNullOrWhiteSpace(filtered.Text) || filtered.Kept.Count == 0)
            {
                Logger.Log($"OverflowManager: [{batchStart}..{batchEnd}) 的总结全部与已有条目重复，不新增");
                var emptyId = CommitEmptySegment(batchStart, batchEnd, tokenCount, msgThreshold);
                // 复述也是信号：被重复提到的事实衰减得更慢
                if (emptyId > 0 && filtered.Duplicates.Count > 0)
                    MemoryGraph?.IngestSummary(EffectiveProvider, emptyId, Array.Empty<string>(), filtered.Duplicates, markers, DateTime.UtcNow);
                return SummarizeOutcome.Empty;
            }
            summary = filtered.Text;

            var summaryId = _database.CreateSummary(
                EffectiveProvider, summary, batchStart, batchEnd - 1, tokenCount, msgThreshold, OverflowSummaryKind.Segment);

            var segments = batch.Select((m, i) => new OverflowSegmentData
            {
                ContentHash = ComputeSimpleHash(m.Content ?? ""),
                MessageIndex = batchStart + i,
                Role = m.Role,
                ContentPreview = Truncate(m.Content, 200),
                TokenCount = TokenCounter.EstimateTokenCount(m.Content ?? "")
            }).ToList();

            if (summaryId > 0)
            {
                _database.AddSegments(summaryId, segments);
                MemoryGraph?.IngestSummary(EffectiveProvider, summaryId, filtered.Kept, filtered.Duplicates, markers, DateTime.UtcNow);
            }

            ReloadActiveEntries();
            Logger.Log($"OverflowManager: 已提交分片 #{summaryId} 覆盖 [{batchStart}..{batchEnd})，{batch.Count} 条");

            await ConsolidateIfNeededAsync(msgThreshold, generation);
            return SummarizeOutcome.Committed;
        }

        /// <summary>
        /// 这一片没有可存的内容（全是空消息、模型没输出、全部重复）时也落一行 kind=empty：
        /// 它不注入、不进编辑器默认列表，只为让检查点在重启后仍越过这一片，
        /// 不然重启后会把同一片再总结一遍。
        /// </summary>
        private int CommitEmptySegment(int batchStart, int batchEnd, int tokenCount, int msgThreshold)
            => _database.CreateSummary(EffectiveProvider, "", batchStart, batchEnd - 1, tokenCount, msgThreshold, OverflowSummaryKind.Empty);

        /// <summary>
        /// 找出 <paramref name="candidates"/> 里与已有条目意思相同、只是换了说法的行。
        /// 返回「候选行 → 它重复的已有行」。向量服务没开、不可用或调用失败都返回空表，退回纯字面去重。
        /// </summary>
        private async Task<Dictionary<string, string>> FindSemanticDuplicatesAsync(
            IReadOnlyList<string> candidates, IReadOnlyList<string> knownEntries)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var embedding = EmbeddingService;
            if (embedding is null || !embedding.IsAvailable)
                return result;

            static string? FactText(string line)
            {
                var text = FactSimilarity.StripListPrefix(line);
                var key = FactSimilarity.Normalize(text);
                return key.Length < 4 || FactSimilarity.IsLabelLine(line, key) ? null : text;
            }

            var knownLines = knownEntries
                .SelectMany(e => e.Replace("\r\n", "\n").Split('\n'))
                .Select(l => (Raw: l.Trim(), Text: FactText(l)))
                .Where(x => x.Text is not null)
                .ToList();
            var candidateLines = candidates
                .Select(l => (Raw: l.Trim(), Text: FactText(l)))
                .Where(x => x.Text is not null)
                .ToList();
            if (knownLines.Count == 0 || candidateLines.Count == 0)
                return result;

            try
            {
                // 候选行在前：已有行大多已在缓存里，新行受 maxNew 限制时先保证候选行有向量
                var texts = candidateLines.Select(x => x.Text!).Concat(knownLines.Select(x => x.Text!)).Distinct().ToList();
                var vectors = await embedding.GetOrEmbedAsync(texts);
                if (vectors is null)
                    return result;

                foreach (var (raw, text) in candidateLines)
                {
                    if (!vectors.TryGetValue(text!, out var v))
                        continue;

                    string? best = null;
                    var bestScore = SemanticDuplicateThreshold;
                    foreach (var known in knownLines)
                    {
                        if (!vectors.TryGetValue(known.Text!, out var kv))
                            continue;
                        var score = EmbeddingService.CosineSimilarity(v, kv);
                        if (score >= bestScore && FactSimilarity.AllowsSemanticMatch(raw, known.Raw))
                        {
                            best = known.Raw;
                            bestScore = score;
                        }
                    }
                    if (best is not null)
                        result[raw] = best;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"OverflowManager: 语义去重失败，只做字面去重: {ex.Message}");
                result.Clear();
            }
            return result;
        }

        /// <summary>带上发送时间：模型要靠它把「明天」「上周」换成具体日期。</summary>
        private static string FormatForSummary(Message message)
        {
            if (message.UnixTime is long seconds && seconds > 0)
            {
                var time = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
                return $"[{message.Role} {time:yyyy-MM-dd HH:mm}]: {message.Content}";
            }
            return $"[{message.Role}]: {message.Content}";
        }

        private const string CompactPromptFallback =
            "You are compacting a factual summary that has grown too long. Merge duplicates, drop the least " +
            "important details, and keep every fact that future conversations may need. Preserve the original " +
            "language. Output only the compacted summary.";

        private const string ConsolidateInstruction =
            "The entries below are in chronological order. When two entries disagree about the same matter, keep the " +
            "later one. Output one fact per line, each line starting with \"- \", with no headings.";

        /// <summary>
        /// 有效条目总量超出预算时，把最旧的几条合并成一条 consolidated。
        ///
        /// 只动旧条目：从最新往回数，预算的一半留给近期分段原样保留，其余合并。
        /// 合并请求里只有这些旧条目、没有新对话，模型无从「续写」。
        /// 合并失败时什么都不改——分段都还在，注入时 ClampSummaryForPrompt 会从最旧的一头裁，
        /// 下次落新分段时再试。
        /// </summary>
        private async Task ConsolidateIfNeededAsync(int msgThreshold, int generation)
        {
            var budget = SummaryTokenBudget;
            var entries = _activeEntries;
            if (budget <= 0 || entries.Count == 0)
                return;

            var sizes = entries.Select(e => TokenCounter.EstimateTokenCount(e.SummaryText)).ToList();
            var total = sizes.Sum();
            if (total <= budget)
                return;

            if (_consolidationSkipsLeft > 0)
            {
                _consolidationSkipsLeft--;
                Logger.Log($"OverflowManager: 合并已连续失败 {_consolidationFailures} 次，退避中，本次不合并（还要跳过 {_consolidationSkipsLeft} 次）");
                return;
            }

            var kept = 0;
            var splitAt = entries.Count;
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                if (kept + sizes[i] > budget / 2)
                    break;
                kept += sizes[i];
                splitAt = i;
            }

            // 最新一条自己就超过一半预算时 splitAt 停在末尾，那就全部合并
            var toMerge = entries.Take(splitAt == entries.Count ? entries.Count : splitAt).ToList();
            if (toMerge.Count == 0)
                return;

            var mergeBudget = Math.Max(128, budget - kept);
            Logger.Log($"OverflowManager: 有效总结 {total} tokens 超出预算 {budget}，合并最旧的 {toMerge.Count} 条（保留近期 {entries.Count - toMerge.Count} 条）");

            string merged;
            try
            {
                var prompt = PromptHelper.Get("Overflow_Summary_Compact", _settings.PromptLanguage);
                if (prompt.StartsWith("[Prompt Missing"))
                    prompt = CompactPromptFallback;
                prompt += "\n" + ConsolidateInstruction + $"\nThe result must stay under roughly {mergeBudget} tokens.";

                merged = await _chatCore.Summarize(prompt, string.Join("\n\n", toMerge.Select(e => e.SummaryText.Trim())));
            }
            // SummarizeFailedException 也走这里：绝不能用失败文案替换掉本来有效的条目
            catch (Exception ex)
            {
                Logger.Log($"OverflowManager: 合并旧总结失败，保留原分段待下次再合: {ex.Message}");
                BackOffConsolidation();
                return;
            }

            if (generation != _generation)
                return;

            merged = string.IsNullOrWhiteSpace(merged) ? "" : FactSimilarity.Filter(merged, Array.Empty<string>()).Text;
            if (string.IsNullOrWhiteSpace(merged))
            {
                Logger.Log("OverflowManager: 合并结果为空，保留原分段待下次再合");
                BackOffConsolidation();
                return;
            }

            _consolidationFailures = 0;
            _consolidationSkipsLeft = 0;

            // 仍超标时硬截断。保留开头：近期事实在后面的分段里，合并块的独有价值在更早的部分
            if (TokenCounter.EstimateTokenCount(merged) > mergeBudget)
                merged = TruncateToTokenBudget(merged, mergeBudget);

            var id = _database.ConsolidateSummaries(EffectiveProvider, merged,
                toMerge.Min(e => e.SegmentStartIndex), toMerge.Max(e => e.SegmentEndIndex),
                toMerge.Sum(e => e.TokenCount), msgThreshold, toMerge.Select(e => e.Id).ToList());
            if (id > 0)
                ReloadActiveEntries();
        }

        /// <summary>
        /// 失败一次跳过 2 次合并机会，之后 4、8、16 次封顶。
        /// 分段照常落库，注入时 ClampSummaryForPrompt 从最旧的一头裁，超预算期间不会撑爆窗口。
        /// </summary>
        private void BackOffConsolidation()
        {
            _consolidationFailures++;
            _consolidationSkipsLeft = Math.Min(16, 1 << Math.Min(_consolidationFailures, 5));
        }

        private void ReloadActiveEntries()
        {
            var entries = _database.GetActiveSummaries(EffectiveProvider);
            _activeEntries = entries;
            var text = string.Join("\n\n", entries.Select(e => e.SummaryText.Trim()).Where(t => t.Length > 0));
            _currentSummary = text.Length == 0 ? null : text;
        }

        private const string TruncationMarker = "\n…[summary truncated]";

        /// <summary>
        /// 按字符裁剪文本直到估算 token 数落入预算内。TokenCounter 没有反函数，
        /// 因此先按比例估一个长度，再逐步收缩。
        /// </summary>
        private static string TruncateToTokenBudget(string text, int budget)
        {
            var markerTokens = TokenCounter.EstimateTokenCount(TruncationMarker);
            var target = Math.Max(1, budget - markerTokens);

            var length = text.Length;
            for (int pass = 0; pass < 12 && length > 0; pass++)
            {
                var tokens = TokenCounter.EstimateTokenCount(text.Substring(0, length));
                if (tokens <= target)
                    break;

                // 按当前超标比例收缩，再多砍 5% 保证收敛
                var ratio = (double)target / tokens;
                var next = (int)(length * ratio * 0.95);
                length = next < length ? Math.Max(1, next) : length - 1;
            }

            return text.Substring(0, length).TrimEnd() + TruncationMarker;
        }

        /// <summary>
        /// Search overflow summaries by keyword.
        /// </summary>
        public List<OverflowSummaryRecord> SearchSummaries(string keyword, int limit = 10)
            => _database.SearchSummaries(EffectiveProvider, keyword, limit);

        /// <summary>
        /// Count summaries for the editor's pager.
        /// </summary>
        /// <param name="includeInactive">连同已被合并掉的旧分段、空片、旧版滚动总结一起数。</param>
        public int GetSummaryCount(bool includeInactive = false) => _database.GetSummaryCount(EffectiveProvider, includeInactive);

        /// <summary>
        /// Get one page of summaries, newest first.
        /// </summary>
        public List<OverflowSummaryRecord> GetSummariesPage(int offset, int limit, bool includeInactive = false)
            => _database.GetSummariesPage(EffectiveProvider, offset, limit, includeInactive);

        /// <summary>
        /// 检索用的只读快照：不同步记录、不回填，只读缓存。图谱不可用时返回 null。
        /// </summary>
        public MemoryGraphData? GetMemoryGraphSnapshot()
        {
            var graph = MemoryGraph;
            if (graph is null)
                return null;
            var data = graph.Load(EffectiveProvider);
            return new MemoryGraphData
            {
                Atoms = data.Atoms,
                Entities = data.Entities,
                ActiveSummaryIds = new HashSet<int>(_activeEntries.Select(e => e.Id))
            };
        }

        /// <summary>
        /// 给星云图准备数据：先把重要记录同步成原子、旧库没有原子时从有效条目回填，再整体读出。
        /// 会读写数据库，调用方放在后台线程。
        /// </summary>
        public MemoryGraphData? LoadMemoryGraphData(RecordManager? records = null)
        {
            var graph = MemoryGraph;
            if (graph is null)
                return null;

            var now = DateTime.UtcNow;
            records ??= _recordManager;
            if (records is not null)
            {
                try
                {
                    graph.SyncRecords(records.GetAllRecordsForEditing(), now);
                }
                catch (Exception ex)
                {
                    Logger.Log($"OverflowManager: 同步重要记录到图谱失败: {ex.Message}");
                }
            }

            var entries = _activeEntries;
            graph.BackfillIfEmpty(EffectiveProvider, entries, now);

            var data = graph.Load(EffectiveProvider);
            var active = new HashSet<int>(entries.Select(e => e.Id));
            return new MemoryGraphData { Atoms = data.Atoms, Entities = data.Entities, ActiveSummaryIds = active };
        }

        /// <summary>
        /// Get segments associated with a summary.
        /// </summary>
        public List<OverflowSegmentRecord> GetSegmentsForSummary(int summaryId)
            => _database.GetSegmentsForSummary(summaryId);

        /// <summary>
        /// Update a summary's text (user edit) and refresh in-memory state so the
        /// injected rolling summary reflects the edit immediately.
        /// </summary>
        public void UpdateSummaryText(int summaryId, string newText)
        {
            _database.UpdateSummaryText(summaryId, newText);
            MemoryGraph?.ReplaceSummary(EffectiveProvider, summaryId, newText, DateTime.UtcNow);
            RefreshFromDatabase();
        }

        /// <summary>
        /// 删除一条总结并按库里剩下的重建内存状态。删的是哪一条，注入里就少哪一条；
        /// 删掉合并块不会让被它合并的旧分段复活。
        /// </summary>
        public void DeleteSummary(int summaryId)
        {
            _database.DeleteSummary(summaryId);
            MemoryGraph?.DeleteSummary(summaryId);
            RefreshFromDatabase();
        }

        /// <summary>
        /// Re-derive the active summary entries and checkpoint from the database.
        /// </summary>
        private void RefreshFromDatabase()
        {
            _lastSummarizedIndex = 0;
            _currentSummary = null;
            _activeEntries = new();
            RestoreFromDatabase();
        }

        /// <summary>
        /// Clear all overflow data for the current provider.
        /// </summary>
        public void ClearAll()
        {
            Interlocked.Increment(ref _generation);
            _lastSummarizedIndex = 0;
            _currentSummary = null;
            _activeEntries = new();
            AccumulatedOverflowMessageCount = 0;
            AccumulatedOverflowTokens = 0;
            _consolidationFailures = 0;
            _consolidationSkipsLeft = 0;
            _database.ClearAll(EffectiveProvider);
            MemoryGraph?.Clear(EffectiveProvider);
        }

        private string ExtractAndExecuteRecordCommands(string summary)
        {
            try
            {
                var recordRegex = new System.Text.RegularExpressions.Regex(
                    @"<\|\s*record\s*_begin\s*\|>(.*?)<\|\s*record\s*_end\s*\|>",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                var matches = recordRegex.Matches(summary);
                if (matches.Count == 0) return summary;

                var textRegex = new System.Text.RegularExpressions.Regex(@"text\s*\(\s*""([^""]*)""\s*\)");
                var weightRegex = new System.Text.RegularExpressions.Regex(@"weight\s*\(\s*(\d+)\s*\)");

                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var commandValue = match.Groups[1].Value;
                    var textMatch = textRegex.Match(commandValue);
                    var weightMatch = weightRegex.Match(commandValue);
                    if (textMatch.Success)
                    {
                        var content = textMatch.Groups[1].Value;
                        var weight = weightMatch.Success ? int.Parse(weightMatch.Groups[1].Value) : 5;
                        weight = Math.Clamp(weight, 1, 10);
                        _recordManager?.CreateRecord(content, weight);
                    }
                }
                var cleaned = recordRegex.Replace(summary, "").Trim();
                cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\n{3,}", "\n\n");
                return cleaned;
            }
            catch (Exception ex)
            {
                Logger.Log($"Overflow memory extraction failed: {ex.Message}");
                return summary;
            }
        }

        private static string ComputeSimpleHash(string input)
        {
            if (string.IsNullOrEmpty(input)) return "empty";
            unchecked { int hash = 17; foreach (char c in input) hash = hash * 31 + c; return hash.ToString("x8"); }
        }

        private static string Truncate(string? text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= maxLen ? text : text.Substring(0, maxLen) + "...";
        }

        private string GetDatabasePath()
        {
            var docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dataPath = Path.Combine(docPath, "VPetLLM", "Chat");
            if (!Directory.Exists(dataPath)) Directory.CreateDirectory(dataPath);
            return Path.Combine(dataPath, "chat_history.db");
        }
    }
}
