using VPetLLM.Core.Data.Database;

namespace VPetLLM.Core.Data.Managers
{
    /// <summary>
    /// Manages the overflow-based context handling.
    /// When messages are evicted from the prompt window (overflow mode),
    /// this manager tracks them and triggers summaries when thresholds are met.
    /// Summaries are stored both as overflow_summaries (for expert retrieval)
    /// and as important_records (via RecordManager) for regular context injection.
    /// </summary>
    public class OverflowManager
    {
        private readonly OverflowDatabase _database;
        private readonly RecordManager? _recordManager;
        private readonly Setting _settings;
        private readonly string _providerName;
        private readonly ChatCoreBase _chatCore;

        /// <summary>
        /// Accumulated token count of overflowed messages since last summary trigger.
        /// </summary>
        private int _accumulatedOverflowTokens;

        /// <summary>
        /// Accumulated message count of overflowed messages since last summary trigger.
        /// </summary>
        private int _accumulatedOverflowMessageCount;

        /// <summary>
        /// Public accessor for UI — current accumulated overflow tokens waiting for summary.
        /// </summary>
        public int AccumulatedOverflowTokens => _accumulatedOverflowTokens;

        /// <summary>
        /// Public accessor for UI — current accumulated overflow message count waiting for summary.
        /// </summary>
        public int AccumulatedOverflowMessageCount => _accumulatedOverflowMessageCount;

        /// <summary>
        /// The global message index at which the next overflow segment starts.
        /// </summary>
        private int _nextMessageIndex;

        public OverflowManager(Setting settings, string providerName, ChatCoreBase chatCore, RecordManager? recordManager)
        {
            _settings = settings;
            _providerName = providerName;
            _chatCore = chatCore;
            _recordManager = recordManager;
            _database = new OverflowDatabase(GetDatabasePath());
            _accumulatedOverflowTokens = _database.GetTotalOverflowedTokens();
            _nextMessageIndex = 0;
        }

        /// <summary>
        /// Tracks recently processed overflow ranges to prevent double-counting if
        /// the same messages are evicted again (e.g., on chat retry before success).
        /// </summary>
        private readonly HashSet<int> _processedSegmentRanges = new HashSet<int>();
        private int _lastRangeHash;

        /// <summary>
        /// Called when messages are evicted from the prompt window (overflowed).
        /// Tracks the evicted messages and triggers a summary when accumulated tokens
        /// exceed the threshold. Idempotent: duplicate calls for the same message range are ignored.
        /// </summary>
        /// <param name="overflowedMessages">The messages that were evicted from the prompt.</param>
        /// <param name="overflowedTokenCount">Estimated token count of the evicted messages.</param>
        public async Task OnMessagesOverflowedAsync(List<Message> overflowedMessages, int overflowedTokenCount)
        {
            if (overflowedMessages is null || overflowedMessages.Count == 0)
                return;

            // Compute a range hash to detect duplicate notifications
            var rangeHash = ComputeRangeHash(overflowedMessages);
            if (_lastRangeHash == rangeHash && _accumulatedOverflowTokens > 0)
            {
                // Same range already processed; skip double-counting
                Logger.Log($"OverflowManager: Skipping duplicate overflow notification (hash={rangeHash:x8})");
                return;
            }
            _lastRangeHash = rangeHash;

            _accumulatedOverflowTokens += overflowedTokenCount;
            _accumulatedOverflowMessageCount += overflowedMessages.Count;
            var segmentStartIndex = _nextMessageIndex;
            _nextMessageIndex += overflowedMessages.Count;

            Logger.Log($"OverflowManager: {overflowedMessages.Count} messages overflowed ({overflowedTokenCount} tokens). Accumulated: {_accumulatedOverflowTokens}/{_settings.OverflowSummaryTriggerTokens}");

            if (ShouldTriggerSummary())
            {
                await TriggerSummaryAsync(overflowedMessages, segmentStartIndex, overflowedTokenCount);
            }
        }

