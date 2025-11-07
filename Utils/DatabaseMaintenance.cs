using Microsoft.Data.Sqlite;
using System.IO;

namespace VPetLLM.Utils
{
    /// <summary>
    /// 数据库维护工具类
    /// </summary>
    public static class DatabaseMaintenance
    {
        /// <summary>
        /// 优化数据库（VACUUM）
        /// </summary>
        public static void OptimizeDatabase(string dbPath)
        {
            try
            {
                if (!File.Exists(dbPath))
                {
                    Logger.Log("数据库文件不存在，无需优化");
                    return;
                }

                var connectionString = $"Data Source={dbPath}";
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "VACUUM";
                command.ExecuteNonQuery();

                Logger.Log("数据库优化完成");
            }
            catch (Exception ex)
            {
                Logger.Log($"数据库优化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 备份数据库
        /// </summary>
        public static bool BackupDatabase(string dbPath, string backupPath = null)
        {
            try
            {
                if (!File.Exists(dbPath))
                {
                    Logger.Log("数据库文件不存在，无法备份");
                    return false;
                }

                if (string.IsNullOrEmpty(backupPath))
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    backupPath = dbPath.Replace(".db", $"_backup_{timestamp}.db");
                }

                File.Copy(dbPath, backupPath, true);
                Logger.Log($"数据库备份成功: {backupPath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"数据库备份失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取数据库大小
        /// </summary>
        public static long GetDatabaseSize(string dbPath)
        {
            try
            {
                if (!File.Exists(dbPath))
                {
                    return 0;
                }

                var fileInfo = new FileInfo(dbPath);
                return fileInfo.Length;
            }
            catch (Exception ex)
            {
                Logger.Log($"获取数据库大小失败: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// 获取数据库统计信息
        /// </summary>
        public static DatabaseStats GetDatabaseStats(string dbPath)
        {
            var stats = new DatabaseStats();

            try
            {
                if (!File.Exists(dbPath))
                {
                    return stats;
                }

                var connectionString = $"Data Source={dbPath}";
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                // 获取总消息数
                var countCommand = connection.CreateCommand();
                countCommand.CommandText = "SELECT COUNT(*) FROM chat_history";
                stats.TotalMessages = Convert.ToInt32(countCommand.ExecuteScalar());

                // 获取各提供商的消息数
                var providerCommand = connection.CreateCommand();
                providerCommand.CommandText = @"
                    SELECT provider, COUNT(*) as count
                    FROM chat_history
                    GROUP BY provider
                ";

                using var reader = providerCommand.ExecuteReader();
                while (reader.Read())
                {
                    var provider = reader.GetString(0);
                    var count = reader.GetInt32(1);
                    stats.MessagesByProvider[provider] = count;
                }

                stats.DatabaseSize = GetDatabaseSize(dbPath);
            }
            catch (Exception ex)
            {
                Logger.Log($"获取数据库统计信息失败: {ex.Message}");
            }

            return stats;
        }

        /// <summary>
        /// 清理旧的备份文件
        /// </summary>
        public static void CleanOldBackups(string dbDirectory, int keepCount = 5)
        {
            try
            {
                if (!Directory.Exists(dbDirectory))
                {
                    return;
                }

                var backupFiles = Directory.GetFiles(dbDirectory, "*_backup_*.db")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .ToList();

                if (backupFiles.Count <= keepCount)
                {
                    return;
                }

                var filesToDelete = backupFiles.Skip(keepCount);
                foreach (var file in filesToDelete)
                {
                    try
                    {
                        file.Delete();
                        Logger.Log($"已删除旧备份: {file.Name}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"删除备份文件失败 {file.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"清理旧备份失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 数据库统计信息
    /// </summary>
    public class DatabaseStats
    {
        public int TotalMessages { get; set; }
        public Dictionary<string, int> MessagesByProvider { get; set; } = new Dictionary<string, int>();
        public long DatabaseSize { get; set; }

        public string GetFormattedSize()
        {
            if (DatabaseSize < 1024)
                return $"{DatabaseSize} B";
            else if (DatabaseSize < 1024 * 1024)
                return $"{DatabaseSize / 1024.0:F2} KB";
            else
                return $"{DatabaseSize / (1024.0 * 1024.0):F2} MB";
        }
    }
}
