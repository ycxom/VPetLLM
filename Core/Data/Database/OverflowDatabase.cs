using Microsoft.Data.Sqlite;

namespace VPetLLM.Core.Data.Database
{
    /// <summary>
    /// Database manager for overflow segments and summaries.
    /// Stores metadata about messages evicted from the prompt window,
    /// and the summaries generated from them.
    /// </summary>
    public class OverflowDatabase : IDisposable
    {
        private readonly string _connectionString;
        private readonly string _dbPath;

        public OverflowDatabase(string dbPath)
        {
            _dbPath = dbPath;
            _connectionString = $"Data Source={dbPath}";
            InitializeTables();
        }

        private void InitializeTables()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS overflow_summaries (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        summary_text TEXT NOT NULL,
                        segment_start_index INTEGER NOT NULL,
                        segment_end_index INTEGER NOT NULL,
                        token_count INTEGER NOT NULL DEFAULT 0,
                        threshold INTEGER NOT NULL DEFAULT 0,
                        created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                    );

                    CREATE INDEX IF NOT EXISTS idx_overflow_summaries_created
                        ON overflow_summaries(created_at);

                    CREATE TABLE IF NOT EXISTS overflow_segments (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        summary_id INTEGER,
                        content_hash TEXT NOT NULL,
                        message_index INTEGER NOT NULL,
                        role TEXT,
                        content_preview TEXT,
                        token_count INTEGER NOT NULL DEFAULT 0,
                        created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                        FOREIGN KEY (summary_id) REFERENCES overflow_summaries(id)
                    );

                    CREATE INDEX IF NOT EXISTS idx_overflow_segments_summary
                        ON overflow_segments(summary_id);
                    CREATE INDEX IF NOT EXISTS idx_overflow_segments_hash
                        ON overflow_segments(content_hash);
                ";
                cmd.ExecuteNonQuery();

                // 兼容旧表：尝试添加 threshold 列（已存在则忽略）
                try
                {
                    var alterCmd = connection.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE overflow_summaries ADD COLUMN threshold INTEGER NOT NULL DEFAULT 0";
                    alterCmd.ExecuteNonQuery();
                }
                catch (SqliteException)
                {
                    // 列已存在，忽略
                }

