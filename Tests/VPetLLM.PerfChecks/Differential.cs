using System.Reflection;
using System.Text;
using VPetLLM.Core.Abstractions.Base;
using VPetLLM.Core.Data.Database;
using VPetLLM.Core.Services.Embedding;

namespace VPetLLM.PerfChecks;

/// <summary>
/// 新旧词法打分的逐位差分。不接数据库时只跑构造的边界输入和随机输入；
/// 给了库就连真实语料一起比。失败时只打印下标和分数，不打印消息内容。
/// </summary>
internal static class Differential
{
    private static readonly Func<string, List<string>> Tokenize;
    private static readonly Func<IReadOnlyList<string>, IReadOnlyList<string>, double[]> Score;
    private static readonly Func<IReadOnlyList<string>, List<string>, int[]> CountCoverage;

    private static int _failures;

    static Differential()
    {
        var assembly = typeof(ChatHistoryDatabase).Assembly;
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;

        Tokenize = assembly.GetType("VPetLLM.Core.Services.MemoryTokenizer")!.GetMethod("Tokenize")!
            .CreateDelegate<Func<string, List<string>>>();
        Score = assembly.GetType("VPetLLM.Core.Services.Bm25")!.GetMethod("Score")!
            .CreateDelegate<Func<IReadOnlyList<string>, IReadOnlyList<string>, double[]>>();
        CountCoverage = assembly.GetType("VPetLLM.Core.Services.MemoryRetrievalService")!.GetMethod("CountCoverage", flags)!
            .CreateDelegate<Func<IReadOnlyList<string>, List<string>, int[]>>();
    }

    /// <summary>分词规则和大小写语义的边界都在这里，随机输入也从这些片段里拼。</summary>
    private static readonly string[] Fragments =
    {
        "", " ", "\n\t", "a", "A", "the", "THE", "The", "x", "ab", "AB", "Ab",
        "咖啡", "咖", "啡", "我", "的", "什么", "一个", "没有", "喜欢喝咖啡", "电影院",
        "K", "K", "Kk", "s", "ſ", "ſs", "ß", "SS", "ss", "ẞ",
        "İ", "ı", "I", "i", "Iİı", "Σ", "σ", "ς", "ΣΣ", "Ω", "Ω",
        "😀", "a😀b", "\uD83D", "\uDE00", "咖\uD83D啡", "𠀋",
        "١٢٣", "½", "Ⅷ", "ⅷ", "ｅｎ", "Ａ", "カタカナ", "ひらがな", "한글",
        "䷿", "一", "鿿", "ꀀ", "㐀",
        "。", "，", "！", "-", "_", "'", "/", ".", "\0",
        "GitHub", "VPet", "2024", "k8s", "K8S", "hello", "world", "error",
        new string('a', 127), new string('b', 128), new string('c', 129), new string('Z', 5000),
        new string('咖', 300),
    };