        /// <summary>
        /// Checks whether accumulated overflow tokens exceed the trigger threshold.
        /// When OverflowThresholdSyncGlobal is true, uses global HistoryCompressionThreshold
        /// and HistoryCompressionTokenThreshold. Otherwise uses OverflowSummaryTriggerTokens.
        /// </summary>
        public bool ShouldTriggerSummary()
        {
            if (_settings.OverflowThresholdSyncGlobal)
            {
                // Sync with global thresholds — use CompressionMode to decide trigger method
                switch (_settings.CompressionMode)
                {
                    case Setting.CompressionTriggerMode.MessageCount:
                        return _accumulatedOverflowMessageCount >= _settings.HistoryCompressionThreshold;

                    case Setting.CompressionTriggerMode.TokenCount:
                        return _accumulatedOverflowTokens >= _settings.HistoryCompressionTokenThreshold;

                    case Setting.CompressionTriggerMode.Both:
                    default:
                        return _accumulatedOverflowMessageCount >= _settings.HistoryCompressionThreshold
                            || _accumulatedOverflowTokens >= _settings.HistoryCompressionTokenThreshold;
                }
            }
            else
            {
                // Independent threshold
                return _accumulatedOverflowTokens >= _settings.OverflowSummaryTriggerTokens
                       && _settings.OverflowSummaryTriggerTokens > 0;
            }
        }

        /// <summary>
        /// Triggers an overflow summary: calls the LLM to summarize the overflowed content,
        /// stores the summary, creates memory records, and resets the accumulator.
        /// </summary>
        public async Task TriggerSummaryAsync(List<Message> overflowedMessages, int segmentStartIndex, int tokenCount)
        {
            try
            {
                Logger.Log($"OverflowManager: Triggering overflow summary for {overflowedMessages.Count} messages ({tokenCount} tokens)");

                // Build history text for summarization
                var historyText = string.Join("\n", overflowedMessages
                    .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                    .Select(m => $"[{m.Role}]: {m.Content}"));

                if (string.IsNullOrWhiteSpace(historyText))
                {
                    Logger.Log("OverflowManager: No content to summarize, skipping");
                    _accumulatedOverflowTokens = 0;
                _accumulatedOverflowMessageCount = 0;
                    _accumulatedOverflowMessageCount = 0;
                    return;
                }

                // Get the overflow summary prompt
                var systemPrompt = PromptHelper.Get("Overflow_Summary_Prefix", _settings.PromptLanguage);
                if (systemPrompt.StartsWith("[Prompt Missing"))
                {
                    // Fall back to the existing compression summary prefix
                    systemPrompt = PromptHelper.Get("Context_Summary_Prefix", _settings.PromptLanguage);
                }

                // Add record hint if enabled
                if (_settings.EnableCompressionRecords && _recordManager is not null)
                {
                    systemPrompt += "\n" + PromptHelper.Get("Context_Summary_RecordHint", _settings.PromptLanguage);
                }

                // Call the LLM to summarize.
                // Uses the existing Summarize method which selects nodes via "Compression" purpose,
                // allowing CompressionOnly channels to be used for overflow summaries.
                var summary = await _chatCore.Summarize(systemPrompt, historyText);

                if (string.IsNullOrWhiteSpace(summary))
                {
                    Logger.Log("OverflowManager: Summary returned empty, resetting accumulator");
                    _accumulatedOverflowTokens = 0;
                _accumulatedOverflowMessageCount = 0;
                    _accumulatedOverflowMessageCount = 0;
                    return;
                }

                // Extract and execute record commands from the summary
                if (_settings.EnableCompressionRecords && _recordManager is not null)
                {
                    summary = ExtractAndExecuteRecordCommands(summary);
                }

                // Store the summary in the overflow database
                var summaryId = _database.CreateSummary(summary, segmentStartIndex,
                    segmentStartIndex + overflowedMessages.Count - 1, tokenCount);

                // Store segment metadata for keyword-based retrieval
                var segments = overflowedMessages.Select((m, i) => new OverflowSegmentData
                {
                    ContentHash = ComputeSimpleHash(m.Content ?? ""),
                    MessageIndex = segmentStartIndex + i,
                    Role = m.Role,
                    ContentPreview = Truncate(m.Content, 200),
                    TokenCount = TokenCounter.EstimateTokenCount(m.Content ?? "")
                }).ToList();

                if (summaryId > 0)
                {
                    _database.AddSegments(summaryId, segments);
                }

                // Reset accumulator after successful summary
                _accumulatedOverflowTokens = 0;
                _accumulatedOverflowMessageCount = 0;

                Logger.Log($"OverflowManager: Overflow summary completed. SummaryId={summaryId}, Reset accumulator.");
            }
            catch (Exception ex)
            {
                Logger.Log($"OverflowManager: Failed to trigger overflow summary: {ex.Message}");
                // Don't reset accumulator on failure — try again next time
            }
        }