                Logger.Log("Overflow database tables initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to initialize overflow tables: {ex.Message}");
            }
        }

        /// <summary>
        /// Create an overflow summary record.
        /// </summary>
        public int CreateSummary(string summaryText, int segmentStartIndex, int segmentEndIndex, int tokenCount, int threshold = 0)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO overflow_summaries (summary_text, segment_start_index, segment_end_index, token_count, threshold, created_at)
                    VALUES (@text, @start, @end, @tokens, @threshold, @time);
                    SELECT last_insert_rowid();
                ";
                cmd.Parameters.AddWithValue("@text", summaryText);
                cmd.Parameters.AddWithValue("@start", segmentStartIndex);
                cmd.Parameters.AddWithValue("@end", segmentEndIndex);
                cmd.Parameters.AddWithValue("@tokens", tokenCount);
                cmd.Parameters.AddWithValue("@threshold", threshold);
                cmd.Parameters.AddWithValue("@time", DateTime.UtcNow);

                var id = Convert.ToInt32(cmd.ExecuteScalar());
                Logger.Log($"Created overflow summary #{id} covering messages [{segmentStartIndex}-{segmentEndIndex}], {tokenCount} tokens");
                return id;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to create overflow summary: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Add overflow segment records linked to a summary.
        /// </summary>
        public void AddSegments(int summaryId, List<OverflowSegmentData> segments)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                using var transaction = connection.BeginTransaction();
                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO overflow_segments (summary_id, content_hash, message_index, role, content_preview, token_count, created_at)
                    VALUES (@sid, @hash, @idx, @role, @preview, @tokens, @time);
                ";
                var sidParam = cmd.Parameters.Add("@sid", SqliteType.Integer);
                var hashParam = cmd.Parameters.Add("@hash", SqliteType.Text);
                var idxParam = cmd.Parameters.Add("@idx", SqliteType.Integer);
                var roleParam = cmd.Parameters.Add("@role", SqliteType.Text);
                var previewParam = cmd.Parameters.Add("@preview", SqliteType.Text);
                var tokensParam = cmd.Parameters.Add("@tokens", SqliteType.Integer);
                var timeParam = cmd.Parameters.Add("@time", SqliteType.Text);

                foreach (var seg in segments)
                {
                    sidParam.Value = summaryId;
                    hashParam.Value = seg.ContentHash ?? "";
                    idxParam.Value = seg.MessageIndex;
                    roleParam.Value = (object?)seg.Role ?? DBNull.Value;
                    previewParam.Value = (object?)seg.ContentPreview ?? DBNull.Value;
                    tokensParam.Value = seg.TokenCount;
                    timeParam.Value = DateTime.UtcNow.ToString("o");
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
                Logger.Log($"Added {segments.Count} overflow segments for summary #{summaryId}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to add overflow segments: {ex.Message}");
            }
        }

        /// <summary>
        /// Search overflow summaries by keyword (searches summary_text).
        /// </summary>
        public List<OverflowSummaryRecord> SearchSummaries(string keyword, int limit = 10)
        {
            var results = new List<OverflowSummaryRecord>();
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT id, summary_text, segment_start_index, segment_end_index, token_count, threshold, created_at
                    FROM overflow_summaries
                    WHERE summary_text LIKE @kw
                    ORDER BY created_at DESC
                    LIMIT @limit
                ";
                cmd.Parameters.AddWithValue("@kw", $"%{keyword}%");
                cmd.Parameters.AddWithValue("@limit", limit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new OverflowSummaryRecord
                    {
                        Id = reader.GetInt32(0),
                        SummaryText = reader.GetString(1),
                        SegmentStartIndex = reader.GetInt32(2),
                        SegmentEndIndex = reader.GetInt32(3),
                        TokenCount = reader.GetInt32(4),
                        Threshold = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                        CreatedAt = reader.GetDateTime(6)
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to search overflow summaries: {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// Get segments for a specific summary.
        /// </summary>
        public List<OverflowSegmentRecord> GetSegmentsForSummary(int summaryId)
        {
            var results = new List<OverflowSegmentRecord>();
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT id, summary_id, content_hash, message_index, role, content_preview, token_count, created_at
                    FROM overflow_segments
                    WHERE summary_id = @sid
                    ORDER BY message_index ASC
                ";
                cmd.Parameters.AddWithValue("@sid", summaryId);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new OverflowSegmentRecord
                    {
                        Id = reader.GetInt32(0),
                        SummaryId = reader.GetInt32(1),
                        ContentHash = reader.GetString(2),
                        MessageIndex = reader.GetInt32(3),
                        Role = reader.IsDBNull(4) ? null : reader.GetString(4),
                        ContentPreview = reader.IsDBNull(5) ? null : reader.GetString(5),
                        TokenCount = reader.GetInt32(6),
                        CreatedAt = reader.GetDateTime(7)
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get segments for summary #{summaryId}: {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// Delete a summary and its associated segments by ID.
        /// </summary>
        public void DeleteSummary(int summaryId)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                using var transaction = connection.BeginTransaction();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM overflow_segments WHERE summary_id = @id";
                cmd.Parameters.AddWithValue("@id", summaryId);
                cmd.ExecuteNonQuery();

                cmd.CommandText = "DELETE FROM overflow_summaries WHERE id = @id";
                cmd.ExecuteNonQuery();

                transaction.Commit();
                Logger.Log($"Deleted overflow summary #{summaryId} and its segments");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to delete summary #{summaryId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the total count of overflowed tokens across all summaries.
        /// </summary>
        public int GetTotalOverflowedTokens()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(SUM(token_count), 0) FROM overflow_summaries";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get total overflowed tokens: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Get the maximum segment_end_index across all summaries (for restoring _lastSummarizedIndex).
        /// </summary>
        public int GetMaxSegmentEndIndex()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(MAX(segment_end_index), 0) FROM overflow_summaries";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get max segment end index: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Get the threshold from the most recent summary (for restoring _lastSummarizedThreshold).
        /// </summary>
        public int GetMaxSegmentThreshold()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(threshold, 0) FROM overflow_summaries ORDER BY created_at DESC LIMIT 1";
                var result = cmd.ExecuteScalar();
                return result is not null ? Convert.ToInt32(result) : 0;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get max segment threshold: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Store just the threshold marker (for tracking config changes without creating a summary).
        /// </summary>
        public void StoreThresholdMarker(int threshold)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "INSERT INTO overflow_summaries (summary_text, segment_start_index, segment_end_index, token_count, threshold) VALUES (@text, 0, 0, 0, @th)";
                cmd.Parameters.AddWithValue("@text", $"[threshold marker: {threshold}]");
                cmd.Parameters.AddWithValue("@th", threshold);
                cmd.ExecuteNonQuery();
                Logger.Log($"Stored threshold marker: {threshold}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to store threshold marker: {ex.Message}");
            }
        }

        /// <summary>
        /// Get all summary texts ordered by creation (for restoring _summaryChunks).
        /// </summary>
        public List<string> GetAllSummaryTexts()
        {
            var results = new List<string>();
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT summary_text FROM overflow_summaries ORDER BY created_at ASC";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    results.Add(reader.GetString(0));
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get all summary texts: {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// Clear all overflow data.
        /// </summary>
        public void ClearAll()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM overflow_segments; DELETE FROM overflow_summaries;";
                cmd.ExecuteNonQuery();
                Logger.Log("Cleared all overflow data");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to clear overflow data: {ex.Message}");
            }
        }

        public void Dispose()
        {
            // SQLite connections are auto-closed
        }
    }

    /// <summary>
    /// Data for creating a new overflow segment.
    /// </summary>
    public class OverflowSegmentData
    {
        public string? ContentHash { get; set; }
        public int MessageIndex { get; set; }
        public string? Role { get; set; }
        public string? ContentPreview { get; set; }
        public int TokenCount { get; set; }
    }

    /// <summary>
    /// Record representing an overflow summary row.
    /// </summary>
    public class OverflowSummaryRecord
    {
        public int Id { get; set; }
        public string SummaryText { get; set; } = string.Empty;
        public int SegmentStartIndex { get; set; }
        public int SegmentEndIndex { get; set; }
        public int TokenCount { get; set; }
        public int Threshold { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Record representing an overflow segment row.
    /// </summary>
    public class OverflowSegmentRecord
    {
        public int Id { get; set; }
        public int SummaryId { get; set; }
        public string ContentHash { get; set; } = string.Empty;
        public int MessageIndex { get; set; }
        public string? Role { get; set; }
        public string? ContentPreview { get; set; }
        public int TokenCount { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
