using System.Reflection;
using System.Runtime.CompilerServices;
using VPetLLM.Core.Abstractions.Base;
using VPetLLM.Core.Data.Database;
using VPetLLM.Core.Data.Managers;
using VPetLLM.Core.Services;

namespace VPetLLM.PerfChecks;

/// <summary>
/// 走 MemoryRetrievalService 的完整检索，把各查询的输出落到文件，用来比较改动前后的排序。
/// 输出是聊天原文，只写到调用方给的路径，不打印。
/// </summary>
internal static class Golden
{
    internal static readonly string[] Queries =
    {
        "你还记得我上次说喜欢喝什么咖啡吗，还有周末想去看的那部电影",
        "咖啡",
        "电影",
        "猫",
        "天气怎么样",
        "VPet 插件",
        "GitHub API",
        "hello world",
        "Python error",
        "2024",
        "K8s",
        "the and of",
    };

    public static int Run(string sourceDb, string outFile)
    {
        var work = Path.Combine(Path.GetDirectoryName(sourceDb)!, "golden_work.db");
        File.Copy(sourceDb, work, overwrite: true);

        List<Message> history;
        using (var db = new ChatHistoryDatabase(work))
            history = db.GetAllHistory();

        var service = CreateService(history);
        var build = typeof(MemoryRetrievalService).GetMethod("BuildRawResultsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        using var writer = new StreamWriter(outFile);
        foreach (var query in Queries)
        {
            foreach (var (window, budget) in new (int?, int?)[] { (null, 500), (null, 100000), (5, null) })
            {
                var output = ((Task<string>)build.Invoke(service, new object?[] { query, window, budget })!).GetAwaiter().GetResult();
                writer.WriteLine($"=== {query} window={window} budget={budget}");
                writer.WriteLine(output);
            }
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(work);
        return 0;
    }

    /// <summary>HistoryManager/Setting 的构造都绑死用户数据目录，这里绕开构造，只填检索要读的字段。</summary>
    internal static MemoryRetrievalService CreateService(List<Message> history)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var settings = (Setting)RuntimeHelpers.GetUninitializedObject(typeof(Setting));

        var manager = (HistoryManager)RuntimeHelpers.GetUninitializedObject(typeof(HistoryManager));
        typeof(HistoryManager).GetField("_history", flags)!.SetValue(manager, history);
        typeof(HistoryManager).GetField("_historyLock", flags)!.SetValue(manager, new object());
        typeof(HistoryManager).GetField("_settings", flags)!.SetValue(manager, settings);

        return new MemoryRetrievalService(settings, null!, manager, null, null);
    }
}
