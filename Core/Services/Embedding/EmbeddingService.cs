using VPetLLM.Core.Data.Database;

namespace VPetLLM.Core.Services.Embedding
{
    /// <summary>
    /// 把 <see cref="IEmbeddingProvider"/> 和 <see cref="EmbeddingStore"/> 串起来，
    /// 对检索侧暴露两个动作：拿查询向量、拿候选文档已缓存的向量。
    ///
    /// 冷启动策略是「自暖」而不是启动时全量回填：检索时只用已缓存的向量参与排序，
    /// 缺向量的文档退出向量路（RRF 仍有 BM25 和覆盖率两路），同时把它们排进后台
    /// 队列。下一次检索这些文档就有向量了。因此：
    ///   - 首次使用不阻塞、不卡启动、不会为 1000+ 条历史一次性打爆 API
    ///   - 常被检索到的文档最先获得向量，冷门文档可能永远不嵌入 —— 这正是想要的
    /// </summary>
    public sealed class EmbeddingService
    {
        private readonly IEmbeddingProvider _provider;
        private readonly EmbeddingStore _store;
        private readonly int _maxBatchSize;
        private readonly int _maxBackfillPerRound;
        private readonly TimeSpan _timeout;

        /// <summary>同一时刻只允许一个回填任务，避免重复嵌入同一批文本。</summary>
        private readonly SemaphoreSlim _backfillGate = new(1, 1);

        /// <summary>
        /// 连续失败计数。达到上限后停用向量路，避免每次检索都白等一个超时。
        /// 任意一次成功即清零。
        /// </summary>
        private int _consecutiveFailures;
        private const int MaxConsecutiveFailures = 3;

        public string ModelKey => _provider.ModelKey;

        /// <summary>后端连续失败多次后自动熄火，检索侧据此跳过向量路。</summary>
        public bool IsAvailable => _consecutiveFailures < MaxConsecutiveFailures;

        public EmbeddingService(
            IEmbeddingProvider provider,
            EmbeddingStore store,
            int maxBatchSize = 32,
            int maxBackfillPerRound = 64,
            int timeoutSeconds = 10)
        {
            _provider = provider;
            _store = store;
            _maxBatchSize = Math.Max(1, maxBatchSize);
            _maxBackfillPerRound = Math.Max(0, maxBackfillPerRound);
            _timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
        }

        /// <summary>
        /// 嵌入查询文本。失败返回 null —— 调用方据此跳过向量路，而不是让整个检索失败。
        /// </summary>
        public async Task<float[]?> EmbedQueryAsync(string query)
        {
            if (!IsAvailable || string.IsNullOrWhiteSpace(query))
                return null;

            try
            {
                using var cts = new CancellationTokenSource(_timeout);
                var result = await _provider.EmbedAsync(new[] { query }, cts.Token);

                var vector = result.Count > 0 ? result[0] : null;
                if (vector is null || vector.Length == 0)
                    return null;

                Interlocked.Exchange(ref _consecutiveFailures, 0);

                // 查询向量也要归一化，否则和库里归一化过的向量算点积得不到余弦
                EmbeddingStore.NormalizeInPlace(vector);
                return vector;
            }
            catch (Exception ex)
            {
                var failures = Interlocked.Increment(ref _consecutiveFailures);
                Logger.Log($"EmbeddingService: 查询嵌入失败（连续 {failures} 次）: {ex.Message}");
                if (failures >= MaxConsecutiveFailures)
                    Logger.Log("EmbeddingService: 连续失败达到上限，向量检索已自动停用直到下次成功");
                return null;
            }
        }

        /// <summary>取回这批文本里已缓存的向量，键为文本本身。</summary>
        public Dictionary<string, float[]> GetCachedVectors(IReadOnlyList<string> texts)
        {
            var byHash = new Dictionary<string, string>(StringComparer.Ordinal);   // hash -> text
            foreach (var text in texts)
            {
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                byHash[EmbeddingStore.ComputeHash(text)] = text;
            }

            var vectors = _store.GetMany(byHash.Keys, _provider.ModelKey);

            var result = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (var (hash, vector) in vectors)
            {
                if (byHash.TryGetValue(hash, out var text))
                    result[text] = vector;
            }
            return result;
        }

        /// <summary>
        /// 火抛地为缺向量的文本补齐向量。有回填任务在跑时直接跳过本次
        /// （下次检索还会再排一遍，不必排队）。
        /// </summary>
        public void QueueBackfill(IReadOnlyList<string> missingTexts)
        {
            if (!IsAvailable || _maxBackfillPerRound == 0 || missingTexts.Count == 0)
                return;

            _ = BackfillAsync(missingTexts);
        }

        private async Task BackfillAsync(IReadOnlyList<string> missingTexts)
        {
            if (!_backfillGate.Wait(0))
                return;   // 已有回填在跑

            try
            {
                var pending = missingTexts
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.Ordinal)
                    .Take(_maxBackfillPerRound)
                    .ToList();

                if (pending.Count == 0)
                    return;

                var embedded = 0;
                for (int offset = 0; offset < pending.Count; offset += _maxBatchSize)
                {
                    var batch = pending.Skip(offset).Take(_maxBatchSize).ToList();

                    using var cts = new CancellationTokenSource(_timeout);
                    var vectors = await _provider.EmbedAsync(batch, cts.Token);

                    var toStore = new List<(string, float[])>(batch.Count);
                    for (int i = 0; i < batch.Count && i < vectors.Count; i++)
                    {
                        if (vectors[i] is float[] v && v.Length > 0)
                            toStore.Add((EmbeddingStore.ComputeHash(batch[i]), v));
                    }

                    _store.PutMany(toStore, _provider.ModelKey);
                    embedded += toStore.Count;
                }

                Interlocked.Exchange(ref _consecutiveFailures, 0);
                Logger.Log($"EmbeddingService: 回填 {embedded}/{pending.Count} 条向量");
            }
            catch (Exception ex)
            {
                // 火抛任务，异常必须就地记录否则被静默吞掉
                var failures = Interlocked.Increment(ref _consecutiveFailures);
                Logger.Log($"EmbeddingService: 向量回填失败（连续 {failures} 次）: {ex.Message}");
            }
            finally
            {
                _backfillGate.Release();
            }
        }

        /// <summary>
        /// 归一化向量的余弦相似度即点积。两侧都已归一化，这里不再除模长。
        /// </summary>
        public static double CosineSimilarity(float[] a, float[] b)
        {
            if (a.Length != b.Length)
                return 0.0;   // 维度不一致（换过模型的脏缓存），当作不相关

            double dot = 0;
            for (int i = 0; i < a.Length; i++)
                dot += (double)a[i] * b[i];
            return dot;
        }
    }
}
