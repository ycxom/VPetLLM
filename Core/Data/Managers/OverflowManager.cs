using VPetLLM.Core.Data.Database;
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
    /// The summary is a single rolling text: each new summarization feeds the
    /// current summary back to the LLM and stores the complete updated version,
    /// which supersedes the previous one. It is injected into the prompt as a
    /// [Previous Conversation Summary] system message, and the prompt only
    /// contains messages after the checkpoint (sliding window).
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
        /// Rolling summary text. Each summarization replaces it with the
        /// complete updated version. Injected into prompt as context.
        /// </summary>
        private string? _currentSummary;

        /// <summary>
        /// Incremented on ClearAll so an in-flight summarization started before
        /// the clear discards its result instead of resurrecting stale state.
        /// </summary>
        private int _generation;

        private volatile int _isTriggering;  // 0 = idle, 1 = running — 防止并发触发多个 Summarize

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
            _database = new OverflowDatabase(databasePath ?? GetDatabasePath());

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
                var endIndex = _database.GetMaxSegmentEndIndex(provider);
                if (endIndex > 0)
                {
                    _lastSummarizedIndex = endIndex;
                    _currentSummary = _database.GetLatestSummaryText(provider);
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
        /// Triggers an incremental overflow summary. The LLM receives the current
        /// rolling summary plus the newly overflowed messages, and returns the
        /// complete updated summary which replaces the previous one.
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
                        if (batchSize > 1)
                        {
                            // 还能再切：对半退让后重试，不推进游标。
                            // 这条路同时兜住了「我们估的 token 数比模型实际算的小」。
                            _retreatBatchSize = Math.Max(1, batchSize / 2);
                            Logger.Log($"OverflowManager: [{batchStart}..{batchStart + batchSize}) 总结失败，" +
                                       $"批量退让至 {_retreatBatchSize} 重试: {ex.Message}");
                            continue;
                        }

                        // 已经只剩一条还失败。按要求跳过这一条，但连续失败说明多半是
                        // 服务不可用而不是消息太长——那样一条条跳下去会把整段积压静默吞掉，
                        // 所以到阈值就整轮放弃，留给下次触发重试。
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
        /// 从生效上下文预算里扣掉三块：滚动总结（它每片都会变长，所以每片都要重算）、
        /// 提示词本身、以及留给模型写新总结的输出空间。
        /// </summary>
        private int ResolveRequestTokenBudget()
        {
            var context = _chatCore?.EffectiveContextTokenBudget ?? 0;
            if (context <= 0) context = _settings?.MaxContextTokens ?? 0;
            if (context <= 0) return FallbackRequestTokenBudget;

            var rollingSummary = string.IsNullOrEmpty(_currentSummary)
                ? 0
                : TokenCounter.EstimateTokenCount(_currentSummary);

            // 输出空间：新总结至多 SummaryTokenBudget，没配就按上下文的 15% 估
            var output = SummaryTokenBudget;
            if (output <= 0) output = Math.Max(256, (int)(context * 0.15));

            // 提示词模板 + 估算误差的余量
            const int promptOverhead = 512;

            var available = context - rollingSummary - output - promptOverhead;

            // 预算被滚动总结挤没了也要留一条能走的路：给一个下限，让流程继续推进，
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
        /// 总结一片消息并提交。成功时替换滚动总结——下一片会拿它当
        /// "Previous summary context"，分片之间正是靠这个串起来的。
        /// 失败抛 <see cref="SummarizeFailedException"/> 由调用方决定退让还是跳过。
        /// </summary>
        private async Task<SummarizeOutcome> SummarizeBatchAsync(
            List<Message> batch, int batchStart, int msgThreshold, int generation)
        {
            var historyText = string.Join("\n", batch
                .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => $"[{m.Role}]: {m.Content}"));

            if (string.IsNullOrWhiteSpace(historyText))
                return SummarizeOutcome.Empty;

            var systemPrompt = PromptHelper.Get("Overflow_Summary_Prefix", _settings.PromptLanguage);
            if (systemPrompt.StartsWith("[Prompt Missing"))
                systemPrompt = PromptHelper.Get("Context_Summary_Prefix", _settings.PromptLanguage);

            // If we already have a prior summary, tell the LLM to extend it
            if (!string.IsNullOrEmpty(_currentSummary))
            {
                systemPrompt += "\n\nPrevious summary context:\n" + _currentSummary;
                systemPrompt += "\n\nThe above is the existing summary. Please EXTEND it with the new information below, keeping all prior facts. Output the COMPLETE updated summary.";

                // 没有上限时滚动总结会单调增长，最终成为 prompt 里最大的一块
                var budget = SummaryTokenBudget;
                if (budget > 0)
                    systemPrompt += $"\nKeep the complete summary under roughly {budget} tokens. If it would exceed that, merge or drop the least important facts rather than growing.";
            }

            if (_settings.EnableCompressionRecords && _recordManager is not null)
                systemPrompt += "\n" + PromptHelper.Get("Context_Summary_RecordHint", _settings.PromptLanguage);

            var summary = await _chatCore.Summarize(systemPrompt, historyText);

            if (string.IsNullOrWhiteSpace(summary))
                return SummarizeOutcome.Empty;

            // 上下文在总结期间被清空 — 丢弃过期结果
            if (generation != _generation)
            {
                Logger.Log("OverflowManager: 总结期间上下文已被清空，丢弃本次结果");
                return SummarizeOutcome.Stale;
            }

            // Extract record commands
            if (_settings.EnableCompressionRecords && _recordManager is not null)
                summary = ExtractAndExecuteRecordCommands(summary);

            // 每片都压一次：滚动总结是下一片的输入，放任它涨会把后续预算吃光
            summary = await CompactSummaryIfNeededAsync(summary);

            var batchEnd = batchStart + batch.Count;
            var tokenCount = TokenCounter.EstimateMessagesTokenCount(batch);
            var summaryId = _database.CreateSummary(
                EffectiveProvider, summary, batchStart, batchEnd - 1, tokenCount, msgThreshold);

            var segments = batch.Select((m, i) => new OverflowSegmentData
            {
                ContentHash = ComputeSimpleHash(m.Content ?? ""),
                MessageIndex = batchStart + i,
                Role = m.Role,
                ContentPreview = Truncate(m.Content, 200),
                TokenCount = TokenCounter.EstimateTokenCount(m.Content ?? "")
            }).ToList();

            if (summaryId > 0)
                _database.AddSegments(summaryId, segments);

            _currentSummary = summary;
            Logger.Log($"OverflowManager: 已提交分片 #{summaryId} 覆盖 [{batchStart}..{batchEnd})，{batch.Count} 条");
            return SummarizeOutcome.Committed;
        }

        private const string CompactPromptFallback =
            "You are compacting a factual summary that has grown too long. Merge duplicates, drop the least " +
            "important details, and keep every fact that future conversations may need. Preserve the original " +
            "language. Output only the compacted summary.";

        /// <summary>
        /// 若滚动总结超出 token 预算，先请 LLM 压缩一次；压缩失败或仍然超标时按字符硬截断。
        /// </summary>
        private async Task<string> CompactSummaryIfNeededAsync(string summary)
        {
            var budget = SummaryTokenBudget;
            if (budget <= 0 || string.IsNullOrWhiteSpace(summary))
                return summary;

            if (TokenCounter.EstimateTokenCount(summary) <= budget)
                return summary;

            var before = TokenCounter.EstimateTokenCount(summary);
            Logger.Log($"OverflowManager: 滚动总结 {before} tokens 超出预算 {budget}，开始压缩");

            try
            {
                var compactPrompt = PromptHelper.Get("Overflow_Summary_Compact", _settings.PromptLanguage);
                if (compactPrompt.StartsWith("[Prompt Missing"))
                    compactPrompt = CompactPromptFallback;
                compactPrompt += $"\nThe result must stay under roughly {budget} tokens.";

                var compacted = await _chatCore.Summarize(compactPrompt, summary);
                if (!string.IsNullOrWhiteSpace(compacted))
                    summary = compacted;
            }
            // SummarizeFailedException 也走这里：压缩失败时保留未压缩的原总结，
            // 交给下面的硬截断，绝不能用失败文案替换掉一份本来有效的总结。
            catch (Exception ex)
            {
                Logger.Log($"OverflowManager: 总结压缩失败，改用硬截断: {ex.Message}");
            }

            // 压缩后仍超标（或压缩失败）时硬截断。保留开头：近期事实本来就还在滑动窗口里
            // 逐字保存着，总结的独有价值在于更早的那部分。
            if (TokenCounter.EstimateTokenCount(summary) > budget)
                summary = TruncateToTokenBudget(summary, budget);

            Logger.Log($"OverflowManager: 总结压缩完成 {before} → {TokenCounter.EstimateTokenCount(summary)} tokens");
            return summary;
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
        public int GetSummaryCount() => _database.GetSummaryCount(EffectiveProvider);

        /// <summary>
        /// Get one page of summaries, newest first.
        /// </summary>
        public List<OverflowSummaryRecord> GetSummariesPage(int offset, int limit)
            => _database.GetSummariesPage(EffectiveProvider, offset, limit);

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
            RefreshFromDatabase();
        }

        /// <summary>
        /// Delete a summary and roll in-memory state back to what the database
        /// still holds (deleting the latest row falls back to the previous version).
        /// </summary>
        public void DeleteSummary(int summaryId)
        {
            _database.DeleteSummary(summaryId);
            RefreshFromDatabase();
        }

        /// <summary>
        /// Re-derive the rolling summary and checkpoint from the database.
        /// </summary>
        private void RefreshFromDatabase()
        {
            _lastSummarizedIndex = 0;
            _currentSummary = null;
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
            AccumulatedOverflowMessageCount = 0;
            AccumulatedOverflowTokens = 0;
            _database.ClearAll(EffectiveProvider);
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
