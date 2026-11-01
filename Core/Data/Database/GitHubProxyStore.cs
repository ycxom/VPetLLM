using Microsoft.Data.Sqlite;
using VPetLLM.Utils.Data;

namespace VPetLLM.Core.Data.Database
{
    public sealed record GitHubProxyRecord(
        string Url, long? LatencyMs, bool IsAvailable, string? FailureReason, string? LastProbedAt);

    /// <summary>
    /// 云端 GitHub 代理列表和最近一次测速结果，放在 settings.db。
    ///
    /// 这张表只放列表里的地址：直连和用户自己填的代理不进库，否则列表删地址时
    /// 就分不清哪些该清。为此 <see cref="RecordProbe"/> 只更新不插入，
    /// 探测跑到一半地址被列表删掉，也不会被探测结果写回来。
    /// </summary>
    public sealed class GitHubProxyStore
    {
        private readonly string _dbPath;

        public GitHubProxyStore(string dbPath)
        {
            _dbPath = dbPath;
        }

        private SqliteConnection Open()
        {
            var directory = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var connection = SQLiteHelper.OpenConnection(_dbPath);
            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS GitHubProxies (
                    Url TEXT PRIMARY KEY,
                    LatencyMs INTEGER,
                    IsAvailable INTEGER NOT NULL DEFAULT 0,
                    FailureReason TEXT,
                    LastProbedAt TEXT
                )";
            command.ExecuteNonQuery();
            return connection;
        }

        public IReadOnlyList<GitHubProxyRecord> GetAll()
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Url, LatencyMs, IsAvailable, FailureReason, LastProbedAt FROM GitHubProxies";

            var records = new List<GitHubProxyRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                records.Add(new GitHubProxyRecord(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.GetInt64(2) != 0,
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
            return records;
        }

        /// <summary>让表和云端列表一致：新地址插入，已有地址的测速结果保留，不在列表里的删掉。</summary>
        public void Sync(IReadOnlyCollection<string> urls)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();

            var stale = new List<string>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT Url FROM GitHubProxies";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var url = reader.GetString(0);
                    if (!urls.Contains(url)) stale.Add(url);
                }
            }

            foreach (var url in stale)
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM GitHubProxies WHERE Url = $url";
                delete.Parameters.AddWithValue("$url", url);
                delete.ExecuteNonQuery();
            }

            foreach (var url in urls)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT OR IGNORE INTO GitHubProxies (Url) VALUES ($url)";
                insert.Parameters.AddWithValue("$url", url);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public void RecordProbe(string url, long? latencyMs, string? failureReason)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE GitHubProxies
                SET LatencyMs = $latency, IsAvailable = $available, FailureReason = $reason, LastProbedAt = $at
                WHERE Url = $url";
            command.Parameters.AddWithValue("$latency", (object?)latencyMs ?? DBNull.Value);
            command.Parameters.AddWithValue("$available", failureReason is null ? 1 : 0);
            command.Parameters.AddWithValue("$reason", (object?)failureReason ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$url", url);
            command.ExecuteNonQuery();
        }
    }
}