        /// <summary>
        /// Search overflow summaries by keyword. Used by the expert memory retrieval system.
        /// </summary>
        public List<OverflowSummaryRecord> SearchSummaries(string keyword, int limit = 10)
        {
            return _database.SearchSummaries(keyword, limit);
        }

        /// <summary>
        /// Get segments associated with a summary.
        /// </summary>
        public List<OverflowSegmentRecord> GetSegmentsForSummary(int summaryId)
        {
            return _database.GetSegmentsForSummary(summaryId);
        }

        /// <summary>
        /// Get all overflow summaries.
        /// </summary>
        public List<OverflowSummaryRecord> GetAllSummaries()
        {
            return _database.SearchSummaries("", 100);
        }

        /// <summary>
        /// Clear all overflow data (called when context is cleared).
        /// </summary>
        public void ClearAll()
        {
            _accumulatedOverflowTokens = 0;
            _accumulatedOverflowMessageCount = 0;
            _nextMessageIndex = 0;
            _lastRangeHash = 0;
            _database.ClearAll();
        }

        /// <summary>
        /// Extract and execute record commands from summary text.
        /// Mirrors the logic in HistoryManager.ExtractAndExecuteRecordCommands.
        /// </summary>
        private string ExtractAndExecuteRecordCommands(string summary)
        {
            try
            {
                var recordRegex = new System.Text.RegularExpressions.Regex(
                    @"<\|\s*record\s*_begin\s*\|>(.*?)<\|\s*record\s*_end\s*\|>",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                var matches = recordRegex.Matches(summary);
                if (matches.Count == 0)
                    return summary;

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

                        if (_recordManager is not null)
                        {
                            var recordId = _recordManager.CreateRecord(content, weight);
                            Logger.Log($"Overflow memory extracted: Created record #{recordId} - '{content}' (weight: {weight})");
                        }
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
            // Simple fast hash for content fingerprinting
            unchecked
            {
                int hash = 17;
                foreach (char c in input)
                    hash = hash * 31 + c;
                return hash.ToString("x8");
            }
        }

        /// <summary>
        /// Compute a hash representing a range of overflowed messages for deduplication.
        /// Uses the first and last message content hashes + count.
        /// </summary>
        private static int ComputeRangeHash(List<Message> messages)
        {
            if (messages.Count == 0) return 0;
            unchecked
            {
                int hash = messages.Count;
                var first = messages[0].Content ?? "";
                var last = messages[messages.Count - 1].Content ?? "";
                foreach (char c in first) hash = hash * 31 + c;
                foreach (char c in last) hash = hash * 31 + c;
                return hash;
            }
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
            if (!Directory.Exists(dataPath))
            {
                Directory.CreateDirectory(dataPath);
            }
            return Path.Combine(dataPath, "chat_history.db");
        }
    }
}
