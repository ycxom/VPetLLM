namespace VPetLLM.Core.Services
{
    /// <summary>
    /// 全语料逐篇计算用的并行循环。语料小时串行，线程调度的开销比省下的多；
    /// 并行时最多占一半核心，把算力留给宠物动画所在的 UI 线程。
    /// </summary>
    internal static class CorpusParallel
    {
        private const int MinParallelDocs = 1024;

        public static readonly ParallelOptions Options = new()
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8)
        };

        public static void For(int count, Action<int> body)
        {
            if (count < MinParallelDocs)
            {
                for (int i = 0; i < count; i++)
                    body(i);
                return;
            }

            Parallel.For(0, count, Options, body);
        }
    }

    /// <summary>接收分词结果。token 只在回调期间有效，要留下来得自己拷贝。</summary>
    internal interface ITokenSink
    {
        void Accept(ReadOnlySpan<char> token);
    }

    /// <summary>
    /// 记忆检索的分词规则：中文按字符 bigram，拉丁文/数字按词，<b>保留重复</b>
    /// （BM25 需要词频）。每次检索都要过一遍全语料，所以逐词回调，不给每个词分配字符串。
    /// </summary>
    internal static class MemoryTokenizer
    {
        private const int StackTokenLength = 128;

        private enum Run { None, Cjk, Latin }

        public static List<string> Tokenize(string? text)
        {
            var sink = new ListSink(new List<string>());
            Scan(text, ref sink);
            return sink.Terms;
        }

        public static void Scan<TSink>(ReadOnlySpan<char> text, ref TSink sink) where TSink : struct, ITokenSink
        {
            var run = Run.None;
            var start = 0;

            for (int i = 0; i < text.Length; i++)
            {
                var c = text[i];
                var current = IsCjk(c) ? Run.Cjk : char.IsLetterOrDigit(c) ? Run.Latin : Run.None;
                if (current == run)
                    continue;

                Flush(text[start..i], run, ref sink);
                run = current;
                start = i;
            }

            Flush(text[start..], run, ref sink);
        }

        private static void Flush<TSink>(ReadOnlySpan<char> run, Run kind, ref TSink sink) where TSink : struct, ITokenSink
        {
            if (kind == Run.Cjk)
            {
                // 孤立单字（被标点隔开）本身就是一个词
                if (run.Length == 1)
                {
                    Emit(run, ref sink);
                    return;
                }

                for (int i = 0; i + 1 < run.Length; i++)
                    Emit(run.Slice(i, 2), ref sink);
            }
            else if (kind == Run.Latin && run.Length >= 2)
            {
                Span<char> lower = run.Length <= StackTokenLength ? stackalloc char[StackTokenLength] : new char[run.Length];
                lower = lower[..run.Length];
                for (int i = 0; i < run.Length; i++)
                    lower[i] = char.ToLowerInvariant(run[i]);

                Emit(lower, ref sink);
            }
        }

        private static void Emit<TSink>(ReadOnlySpan<char> token, ref TSink sink) where TSink : struct, ITokenSink
        {
            if (!StopWordLookup.Contains(token))
                sink.Accept(token);
        }

        private static bool IsCjk(char c) => c >= '一' && c <= '鿿';

        private readonly struct ListSink(List<string> terms) : ITokenSink
        {
            public List<string> Terms => terms;

            public void Accept(ReadOnlySpan<char> token) => terms.Add(token.ToString());
        }

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

        private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> StopWordLookup =
            StopWords.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>
    /// BM25 稀疏检索打分。
    ///
    /// 相对「命中了几个词」的计数，BM25 多出三件事：
    ///   1. IDF —— 罕见词权重高。"咖啡" 只出现在一条记忆里，"喜欢" 到处都是，
    ///      前者的命中远比后者有信息量。这是排序变准的主因。
    ///   2. TF 饱和 —— 同一个词出现 10 次不等于比出现 1 次相关 10 倍。
    ///   3. 长度归一化 —— 长消息不因为词多就天然占优。
    ///
    /// 在内存中直接算：VPetLLM 的历史本来就整份驻留在 HistoryManager 里，
    /// 建一份磁盘倒排索引只会引入同步和回填的复杂度，换不来任何 I/O 收益。
    /// 也不留内存索引：语料随时被编辑，而现场扫描万条消息只要几毫秒。
    /// </summary>
    internal static class Bm25
    {
        private const double K1 = 1.2;   // TF 饱和速度
        private const double B = 0.75;   // 长度归一化强度

        /// <summary>
        /// 为每篇文档打分。返回数组与 <paramref name="docs"/> 一一对应，
        /// 未命中任何查询词的文档得分为 0。
        /// </summary>
        /// <param name="docs">每篇文档的原文。</param>
        /// <param name="queryTerms">去重后的查询词。</param>
        public static double[] Score(IReadOnlyList<string> docs, IReadOnlyList<string> queryTerms)
        {
            var n = docs.Count;
            var scores = new double[n];
            if (n == 0 || queryTerms.Count == 0)
                return scores;

            var slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var slotOfTerm = new int[queryTerms.Count];
            for (int q = 0; q < queryTerms.Count; q++)
            {
                if (!slots.TryGetValue(queryTerms[q], out var slot))
                    slots[queryTerms[q]] = slot = slots.Count;
                slotOfTerm[q] = slot;
            }

            // 只记查询词的词频（文档数 × 查询词数的扁平表），其余的词只计入文档长度
            var slotCount = slots.Count;
            var lookup = slots.GetAlternateLookup<ReadOnlySpan<char>>();
            var termFreqs = new int[n * slotCount];
            var lengths = new int[n];

            CorpusParallel.For(n, i =>
            {
                var sink = new TermCounter(lookup, termFreqs, i * slotCount);
                MemoryTokenizer.Scan(docs[i], ref sink);
                lengths[i] = sink.Length;
            });

            double totalLength = 0;
            foreach (var length in lengths)
                totalLength += length;

            var avgLength = totalLength / n;
            if (avgLength <= 0)
                return scores;

            // 按查询词原顺序累加：浮点加法不满足交换律，换顺序分数就会差在末位
            foreach (var slot in slotOfTerm)
            {
                // 文档频率：包含该词的文档数
                var df = 0;
                for (int i = 0; i < n; i++)
                    if (termFreqs[i * slotCount + slot] > 0)
                        df++;

                if (df == 0)
                    continue;

                // Robertson-Sparck Jones IDF 的常用平滑形式，恒为正
                var idf = Math.Log(1.0 + (n - df + 0.5) / (df + 0.5));

                for (int i = 0; i < n; i++)
                {
                    var tf = termFreqs[i * slotCount + slot];
                    if (tf == 0)
                        continue;

                    var docLen = lengths[i];
                    var denominator = tf + K1 * (1.0 - B + B * docLen / avgLength);
                    scores[i] += idf * (tf * (K1 + 1.0)) / denominator;
                }
            }

            return scores;
        }

        private struct TermCounter(
            Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup,
            int[] termFreqs,
            int offset) : ITokenSink
        {
            public int Length { get; private set; }

            public void Accept(ReadOnlySpan<char> token)
            {
                Length++;
                if (lookup.TryGetValue(token, out var slot))
                    termFreqs[offset + slot]++;
            }
        }
    }

    /// <summary>
    /// Reciprocal Rank Fusion：score(d) = Σᵢ 1 / (k + rankᵢ(d))
    ///
    /// 只看排名不看原始分数，因此融合的多路排序不需要对齐量纲
    /// （BM25 的分数是无上界的实数，覆盖率是 [0,1]，直接加权求和没有意义）。
    ///
    /// 前提是各路排的是**同一批文档**。若各路来源互不相交，Σ 恒只有一项，
    /// RRF 退化成纯粹的排名归一化，反而抹掉了原始分数的信息量。
    ///
    /// Cormack, Clarke &amp; Buettcher (2009).
    /// </summary>
    internal static class RrfFusion
    {
        /// <summary>论文推荐值，控制排名靠后文档的分数衰减速度。</summary>
        public const int DefaultK = 60;

        /// <summary>
        /// 融合若干条排序列表。每条列表是按相关性降序排列的文档下标。
        /// </summary>
        /// <returns>文档下标 → RRF 分数。未出现在任何列表中的文档不会出现在结果里。</returns>
        public static Dictionary<int, double> Fuse(int k, params IReadOnlyList<int>[] rankings)
        {
            var fused = new Dictionary<int, double>();

            foreach (var ranking in rankings)
            {
                if (ranking is null)
                    continue;

                for (int rank = 0; rank < ranking.Count; rank++)
                {
                    var docIndex = ranking[rank];
                    // rank 从 0 开始，+1 转成论文里的 1-based 名次
                    var contribution = 1.0 / (k + rank + 1);
                    fused[docIndex] = fused.TryGetValue(docIndex, out var acc)
                        ? acc + contribution
                        : contribution;
                }
            }

            return fused;
        }
    }
}
