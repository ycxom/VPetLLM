using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using VPetLLM.Core.Abstractions.Base;
using VPetLLM.Core.Data.Database;
using VPetLLM.Core.Services.Embedding;
using VPetLLM.Utils.Common;
using VPetLLM.Utils.Data;

namespace VPetLLM.PerfChecks;

/// <summary>
/// 用真实规模的聊天库副本量各段耗时。只读写传入路径旁边的工作副本，不碰用户数据目录。
/// </summary>
internal static class Bench
{
    private const string Query = "你还记得我上次说喜欢喝什么咖啡吗，还有周末想去看的那部电影";

    private static readonly List<(string Name, double Ms)> Results = new();

    public static int Run(string sourceDb)
    {
        var work = Path.Combine(Path.GetDirectoryName(sourceDb)!, "bench_work.db");
        File.Copy(sourceDb, work, overwrite: true);

        string provider;
        using (var c = SQLiteHelper.OpenConnection(work))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT provider FROM chat_history GROUP BY provider ORDER BY COUNT(*) DESC LIMIT 1";
            provider = (string)cmd.ExecuteScalar()!;
        }

        Measure("open+PRAGMA x500", 3, () =>
        {
            for (int i = 0; i < 500; i++)
            {
                using var c = SQLiteHelper.OpenConnection(work);
            }
        });

        Measure("open only x500", 3, () =>
        {
            for (int i = 0; i < 500; i++)
            {
                using var c = new SqliteConnection(SQLiteHelper.BuildConnectionString(work));
                c.Open();
            }
        });

        ChatHistoryDatabase db = null!;
        Measure("ChatHistoryDatabase ctor", 3, () => db = new ChatHistoryDatabase(work));

        List<Message> providerHistory = null!;
        Measure("GetHistory(main provider)", 5, () => providerHistory = db.GetHistory(provider));
        List<Message> all = null!;
        Measure("GetAllHistory", 5, () => all = db.GetAllHistory());
        Console.WriteLine($"messages: main={providerHistory.Count} all={all.Count}");

        Measure("history.ToList x100", 3, () =>
        {
            for (int i = 0; i < 100; i++)
                _ = all.ToList();
        });

        Measure("EstimateMessagesTokenCount(all)", 5, () => TokenCounter.EstimateMessagesTokenCount(all));

        var assembly = typeof(ChatHistoryDatabase).Assembly;
        var serviceType = assembly.GetType("VPetLLM.Core.Services.MemoryRetrievalService")!;
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var extract = serviceType.GetMethod("ExtractKeywords", flags)!.CreateDelegate<Func<string, List<string>>>();
        var countCoverage = serviceType.GetMethod("CountCoverage", flags)!
            .CreateDelegate<Func<IReadOnlyList<string>, List<string>, int[]>>();
        var bm25 = assembly.GetType("VPetLLM.Core.Services.Bm25")!.GetMethod("Score")!
            .CreateDelegate<Func<IReadOnlyList<string>, IReadOnlyList<string>, double[]>>();

        var texts = all.Select(m => m.Content ?? "").Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var keywords = extract(Query);
        Console.WriteLine($"docs={texts.Count} keywords={keywords.Count}");

        var parallelOptions = (ParallelOptions)assembly.GetType("VPetLLM.Core.Services.CorpusParallel")!
            .GetField("Options")!.GetValue(null)!;
        var maxThreads = parallelOptions.MaxDegreeOfParallelism;
        Console.WriteLine($"max threads={maxThreads}");

        Measure("Bm25.Score", 5, () => bm25(texts, keywords));
        Measure("CountCoverage", 5, () => countCoverage(texts, keywords));
        parallelOptions.MaxDegreeOfParallelism = 1;
        Measure("Bm25.Score(1 thread)", 5, () => bm25(texts, keywords));
        Measure("CountCoverage(1 thread)", 5, () => countCoverage(texts, keywords));
        parallelOptions.MaxDegreeOfParallelism = maxThreads;

        Measure("SHA256(corpus)", 3, () =>
        {
            foreach (var t in texts)
                EmbeddingStore.ComputeHash(t);
        });

        var random = new Random(1);
        var vectors = texts.Select(_ => RandomUnit(random, 1024)).ToList();
        var queryVector = RandomUnit(random, 1024);
        Measure("Cosine(corpus x1024d)", 5, () =>
        {
            foreach (var v in vectors)
                EmbeddingService.CosineSimilarity(queryVector, v);
        });

        var embedPath = Path.Combine(Path.GetDirectoryName(sourceDb)!, "bench_embed.db");
        File.Delete(embedPath);
        var store = new EmbeddingStore(embedPath);
        var hashes = texts.Select(t => EmbeddingStore.ComputeHash(t)).Distinct().ToList();
        Measure("EmbeddingStore.PutMany", 1, () =>
            store.PutMany(hashes.Select((h, i) => (h, vectors[i])).ToList(), "m"));
        Measure("EmbeddingStore.GetMany", 5, () => store.GetMany(hashes, "m"));

        Measure("AddMessage x200", 3, () =>
        {
            for (int i = 0; i < 200; i++)
                db.AddMessage("bench", new Message { Role = "user", Content = "性能基准消息", UnixTime = 1 });
        });

        Console.WriteLine();
        foreach (var (name, ms) in Results)
            Console.WriteLine($"{ms,10:F2} ms  {name}");

        db.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(work);
        File.Delete(embedPath);
        return 0;
    }

    private static void Measure(string name, int rounds, Action action)
    {
        var samples = new double[rounds];
        for (int i = 0; i < rounds; i++)
        {
            var watch = Stopwatch.StartNew();
            action();
            samples[i] = watch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        Results.Add((name, samples[rounds / 2]));
    }

    private static float[] RandomUnit(Random random, int dims)
    {
        var v = new float[dims];
        for (int i = 0; i < dims; i++)
            v[i] = (float)(random.NextDouble() - 0.5);
        EmbeddingStore.NormalizeInPlace(v);
        return v;
    }
}
