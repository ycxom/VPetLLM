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

        /// <summary>向量检索后端。null 表示未配置，此时 RRF 只融合两条词法路。</summary>
        private readonly Embedding.EmbeddingService? _embeddingService;

        // ---- 排序模型 ----
        // final = α·rrf + β·importance + γ·recency
        //
        // rrf 融合三路对**同一批候选**的排序：
        //   路 1：BM25（IDF 加权，罕见词命中更值钱）
        //   路 2：覆盖率（命中了查询里多少比例的不同词）
        //   路 3：向量余弦（语义相似，可选；后端不可用时自动退出）
        // 三者量纲互不相同（BM25 无上界、覆盖率 [0,1]、余弦 [-1,1]），直接加权
        // 求和没有意义，而 RRF 只看排名，天然免疫量纲问题。三路会有分歧：BM25
        // 偏爱罕见词的单点强命中，覆盖率偏爱广泛命中，向量能召回零词法重叠的
        // 语义近邻，融合后更稳。
        //
        // 三维加权「求和」而非相乘：任一维度低分不应把整体清零。一条很久以前
        // 记下、但和当前问题高度相关的记忆，仍然应该被召回。
        private const double ScoreAlphaRelevance = 0.5;
        private const double ScoreBetaImportance = 0.25;
        private const double ScoreGammaRecency = 0.25;

        /// <summary>每条词法路最多贡献多少个候选进 RRF。</summary>
        private const int LexicalRouteTopK = 50;

        /// <summary>向量路最多贡献多少个候选进 RRF。</summary>
        private const int VectorRouteTopK = 30;

        /// <summary>
        /// 余弦相似度低于此值的文档不进向量路。归一化向量的余弦对任意两段中文文本
        /// 通常都是正数，不设阈值的话全语料都会挤进候选。
        /// </summary>
        private const double MinCosineSimilarity = 0.35;

        /// <summary>
        /// 最终入选的候选数上限（上下文扩展之前）。这同时限制了扩展后的规模，
        /// 以及 SearchWithExpertAsync 喂给专家模型的载荷 —— 后者原本没有任何上限，
        /// 召回率一提高，那次 LLM 调用的输入 token 就跟着失控。
        /// </summary>
        private const int MaxSelectedCandidates = 20;

        /// <summary>每天的新鲜度衰减率，recency = exp(-rate × 天数)。100 天后约衰减到 0.37。</summary>
        private const double RecencyDecayRatePerDay = 0.01;

        // ---- 各来源的固有重要性（0..1）----
        // 记录用自身的 Weight/10；其余用常量。上下文邻居只是为了让专家模型
        // 读懂对话，本身没被命中，重要性最低。
        private const double ImportanceSummary = 0.6;
        private const double ImportanceChat = 0.5;
        private const double ImportanceChatContext = 0.25;

        public MemoryRetrievalService(
            Setting settings,
            ChatCoreBase chatCore,
            HistoryManager historyManager,
            OverflowManager? overflowManager,
            RecordManager? recordManager,
            Embedding.EmbeddingService? embeddingService = null)
        {
            _settings = settings;
            _chatCore = chatCore;
            _historyManager = historyManager;
            _overflowManager = overflowManager;
            _recordManager = recordManager;
            _embeddingService = embeddingService;
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
            var raw = await BuildRawResultsAsync(userQuery, contextWindow: null, tokenBudget: tokenBudget);
            if (string.IsNullOrEmpty(raw))
                return string.Empty;

            if (_settings.EnableExpertMemoryRetrieval)
                return await SummarizeWithExpertAsync(raw);

            return WrapForInjection(raw);
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
            // 无 token 预算：交给专家模型自己压缩。候选数已被 MaxSelectedCandidates 限住，
            // 载荷不会像以前那样随召回率一起失控。
            var raw = await BuildRawResultsAsync(userQuery, contextWindow: contextWindow, tokenBudget: null);
            if (string.IsNullOrEmpty(raw))
                return string.Empty;

            return await SummarizeWithExpertAsync(raw);
        }

        /// <summary>
        /// Performs local search only (no LLM summarization).
        /// Used by the memory retrieval tool handler (AI decides when to call).
        /// </summary>
        public async Task<string> SearchLocalOnlyAsync(string userQuery, int tokenBudget = 500)
        {
            var raw = await BuildRawResultsAsync(userQuery, contextWindow: null, tokenBudget: tokenBudget);
            return string.IsNullOrEmpty(raw) ? string.Empty : WrapForInjection(raw);
        }

        /// <summary>
        /// 检索三路记忆（重要记录 / 滚动总结 / 聊天历史），排序、截断、去重，
        /// 可选扩展对话上下文并裁剪到 token 预算，返回拼接好的纯文本。
        /// </summary>
        /// <param name="contextWindow">非 null 时，把每条入选的历史消息前后各扩展这么多条。</param>
        /// <param name="tokenBudget">非 null 时裁剪到该预算。</param>
        private async Task<string> BuildRawResultsAsync(string userQuery, int? contextWindow, int? tokenBudget)
        {
            if (string.IsNullOrWhiteSpace(userQuery))
                return string.Empty;

            try
            {
                var keywords = ExtractKeywords(userQuery);
                if (keywords.Count == 0)
                    return string.Empty;

                // 候选是**全语料**，不是词法命中的子集。两个理由：
                //   1. BM25 的 IDF 必须在全语料上统计文档频率。只在「已命中」子集上算，
                //      每篇文档都至少含一个查询词，df 被系统性抬高，IDF 被压平。
                //   2. 向量路要召回的正是零词法重叠的文档（"我爱喝拿铁" ↔ "用户偏好咖啡"），
                //      它们本来就进不了词法命中的子集。
                var candidates = CollectCandidates();
                if (candidates.Count == 0)
                    return string.Empty;

                var selected = await RankAndSelectAsync(candidates, keywords, userQuery);
                if (selected.Count == 0)
                    return string.Empty;

                var hits = contextWindow is int window && window > 0
                    ? ExpandWithConversationContext(selected, window)
                    : selected;

                // 去重后按得分降序输出
                var seen = new HashSet<string>();
                var emitted = new List<MemoryHit>();
                var sb = new StringBuilder();
                var usedTokens = 0;

                foreach (var hit in hits.OrderByDescending(h => h.FinalScore))
                {
                    if (!seen.Add(hit.Content.Trim()))
                        continue;

                    if (tokenBudget.HasValue)
                    {
                        var cost = TokenCounter.EstimateTokenCount(hit.Content);
                        if (usedTokens + cost > tokenBudget.Value)
                            continue;   // 跳过这条，后面可能还有装得下的短记录
                        usedTokens += cost;
                    }

                    sb.AppendLine(hit.Content);
                    emitted.Add(hit);
                }

                // 召回强化：只对真正进入结果的记录计数，入选但被预算挤掉的不算
                var recalledIds = emitted.Where(h => h.RecordId.HasValue).Select(h => h.RecordId!.Value).ToList();
                if (recalledIds.Count > 0)
                    _recordManager?.RecordAccess(recalledIds);

                return sb.ToString().Trim();
            }
            catch (Exception ex)
            {
                Logger.Log($"MemoryRetrievalService: Error retrieving memories: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 三路排序 → RRF 融合 → 三维加权 → 取 top-N。
        /// 向量路缺席（未配置 / 后端不可用 / 该文档还没有缓存向量）时自动退出，
        /// 其余两路照常工作。
        /// </summary>
        private async Task<List<MemoryHit>> RankAndSelectAsync(
            List<MemoryHit> candidates, List<string> keywords, string userQuery)
        {
            var docTokens = candidates.Select(c => (IReadOnlyList<string>)Tokenize(c.RawContent)).ToList();

            // 路 1：BM25（IDF 在全语料上统计）
            var bm25Scores = Bm25.Score(docTokens, keywords);
            var bm25Ranking = Enumerable.Range(0, candidates.Count)
                .Where(i => bm25Scores[i] > 0)
                .OrderByDescending(i => bm25Scores[i])
                .Take(LexicalRouteTopK)
                .ToList();

            // 路 2：覆盖率（命中了查询里多少个不同的词）
            for (int i = 0; i < candidates.Count; i++)
                candidates[i].MatchedTerms = CountMatches(candidates[i].RawContent, keywords);

            var coverageRanking = Enumerable.Range(0, candidates.Count)
                .Where(i => candidates[i].MatchedTerms > 0)
                .OrderByDescending(i => candidates[i].MatchedTerms)
                .Take(LexicalRouteTopK)
                .ToList();

            // 路 3：向量（语义）
            var vectorRanking = await BuildVectorRankingAsync(candidates, userQuery);

            var fused = RrfFusion.Fuse(RrfFusion.DefaultK, bm25Ranking, coverageRanking, vectorRanking);
            if (fused.Count == 0)
                return new List<MemoryHit>();

            var maxRrf = fused.Values.Max();
            if (maxRrf <= 0)
                maxRrf = 1.0;

            var now = DateTimeOffset.UtcNow;
            var scored = new List<MemoryHit>(fused.Count);

            foreach (var (index, rrf) in fused)
            {
                var hit = candidates[index];

                var recency = 1.0;
                if (hit.Timestamp is DateTimeOffset ts)
                {
                    var daysOld = Math.Max(0.0, (now - ts).TotalDays);
                    recency = Math.Exp(-RecencyDecayRatePerDay * daysOld);
                }

                hit.FinalScore = ScoreAlphaRelevance * (rrf / maxRrf)
                               + ScoreBetaImportance * Math.Clamp(hit.Importance, 0.0, 1.0)
                               + ScoreGammaRecency * recency;
                scored.Add(hit);
            }

            // 截断：向量路会给几乎所有文档非零余弦，不设上限的话候选会爆。
            // 这同时限制了后面上下文扩展的规模，以及喂给专家模型的载荷。
            return scored.OrderByDescending(h => h.FinalScore).Take(MaxSelectedCandidates).ToList();
        }

        /// <summary>
        /// 向量路排序。返回按余弦相似度降序的候选下标；不可用时返回空列表
        /// （RRF 会忽略空列表，退化为词法两路）。
        ///
        /// 只对**已缓存向量**的文档打分，缺向量的文档本轮不参与，并被排进后台回填队列 ——
        /// 因此冷启动不阻塞、不会为上千条历史一次性打爆 API，常被检索到的文档最先获得向量。
        /// </summary>
        private async Task<List<int>> BuildVectorRankingAsync(List<MemoryHit> candidates, string userQuery)
        {
            var empty = new List<int>();
            if (_embeddingService is null || !_embeddingService.IsAvailable)
                return empty;

            var queryVector = await _embeddingService.EmbedQueryAsync(userQuery);
            if (queryVector is null)
                return empty;

            var texts = candidates.Select(c => c.RawContent).ToList();
            var cached = _embeddingService.GetCachedVectors(texts);

            var scored = new List<(int Index, double Similarity)>();
            var missing = new List<string>();

            for (int i = 0; i < candidates.Count; i++)
            {
                var text = candidates[i].RawContent;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                if (cached.TryGetValue(text, out var vector))
                {
                    var similarity = Embedding.EmbeddingService.CosineSimilarity(queryVector, vector);
                    if (similarity >= MinCosineSimilarity)
                        scored.Add((i, similarity));
                }
                else
                {
                    missing.Add(text);
                }
            }

            // 自暖：本轮缺向量的文档排进后台队列，下次检索它们就能参与向量路
            if (missing.Count > 0)
                _embeddingService.QueueBackfill(missing);

            return scored
                .OrderByDescending(x => x.Similarity)
                .Take(VectorRouteTopK)
                .Select(x => x.Index)
                .ToList();
        }

        /// <summary>
        /// 为入选的历史消息补上前后各 <paramref name="window"/> 条邻居，让专家模型
        /// 读得懂对话。邻居本身没被检索命中，relevance 记 0，靠 importance/recency 垫底。
        /// </summary>
        private List<MemoryHit> ExpandWithConversationContext(List<MemoryHit> selected, int window)
        {
            var fullHistory = _historyManager.GetHistory();
            var selectedIndices = selected
                .Where(h => h.HistoryIndex.HasValue)
                .Select(h => h.HistoryIndex!.Value)
                .ToHashSet();

            if (selectedIndices.Count == 0)
                return selected;

            var result = new List<MemoryHit>(selected);
            var added = new HashSet<int>(selectedIndices);
            var now = DateTimeOffset.UtcNow;

            foreach (var idx in selectedIndices)
            {
                for (int offset = -window; offset <= window; offset++)
                {
                    var target = idx + offset;
                    if (target < 0 || target >= fullHistory.Count || !added.Add(target))
                        continue;

                    var msg = fullHistory[target];
                    if (string.IsNullOrWhiteSpace(msg.Content))
                        continue;

                    var timestamp = ToTimestamp(msg.UnixTime);

                    var recency = 1.0;
                    if (timestamp is DateTimeOffset ts)
                        recency = Math.Exp(-RecencyDecayRatePerDay * Math.Max(0.0, (now - ts).TotalDays));

                    result.Add(new MemoryHit
                    {
                        Content = $"[{msg.Role}]: {msg.Content}",
                        RawContent = msg.Content,
                        Importance = ImportanceChatContext,
                        Timestamp = timestamp,
                        HistoryIndex = target,
                        // relevance = 0：邻居没被任何一路命中
                        FinalScore = ScoreBetaImportance * ImportanceChatContext + ScoreGammaRecency * recency
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// 收集全部候选：重要记录 + 滚动总结 + 全部聊天历史。不做任何过滤 ——
        /// 过滤是排序阶段的事。
        /// </summary>
        private List<MemoryHit> CollectCandidates()
        {
            var candidates = new List<MemoryHit>();

            // 1. 重要记录
            if (_recordManager is not null && _settings.Records?.EnableRecords == true)
            {
                foreach (var record in _recordManager.GetAllRecordsForEditing())
                {
                    if (string.IsNullOrWhiteSpace(record.Content))
                        continue;

                    candidates.Add(new MemoryHit
                    {
                        Content = $"[记忆 #{record.Id}] {record.Content}",
                        RawContent = record.Content,
                        // 记录自身的权重就是它的重要性（0-10 → 0-1）
                        Importance = Math.Clamp(record.Weight / 10.0, 0.0, 1.0),
                        // 被频繁召回的记录在新鲜度上不显得陈旧
                        Timestamp = new DateTimeOffset(DateTime.SpecifyKind(record.ReferenceTime, DateTimeKind.Utc)),
                        RecordId = record.Id
                    });
                }
            }

            // 2. 滚动总结。
            // overflow_summaries 表里每一行都是前一行的超集（替换制滚动总结），
            // 因此只看当前那一份。而且在 overflow 模式下它已经被 ChatCoreBase 作为
            // [Previous Conversation Summary] 注入进每次 prompt 了，再检索一遍纯属重复。
            var summaryAlreadyInPrompt =
                _settings.KeepContext &&
                _settings.OverflowMode == Setting.ContextOverflowMode.Overflow;

            if (!summaryAlreadyInPrompt && _overflowManager?.LatestSummary is string summary && summary.Length > 0)
            {
                candidates.Add(new MemoryHit
                {
                    Content = $"[历史摘要] {summary}",
                    RawContent = summary,
                    Importance = ImportanceSummary,
                    // 滚动总结是替换制，始终反映最新状态，不做时间衰减
                    Timestamp = null
                });
            }

            // 3. 聊天历史
            var fullHistory = _historyManager.GetHistory();
            for (int i = 0; i < fullHistory.Count; i++)
            {
                var msg = fullHistory[i];
                if (string.IsNullOrWhiteSpace(msg.Content))
                    continue;

                candidates.Add(new MemoryHit
                {
                    Content = $"[{msg.Role}]: {msg.Content}",
                    RawContent = msg.Content,
                    Importance = ImportanceChat,
                    Timestamp = ToTimestamp(msg.UnixTime),
                    HistoryIndex = i
                });
            }

            return candidates;
        }

        /// <summary>历史消息的时间戳可能缺失（旧数据），缺失时不参与时间衰减。</summary>
        private static DateTimeOffset? ToTimestamp(long? unixSeconds)
        {
            if (unixSeconds is not long s || s <= 0)
                return null;

            try { return DateTimeOffset.FromUnixTimeSeconds(s); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        /// <summary>
        /// 统计 <paramref name="text"/> 中命中了多少个不同的检索词。
        /// 命中词数越多说明越相关，用来替代原先"命中即固定分、命中一个就 break"的做法。
        /// </summary>
        private static int CountMatches(string? text, List<string> keywords)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            var count = 0;
            foreach (var kw in keywords)
            {
                if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }

        private string WrapForInjection(string body)
        {
            var template = PromptHelper.Get("Memory_Retrieval_Injection", _settings.PromptLanguage);
            if (!template.StartsWith("[Prompt Missing"))
                return template.Replace("{Memories}", body);

            return $"[检索到的记忆]\n{body}\n[/检索到的记忆]";
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
                    return WrapForInjection(summary);
            }
            catch (Exception ex)
            {
                Logger.Log($"MemoryRetrievalService: Expert summarization failed: {ex.Message}");
            }

            return rawMemories;
        }

        /// <summary>
        /// 检索词表。中文按字符 bigram 切分，拉丁文/数字按词切分。
        ///
        /// 原先只按空格和标点切词：中文自然语句里没有空格，整个子句会变成一个
        /// "关键词"，再拿它去做整串 Contains 匹配，实际上永远匹配不上
        /// （"你还记得我喜欢喝什么咖啡吗" → 唯一的词就是它自己）。
        /// bigram 让 "喜欢咖啡" 产出 "喜欢/欢咖/咖啡"，其中跨词边界的碎片
        /// （"欢咖"）几乎不会命中任何文本，因此噪声代价很低。
        /// </summary>
        internal static List<string> ExtractKeywords(string query)
            => Tokenize(query).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxQueryTerms).ToList();

        /// <summary>bigram 比整词多得多，查询词上限相应放宽。</summary>
        private const int MaxQueryTerms = 24;

        /// <summary>
        /// 分词，<b>保留重复</b>。BM25 需要词频，因此不能在这里去重
        /// （<see cref="ExtractKeywords"/> 是它的去重版本，用于查询侧）。
        /// </summary>
        internal static List<string> Tokenize(string query)
        {
            var terms = new List<string>();
            if (string.IsNullOrWhiteSpace(query))
                return terms;

            var cjk = new StringBuilder();
            var latin = new StringBuilder();

            void AddTerm(string t)
            {
                if (t.Length > 0 && !StopWords.Contains(t))
                    terms.Add(t);
            }

            void FlushCjk()
            {
                var s = cjk.ToString();
                cjk.Clear();
                if (s.Length == 0)
                    return;

                if (s.Length == 1)
                {
                    // 孤立单字（被标点隔开）本身就是一个词
                    AddTerm(s);
                    return;
                }

                for (int i = 0; i + 1 < s.Length; i++)
                    AddTerm(s.Substring(i, 2));
            }

            void FlushLatin()
            {
                var s = latin.ToString();
                latin.Clear();
                if (s.Length >= 2)
                    AddTerm(s);
            }

            foreach (var c in query)
            {
                if (IsCjk(c))
                {
                    FlushLatin();
                    cjk.Append(c);
                }
                else if (char.IsLetterOrDigit(c))
                {
                    FlushCjk();
                    latin.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    FlushCjk();
                    FlushLatin();
                }
            }
            FlushCjk();
            FlushLatin();

            return terms;
        }

        private static bool IsCjk(char c) => c >= '一' && c <= '鿿';

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            // 拉丁文虚词
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could",
            "should", "may", "might", "can", "shall", "to", "of", "in", "for",
            "on", "with", "at", "by", "from", "as", "into", "through", "during",
            "before", "after", "above", "below", "between", "and", "but", "or",
            "nor", "not", "so", "yet", "both", "either", "neither", "each", "every",
            "all", "any", "few", "more", "most", "other", "some", "such", "no",
            "only", "own", "same", "than", "too", "very", "just", "about", "now",

            // 中文单字虚词（仅在被标点隔开成孤立单字时才会被切出来）
            "的", "了", "在", "是", "我", "有", "和", "就", "不", "都", "一",
            "个", "上", "也", "很", "到", "说", "要", "去", "你", "会", "着",
            "看", "好", "这", "他", "她", "它", "那", "吗", "吧", "呢", "啊",
            "哦", "嗯", "呀", "哈",

            // 中文二字虚词 —— 正好是 bigram 的粒度，命中率高且无信息量
            "没有", "自己", "们的", "什么", "怎么", "如何", "可以", "这个", "那个",
            "我们", "你们", "他们", "她们", "记得", "还记", "知道", "觉得", "认为",
            "因为", "所以", "但是", "然后", "还有", "或者", "如果", "就是", "这样",
            "那样", "一个", "一下", "一些", "现在", "已经", "曾经", "以及", "而且",
        };

        private sealed class MemoryHit
        {
            /// <summary>带来源前缀的展示文本，最终注入 prompt 的就是它。</summary>
            public string Content { get; set; } = string.Empty;

            /// <summary>
            /// 不含来源前缀的原文，供 BM25 分词使用。前缀（"[记忆 #3] "、"[user]: "）
            /// 在同源文档里普遍存在，混进去只会污染词表。
            /// </summary>
            public string RawContent { get; set; } = string.Empty;

            /// <summary>命中了多少个不同的检索词。</summary>
            public int MatchedTerms { get; set; }

            /// <summary>来源固有重要性，0..1。</summary>
            public double Importance { get; set; }

            /// <summary>用于时间衰减的时间基准，null 表示不衰减。</summary>
            public DateTimeOffset? Timestamp { get; set; }

            /// <summary>来自重要记录时的记录 ID，用于召回强化。</summary>
            public int? RecordId { get; set; }

            /// <summary>来自聊天历史时在历史列表中的下标，用于上下文扩展。</summary>
            public int? HistoryIndex { get; set; }

            public double FinalScore { get; set; }
        }
    }
}