    public static int Run(string? sourceDb)
    {
        var random = new Random(20261103);

        CheckTokenizer(Fragments);
        for (int round = 0; round < 3000; round++)
            CheckTokenizer(new[] { RandomText(random, 12) });

        CheckEdgeCases();

        for (int round = 0; round < 400; round++)
        {
            var docs = Enumerable.Range(0, random.Next(0, 24)).Select(_ => RandomText(random, 8)).ToList();
            var query = Enumerable.Range(0, random.Next(0, 6)).Select(_ => RandomQueryTerm(random)).ToList();
            CheckCorpus(docs, query, $"random#{round}");
        }

        // 过 CorpusParallel 的并行门槛：并行写词频表时不能串到别的文档
        var large = Enumerable.Range(0, 3000).Select(_ => RandomText(random, 6)).ToList();
        for (int round = 0; round < 30; round++)
        {
            var query = Enumerable.Range(0, random.Next(1, 8)).Select(_ => RandomQueryTerm(random)).ToList();
            CheckCorpus(large, query, $"large#{round}");
        }

        CheckCosine(random);
        CheckEmbeddingStore();

        if (sourceDb is not null)
            CheckRealCorpus(sourceDb);

        Console.WriteLine(_failures == 0 ? "differential: OK" : $"differential: {_failures} FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void CheckEdgeCases()
    {
        var docs = new List<string> { "the the the", "咖啡 COFFEE coffee", "什么", "", "K8S k8s K8s", "K8s" };

        // 查询词重复、只差大小写：旧实现每个都累加一遍
        CheckCorpus(docs, new List<string> { "coffee", "coffee" }, "dup");
        CheckCorpus(docs, new List<string> { "coffee", "COFFEE", "Coffee" }, "dup-case");
        // 查询词是停用词：文档里被分词过滤掉，tf 恒 0
        CheckCorpus(docs, new List<string> { "the", "什么", "coffee" }, "stop");
        // 全空语料：平均长度 0
        CheckCorpus(new List<string> { "", " ", "。" }, new List<string> { "coffee" }, "empty-docs");
        CheckCorpus(new List<string>(), new List<string> { "coffee" }, "no-docs");
        CheckCorpus(docs, new List<string>(), "no-query");
        // 查询词比分词结果短/含非词字符：BM25 不命中，覆盖率按子串命中
        CheckCorpus(docs, new List<string> { "c", "咖", " ", "8s" }, "substring");
        CheckCorpus(docs, new List<string> { "k8s", "K8S" }, "kelvin");
    }

    private static void CheckTokenizer(IEnumerable<string> inputs)
    {
        foreach (var input in inputs)
        {
            var expected = LegacyRanking.Tokenize(input);
            var actual = Tokenize(input);
            if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
                Fail($"tokenize: {expected.Count} vs {actual.Count} tokens, input length {input.Length}");
        }
    }

    private static void CheckCorpus(List<string> docs, List<string> query, string name)
    {
        var tokens = docs.Select(d => (IReadOnlyList<string>)LegacyRanking.Tokenize(d)).ToList();

        var expected = LegacyRanking.Score(tokens, query);
        var actual = Score(docs, query);
        for (int i = 0; i < docs.Count; i++)
        {
            if (BitConverter.DoubleToInt64Bits(expected[i]) != BitConverter.DoubleToInt64Bits(actual[i]))
            {
                Fail($"{name}: doc {i} score {expected[i]:R} vs {actual[i]:R}");
                break;
            }
        }

        var coverage = CountCoverage(docs, query);
        for (int i = 0; i < docs.Count; i++)
        {
            var expectedCount = LegacyRanking.CountMatches(docs[i], query);
            if (expectedCount != coverage[i])
            {
                Fail($"{name}: doc {i} coverage {expectedCount} vs {coverage[i]}");
                break;
            }
        }
    }

    private static void CheckCosine(Random random)
    {
        foreach (var dim in new[] { 0, 1, 3, 7, 8, 9, 15, 16, 17, 31, 33, 384, 768, 1024, 1536 })
        {
            for (int round = 0; round < 50; round++)
            {
                var a = RandomUnit(random, dim);
                var b = RandomUnit(random, dim);
                // SIMD 按 lane 累加，顺序和标量不同，末位可以有舍入差
                if (Math.Abs(LegacyRanking.Cosine(a, b) - EmbeddingService.CosineSimilarity(a, b)) > 1e-12)
                    Fail($"cosine: dim {dim}");
            }
        }

        float[] shorter = { 1, 1, 1 };
        float[] longer = { 1, 1, 1, 1 };
        if (EmbeddingService.CosineSimilarity(shorter, longer) != 0.0 || EmbeddingService.CosineSimilarity(longer, shorter) != 0.0)
            Fail("cosine: dimension mismatch");
    }

    private static void CheckEmbeddingStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vpetllm_perf_{Guid.NewGuid():N}.db");
        try
        {
            var store = new EmbeddingStore(path);
            store.PutMany(new[] { ("h1", new[] { 3f, 4f }), ("h2", new[] { 0f, 2f }), ("h3", new[] { 1f, 0f }) }, "m1");
            store.PutMany(new[] { ("h1", new[] { 1f, 0f, 0f }) }, "m2");

            var got = store.GetMany(new[] { "h1", "h2", "absent" }, "m1");
            if (got.Count != 2 || !got.ContainsKey("h1") || !got.ContainsKey("h2"))
                Fail($"store: expected h1,h2 only, got {got.Count}");
            else if (got["h1"].Length != 2 || Math.Abs(got["h1"][0] - 0.6f) > 1e-6 || Math.Abs(got["h1"][1] - 0.8f) > 1e-6)
                Fail("store: vector not normalized");

            if (store.GetMany(new[] { "h1" }, "m2")["h1"].Length != 3)
                Fail("store: model isolation");
            if (store.GetMany(Array.Empty<string>(), "m1").Count != 0)
                Fail("store: empty request");

            // 同键再写是覆盖，整批共用的语句不能把上一条的参数带过来
            store.PutMany(new[] { ("h1", new[] { 0f, 1f }), ("h2", new[] { 1f, 0f }) }, "m1");
            var updated = store.GetMany(new[] { "h1", "h2" }, "m1");
            if (updated["h1"][1] != 1f || updated["h2"][0] != 1f)
                Fail("store: upsert");

            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "UPDATE embeddings SET dim = 99 WHERE content_hash = 'h2' AND model_key = 'm1'";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "SELECT COUNT(*), COUNT(updated_at), MIN(length(updated_at)) FROM embeddings";
                using var reader = cmd.ExecuteReader();
                reader.Read();
                if (reader.GetInt32(0) != 4 || reader.GetInt32(1) != 4 || reader.GetInt32(2) < 19)
                    Fail("store: row count or timestamp");
            }

            if (store.GetMany(new[] { "h1", "h2" }, "m1").ContainsKey("h2"))
                Fail("store: dirty blob should be a miss");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                File.Delete(path + suffix);
        }
    }

