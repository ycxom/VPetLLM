using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using VPetLLM.Infrastructure.Configuration;

namespace VPetLLM.PerfChecks;

/// <summary>
/// 量 Setting.Save 全程。Setting 的库路径固定在文档目录，这里先把 _storage 换成指向
/// 临时目录的实例并校验过再保存，真实 settings.db 和备份一律不写。
/// </summary>
internal static class SettingsBench
{
    public static int Run(string scratchDir)
    {
        var dir = Path.Combine(scratchDir, "settings_bench");
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "settings.db");

        var watch = Stopwatch.StartNew();
        var settings = new Setting(dir);
        Console.WriteLine($"new Setting: {watch.Elapsed.TotalMilliseconds:F1} ms");

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var storage = new SQLiteSettingStorage(dir);
        typeof(SQLiteSettingStorage).GetField("_databasePath", flags)!.SetValue(storage, db);
        typeof(SQLiteSettingStorage).GetField("_backupManager", flags)!.SetValue(storage, new BackupManager(db));
        if (storage.GetStorageLocation() != db || !storage.Initialize())
        {
            Console.WriteLine("临时库未就绪，放弃");
            return 1;
        }

        var field = typeof(Setting).GetField("_storage", BindingFlags.Static | BindingFlags.NonPublic)!;
        field.SetValue(null, storage);
        if (!ReferenceEquals(field.GetValue(null), storage))
        {
            Console.WriteLine("storage 替换失败，放弃");
            return 1;
        }

        var samples = new List<double>();
        for (int i = 0; i < 12; i++)
        {
            watch.Restart();
            settings.Save();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"Setting.Save: first={samples[0]:F1} ms, rest median={samples.Skip(1).Order().ElementAt(5):F1} ms, max={samples.Skip(1).Max():F1} ms");

        var backup = new BackupManager(db);
        watch.Restart();
        for (int i = 0; i < 20; i++)
            backup.CreateBackup();
        Console.WriteLine($"CreateBackup: {watch.Elapsed.TotalMilliseconds / 20:F2} ms/次");

        watch.Restart();
        for (int i = 0; i < 1000; i++)
            backup.BackupIfDue();
        Console.WriteLine($"BackupIfDue（未到间隔）: {watch.Elapsed.TotalMilliseconds / 1000:F3} ms/次");

        // 空库太小，撑到接近真实库的大小再量一次
        using (var filler = global::VPetLLM.Utils.Data.SQLiteHelper.OpenConnection(db))
        using (var cmd = filler.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE bench_filler (data BLOB);"
                + "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 512)"
                + "INSERT INTO bench_filler SELECT randomblob(4096) FROM n;";
            cmd.ExecuteNonQuery();
        }
        Console.WriteLine($"填充后库大小: {new FileInfo(db).Length / 1024} KB + WAL {new FileInfo(db + "-wal").Length / 1024} KB");
        watch.Restart();
        for (int i = 0; i < 20; i++)
            backup.CreateBackup();
        Console.WriteLine($"CreateBackup（约 2MB）: {watch.Elapsed.TotalMilliseconds / 20:F2} ms/次");

        SqliteConnection.ClearAllPools();
        return 0;
    }
}
