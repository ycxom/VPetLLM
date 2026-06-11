using VPetLLM.Core.Data.Database;

namespace VPetLLM.Core.Data.Managers
{
    /// <summary>
    /// Manages the overflow-based context handling for long-context mode.
    /// 
    /// Logic (example with message threshold = 10):
    ///   9 msgs → nothing
    ///   10 msgs → first summary of msgs[0..10), creates memory record
    ///   11 msgs → 1 overflowed, attaches summary note
    ///   20 msgs → 10 overflowed since last summary → incremental summary of overflowed batch
    ///   21 msgs → 1 new overflow, attaches summary note
    /// 
    /// Summaries accumulate incrementally and are injected into the prompt as a
    /// [Previous Conversation Summary] system message.
    /// Messages are NEVER deleted from history.
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
        /// Accumulated summary text. Injected into prompt as context.
        /// </summary>
        private readonly List<string> _summaryChunks = new();

        /// <summary>
        /// The full concatenated summary, or null if no summary exists.
        /// </summary>
        public string? LatestSummary => _summaryChunks.Count > 0
            ? string.Join("\n\n---\n\n", _summaryChunks)
            : null;

        /// <summary>
        /// Whether any summary has been created (for UI display).
        /// </summary>
        public bool HasSummary => _summaryChunks.Count > 0;

        /// <summary>
        /// Number of messages overflowed since last summary (for UI).
        /// </summary>
        public int AccumulatedOverflowMessageCount { get; private set; }

        /// <summary>
        /// Estimated tokens overflowed since last summary (for UI).
        /// </summary>
        public int AccumulatedOverflowTokens { get; private set; }

        /// <summary>
        /// Trigger overflow check with explicit snapshot data.
        /// Should be called by the Provider AFTER a successful API round and history save.
        /// This replaces the old fire-and-forget from GetCoreHistoryCommonAsync to avoid
        /// triggering overflow summary when the Chat API request ultimately failed/retried.
        /// </summary>
        public void TriggerCheck(List<Message> fullHistory, int snapshotCount)
        {
            // Fire-and-forget the async check (it runs on thread pool)
            _ = CheckAndTriggerAsync(fullHistory, snapshotCount);
        }

        private int _lastRangeHash;
        private volatile int _isTriggering;  // 0 = idle, 1 = running — 防止并发触发多个 Summarize
        private int _lastSummarizedThreshold; // 上次总结时的阈值，用于检测阈值变更

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
                var endIndex = _database.GetMaxSegmentEndIndex();
                if (endIndex > 0)
                {
                    _lastSummarizedIndex = endIndex;
                    _lastSummarizedThreshold = _database.GetMaxSegmentThreshold();
                    var summaries = _database.GetAllSummaryTexts();
                    _summaryChunks.Clear();
                    _summaryChunks.AddRange(summaries);
                    Logger.Log($"OverflowManager: 从数据库恢复状态 - lastSummarizedIndex={_lastSummarizedIndex} threshold={_lastSummarizedThreshold} summaries={_summaryChunks.Count}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"OverflowManager: 恢复数据库状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if any overflow has occurred and trigger summaries as needed.
        /// Called after every user message is added to history.
        /// 
        /// State machine:
        ///   1. Capture snapshotCount at call time (not a reference to mutable list)
        ///   2. Count messages since the committed checkpoint (_lastSummarizedIndex)
        ///   3. If accumulated >= threshold → trigger summary
        ///   4. After summary completes → advance checkpoint (committed to DB)
        /// </summary>
        /// <param name="fullHistory">The message list reference (may grow after call; use snapshotCount for bounds).</param>
        /// <param name="snapshotCount">The number of messages at call time, captured by the caller.</param>
        public async Task CheckAndTriggerAsync(List<Message> fullHistory, int snapshotCount)
        {
            if (fullHistory is null || snapshotCount == 0)
                return;

            var msgThreshold = _settings.HistoryCompressionThreshold;
            if (msgThreshold <= 0) msgThreshold = 20;

            // 检测阈值变更：如果用户调整了溢出阈值，需要相应地调整检查点
            if (_lastSummarizedThreshold > 0 && _lastSummarizedThreshold != msgThreshold)
            {
                // 旧：checkpoint + oldThreshold = 当时的总消息数（近似）
                // 新：应调整为 checkpointNew + newThreshold ≈ 同样的总消息数
                // 所以 checkpointNew = checkpoint + oldThreshold - newThreshold
                var oldCheckpoint = _lastSummarizedIndex;
                var adjusted = oldCheckpoint + _lastSummarizedThreshold - msgThreshold;
                var newCheckpoint = Math.Max(0, adjusted);

                Logger.Log($"OverflowManager: 阈值变更 {_lastSummarizedThreshold}→{msgThreshold}，检查点 {oldCheckpoint}→{newCheckpoint}");

                _lastSummarizedIndex = newCheckpoint;
                _lastSummarizedThreshold = msgThreshold;

                // 同时持久化到数据库（标记新的阈值已生效）
                _database.StoreThresholdMarker(msgThreshold);
            }
            else if (_lastSummarizedThreshold == 0 && _lastSummarizedIndex > 0)
            {
                // 首次检测到已有检查点但无阈值标记，记录当前阈值
                _lastSummarizedThreshold = msgThreshold;
                _database.StoreThresholdMarker(msgThreshold);
            }

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

            // 存在溢出 — 从 fullHistory 中提取（仍用原始引用，但范围由快照计数限定）
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

        /// <summary>
        /// Triggers an incremental overflow summary.
        /// </summary>
        private async Task TriggerSummaryAsync(List<Message> overflowedMessages, int segmentStart, int segmentEnd, int msgThreshold)
        {
            try
            {
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
                if (_summaryChunks.Count > 0)
                {
                    systemPrompt += "\n\nPrevious summary context:\n" + string.Join("\n", _summaryChunks.TakeLast(2));
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

                // Extract record commands
                if (_settings.EnableCompressionRecords && _recordManager is not null)
                    summary = ExtractAndExecuteRecordCommands(summary);

                // Store in database
                var tokenCount = TokenCounter.EstimateMessagesTokenCount(overflowedMessages);
                var summaryId = _database.CreateSummary(summary, segmentStart, segmentEnd - 1, tokenCount, msgThreshold);

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

                // Append to summary chunks and advance index
                _summaryChunks.Add(summary);
                _lastSummarizedIndex = segmentEnd;
                _lastSummarizedThreshold = msgThreshold; // 记录本次总结时的阈值快照
                AccumulatedOverflowMessageCount = 0;
                AccumulatedOverflowTokens = 0;

                Logger.Log($"OverflowManager: Incremental summary completed. SummaryId={summaryId}, lastSummarizedIndex={_lastSummarizedIndex}, total chunks={_summaryChunks.Count}");
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
            => _database.SearchSummaries(keyword, limit);

        /// <summary>
        /// Get segments associated with a summary.
        /// </summary>
        public List<OverflowSegmentRecord> GetSegmentsForSummary(int summaryId)
            => _database.GetSegmentsForSummary(summaryId);

        /// <summary>
        /// Clear all overflow data.
        /// </summary>
        public void ClearAll()
        {
            _lastSummarizedIndex = 0;
            _summaryChunks.Clear();
            AccumulatedOverflowMessageCount = 0;
            AccumulatedOverflowTokens = 0;
            _lastRangeHash = 0;
            _database.ClearAll();
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
