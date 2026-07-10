namespace VPetLLM.Core.Services
{
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
    /// </summary>
    internal static class Bm25
    {
        private const double K1 = 1.2;   // TF 饱和速度
        private const double B = 0.75;   // 长度归一化强度

        /// <summary>
        /// 为每篇文档打分。返回数组与 <paramref name="docTokens"/> 一一对应，
        /// 未命中任何查询词的文档得分为 0。
        /// </summary>
        /// <param name="docTokens">每篇文档的分词结果（含重复，用于统计词频）。</param>
        /// <param name="queryTerms">去重后的查询词。</param>
        public static double[] Score(
            IReadOnlyList<IReadOnlyList<string>> docTokens,
            IReadOnlyList<string> queryTerms)
        {
            var n = docTokens.Count;
            var scores = new double[n];
            if (n == 0 || queryTerms.Count == 0)
                return scores;

            // 每篇文档的词频表 + 长度
            var termFreqs = new Dictionary<string, int>[n];
            double totalLength = 0;
            for (int i = 0; i < n; i++)
            {
                var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in docTokens[i])
                    tf[t] = tf.TryGetValue(t, out var c) ? c + 1 : 1;
                termFreqs[i] = tf;
                totalLength += docTokens[i].Count;
            }

            var avgLength = totalLength / n;
            if (avgLength <= 0)
                return scores;

            foreach (var term in queryTerms)
            {
                // 文档频率：包含该词的文档数
                var df = 0;
                for (int i = 0; i < n; i++)
                    if (termFreqs[i].ContainsKey(term))
                        df++;

                if (df == 0)
                    continue;

                // Robertson-Sparck Jones IDF 的常用平滑形式，恒为正
                var idf = Math.Log(1.0 + (n - df + 0.5) / (df + 0.5));

                for (int i = 0; i < n; i++)
                {
                    if (!termFreqs[i].TryGetValue(term, out var tf))
                        continue;

                    var docLen = docTokens[i].Count;
                    var denominator = tf + K1 * (1.0 - B + B * docLen / avgLength);
                    scores[i] += idf * (tf * (K1 + 1.0)) / denominator;
                }
            }

            return scores;
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
