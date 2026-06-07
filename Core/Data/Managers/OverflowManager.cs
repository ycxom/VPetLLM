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

        private int _lastRangeHash;

        public OverflowManager(Setting settings, string providerName, ChatCoreBase chatCore, RecordManager? recordManager)
        {
            _settings = settings;
            _providerName = providerName;
            _chatCore = chatCore;
            _recordManager = recordManager;
            _database = new OverflowDatabase(GetDatabasePath());
        }

        /// <summary>
        /// Check if any overflow has occurred and trigger summaries as needed.
        /// Called after every user message is added to history.
        /// </summary>
        /// <param name="fullHistory">The complete message history.</param>
        public async Task CheckAndTriggerAsync(List<Message> fullHistory)
        {
            if (fullHistory is null || fullHistory.Count == 0)
                return;

            var msgThreshold = _settings.HistoryCompressionThreshold;
            var tokenThreshold = _settings.HistoryCompressionTokenThreshold;
            if (msgThreshold <= 0) msgThreshold = 20;

            // Get the overflowed portion: messages from _lastSummarizedIndex to (count - threshold)
            var keepCount = Math.Min(msgThreshold, fullHistory.Count);
            var overflowStart = _lastSummarizedIndex;
            var overflowEnd = fullHistory.Count - keepCount;

            if (overflowEnd <= overflowStart)
            {
                // No new messages to summarize
                AccumulatedOverflowMessageCount = Math.Max(0, fullHistory.Count - keepCount - _lastSummarizedIndex);
                AccumulatedOverflowTokens = TokenCounter.EstimateMessagesTokenCount(
                    fullHistory.Skip(_lastSummarizedIndex).Take(Math.Max(0, fullHistory.Count - keepCount - _lastSummarizedIndex)));
                return;
            }

            // Overflow exists — compute overflow range
            var overflowed = fullHistory.Skip(overflowStart).Take(overflowEnd - overflowStart).ToList();
            var ovTokens = TokenCounter.EstimateMessagesTokenCount(overflowed);

            AccumulatedOverflowMessageCount = overflowed.Count;
            AccumulatedOverflowTokens = ovTokens;

            Logger.Log($"OverflowManager: overflowStart={overflowStart} overflowEnd={overflowEnd} totalHistory={fullHistory.Count} keep={keepCount} overflowed={overflowed.Count} tokens={ovTokens}");

            // Trigger if count or token threshold met
            var shouldTrigger = false;
            switch (_settings.CompressionMode)
            {
                case Setting.CompressionTriggerMode.MessageCount:
                    shouldTrigger = overflowed.Count >= msgThreshold;
                    break;
                case Setting.CompressionTriggerMode.TokenCount:
                    shouldTrigger = ovTokens >= tokenThreshold;
                    break;
                case Setting.CompressionTriggerMode.Both:
                default:
                    shouldTrigger = overflowed.Count >= msgThreshold || ovTokens >= tokenThreshold;
                    break;
            }

            if (shouldTrigger && overflowed.Count > 0)
            {
                await TriggerSummaryAsync(overflowed, overflowStart, overflowEnd);
            }
        }

        /// <summary>
        /// Triggers an incremental overflow summary.
        /// </summary>
        private async Task TriggerSummaryAsync(List<Message> overflowedMessages, int segmentStart, int segmentEnd)
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
                var summaryId = _database.CreateSummary(summary, segmentStart, segmentEnd - 1, tokenCount);

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
