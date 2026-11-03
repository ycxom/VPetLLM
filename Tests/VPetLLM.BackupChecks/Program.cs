using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VPetLLM.Infrastructure.Configuration;
using VPetLLM.Infrastructure.Exceptions;
using VPetLLM;
using VPetLLM.Utils.Data;
using VPetLLM.Utils.Security;

// 设置库备份与保存的回归检查。全部在临时目录里跑，真实 settings.db 和备份一律不写。
//
// 被检查的是这几件事：
//   1. 备份带上 WAL 里的新数据（实机上 5 份备份都停在主库最后一次 checkpoint）；
//   2. 按间隔备份，5 份不再挤在 10 分钟内；
//   3. 库坏了不产生备份，也就挤不掉好备份；
//   4. 恢复时旧 WAL 不回放到恢复后的库上，池里的连接不挡路；
//   5. 恢复前的应急副本带上 WAL，清理备份不漏下临时文件；
//   6. 文件头坏了（打开就报“不是数据库”）启动时也能从备份恢复，备份也坏时不死循环；
//   7. 清理明文残留时连应急副本一起清；
//   8. 没改动的保存不碰库：宿主约 2 分钟存一次，以前每次都整库重写，密钥每次换一份新密文。

static class Program
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  [PASS] {name}"); }
        else { _fail++; Console.WriteLine($"  [FAIL] {name}  {detail}"); }
    }

    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        var root = Path.Combine(Path.GetTempPath(), $"vpet-backup-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Test_BackupCarriesWal(Sub(root, "wal"));
            Test_BackupSnapshotIsConsistent(Sub(root, "snapshot"));
            Test_Interval(Sub(root, "interval"));
            Test_Rotation(Sub(root, "rotation"));
            Test_CorruptDbKeepsGoodBackups(Sub(root, "corrupt"));
            Test_UnreadableDbKeepsGoodBackups(Sub(root, "notadb"));
            Test_RestoreIgnoresStaleWal(Sub(root, "stalewal"));
            Test_RestoreWithPooledHandle(Sub(root, "pooled"));
            Test_StorageRecovery(Sub(root, "storage"));
            Test_HeaderCorruptionRecovers(Sub(root, "header"));
            Test_Purge(Sub(root, "purge"));
            Test_PurgeEmergency(Sub(root, "purge-emergency"));
            Test_UnchangedSaveWritesNothing(Sub(root, "unchanged"));
            Test_SettingSaveWritesOnlyChanges(Sub(root, "setting"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"===== 通过 {_pass} / 失败 {_fail} =====");
        return _fail == 0 ? 0 : 1;
    }

    static string Sub(string root, string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── 工具 ───────────────────────────────────────────────────────

    /// <summary>和线上同一条路开的池化连接，WAL 模式，新值留在 -wal 里不进主库。</summary>
    static SqliteConnection NewDb(string db, string value)
    {
        var c = SQLiteHelper.OpenConnection(db);
        Exec(c, "CREATE TABLE t(k TEXT PRIMARY KEY, v TEXT)");
        Set(c, value);
        Exec(c, "PRAGMA wal_checkpoint(TRUNCATE)");
        return c;
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static void Set(SqliteConnection c, string value, string key = "x")
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO t(k, v) VALUES (@k, @v)";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>独立、不入池的连接读，用完句柄立刻释放。</summary>
    static string? Scalar(string db, string sql)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString();
        using var c = new SqliteConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }

    static string? Read(string db) => Scalar(db, "SELECT v FROM t WHERE k = 'x'");

    static string BackupDir(string dir) => Path.Combine(dir, "backups");

    static string[] Files(string dir) =>
        Directory.Exists(BackupDir(dir)) ? Directory.GetFiles(BackupDir(dir)) : Array.Empty<string>();

    /// <summary>
    /// 列备份目录，直到满足预期或等满 2 秒。刚覆写过又删掉的文件赶上杀毒软件正扫着，
    /// 会在目录里多留一小会儿（本机卡巴斯基实测，紧跟在删除后的计数偶发多一份）。
    /// 真多出来、真没删掉的文件等多久都不会消失，挡不住真问题
    /// </summary>
    static string[] FilesWhen(string dir, Func<string[], bool> expected)
    {
        var files = Files(dir);
        for (var i = 0; i < 40 && !expected(files); i++)
        {
            Thread.Sleep(50);
            files = Files(dir);
        }
        return files;
    }

    static bool Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 40; i++)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    static string Names(IEnumerable<string> paths) => string.Join(" ", paths.Select(Path.GetFileName));

    /// <summary>把一份备份改成 age 之前做的：改名是因为文件名只精确到秒，同一秒内的新备份会盖掉它。</summary>
    static string Age(string path, TimeSpan age)
    {
        var when = DateTime.Now - age;
        var aged = Path.Combine(Path.GetDirectoryName(path)!, $"settings_backup_{when:yyyyMMdd_HHmmss}.db");
        if (aged != path) File.Move(path, aged, overwrite: true);
        File.SetCreationTime(aged, when);
        return aged;
    }

    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>一个足够大、干净（WAL 已并回主库）且已关闭的库，随后可以直接按字节破坏。</summary>
    static void NewCheckpointedDb(string db)
    {
        using (var c = NewDb(db, "good"))
        {
            for (int i = 0; i < 300; i++)
                Set(c, new string('r', 300), $"k{i}");
            Exec(c, "PRAGMA wal_checkpoint(TRUNCATE)");
            SqliteConnection.ClearPool(c);
        }
    }

    /// <summary>做满 5 份好备份，都已过了备份间隔。</summary>
    static List<string> SeedGoodBackups(string db)
    {
        var backup = new BackupManager(db);
        var good = new List<string>();
        for (int i = 5; i >= 1; i--)
            good.Add(Age(backup.CreateBackup(), BackupManager.MinBackupInterval * (i + 1)));
        return good;
    }

    // ── 用例 ───────────────────────────────────────────────────────

    static void Test_BackupCarriesWal(string dir)
    {
        Console.WriteLine("\n[备份带上 WAL 里的新数据]");
        var db = Path.Combine(dir, "settings.db");

        using var live = NewDb(db, "v1");
        Set(live, "v2");
        Check("前提：v2 只在 WAL 里", new FileInfo(db + "-wal").Length > 0);

        var path = new BackupManager(db).CreateBackup();

        // 先看目录再读：读备份本身会让 SQLite 临时建 -wal
        var files = FilesWhen(dir, f => f.Length == 1);
        Check("备份目录只有这一份，没有 -wal/-shm/临时文件", files.Length == 1 && files[0] == path, Names(files));
        // 头部第 18、19 字节为 2 表示 WAL：那样校验时会在临时文件旁建 -shm，赶上被占着就删不掉
        var header = new byte[20];
        using (var f = File.OpenRead(path)) f.ReadExactly(header);
        Check("备份不是 WAL 模式", header[18] == 1 && header[19] == 1, $"{header[18]}/{header[19]}");
        Check("备份里是最新值", Read(path) == "v2", Read(path) ?? "null");
        Check("备份完整", Scalar(path, "PRAGMA quick_check") == "ok");

        Set(live, "v3");
        Check("备份不影响正在用的连接", Read(db) == "v3");
    }

    static void Test_BackupSnapshotIsConsistent(string dir)
    {
        Console.WriteLine("\n[写入中备份：快照一致]");
        var db = Path.Combine(dir, "settings.db");

        using var live = NewDb(db, "0");
        Set(live, "0", "y");

        // 两个键在同一事务里同步改：任何一份备份里两者不等，就是撕裂的快照
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (int i = 1; !stop.IsCancellationRequested; i++)
            {
                using var tx = live.BeginTransaction();
                Set(live, i.ToString());
                Set(live, i.ToString(), "y");
                tx.Commit();
            }
        });

        var backup = new BackupManager(db);
        var torn = 0;
        var distinct = new HashSet<string>();
        for (int i = 0; i < 15; i++)
        {
            var path = backup.CreateBackup();
            var x = Read(path);
            var y = Scalar(path, "SELECT v FROM t WHERE k = 'y'");
            if (x != y) torn++;
            distinct.Add(x ?? "");
            File.Delete(path);
            Thread.Sleep(5);
        }
        stop.Cancel();
        writer.GetAwaiter().GetResult();

        Check("写入途中的备份没有撕裂", torn == 0, $"{torn} 份不一致");
        Check("确实抓到了多个时刻", distinct.Count > 1, $"{distinct.Count}");
    }

    static void Test_Interval(string dir)
    {
        Console.WriteLine("\n[按间隔备份]");
        var db = Path.Combine(dir, "settings.db");
        using var live = NewDb(db, "v1");
        var backup = new BackupManager(db);

        backup.BackupIfDue();
        Check("没有备份时立刻做一份", FilesWhen(dir, f => f.Length == 1).Length == 1, Names(Files(dir)));

        Set(live, "v2");
        backup.BackupIfDue();
        Check("刚备份过：跳过", FilesWhen(dir, f => f.Length == 1).Length == 1 && Read(Files(dir)[0]) == "v1", Names(Files(dir)));

        Age(FilesWhen(dir, f => f.Length == 1)[0], BackupManager.MinBackupInterval - TimeSpan.FromMinutes(5));
        backup.BackupIfDue();
        Check("差一点到间隔：还是跳过", FilesWhen(dir, f => f.Length == 1).Length == 1, Names(Files(dir)));

        Age(FilesWhen(dir, f => f.Length == 1)[0], BackupManager.MinBackupInterval + TimeSpan.FromMinutes(5));
        backup.BackupIfDue();
        var files = FilesWhen(dir, f => f.Length == 2).OrderBy(f => File.GetCreationTime(f)).ToArray();
        Check("到间隔：多一份，新的是最新值", files.Length == 2 && Read(files[1]) == "v2", Names(files));
    }

    static void Test_Rotation(string dir)
    {
        Console.WriteLine("\n[轮换保留 5 份]");
        var db = Path.Combine(dir, "settings.db");
        NewCheckpointedDb(db);
        var seeded = SeedGoodBackups(db);

        new BackupManager(db).BackupIfDue();

        var files = FilesWhen(dir, f => f.Length == 5 && !f.Contains(seeded[0]));
        Check("总数还是 5", files.Length == 5, Names(files));
        Check("挤掉的是最旧那份", !files.Contains(seeded[0]) && seeded.Skip(1).All(files.Contains), Names(files));
        Check("新备份是最近的一份", !seeded.Contains(new BackupManager(db).GetMostRecentBackup()), new BackupManager(db).GetMostRecentBackup() ?? "null");
    }

    static void AssertGoodBackupsSurvive(string dir, BackupManager backup, List<string> good, Dictionary<string, string> hashes)
    {
        for (int i = 0; i < 6; i++)
        {
            try { backup.BackupIfDue(); }
            catch (StorageException) { }   // 和 Save 一样：备份失败只记日志
        }

        var files = FilesWhen(dir, f => f.Length == good.Count && good.All(f.Contains));
        Check("坏库没产生新备份，也没留下临时文件", files.Length == good.Count && good.All(files.Contains), Names(files));
        Check("好备份一份没少、一字节没变", good.All(f => File.Exists(f) && Hash(f) == hashes[f]));
    }

    static void Test_CorruptDbKeepsGoodBackups(string dir)
    {
        Console.WriteLine("\n[页损坏的库不挤掉好备份]");
        var db = Path.Combine(dir, "settings.db");
        NewCheckpointedDb(db);
        var good = SeedGoodBackups(db);
        var hashes = good.ToDictionary(f => f, Hash);

        // 文件头和 sqlite_schema 完好，打开不报错；后面的页全成垃圾。
        // 备份 API 不校验内容，只会原样把坏页抄过去
        using (var f = new FileStream(db, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            var junk = new byte[f.Length - 4096];
            new Random(1).NextBytes(junk);
            f.Position = 4096;
            f.Write(junk);
        }

        AssertGoodBackupsSurvive(dir, new BackupManager(db), good, hashes);
    }

    static void Test_UnreadableDbKeepsGoodBackups(string dir)
    {
        Console.WriteLine("\n[打不开的库不挤掉好备份]");
        var db = Path.Combine(dir, "settings.db");
        NewCheckpointedDb(db);
        var good = SeedGoodBackups(db);
        var hashes = good.ToDictionary(f => f, Hash);

        File.WriteAllBytes(db, new byte[8192]);
        File.WriteAllText(db, new string('x', 8192));

        AssertGoodBackupsSurvive(dir, new BackupManager(db), good, hashes);
    }

    static void Test_RestoreIgnoresStaleWal(string dir)
    {
        Console.WriteLine("\n[恢复：崩溃残留的 WAL 不回放]");
        var src = Path.Combine(dir, "settings.db");
        var crashDir = Path.Combine(dir, "crash");
        Directory.CreateDirectory(crashDir);
        var crash = Path.Combine(crashDir, "settings.db");

        string backupPath;
        using (var live = NewDb(src, "v1"))
        {
            backupPath = new BackupManager(src).CreateBackup();
            Set(live, "v2");

            // 进程崩溃时盘上的样子：主库还是 v1，v2 在 -wal 里，没人持有句柄
            File.Copy(src, crash);
            File.Copy(src + "-wal", crash + "-wal");
            SqliteConnection.ClearPool(live);
        }

        var manager = new BackupManager(crash);
        Check("恢复成功", manager.RestoreFromBackup(backupPath));
        Check("旧 -wal/-shm 已清掉", !File.Exists(crash + "-wal") && !File.Exists(crash + "-shm"));
        Check("恢复后是备份里的值，不是 WAL 里的", Read(crash) == "v1", Read(crash) ?? "null");
        Check("恢复后的库完整", Scalar(crash, "PRAGMA quick_check") == "ok");

        var emergency = crash + ".emergency";
        Check("应急副本带上了 WAL 里的数据", File.Exists(emergency) && Read(emergency) == "v2", File.Exists(emergency) ? Read(emergency) ?? "null" : "no file");

        // 上一次恢复留下的 -wal 不能配到这一次的副本上
        File.WriteAllText(emergency + "-wal", "stale");
        Check("再恢复一次成功", manager.RestoreFromBackup(backupPath));
        Check("没有 WAL 时清掉旧的应急 WAL", !File.Exists(emergency + "-wal"));
    }

    static void Test_RestoreWithPooledHandle(string dir)
    {
        Console.WriteLine("\n[恢复：池里的连接不挡路]");
        var db = Path.Combine(dir, "settings.db");
        var manager = new BackupManager(db);

        string backupPath;
        var live = NewDb(db, "v1");
        backupPath = manager.CreateBackup();
        Set(live, "v2");
        live.Dispose();   // 和 TryRecoverFromBackup 一样：Dispose 只是还回池，文件句柄还开着

        Check("恢复成功", manager.RestoreFromBackup(backupPath));
        using var reopened = SQLiteHelper.OpenConnection(db);
        using var cmd = reopened.CreateCommand();
        cmd.CommandText = "SELECT v FROM t WHERE k = 'x'";
        var value = cmd.ExecuteScalar()?.ToString();
        Check("重新打开读到的是备份里的值", value == "v1", value ?? "null");
    }

    sealed class Probe
    {
        public string Marker { get; set; } = "";
    }

    /// <summary>真实的存储类，库换到临时目录：不换的话动的是用户真实的库和备份。</summary>
    static SQLiteSettingStorage? TempStorage(string dir)
    {
        var db = Path.Combine(dir, "settings.db");
        var storage = new SQLiteSettingStorage(dir);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(SQLiteSettingStorage).GetField("_databasePath", flags)!.SetValue(storage, db);
        typeof(SQLiteSettingStorage).GetField("_backupManager", flags)!.SetValue(storage, new BackupManager(db));
        if (storage.GetStorageLocation() == db) return storage;

        Check("确实用的是临时库", false, storage.GetStorageLocation());
        storage.Dispose();
        return null;
    }

    static string Marker(string file) => Scalar(file, "SELECT value FROM settings WHERE key = 'Marker'") ?? "";

    static void Test_StorageRecovery(string dir)
    {
        Console.WriteLine("\n[走真实存储：Save 按间隔备份，坏了能恢复]");
        var db = Path.Combine(dir, "settings.db");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

        var storage = TempStorage(dir);
        if (storage is null) return;
        try
        {
            Check("临时库初始化成功", storage.Initialize());

            storage.Save(new Probe { Marker = "v1" });
            Check("第一次保存产生第一份备份", FilesWhen(dir, f => f.Length == 1).Length == 1, Names(Files(dir)));
            Age(FilesWhen(dir, f => f.Length == 1)[0], BackupManager.MinBackupInterval + TimeSpan.FromMinutes(1));

            storage.Save(new Probe { Marker = "v2" });
            storage.Save(new Probe { Marker = "v3" });
            var newest = new BackupManager(db).GetMostRecentBackup()!;
            Check("过了间隔的那次保存留下保存前的 v1", FilesWhen(dir, f => f.Length == 2).Length == 2 && Marker(newest) == "v1", Marker(newest));

            var ok = (bool)typeof(SQLiteSettingStorage).GetMethod("TryRecoverFromBackup", flags)!.Invoke(storage, null)!;
            Check("恢复成功", ok);
            var json = storage.LoadAsJson() ?? "";
            Check("恢复后读到备份里的值", json.Contains("v1") && !json.Contains("v3"), json);

            storage.Save(new Probe { Marker = "v4" });
            Check("恢复后还能继续保存", (storage.LoadAsJson() ?? "").Contains("v4"));
        }
        finally
        {
            storage.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    static void Test_Purge(string dir)
    {
        Console.WriteLine("\n[清理备份不漏文件]");
        var db = Path.Combine(dir, "settings.db");
        NewCheckpointedDb(db);

        var manager = new BackupManager(db);
        var path = manager.CreateBackup();
        File.WriteAllText(path + ".tmp", "崩溃时没写完的备份");

        var purged = manager.PurgeAllBackups();
        var left = FilesWhen(dir, f => f.Length == 0);
        Check("连没写完的临时文件一起清", purged == 2 && left.Length == 0, $"清了 {purged} 份，剩 {Names(left)}");
    }

    static void Garble(string db)
    {
        using var f = new FileStream(db, FileMode.Open, FileAccess.Write);
        f.Write(Enumerable.Repeat((byte)'x', 100).ToArray());
    }

    static void Test_HeaderCorruptionRecovers(string dir)
    {
        Console.WriteLine("\n[文件头坏了：启动时从备份恢复]");
        var db = Path.Combine(dir, "settings.db");

        var storage = TempStorage(dir);
        if (storage is null) return;
        try
        {
            Check("临时库初始化成功", storage.Initialize());
            storage.Save(new Probe { Marker = "v1" });
            Age(FilesWhen(dir, f => f.Length == 1)[0], BackupManager.MinBackupInterval + TimeSpan.FromMinutes(1));
            storage.Save(new Probe { Marker = "v2" });
            Check("前提：最新备份里是 v1", Marker(new BackupManager(db).GetMostRecentBackup()!) == "v1");
        }
        finally
        {
            storage.Dispose();
            SqliteConnection.ClearAllPools();
        }

        // 前 100 字节是文件头：坏了的话打开连接那一步就报 SQLITE_NOTADB，走不到读设置
        Garble(db);

        var reopened = TempStorage(dir);
        if (reopened is null) return;
        try
        {
            var recovered = reopened.Initialize();
            Check("初始化成功（已恢复）", recovered);
            if (recovered)
            {
                var json = reopened.LoadAsJson() ?? "";
                Check("读到备份里的值", json.Contains("v1"), json);
                Check("坏库留作应急副本", File.Exists(db + ".emergency") && File.ReadAllBytes(db + ".emergency")[0] == (byte)'x');
                reopened.Save(new Probe { Marker = "v3" });
                Check("恢复后还能继续保存", Marker(db) == "v3", Marker(db));

                // 防套娃的标志用完要复位，不然同一个实例以后再坏一次就恢复不了
                var again = (bool)typeof(SQLiteSettingStorage).GetMethod("TryRecoverFromBackup", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reopened, null)!;
                Check("同一个实例还能再恢复一次", again && Marker(db) == "v1", Marker(db));
            }
        }
        finally
        {
            reopened.Dispose();
            SqliteConnection.ClearAllPools();
        }

        // 备份本身也坏（加校验之前做的备份可能是坏的）：恢复完再初始化又会判成损坏，不能无限套娃
        var backup = new BackupManager(db).GetMostRecentBackup()!;
        Garble(backup);
        Garble(db);
        var broken = TempStorage(dir);
        if (broken is null) return;
        try
        {
            Check("备份也是坏的：初始化失败（上层改走 JSON），不死循环", !broken.Initialize());
        }
        finally
        {
            broken.Dispose();
            SqliteConnection.ClearAllPools();
        }

        foreach (var file in Files(dir)) File.Delete(file);
        Garble(db);
        var none = TempStorage(dir);
        if (none is null) return;
        try
        {
            Check("没有备份：初始化失败", !none.Initialize());
        }
        finally
        {
            none.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    static void Test_PurgeEmergency(string dir)
    {
        Console.WriteLine("\n[清理明文残留：连恢复前的应急副本一起清]");
        var db = Path.Combine(dir, "settings.db");

        var storage = TempStorage(dir);
        if (storage is null) return;
        try
        {
            Check("临时库初始化成功", storage.Initialize());
            storage.Save(new Probe { Marker = "v1" });

            // 应急副本是恢复前那份库的原样复制，加密改造之前的里面是明文。
            // 备份目录不在也要清：PurgeAllBackups 以前一看没有目录就直接返回
            File.WriteAllText(db + ".emergency", "\"ApiKey\":\"sk-plain\"");
            File.WriteAllText(db + ".emergency-wal", "\"ApiKey\":\"sk-plain\"");
            Directory.Delete(BackupDir(dir), true);

            Check("清理成功", storage.PurgeResidualPlaintext());
            Check("应急副本和它的 WAL 都清掉了", Eventually(() => !File.Exists(db + ".emergency") && !File.Exists(db + ".emergency-wal")));
        }
        finally
        {
            storage.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    // ── 没改动不写 ─────────────────────────────────────────────────

    sealed class SecretBox
    {
        [Newtonsoft.Json.JsonConverter(typeof(ProtectedStringConverter))]
        public string ApiKey { get; set; } = "";
    }

    sealed class SecretProbe
    {
        public string Marker { get; set; } = "";
        public int Count { get; set; }
        public SecretBox Box { get; set; } = new();
    }

    static SqliteConnection Observe(string db)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    /// <summary>别的连接提交过写入，这个数就会变；只读、空事务都不会让它变。</summary>
    static long DataVersion(SqliteConnection observer)
    {
        using var cmd = observer.CreateCommand();
        cmd.CommandText = "PRAGMA data_version";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>这个连接改过的行数（累计）。</summary>
    static long TotalChanges(SQLiteSettingStorage storage)
    {
        using var cmd = storage.GetConnection()!.CreateCommand();
        cmd.CommandText = "SELECT total_changes()";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>模拟换了一个进程：这个进程里加密、解密过的密钥都不记得了。</summary>
    static void ForgetCiphers() =>
        (typeof(SecretProtector).GetField("_stableCiphers", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            as System.Collections.IDictionary)?.Clear();

    static void Test_UnchangedSaveWritesNothing(string dir)
    {
        Console.WriteLine("\n[存储层：没改动的保存不碰库]");
        var db = Path.Combine(dir, "settings.db");

        var storage = TempStorage(dir);
        if (storage is null) return;
        try
        {
            Check("临时库初始化成功", storage.Initialize());
            var probe = new SecretProbe { Marker = "v1", Count = 1, Box = new SecretBox { ApiKey = "sk-test" } };
            storage.Save(probe);
            Age(FilesWhen(dir, f => f.Length == 1)[0], BackupManager.MinBackupInterval + TimeSpan.FromMinutes(1));

            using var observer = Observe(db);
            var version = DataVersion(observer);
            storage.Save(probe);
            storage.Save(probe);
            Check("再存两次：库里没有任何提交", DataVersion(observer) == version);
            Check("也不备份", FilesWhen(dir, f => f.Length == 1).Length == 1, Names(Files(dir)));

            ForgetCiphers();
            var reloaded = new SecretProbe();
            Newtonsoft.Json.JsonConvert.PopulateObject(storage.LoadAsJson()!, reloaded);
            Check("读回来是明文", reloaded.Box.ApiKey == "sk-test", reloaded.Box.ApiKey);
            storage.Save(reloaded);
            Check("换个进程读回来原样再存：库里没有任何提交（沿用库里那份密文）", DataVersion(observer) == version);

            var changes = TotalChanges(storage);
            reloaded.Count = 2;
            storage.Save(reloaded);
            Check("改了一项：只写这一行", TotalChanges(storage) - changes == 1, $"{TotalChanges(storage) - changes} 行");
            Check("值确实写进去了", Scalar(db, "SELECT value FROM settings WHERE key = 'Count'") == "2");
            Check("有改动、到了间隔：先备份", FilesWhen(dir, f => f.Length == 2).Length == 2, Names(Files(dir)));
            var newest = new BackupManager(db).GetMostRecentBackup()!;
            Check("备份是改动之前的样子", Scalar(newest, "SELECT value FROM settings WHERE key = 'Count'") == "1");

            reloaded.Box.ApiKey = "sk-other";
            changes = TotalChanges(storage);
            storage.Save(reloaded);
            var stored = Scalar(db, "SELECT value FROM settings WHERE key = 'Box'") ?? "";
            Check("改了密钥：照样写，写的是密文", TotalChanges(storage) - changes == 1 && stored.Contains(SecretProtector.Prefix) && !stored.Contains("sk-other"), stored);
        }
        finally
        {
            storage.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    static readonly FieldInfo SettingStorage = typeof(Setting).GetField("_storage", BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>
    /// Setting 的构造会去读文档目录里的真实库（库路径写死），只读不写；
    /// 构造完把静态 _storage 换成临时库并校验过，之后的保存才不碰用户的库。
    /// </summary>
    static Setting? NewSettings(string dir, SQLiteSettingStorage storage)
    {
        var s = new Setting(dir);
        SettingStorage.SetValue(null, storage);
        if (!ReferenceEquals(SettingStorage.GetValue(null), storage))
        {
            Check("Setting 换成临时库", false);
            return null;
        }
        s.OpenAI.OpenAINodes.Clear();
        s.Gemini.GeminiNodes.Clear();
        s.Ollama.OllamaNodes.Clear();
        s.LMStudio.LMStudioNodes.Clear();
        s.Free.FreeNodes.Clear();
        s.Tools = new List<Setting.ToolSetting>();
        s.ModelCache = new Setting.ModelCacheSetting();
        return s;
    }

    static string Secrets(string db) =>
        Scalar(db, "SELECT group_concat(node_data, '|') FROM (SELECT node_data FROM provider_nodes ORDER BY id)") + "#" +
        Scalar(db, "SELECT group_concat(config_data, '|') FROM (SELECT config_data FROM plugin_data ORDER BY id)");

    /// <summary>settings 表和模型缓存表的全部行，用来看一次保存具体改了哪些</summary>
    static Dictionary<string, string> Rows(string db)
    {
        var rows = new Dictionary<string, string>();
        using var c = Observe(db);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 'settings.' || key, value FROM settings UNION ALL SELECT 'model_cache.' || cache_key, models_json || created_at || updated_at FROM model_cache";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows[reader.GetString(0)] = reader.GetString(1);
        return rows;
    }

    static void Test_SettingSaveWritesOnlyChanges(string dir)
    {
        Console.WriteLine("\n[Setting.Save：节点、工具、模型缓存没改动也不重写]");
        var db = Path.Combine(dir, "settings.db");

        var storage = TempStorage(dir);
        if (storage is null) return;
        try
        {
            Check("临时库初始化成功", storage.Initialize());
            var settings = NewSettings(dir, storage);
            if (settings is null) return;

            settings.OpenAI.OpenAINodes.Add(new Setting.OpenAINodeSetting { Name = "o1", ApiKey = "sk-o1" });
            settings.Gemini.GeminiNodes.Add(new Setting.GeminiNodeSetting { Name = "g1", ApiKey = "sk-g1" });
            settings.Tools.Add(new Setting.ToolSetting { Name = "t1", ApiKey = "sk-t1" });
            settings.ModelCache.Cache["c1"] = new Setting.ModelCacheEntry
            {
                // 读回时 DateTime.Parse 一律转成本地时间：UTC 时间会在重启后的第一次保存里被改写一次成本地格式（之后就稳定了），
                // 这里用本地时间，好让"读回来原样再存一行不写"能严格断言
                CacheKey = "c1", Models = new List<string> { "m1" }, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
            };
            settings.ModelCache.ChannelCacheLinks["OpenAI.o1"] = "c1";
            settings.FallbackProviders = new List<Setting.ProviderFallbackConfig>
            {
                new() { ProviderType = "OpenAI" }, new() { ProviderType = "Gemini", Priority = 1 }
            };
            settings.Save();
            Check("前提：节点、工具、模型缓存都落库了",
                  Scalar(db, "SELECT COUNT(*) FROM provider_nodes") == "2" && Scalar(db, "SELECT COUNT(*) FROM plugin_data WHERE type = 'Tool'") == "1"
                  && Scalar(db, "SELECT COUNT(*) FROM model_cache") == "1" && Scalar(db, "SELECT COUNT(*) FROM channel_cache_links") == "1");

            using var observer = Observe(db);
            var version = DataVersion(observer);
            settings.Save();
            settings.Save();
            Check("再存两次：库里没有任何提交", DataVersion(observer) == version);

            var secrets = Secrets(db);
            ForgetCiphers();
            var reloaded = NewSettings(dir, storage);
            if (reloaded is null) return;
            // 构造时带默认项的列表：读设置时要整体换成库里的，追加的话这个哨兵会留下来
            reloaded.FallbackProviders = new List<Setting.ProviderFallbackConfig> { new() { ProviderType = "sentinel" } };
            typeof(Setting).GetMethod("LoadSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reloaded, null);
            Check("读回来的节点是明文", reloaded.OpenAI.OpenAINodes.Single().ApiKey == "sk-o1");
            Check("读回来列表整体替换、不叠加", reloaded.FallbackProviders.Select(p => p.ProviderType).SequenceEqual(new[] { "OpenAI", "Gemini" })
                  && reloaded.FloatingSidebar.EnabledButtons.SequenceEqual(settings.FloatingSidebar.EnabledButtons),
                  string.Join(",", reloaded.FallbackProviders.Select(p => p.ProviderType)));
            var before = Rows(db);
            version = DataVersion(observer);
            reloaded.Save();
            var after = Rows(db);
            var rewritten = after.Where(kv => !before.TryGetValue(kv.Key, out var v) || v != kv.Value).Select(kv => kv.Key).ToList();
            Check("换个进程读回来原样再存：库里没有任何提交", DataVersion(observer) == version, string.Join(", ", rewritten));
            Check("节点和工具的密文原样不动", Secrets(db) == secrets, Secrets(db));

            var changes = TotalChanges(storage);
            reloaded.OpenAI.OpenAINodes[0].Model = "changed";
            reloaded.Save();
            Check("改了一个节点：只写这一行", TotalChanges(storage) - changes == 1, $"{TotalChanges(storage) - changes} 行");
            Check("改动确实写进去了", Scalar(db, "SELECT node_data FROM provider_nodes ORDER BY id LIMIT 1")?.Contains("changed") == true);

            reloaded.Tools[0].Url = "changed";
            reloaded.Save();
            Check("改了工具：照样写进去", Scalar(db, "SELECT config_data FROM plugin_data WHERE type = 'Tool'")?.Contains("changed") == true);

            changes = TotalChanges(storage);
            reloaded.ModelCache.Cache["c1"].Models.Add("m2");
            reloaded.Save();
            Check("改了模型缓存：照样写进去", Scalar(db, "SELECT models_json FROM model_cache")?.Contains("m2") == true && TotalChanges(storage) > changes);

            reloaded.ModelCache.ChannelCacheLinks["Gemini.g1"] = "c1";
            reloaded.Save();
            Check("改了缓存链接：照样写进去", Scalar(db, "SELECT COUNT(*) FROM channel_cache_links") == "2");

            // 设置表本身没变、只改了节点/工具/模型缓存：这些写入之前同样要按间隔备份
            void BacksUpFirst(string what, Action change, string sql, string old)
            {
                foreach (var (file, i) in Files(dir).OrderByDescending(f => f).Select((f, i) => (f, i)))
                    Age(file, BackupManager.MinBackupInterval + TimeSpan.FromMinutes(1 + i));
                var before = Files(dir).ToHashSet();
                change();
                reloaded.Save();
                var newest = new BackupManager(db).GetMostRecentBackup()!;
                var after = FilesWhen(dir, f => f.Except(before).Count() == 1);
                var inBackup = Scalar(newest, sql);
                Check($"只改了{what}：写之前先备份", after.Except(before).Count() == 1 && inBackup == old,
                    $"之前 {Names(before)}，之后 {Names(after)}；最新 {Path.GetFileName(newest)} 里是 {inBackup}");
            }
            BacksUpFirst("节点", () => reloaded.OpenAI.OpenAINodes[0].Model = "m-backup",
                "SELECT COUNT(*) FROM provider_nodes WHERE node_data LIKE '%m-backup%'", "0");
            BacksUpFirst("工具", () => reloaded.Tools[0].Url = "u-backup",
                "SELECT COUNT(*) FROM plugin_data WHERE config_data LIKE '%u-backup%'", "0");
            BacksUpFirst("模型缓存", () => reloaded.ModelCache.Cache["c1"].Models.Add("m-backup"),
                "SELECT COUNT(*) FROM model_cache WHERE models_json LIKE '%m-backup%'", "0");

            // 旧版每次启动都往 EnabledButtons 里追加一份默认按钮，库里已经叠出来的要在读的时候去掉
            reloaded.FloatingSidebar.EnabledButtons = new List<string> { "settings", "asr", "settings", "asr", "settings" };
            reloaded.Save();
            var deduped = NewSettings(dir, storage);
            if (deduped is null) return;
            typeof(Setting).GetMethod("LoadSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(deduped, null);
            Check("叠出来的重复按钮读回时去重", deduped.FloatingSidebar.EnabledButtons.SequenceEqual(new[] { "settings", "asr" }),
                  string.Join(",", deduped.FloatingSidebar.EnabledButtons));
        }
        finally
        {
            storage.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }
}
