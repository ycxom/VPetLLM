using Microsoft.Data.Sqlite;
using VPetLLM.Utils.Data;

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
            _connectionString = SQLiteHelper.BuildConnectionString(dbPath);
            InitializeTables();
        }

        private void InitializeTables()
        {
            try
            {
                using var connection = SQLiteHelper.OpenConnection(_dbPath);

                // 步骤1：只建表（含不引用 provider 的索引）。
                // 注意：引用 provider / threshold 的索引不能放这里 —— 旧库的
                // overflow_summaries 没有这些列，在补列之前建索引会抛
                // "no such column: provider" 使整批回滚，后面的迁移也就跑不到。
                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS overflow_summaries (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        provider TEXT NOT NULL DEFAULT '',
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

                // 步骤2：补列（旧库升级路径）。用 PRAGMA 查列后再 ALTER，
                // 比 try/catch 吞 SqliteException 更精确（不会掩盖其它错误）。
                var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA table_info(overflow_summaries)";
                    using var reader = pragma.ExecuteReader();
                    while (reader.Read())
                        existingCols.Add(reader.GetString(1));   // 1 = name
                }

                void AddColumn(string name, string definition)
                {
                    if (existingCols.Contains(name)) return;
                    using var alter = connection.CreateCommand();
                    alter.CommandText = $"ALTER TABLE overflow_summaries ADD COLUMN {name} {definition}";
                    alter.ExecuteNonQuery();
                    Logger.Log($"overflow_summaries: 已添加列 {name}");
                }

                AddColumn("threshold", "INTEGER NOT NULL DEFAULT 0");
                // 旧数据 provider 为 ''，与"不按提供商分开聊天"模式的键一致
                AddColumn("provider", "TEXT NOT NULL DEFAULT ''");
                // 旧行都是替换制滚动总结（每行是前一行的超集），默认值正好把它们标成 rolling
                AddColumn("kind", $"TEXT NOT NULL DEFAULT '{OverflowSummaryKind.Rolling}'");
                AddColumn("superseded", "INTEGER NOT NULL DEFAULT 0");

                // 步骤3：补列之后再建引用这些列的索引
                using (var idxCmd = connection.CreateCommand())
                {
                    idxCmd.CommandText = @"
                        CREATE INDEX IF NOT EXISTS idx_overflow_summaries_provider
                            ON overflow_summaries(provider);";
                    idxCmd.ExecuteNonQuery();
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
        public int CreateSummary(string provider, string summaryText, int segmentStartIndex, int segmentEndIndex, int tokenCount,
            int threshold = 0, string kind = OverflowSummaryKind.Segment)
        {
            try
            {
                using var connection = SQLiteHelper.OpenConnection(_dbPath);
                var id = InsertSummary(connection, provider, summaryText, segmentStartIndex, segmentEndIndex, tokenCount, threshold, kind);
                Logger.Log($"Created overflow summary #{id} ({kind}) covering messages [{segmentStartIndex}-{segmentEndIndex}], {tokenCount} tokens");
                return id;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to create overflow summary: {ex.Message}");
                return -1;
            }
        }

        private static int InsertSummary(SqliteConnection connection, string provider, string summaryText,
            int segmentStartIndex, int segmentEndIndex, int tokenCount, int threshold, string kind)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO overflow_summaries (provider, summary_text, segment_start_index, segment_end_index, token_count, threshold, kind, created_at)
                VALUES (@provider, @text, @start, @end, @tokens, @threshold, @kind, @time);
                SELECT last_insert_rowid();
            ";
            cmd.Parameters.AddWithValue("@provider", provider ?? "");
            cmd.Parameters.AddWithValue("@text", summaryText);
            cmd.Parameters.AddWithValue("@start", segmentStartIndex);
            cmd.Parameters.AddWithValue("@end", segmentEndIndex);
            cmd.Parameters.AddWithValue("@tokens", tokenCount);
            cmd.Parameters.AddWithValue("@threshold", threshold);
            cmd.Parameters.AddWithValue("@kind", kind);
            cmd.Parameters.AddWithValue("@time", DateTime.UtcNow);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        /// <summary>
        /// 把几条旧总结合并成一条 consolidated，并把被合并的行标成 superseded。
        /// 同一事务内完成：不会出现「新行已写、旧行还算有效」导致内容注入两遍。
        /// </summary>
        public int ConsolidateSummaries(string provider, string summaryText, int segmentStartIndex, int segmentEndIndex,
            int tokenCount, int threshold, IReadOnlyCollection<int> mergedIds)
        {
            try
            {
                using var connection = SQLiteHelper.OpenConnection(_dbPath);
                using var transaction = connection.BeginTransaction();

                var id = InsertSummary(connection, provider, summaryText, segmentStartIndex, segmentEndIndex,
                    tokenCount, threshold, OverflowSummaryKind.Consolidated);

                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE overflow_summaries SET superseded = 1 WHERE id = @id AND provider = @provider";
                var idParam = update.Parameters.Add("@id", SqliteType.Integer);
                update.Parameters.AddWithValue("@provider", provider ?? "");
                foreach (var mergedId in mergedIds)
                {
                    idParam.Value = mergedId;
                    update.ExecuteNonQuery();
                }

                transaction.Commit();
                Logger.Log($"Consolidated {mergedIds.Count} overflow summaries into #{id} covering [{segmentStartIndex}-{segmentEndIndex}]");
                return id;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to consolidate overflow summaries: {ex.Message}");
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
                using var connection = SQLiteHelper.OpenConnection(_dbPath);

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
        /// Excludes legacy threshold-marker rows.
        /// </summary>
        public List<OverflowSummaryRecord> SearchSummaries(string provider, string keyword, int limit = 10)
        {
            var results = new List<OverflowSummaryRecord>();
            try
            {
                using var connection = SQLiteHelper.OpenConnection(_dbPath);

                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT id, summary_text, segment_start_index, segment_end_index, token_count, threshold, created_at, kind, superseded
                    FROM overflow_summaries
                    WHERE provider = @provider
                      AND summary_text LIKE @kw
                      AND kind <> @empty
                      AND summary_text NOT LIKE '[threshold marker:%'
                    ORDER BY id DESC
                    LIMIT @limit
                ";
                cmd.Parameters.AddWithValue("@provider", provider ?? "");
                cmd.Parameters.AddWithValue("@empty", OverflowSummaryKind.Empty);
                cmd.Parameters.AddWithValue("@kw", $"%{keyword}%");
                cmd.Parameters.AddWithValue("@limit", limit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(ReadSummary(reader));
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to search overflow summaries: {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// 「有效条目」的 WHERE 条件：没被合并掉、不是空片、不是旧 marker，
        /// rolling 只认最新一行。用到它的查询要带 @provider/@rolling/@empty 三个参数。
        /// </summary>
        private const string ActiveFilter = @"
                      superseded = 0
                      AND kind <> @empty
                      AND summary_text NOT LIKE '[threshold marker:%'
                      AND (kind <> @rolling OR id = (
                          SELECT MAX(id) FROM overflow_summaries
                          WHERE provider = @provider AND kind = @rolling
                            AND summary_text NOT LIKE '[threshold marker:%'))";

        /// <summary>编辑器「显示已合并/空片」打开时的条件：只滤掉旧 marker。</summary>
        private const string AnyFilter = "summary_text NOT LIKE '[threshold marker:%'";

        private static void AddFilterParameters(SqliteCommand cmd, string provider)
        {
            cmd.Parameters.AddWithValue("@provider", provider ?? "");
            cmd.Parameters.AddWithValue("@rolling", OverflowSummaryKind.Rolling);
            cmd.Parameters.AddWithValue("@empty", OverflowSummaryKind.Empty);
        }

        /// <summary>
        /// Count summaries for a provider. Excludes legacy threshold-marker rows,
        /// 与 <see cref="GetSummariesPage"/> 用同一套过滤条件，页数才算得准。
        /// 默认只数有效条目（和注入 prompt 的是同一批）；includeInactive 时连已合并行、空片一起数。
        /// </summary>
        public int GetSummaryCount(string provider, bool includeInactive = false)
        {
            try
            {
                using var connection = SQLiteHelper.OpenConnection(_dbPath);

                var cmd = connection.CreateCommand();
                cmd.CommandText = $@"
                    SELECT COUNT(*)
                    FROM overflow_summaries
                    WHERE provider = @provider
                      AND {(includeInactive ? AnyFilter : ActiveFilter)}
                ";
                AddFilterParameters(cmd, provider);
                return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to count overflow summaries: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Get one page of summaries, newest first.
        /// </summary>
        public List<OverflowSummaryRecord> GetSummariesPage(string provider, int offset, int limit, bool includeInactive = false)
        {
            var results = new List<OverflowSummaryRecord>();
            if (limit <= 0) return results;

            try
            {
                using var connection = SQLiteHelper.OpenConnection(_dbPath);

                var cmd = connection.CreateCommand();
                cmd.CommandText = $@"
                    SELECT id, summary_text, segment_start_index, segment_end_index, token_count, threshold, created_at, kind, superseded
                    FROM overflow_summaries
                    WHERE provider = @provider
                      AND {(includeInactive ? AnyFilter : ActiveFilter)}
                    ORDER BY id DESC
                    LIMIT @limit OFFSET @offset
                ";
                AddFilterParameters(cmd, provider);
                cmd.Parameters.AddWithValue("@limit", limit);
                cmd.Parameters.AddWithValue("@offset", Math.Max(0, offset));

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(ReadSummary(reader));
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get overflow summaries page: {ex.Message}");
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
                using var connection = SQLiteHelper.OpenConnection(_dbPath);

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
        /// Update the text of an existing summary (user edit from the record editor).
        /// </summary>
        public void UpdateSummaryText(int summaryId, string summaryText)
        {
            try
            {
                using var connection = SQLiteHelper.OpenConnection(_dbPath);

                var cmd = connection.CreateCommand();
                cmd.CommandText = "UPDATE overflow_summaries SET summary_text = @text WHERE id = @id";
                cmd.Parameters.AddWithValue("@text", summaryText ?? "");
                cmd.Parameters.AddWithValue("@id", summaryId);
                cmd.ExecuteNonQuery();
                Logger.Log($"Updated overflow summary #{summaryId} text");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to update summary #{summaryId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Delete a summary and its associated segments by ID.
        /// </summary>
        public void DeleteSummary(int summaryId)
        {
            try
            {
                using var connection = SQLiteHelper.OpenConnection(_dbPath);

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
        /// Get the total count of overflowed tokens across a provider's summaries.
        /// </summary>
        public int GetTotalOverflowedTokens(string provider)
        {
            try
            {
                using var connection = SQLiteHelper.OpenConnection(_dbPath);

                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(SUM(token_count), 0) FROM overflow_summaries WHERE provider = @provider";
                cmd.Parameters.AddWithValue("@provider", provider ?? "");
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get total overflowed tokens: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Get the maximum segment_end_index for a provider (for restoring _lastSummarizedIndex).
        /// </summary>
        public int GetMaxSegmentEndIndex(string provider)
        {
            try
            {
                using var connection = SQLiteHelper.OpenConnection(_dbPath);
                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT COALESCE(MAX(segment_end_index), 0) FROM overflow_summaries
                    WHERE provider = @provider
                      AND summary_text NOT LIKE '[threshold marker:%'";
                cmd.Parameters.AddWithValue("@provider", provider ?? "");
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get max segment end index: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// 已总结到哪里（不含）：恢复检查点用。
        ///
        /// segment_end_index 存的是**含**的末条下标，直接拿来当检查点会差一位——
        /// 每次重启都把上一片最后一条再总结一遍，它同时还留在滑动窗口里。
        /// 真实库 732 对相邻行里有 74 对因此重叠一条。
        /// </summary>
        public int GetSummarizedEndExclusive(string provider)
        {
            try
            {
                using var connection = SQLiteHelper.OpenConnection(_dbPath);
                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT COALESCE(MAX(segment_end_index) + 1, 0) FROM overflow_summaries
                    WHERE provider = @provider
                      AND summary_text NOT LIKE '[threshold marker:%'";
                cmd.Parameters.AddWithValue("@provider", provider ?? "");
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get summarized end index: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// 当前有效的总结条目，按覆盖的消息先后排列。
        ///
        /// 有效 = 没被合并掉的 segment / consolidated，加上**最新一条** rolling。
        /// rolling 是旧版替换制留下的行，每行都是前一行的超集，只有最新那条算数；
        /// 把它当作最早的一条底稿，后面接新的分段。
        /// </summary>
        public List<OverflowSummaryRecord> GetActiveSummaries(string provider)
        {
            var results = new List<OverflowSummaryRecord>();
            try
            {
                using var connection = SQLiteHelper.OpenConnection(_dbPath);
                var cmd = connection.CreateCommand();
                cmd.CommandText = $@"
                    SELECT id, summary_text, segment_start_index, segment_end_index, token_count, threshold, created_at, kind, superseded
                    FROM overflow_summaries
                    WHERE provider = @provider
                      AND {ActiveFilter}
                    ORDER BY segment_start_index ASC, id ASC";
                AddFilterParameters(cmd, provider);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    results.Add(ReadSummary(reader));
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get active overflow summaries: {ex.Message}");
            }
            return results;
        }

        /// <summary>按 id, summary_text, start, end, tokens, threshold, created_at, kind, superseded 的列序读一行。</summary>
        private static OverflowSummaryRecord ReadSummary(SqliteDataReader reader) => new()
        {
            Id = reader.GetInt32(0),
            SummaryText = reader.GetString(1),
            SegmentStartIndex = reader.GetInt32(2),
            SegmentEndIndex = reader.GetInt32(3),
            TokenCount = reader.GetInt32(4),
            Threshold = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            CreatedAt = reader.GetDateTime(6),
            Kind = reader.IsDBNull(7) ? OverflowSummaryKind.Rolling : reader.GetString(7),
            Superseded = !reader.IsDBNull(8) && reader.GetInt32(8) != 0
        };

        /// <summary>
        /// Clear all overflow data for a provider.
        /// </summary>
        public void ClearAll(string provider)
        {
            try
            {
                using var connection = SQLiteHelper.OpenConnection(_dbPath);

                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    DELETE FROM overflow_segments WHERE summary_id IN
                        (SELECT id FROM overflow_summaries WHERE provider = @provider);
                    DELETE FROM overflow_summaries WHERE provider = @provider;";
                cmd.Parameters.AddWithValue("@provider", provider ?? "");
                cmd.ExecuteNonQuery();
                Logger.Log($"Cleared all overflow data for provider '{provider}'");
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
        public string Kind { get; set; } = OverflowSummaryKind.Rolling;

        /// <summary>已被合并进某条 consolidated，不再注入 prompt（行留着供编辑器查看）。</summary>
        public bool Superseded { get; set; }
    }

    /// <summary>overflow_summaries.kind 的取值。</summary>
    public static class OverflowSummaryKind
    {
        /// <summary>旧版替换制：每行是前一行的超集，只有最新一行有效。</summary>
        public const string Rolling = "rolling";

        /// <summary>只总结了自己那一片消息，与其它分段互不包含。</summary>
        public const string Segment = "segment";

        /// <summary>若干条最旧的分段合并压缩而成。</summary>
        public const string Consolidated = "consolidated";

        /// <summary>
        /// 这一片总结过了但没有新内容（历史全空、模型返回空、或全是已知事实）。
        /// 文本为空、不注入，只用来让检查点在重启后也越过这一片——否则每次重启都要再总结它。
        /// </summary>
        public const string Empty = "empty";
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
