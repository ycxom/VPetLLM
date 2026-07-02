using VPetLLM.Core.Data.Database;

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

        public OverflowManager(Setting settings, string providerName, ChatCoreBase chatCore, RecordManager? recordManager)
        {
            _settings = settings;
            _providerName = providerName;
            _chatCore = chatCore;
            _recordManager = recordManager;
            _database = new OverflowDatabase(GetDatabasePath());

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

                var historyText = string.Join("\n", overflowedMessages
                    .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                    .Select(m => $"[{m.Role}]: {m.Content}"));

                if (string.IsNullOrWhiteSpace(historyText))
                {
                    Logger.Log("OverflowManager: No content to summarize, skipping");
                    return;
                }

                var systemPrompt = PromptHelper.Get("Overflow_Summary_Prefix", _settings.PromptLanguage);
                if (systemPrompt.StartsWith("[Prompt Missing"))
                    systemPrompt = PromptHelper.Get("Context_Summary_Prefix", _settings.PromptLanguage);

                // If we already have a prior summary, tell the LLM to extend it
                if (!string.IsNullOrEmpty(_currentSummary))
                {
                    systemPrompt += "\n\nPrevious summary context:\n" + _currentSummary;
                    systemPrompt += "\n\nThe above is the existing summary. Please EXTEND it with the new information below, keeping all prior facts. Output the COMPLETE updated summary.";
                }

                if (_settings.EnableCompressionRecords && _recordManager is not null)
                    systemPrompt += "\n" + PromptHelper.Get("Context_Summary_RecordHint", _settings.PromptLanguage);

                var summary = await _chatCore.Summarize(systemPrompt, historyText);

                if (string.IsNullOrWhiteSpace(summary))
                {
                    Logger.Log("OverflowManager: Summary returned empty");
                    return;
                }

                // 上下文在总结期间被清空 — 丢弃过期结果
                if (generation != _generation)
                {
                    Logger.Log("OverflowManager: 总结期间上下文已被清空，丢弃本次结果");
                    return;
                }

                // Extract record commands
                if (_settings.EnableCompressionRecords && _recordManager is not null)
                    summary = ExtractAndExecuteRecordCommands(summary);

                // Store in database
                var tokenCount = TokenCounter.EstimateMessagesTokenCount(overflowedMessages);
                var summaryId = _database.CreateSummary(EffectiveProvider, summary, segmentStart, segmentEnd - 1, tokenCount, msgThreshold);

                var segments = overflowedMessages.Select((m, i) => new OverflowSegmentData
                {
                    ContentHash = ComputeSimpleHash(m.Content ?? ""),
                    MessageIndex = segmentStart + i,
                    Role = m.Role,
                    ContentPreview = Truncate(m.Content, 200),
                    TokenCount = TokenCounter.EstimateTokenCount(m.Content ?? "")
                }).ToList();

                if (summaryId > 0)
                    _database.AddSegments(summaryId, segments);

                // Replace the rolling summary and advance the checkpoint
                _currentSummary = summary;
                _lastSummarizedIndex = segmentEnd;
                AccumulatedOverflowMessageCount = 0;
                AccumulatedOverflowTokens = 0;

                Logger.Log($"OverflowManager: Incremental summary completed. SummaryId={summaryId}, lastSummarizedIndex={_lastSummarizedIndex}");
            }
            catch (Exception ex)
            {
                Logger.Log($"OverflowManager: Failed to trigger overflow summary: {ex.Message}");
            }
        }

        /// <summary>
        /// Search overflow summaries by keyword.
        /// </summary>
        public List<OverflowSummaryRecord> SearchSummaries(string keyword, int limit = 10)
            => _database.SearchSummaries(EffectiveProvider, keyword, limit);

        /// <summary>
        /// Get segments associated with a summary.
        /// </summary>
        public List<OverflowSegmentRecord> GetSegmentsForSummary(int summaryId)
            => _database.GetSegmentsForSummary(summaryId);

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
