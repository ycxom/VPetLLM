using VPetLLM.Core.Data.Database;
using VPetLLM.Core.Data.Managers;

namespace VPetLLM.Core.Services
{
    /// <summary>
    /// Expert memory retrieval service.
    /// When the user references something that has been overflowed/summarized out of context,
    /// this service searches keywords across overflow summaries, important records,
    /// and chat history to retrieve relevant context and feed it back to the main LLM.
    /// </summary>
    public class MemoryRetrievalService
    {
        private readonly OverflowManager? _overflowManager;
        private readonly RecordManager? _recordManager;
        private readonly HistoryManager _historyManager;
        private readonly ChatCoreBase _chatCore;
        private readonly Setting _settings;

        public MemoryRetrievalService(
            Setting settings,
            ChatCoreBase chatCore,
            HistoryManager historyManager,
            OverflowManager? overflowManager,
            RecordManager? recordManager)
        {
            _settings = settings;
            _chatCore = chatCore;
            _historyManager = historyManager;
            _overflowManager = overflowManager;
            _recordManager = recordManager;
        }

        /// <summary>
        /// Search for relevant memories based on the user's query.
        /// Searches overflow summaries, important records, and recent history.
        /// </summary>
        /// <param name="userQuery">The user's input query.</param>
        /// <param name="tokenBudget">Maximum tokens to return.</param>
        /// <returns>A formatted string of retrieved memories, or empty if none found.</returns>
        public async Task<string> RetrieveRelevantMemoriesAsync(string userQuery, int tokenBudget = 500)
        {
            if (string.IsNullOrWhiteSpace(userQuery))
                return string.Empty;

            try
            {
                // Extract search keywords from the user query
                var keywords = ExtractKeywords(userQuery);
                if (keywords.Count == 0)
                    return string.Empty;

                var allResults = new List<MemoryHit>();

                // 1. Search important records
                if (_recordManager is not null && _settings.Records?.EnableRecords == true)
                {
                    var records = _recordManager.GetAllRecordsForEditing();
                    foreach (var record in records)
                    {
                        foreach (var kw in keywords)
                        {
                            if (record.Content.Contains(kw, StringComparison.OrdinalIgnoreCase))
                            {
                                allResults.Add(new MemoryHit
                                {
                                    Source = "Record",
                                    Content = $"[记忆 #{record.Id}] {record.Content}",
                                    Relevance = 8,
                                    Keyword = kw
                                });
                                break;
                            }
                        }
                    }
                }

                // 2. Search overflow summaries
                if (_overflowManager is not null)
                {
                    foreach (var kw in keywords)
                    {
                        var summaries = _overflowManager.SearchSummaries(kw, limit: 5);
                        foreach (var summary in summaries)
                        {
                            allResults.Add(new MemoryHit
                            {
                                Source = "OverflowSummary",
                                Content = $"[历史摘要 #{summary.Id}] {summary.SummaryText}",
                                Relevance = 6,
                                Keyword = kw,
                                SummaryId = summary.Id
                            });
                        }
                    }
                }

                // 3. Search recent chat history (messages still in memory)
                var fullHistory = _historyManager.GetHistory();
                foreach (var msg in fullHistory)
                {
                    foreach (var kw in keywords)
                    {
                        if (!string.IsNullOrEmpty(msg.Content) &&
                            msg.Content.Contains(kw, StringComparison.OrdinalIgnoreCase))
                        {
                            allResults.Add(new MemoryHit
                            {
                                Source = "RecentChat",
                                Content = $"[{msg.Role}]: {msg.Content}",
                                Relevance = 5,
                                Keyword = kw
                            });
                            break;
                        }
                    }
                }

                if (allResults.Count == 0)
                    return string.Empty;

                // Deduplicate by content
                var seenContent = new HashSet<string>();
                var uniqueResults = new List<MemoryHit>();
                foreach (var hit in allResults.OrderByDescending(h => h.Relevance))
                {
                    var normalizedContent = hit.Content.Trim();
                    if (seenContent.Add(normalizedContent))
                    {
                        uniqueResults.Add(hit);
                    }
                }

                // Trim to token budget
                var sb = new StringBuilder();
                var currentTokens = 0;
                foreach (var hit in uniqueResults)
                {
                    var hitTokens = TokenCounter.EstimateTokenCount(hit.Content);
                    if (currentTokens + hitTokens > tokenBudget)
                        break;

                    sb.AppendLine(hit.Content);
                    currentTokens += hitTokens;
                }

                var rawResults = sb.ToString().Trim();
                if (string.IsNullOrEmpty(rawResults))
                    return string.Empty;

                // If expert memory retrieval is enabled, summarize the retrieved memories
                if (_settings.EnableExpertMemoryRetrieval)
                {
                    return await SummarizeWithExpertAsync(rawResults);
                }

                // Otherwise, return raw results with a prefix
                var injectionTemplate = PromptHelper.Get("Memory_Retrieval_Injection", _settings.PromptLanguage);
                if (!injectionTemplate.StartsWith("[Prompt Missing"))
                {
                    return injectionTemplate.Replace("{Memories}", rawResults);
                }

                return $"[Retrieved Memories]\n{rawResults}\n[/Retrieved Memories]";
            }
            catch (Exception ex)
            {
                Logger.Log($"MemoryRetrievalService: Error retrieving memories: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Use the expert model to summarize and condense retrieved memories.
        /// </summary>
        private async Task<string> SummarizeWithExpertAsync(string rawMemories)
        {
            try
            {
                var systemPrompt = PromptHelper.Get("Memory_Retrieval_Prefix", _settings.PromptLanguage);
                if (systemPrompt.StartsWith("[Prompt Missing"))
                {
                    systemPrompt = "You are a memory assistant. Summarize the following retrieved memories into a concise, useful context for an ongoing conversation. Only include information relevant to the current discussion. Be brief.";
                }

                // Use the main ChatCore's Summarize method (uses "Compression" purpose for node selection)
                var summary = await _chatCore.Summarize(systemPrompt, rawMemories);

                if (!string.IsNullOrWhiteSpace(summary))
                {
                    var injectionTemplate = PromptHelper.Get("Memory_Retrieval_Injection", _settings.PromptLanguage);
                    if (!injectionTemplate.StartsWith("[Prompt Missing"))
                    {
                        return injectionTemplate.Replace("{Memories}", summary);
                    }
                    return $"[Retrieved Memories]\n{summary}\n[/Retrieved Memories]";
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"MemoryRetrievalService: Expert summarization failed: {ex.Message}");
            }

            return rawMemories;
        }

        /// <summary>
        /// Search with keyword, expand each hit with surrounding context messages,
        /// then call expert model (LLM) to summarize. 
        /// Used by the AI-triggered memory retrieval handler.
        /// </summary>
        /// <param name="userQuery">The AI's search query (keywords).</param>
        /// <param name="contextWindow">Number of messages before/after each hit to include for context.</param>
        /// <returns>Expert-summarized memory context, or empty if nothing found.</returns>
        public async Task<string> SearchWithExpertAsync(string userQuery, int contextWindow = 5)
        {
            if (string.IsNullOrWhiteSpace(userQuery))
                return string.Empty;

            try
            {
                var keywords = ExtractKeywords(userQuery);
                if (keywords.Count == 0)
                    return string.Empty;

                var allResults = new List<MemoryHit>();
                var fullHistory = _historyManager.GetHistory();
                var matchedIndices = new HashSet<int>();

                // 1. Search important records
                if (_recordManager is not null && _settings.Records?.EnableRecords == true)
                {
                    var records = _recordManager.GetAllRecordsForEditing();
                    foreach (var record in records)
                    {
                        foreach (var kw in keywords)
                        {
                            if (record.Content.Contains(kw, StringComparison.OrdinalIgnoreCase))
                            {
                                allResults.Add(new MemoryHit
                                {
                                    Source = "Record",
                                    Content = $"[记忆 #{record.Id}] {record.Content}",
                                    Relevance = 8,
                                    Keyword = kw
                                });
                                break;
                            }
                        }
                    }
                }

                // 2. Search overflow summaries
                if (_overflowManager is not null)
                {
                    foreach (var kw in keywords)
                    {
                        var summaries = _overflowManager.SearchSummaries(kw, limit: 5);
                        foreach (var sum in summaries)
                        {
                            allResults.Add(new MemoryHit
                            {
                                Source = "OverflowSummary",
                                Content = $"[历史摘要 #{sum.Id}] {sum.SummaryText}",
                                Relevance = 6,
                                Keyword = kw,
                                SummaryId = sum.Id
                            });
                        }
                    }
                }

                // 3. Search recent chat history with surrounding context
                for (int i = 0; i < fullHistory.Count; i++)
                {
                    var msg = fullHistory[i];
                    foreach (var kw in keywords)
                    {
                        if (!string.IsNullOrEmpty(msg.Content) &&
                            msg.Content.Contains(kw, StringComparison.OrdinalIgnoreCase))
                        {
                            // Found a match — record its index for context expansion
                            matchedIndices.Add(i);
                            break;
                        }
                    }
                }

                // Expand matched indices with ±contextWindow surrounding messages
                if (matchedIndices.Count > 0)
                {
                    var expandedIndices = new HashSet<int>();
                    foreach (var idx in matchedIndices)
                    {
                        for (int offset = -contextWindow; offset <= contextWindow; offset++)
                        {
                            var targetIdx = idx + offset;
                            if (targetIdx >= 0 && targetIdx < fullHistory.Count)
                                expandedIndices.Add(targetIdx);
                        }
                    }

                    foreach (var idx in expandedIndices.OrderBy(i => i))
                    {
                        var msg = fullHistory[idx];
                        var relevance = matchedIndices.Contains(idx) ? 5 : 3;
                        allResults.Add(new MemoryHit
                        {
                            Source = "RecentChat",
                            Content = $"[{msg.Role}]: {msg.Content}",
                            Relevance = relevance,
                            Keyword = matchedIndices.Contains(idx) ? keywords.First() : ""
                        });
                    }
                }

                if (allResults.Count == 0)
                    return string.Empty;

                // Deduplicate by content
                var seenContent = new HashSet<string>();
                var uniqueResults = new List<MemoryHit>();
                foreach (var hit in allResults.OrderByDescending(h => h.Relevance))
                {
                    if (seenContent.Add(hit.Content.Trim()))
                    {
                        uniqueResults.Add(hit);
                    }
                }

                // Build raw text for expert summarization (no token budget — let expert handle)
                var sb = new StringBuilder();
                foreach (var hit in uniqueResults)
                {
                    sb.AppendLine(hit.Content);
                }

                var rawResults = sb.ToString().Trim();
                if (string.IsNullOrEmpty(rawResults))
                    return string.Empty;

                // Call expert model to summarize
                var summary = await SummarizeWithExpertAsync(rawResults);
                return summary;
            }
            catch (Exception ex)
            {
                Logger.Log($"MemoryRetrievalService: SearchWithExpert error: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Performs local search only (no LLM summarization).
        /// Extracts keywords, searches records/overflow summaries/history,
        /// deduplicates, trims to token budget, and returns formatted results.
        /// Used by the memory retrieval tool handler (AI decides when to call).
        /// </summary>
        public async Task<string> SearchLocalOnlyAsync(string userQuery, int tokenBudget = 500)
        {
            if (string.IsNullOrWhiteSpace(userQuery))
                return string.Empty;

            try
            {
                var keywords = ExtractKeywords(userQuery);
                if (keywords.Count == 0)
                    return string.Empty;

                var allResults = new List<MemoryHit>();

                // 1. Search important records
                if (_recordManager is not null && _settings.Records?.EnableRecords == true)
                {
                    var records = _recordManager.GetAllRecordsForEditing();
                    foreach (var record in records)
                    {
                        foreach (var kw in keywords)
                        {
                            if (record.Content.Contains(kw, StringComparison.OrdinalIgnoreCase))
                            {
                                allResults.Add(new MemoryHit
                                {
                                    Source = "Record",
                                    Content = $"[记忆 #{record.Id}] {record.Content}",
                                    Relevance = 8,
                                    Keyword = kw
                                });
                                break;
                            }
                        }
                    }
                }

                // 2. Search overflow summaries
                if (_overflowManager is not null)
                {
                    foreach (var kw in keywords)
                    {
                        var summaries = _overflowManager.SearchSummaries(kw, limit: 5);
                        foreach (var summary in summaries)
                        {
                            allResults.Add(new MemoryHit
                            {
                                Source = "OverflowSummary",
                                Content = $"[历史摘要 #{summary.Id}] {summary.SummaryText}",
                                Relevance = 6,
                                Keyword = kw,
                                SummaryId = summary.Id
                            });
                        }
                    }
                }

                // 3. Search recent chat history
                var fullHistory = _historyManager.GetHistory();
                foreach (var msg in fullHistory)
                {
                    foreach (var kw in keywords)
                    {
                        if (!string.IsNullOrEmpty(msg.Content) &&
                            msg.Content.Contains(kw, StringComparison.OrdinalIgnoreCase))
                        {
                            allResults.Add(new MemoryHit
                            {
                                Source = "RecentChat",
                                Content = $"[{msg.Role}]: {msg.Content}",
                                Relevance = 5,
                                Keyword = kw
                            });
                            break;
                        }
                    }
                }

                if (allResults.Count == 0)
                    return string.Empty;

                // Deduplicate by content
                var seenContent = new HashSet<string>();
                var uniqueResults = new List<MemoryHit>();
                foreach (var hit in allResults.OrderByDescending(h => h.Relevance))
                {
                    if (seenContent.Add(hit.Content.Trim()))
                    {
                        uniqueResults.Add(hit);
                    }
                }

                // Trim to token budget
                var sb = new StringBuilder();
                var currentTokens = 0;
                foreach (var hit in uniqueResults)
                {
                    var hitTokens = TokenCounter.EstimateTokenCount(hit.Content);
                    if (currentTokens + hitTokens > tokenBudget)
                        break;

                    sb.AppendLine(hit.Content);
                    currentTokens += hitTokens;
                }

                var rawResults = sb.ToString().Trim();
                if (string.IsNullOrEmpty(rawResults))
                    return string.Empty;

                // Wrap with injection template (no LLM summarization)
                var injectionTemplate = PromptHelper.Get("Memory_Retrieval_Injection", _settings.PromptLanguage);
                if (!injectionTemplate.StartsWith("[Prompt Missing"))
                {
                    return injectionTemplate.Replace("{Memories}", rawResults);
                }

                return $"[检索到的记忆]\n{rawResults}\n[/检索到的记忆]";
            }
            catch (Exception ex)
            {
                Logger.Log($"MemoryRetrievalService: Local search error: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Extract search keywords from a user query.
        /// Splits on common delimiters, removes stop words, and returns distinct meaningful terms.
        /// </summary>
        private List<string> ExtractKeywords(string query)
        {
            var keywords = new List<string>();

            // Split on common delimiters
            var words = query.Split(new[] { ' ', ',', '.', '!', '?', '，', '。', '！', '？', '、', '\n', '\r', '\t', '：', ':', '；', ';' },
                StringSplitOptions.RemoveEmptyEntries);

            // Filter stop words and short terms
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
                "have", "has", "had", "do", "does", "did", "will", "would", "could",
                "should", "may", "might", "can", "shall", "to", "of", "in", "for",
                "on", "with", "at", "by", "from", "as", "into", "through", "during",
                "before", "after", "above", "below", "between", "and", "but", "or",
                "nor", "not", "so", "yet", "both", "either", "neither", "each", "every",
                "all", "any", "few", "more", "most", "other", "some", "such", "no",
                "only", "own", "same", "than", "too", "very", "just", "about", "now",
                "的", "了", "在", "是", "我", "有", "和", "就", "不", "人", "都", "一",
                "个", "上", "也", "很", "到", "说", "要", "去", "你", "会", "着", "没有",
                "看", "好", "自己", "这", "他", "她", "它", "们", "那", "什么", "怎么",
                "如何", "哪", "吗", "吧", "呢", "啊", "哦", "嗯", "呀", "哈"
            };

            foreach (var word in words)
            {
                var trimmed = word.Trim();
                if (trimmed.Length >= 2 && !stopWords.Contains(trimmed))
                {
                    keywords.Add(trimmed);
                }
            }

            // Deduplicate
            return keywords.Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        }

        private class MemoryHit
        {
            public string Source { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
            public int Relevance { get; set; }
            public string Keyword { get; set; } = string.Empty;
            public int? SummaryId { get; set; }
        }
    }
}
