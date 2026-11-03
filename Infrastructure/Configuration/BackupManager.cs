using Microsoft.Data.Sqlite;

namespace VPetLLM.Infrastructure.Configuration;

/// <summary>
/// Manages database backup and restoration
/// </summary>
public class BackupManager
{
    private readonly string _databasePath;
    private readonly string _backupDirectory;
    private const int MaxBackups = 5;

    // 保存约 2 分钟一次，每次都备份的话 5 份只覆盖 10 分钟，库坏了好备份也早被挤掉。
    // 代价是恢复时最多丢这么久的设置改动。
    public static readonly TimeSpan MinBackupInterval = TimeSpan.FromHours(6);

    public BackupManager(string databasePath)
    {
        _databasePath = databasePath;
        _backupDirectory = Path.Combine(Path.GetDirectoryName(databasePath) ?? "", "backups");
    }

    /// <summary>
    /// 保存前调用：距离上一份备份够久才备份
    /// </summary>
    public void BackupIfDue()
    {
        var latest = GetMostRecentBackup();
        if (latest is not null && DateTime.Now - File.GetCreationTime(latest) < MinBackupInterval)
        {
            return;
        }

        CreateBackup();
    }

    /// <summary>
    /// Create a backup of the database
    /// </summary>
    /// <returns>Path to the created backup file</returns>
    public string CreateBackup()
    {
        try
        {
            // Create backup directory if it doesn't exist
            if (!Directory.Exists(_backupDirectory))
            {
                Directory.CreateDirectory(_backupDirectory);
            }

            // Check if database file exists
            if (!File.Exists(_databasePath))
            {
                return string.Empty;
            }

            // Generate backup filename with timestamp
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupFileName = $"{Path.GetFileNameWithoutExtension(_databasePath)}_backup_{timestamp}.db";
            var backupPath = Path.Combine(_backupDirectory, backupFileName);

            // 先写临时文件，校验过才改成 .db：轮换和恢复只认 *.db。
            // 名字带随机段：刚删掉的同名文件被别的进程开着时处于删除挂起，同名新建会被拒绝
            var tempPath = $"{backupPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                WriteSnapshot(tempPath);
                RetryOnSharingViolation(() => File.Move(tempPath, backupPath, overwrite: true));
            }
            finally
            {
                // 失败时这里再抛会盖掉真正的原因
                try
                {
                    RetryOnSharingViolation(() => File.Delete(tempPath));
                }
                catch (IOException ex)
                {
                    Logger.Log($"Failed to delete backup temp file: {ex.Message}");
                }
            }

            // Rotate old backups
            RotateBackups();

            return backupPath;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to create backup: {ex.Message}");
            throw new StorageException("Failed to create database backup", ex);
        }
    }

    /// <summary>
    /// 写一份一致的快照：主库开着 WAL，直接复制文件拿不到还没检查点的数据，赶上检查点还会撕裂。
    /// BackupDatabase 只原样搬页不校验，不查的话坏页会抄进备份，把好备份挤掉
    /// </summary>
    private void WriteSnapshot(string path)
    {
        // 临时文件旁一个伴生文件（-journal/-wal/-shm）都不建：刚删的文件赶上杀毒软件正扫着会删不掉，
        // 留在备份目录里。拷的过程不要回滚日志，拷坏了整份丢掉就是
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString()))
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString()))
        {
            source.Open();
            target.Open();
            using (var off = target.CreateCommand())
            {
                off.CommandText = "PRAGMA journal_mode=OFF;";
                off.ExecuteNonQuery();
            }
            source.BackupDatabase(target);
        }

        // 主库的 WAL 标记（头部第 18、19 字节为 2）会原样抄过来，再打开就会建 -wal/-shm。
        // 拷出来的文件没有 WAL 内容，直接改回 1（回滚模式）；恢复后主库打开时会重新切到 WAL
        RetryOnSharingViolation(() =>
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (file.Length < 100)
                return;
            file.Position = 18;
            file.Write([1, 1]);
        });

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA quick_check(1);";
        var result = check.ExecuteScalar()?.ToString();
        if (result != "ok")
        {
            throw new StorageException($"Database failed integrity check: {result}");
        }
    }

    /// <summary>
    /// 刚写完的文件常被杀毒软件、索引服务短暂占着（共享冲突），SQLite 自己也是这样重试的
    /// </summary>
    private static void RetryOnSharingViolation(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException ex) when (attempt < 10 && (ex.HResult & 0xFFFF) is 32 or 33)
            {
                Thread.Sleep(25 * attempt);
            }
        }
    }

    /// <summary>
    /// Restore database from a backup file
    /// </summary>
    /// <param name="backupPath">Path to the backup file</param>
    /// <returns>True if restoration succeeded</returns>
    public bool RestoreFromBackup(string backupPath)
    {
        try
        {
            // Validate backup file exists
            if (!File.Exists(backupPath))
            {
                return false;
            }

            // 连接 Dispose 后只是还回池里，文件句柄还开着，不清的话下面换掉的文件读到的还是旧页
            SqliteConnection.ClearPool(new SqliteConnection(
                global::VPetLLM.Utils.Data.SQLiteHelper.BuildConnectionString(_databasePath)));

            // Create a backup of current database before restoring
            var wal = _databasePath + "-wal";
            if (File.Exists(_databasePath))
            {
                var emergencyBackup = _databasePath + ".emergency";
                File.Copy(_databasePath, emergencyBackup, overwrite: true);
                File.Delete(emergencyBackup + "-wal");
                if (File.Exists(wal))
                {
                    File.Copy(wal, emergencyBackup + "-wal");
                }
            }

            // 必须先删：WAL 不绑定主库，留着会回放到恢复后的库上，等于没恢复
            File.Delete(wal);
            File.Delete(_databasePath + "-shm");

            // Copy backup file to database location
            File.Copy(backupPath, _databasePath, overwrite: true);

            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to restore from backup: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 删除全部数据库备份（逐个零覆盖再删）。
    /// 备份是保存前对整库的快照，加密改造之前产生的备份里 API Key 是明文，
    /// 不清掉的话要等好几天才会被轮换掉。清理后下一次保存会产生新的（密文）备份
    /// </summary>
    /// <returns>删除的备份数量</returns>
    public int PurgeAllBackups()
    {
        var purged = 0;
        try
        {
            // 恢复前留下的应急副本同样是整库，加密改造前的那份里也是明文
            var files = new List<string> { _databasePath + ".emergency", _databasePath + ".emergency-wal" };

            // 比轮换多认 *.db.tmp：写到一半崩溃留下的临时文件内容一样是整库
            if (Directory.Exists(_backupDirectory))
                files.AddRange(Directory.GetFiles(_backupDirectory, "*.db*"));

            foreach (var file in files.Where(File.Exists))
            {
                try
                {
                    SecretProtector.ShredFile(file);
                    purged++;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to purge backup {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to purge backups: {ex.Message}");
        }
        return purged;
    }

    /// <summary>
    /// Rotate backups, keeping only the most recent MaxBackups files
    /// </summary>
    private void RotateBackups()
    {
        try
        {
            if (!Directory.Exists(_backupDirectory))
            {
                return;
            }

            // Get all backup files sorted by creation time (oldest first)
            var backupFiles = Directory.GetFiles(_backupDirectory, "*.db")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.CreationTime)
                .ToList();

            // Delete oldest backups if we exceed the limit
            while (backupFiles.Count > MaxBackups)
            {
                var oldestBackup = backupFiles[0];
                oldestBackup.Delete();
                backupFiles.RemoveAt(0);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to rotate backups: {ex.Message}");
            // Non-critical error, don't throw
        }
    }

    /// <summary>
    /// Get the most recent backup file
    /// </summary>
    /// <returns>Path to the most recent backup, or null if none exist</returns>
    public string? GetMostRecentBackup()
    {
        try
        {
            if (!Directory.Exists(_backupDirectory))
            {
                return null;
            }

            var backupFiles = Directory.GetFiles(_backupDirectory, "*.db")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .ToList();

            return backupFiles.FirstOrDefault()?.FullName;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to get most recent backup: {ex.Message}");
            return null;
        }
    }
}