    private static void CheckRealCorpus(string sourceDb)
    {
        var work = Path.Combine(Path.GetDirectoryName(sourceDb)!, "diff_work.db");
        File.Copy(sourceDb, work, overwrite: true);

        List<Message> history;
        using (var db = new ChatHistoryDatabase(work))
            history = db.GetAllHistory();

        var docs = history.Select(m => m.Content ?? "").ToList();
        var extract = typeof(ChatHistoryDatabase).Assembly.GetType("VPetLLM.Core.Services.MemoryRetrievalService")!
            .GetMethod("ExtractKeywords", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<string, List<string>>>();

        CheckTokenizer(docs);
        foreach (var query in Golden.Queries)
            CheckCorpus(docs, extract(query), "real");
        // 查询词取自语料本身，保证 BM25 有大量真实命中
        CheckCorpus(docs, docs.Take(200).SelectMany(LegacyRanking.Tokenize).Take(40).ToList(), "real-self");
        Console.WriteLine($"real corpus: {docs.Count} docs");

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(work);
    }

    private static string RandomText(Random random, int maxParts)
    {
        var builder = new StringBuilder();
        var parts = random.Next(0, maxParts);
        for (int i = 0; i < parts; i++)
        {
            builder.Append(Fragments[random.Next(Fragments.Length)]);
            if (random.Next(3) == 0)
                builder.Append(' ');
        }
        return builder.ToString();
    }

    private static string RandomQueryTerm(Random random)
    {
        var term = Fragments[random.Next(Fragments.Length)];
        return term.Length == 0 ? "x" : term;
    }

    private static float[] RandomUnit(Random random, int dim)
    {
        var vector = new float[dim];
        for (int i = 0; i < dim; i++)
            vector[i] = (float)(random.NextDouble() - 0.5);
        EmbeddingStore.NormalizeInPlace(vector);
        return vector;
    }

    private static void Fail(string message)
    {
        if (++_failures <= 20)
            Console.WriteLine("FAIL " + message);
    }
}
