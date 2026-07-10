using Microsoft.Data.Sqlite;

namespace VPetLLM.Core.Data.Database
{
    /// <summary>
    /// Database manager for important records, extends existing database functionality
    /// </summary>
    public class ImportantRecordsDatabase : IDisposable
    {
        private readonly string _connectionString;
        private readonly string _dbPath;

        public ImportantRecordsDatabase(string dbPath)
        {
            _dbPath = dbPath;
            _connectionString = $"Data Source={dbPath}";
            InitializeRecordsTable();
        }

        /// <summary>
        /// Initialize the important_records table
        /// </summary>
        private void InitializeRecordsTable()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var createTableCommand = connection.CreateCommand();
                createTableCommand.CommandText = @"
                    CREATE TABLE IF NOT EXISTS important_records (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        content TEXT NOT NULL,
                        weight INTEGER NOT NULL CHECK(weight >= 0 AND weight <= 10),
                        created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                        updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
                    );

                    CREATE INDEX IF NOT EXISTS idx_weight ON important_records(weight);
                    CREATE INDEX IF NOT EXISTS idx_created_at ON important_records(created_at);
                ";
                createTableCommand.ExecuteNonQuery();

                MigrateAccessColumns(connection);

                Logger.Log("Important records table initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to initialize important records table: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 为召回强化补上 access_count / last_access_at 两列（旧库升级路径）。
        /// SQLite 没有 ADD COLUMN IF NOT EXISTS，先查 table_info 再决定是否 ALTER。
        /// </summary>
        private static void MigrateAccessColumns(SqliteConnection connection)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(important_records)";
                using var reader = pragma.ExecuteReader();
                while (reader.Read())
                    existing.Add(reader.GetString(1));   // 1 = name
            }

            void AddColumn(string name, string definition)
            {
                if (existing.Contains(name))
                    return;

                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE important_records ADD COLUMN {name} {definition}";
                alter.ExecuteNonQuery();
                Logger.Log($"important_records: 已添加列 {name}");
            }

            // 累计被检索命中的次数
            AddColumn("access_count", "INTEGER NOT NULL DEFAULT 0");
            // 最后一次被检索命中的时间；NULL 表示从未被召回过
            AddColumn("last_access_at", "DATETIME");
        }

        /// <summary>所有 SELECT 共用的列清单，与 <see cref="ReadRecord"/> 的下标一一对应。</summary>
        private const string RecordColumns = "id, content, weight, created_at, updated_at, access_count, last_access_at";

        private static ImportantRecord ReadRecord(SqliteDataReader reader) => new()
        {
            Id = reader.GetInt32(0),
            Content = reader.GetString(1),
            Weight = reader.GetDouble(2),
            CreatedAt = reader.GetDateTime(3),
            UpdatedAt = reader.GetDateTime(4),
            AccessCount = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            LastAccessAt = reader.IsDBNull(6) ? null : reader.GetDateTime(6)
        };

        /// <summary>
        /// 记录一批记忆被检索命中：累加 access_count、刷新 last_access_at。
        /// 不直接提升 weight —— 强化体现为 <see cref="DecrementAllRecords"/> 里更慢的衰减，
        /// 这样反复召回不会把权重顶到上限后失去区分度。
        /// </summary>
        /// <returns>实际更新的行数。</returns>
        public int RecordAccess(IEnumerable<int> ids)
        {
            var idList = ids?.Distinct().ToList();
            if (idList is null || idList.Count == 0)
                return 0;

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction();

                var updated = 0;
                foreach (var id in idList)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
                        UPDATE important_records
                        SET access_count = MIN(access_count + 1, 1000000),
                            last_access_at = @now
                        WHERE id = @id
                    ";
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow);
                    updated += cmd.ExecuteNonQuery();
                }

                transaction.Commit();
                return updated;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to record access for {idList.Count} records: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Create a new record
        /// </summary>
        /// <param name="content">The content to record</param>
        /// <param name="weight">Initial weight (0-10)</param>
        /// <returns>The ID of the created record, or -1 on failure</returns>
        public int CreateRecord(string content, double weight)
        {
            try
            {
                // Validate parameters
                if (string.IsNullOrWhiteSpace(content))
                {
                    Logger.Log("Cannot create record with empty content");
                    return -1;
                }

                // Clamp weight to valid range
                weight = Math.Clamp(weight, 0, 10);

                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO important_records (content, weight, created_at, updated_at)
                    VALUES (@content, @weight, @created_at, @updated_at);
                    SELECT last_insert_rowid();
                ";

                var now = DateTime.UtcNow;
                command.Parameters.AddWithValue("@content", content);
                command.Parameters.AddWithValue("@weight", weight);
                command.Parameters.AddWithValue("@created_at", now);
                command.Parameters.AddWithValue("@updated_at", now);

                var result = command.ExecuteScalar();
                var recordId = Convert.ToInt32(result);

                Logger.Log($"Created record #{recordId} with weight {weight}");
                return recordId;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to create record: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Get a record by ID
        /// </summary>
        /// <param name="id">The record ID</param>
        /// <returns>The record, or null if not found</returns>
        public ImportantRecord? GetRecord(int id)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = $@"
                    SELECT {RecordColumns}
                    FROM important_records
                    WHERE id = @id
                ";
                command.Parameters.AddWithValue("@id", id);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                    return ReadRecord(reader);

                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get record #{id}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get all active records (weight > 0)
        /// </summary>
        /// <returns>List of active records</returns>
        public List<ImportantRecord> GetActiveRecords()
        {
            var records = new List<ImportantRecord>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = $@"
                    SELECT {RecordColumns}
                    FROM important_records
                    WHERE weight > 0
                    ORDER BY weight DESC, created_at DESC
                ";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                    records.Add(ReadRecord(reader));

                Logger.Log($"Retrieved {records.Count} active records");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get active records: {ex.Message}");
            }

            return records;
        }

        /// <summary>
        /// Update record weight by ID
        /// </summary>
        /// <param name="id">The record ID</param>
        /// <param name="newWeight">The new weight value (0-10)</param>
        /// <returns>True if successful, false otherwise</returns>
        public bool UpdateRecordWeight(int id, double newWeight)
        {
            try
            {
                // Clamp weight to valid range
                newWeight = Math.Clamp(newWeight, 0, 10);

                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    UPDATE important_records
                    SET weight = @weight, updated_at = @updated_at
                    WHERE id = @id
                ";

                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@weight", newWeight);
                command.Parameters.AddWithValue("@updated_at", DateTime.UtcNow);

                var rowsAffected = command.ExecuteNonQuery();

                if (rowsAffected > 0)
                {
                    Logger.Log($"Updated record #{id} weight to {newWeight}");

                    // Delete if weight is 0
                    if (newWeight == 0)
                    {
                        DeleteRecord(id);
                    }

                    return true;
                }
                else
                {
                    Logger.Log($"Record #{id} not found for weight update");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to update record #{id} weight: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Update record content and weight by ID
        /// </summary>
        /// <param name="id">The record ID</param>
        /// <param name="content">The new content</param>
        /// <param name="weight">The new weight value (0-10)</param>
        /// <returns>True if successful, false otherwise</returns>
        public bool UpdateRecord(int id, string content, double weight)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    Logger.Log("Cannot update record with empty content");
                    return false;
                }

                // Clamp weight to valid range
                weight = Math.Clamp(weight, 0, 10);

                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    UPDATE important_records
                    SET content = @content, weight = @weight, updated_at = @updated_at
                    WHERE id = @id
                ";

                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@content", content);
                command.Parameters.AddWithValue("@weight", weight);
                command.Parameters.AddWithValue("@updated_at", DateTime.UtcNow);

                var rowsAffected = command.ExecuteNonQuery();

                if (rowsAffected > 0)
                {
                    Logger.Log($"Updated record #{id} content and weight to {weight}");

                    // Delete if weight is 0
                    if (weight == 0)
                    {
                        DeleteRecord(id);
                    }

                    return true;
                }
                else
                {
                    Logger.Log($"Record #{id} not found for update");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to update record #{id}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Increment record weight by ID (max 10)
        /// </summary>
        /// <param name="id">The record ID</param>
        /// <param name="amount">Amount to increment</param>
        /// <returns>True if successful, false otherwise</returns>
        public bool IncrementRecordWeight(int id, int amount)
        {
            try
            {
                var record = GetRecord(id);
                if (record is null)
                {
                    Logger.Log($"Record #{id} not found for weight increment");
                    return false;
                }

                var newWeight = Math.Clamp(record.Weight + amount, 0, 10);
                return UpdateRecordWeight(id, newWeight);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to increment record #{id} weight: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Decrement record weight by ID (min 0, deletes if reaches 0)
        /// </summary>
        /// <param name="id">The record ID</param>
        /// <param name="amount">Amount to decrement</param>
        /// <returns>True if successful, false otherwise</returns>
        public bool DecrementRecordWeight(int id, int amount)
        {
            try
            {
                var record = GetRecord(id);
                if (record is null)
                {
                    Logger.Log($"Record #{id} not found for weight decrement");
                    return false;
                }

                var newWeight = Math.Clamp(record.Weight - amount, 0, 10);
                return UpdateRecordWeight(id, newWeight);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to decrement record #{id} weight: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Decrement all active records by specified amount, delete those reaching 0
        /// </summary>
        /// <param name="decrementAmount">Amount to decrease weight by (default 1.0)</param>
        /// <returns>Number of records decremented</returns>
        /// <param name="decrementAmount">基础衰减量。</param>
        /// <param name="reinforceOnRecall">
        /// 是否启用召回强化。false 时所有记录以相同速度衰减（本次改动前的行为）。
        /// </param>
        /// <param name="accessWindowDays">
        /// 「最近被召回」的时间窗口。窗口内被召回过的记录额外减半衰减。
        /// </param>
        /// <param name="maxAccessCount">access_count 的饱和点，超过它不再增加抗衰减效果。</param>
        public int DecrementAllRecords(
            double decrementAmount = 1.0,
            bool reinforceOnRecall = true,
            double accessWindowDays = 30.0,
            double maxAccessCount = 10.0)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                using var transaction = connection.BeginTransaction();

                // 召回强化：被检索命中过的记录衰减更慢，最多降到基础衰减的 50%。
                //   effective = decrement * (1 - 0.5 * access_factor * recent_factor)
                //   access_factor = min(1, access_count / maxAccessCount)
                //   recent_factor = 1.0 若最近窗口内被召回过，否则 0.5
                // 从未被召回的记录 access_factor = 0，衰减与改动前完全一致。
                // 必须用 MAX(0.0, ...) 钳住下界：weight 列带 CHECK(weight >= 0)，
                // 一条 weight=0.4 的记录减 1.0 会变成 -0.6 而触发约束失败，
                // 使整条 UPDATE 回滚 —— 所有记录都停止衰减。钳到 0 后由下面的
                // DELETE 负责清理。
                var decrementCommand = connection.CreateCommand();
                decrementCommand.CommandText = reinforceOnRecall
                    ? @"
                    UPDATE important_records
                    SET weight = MAX(0.0, weight - @decrement * (
                            1.0 - 0.5
                                * MIN(1.0, CAST(access_count AS REAL) / @maxAccess)
                                * (CASE WHEN last_access_at IS NOT NULL AND last_access_at >= @window
                                        THEN 1.0 ELSE 0.5 END)
                        )),
                        updated_at = @updated_at
                    WHERE weight > 0
                    "
                    : @"
                    UPDATE important_records
                    SET weight = MAX(0.0, weight - @decrement), updated_at = @updated_at
                    WHERE weight > 0
                    ";
                decrementCommand.Parameters.AddWithValue("@decrement", decrementAmount);
                decrementCommand.Parameters.AddWithValue("@updated_at", DateTime.UtcNow);
                if (reinforceOnRecall)
                {
                    decrementCommand.Parameters.AddWithValue("@maxAccess", Math.Max(1.0, maxAccessCount));
                    decrementCommand.Parameters.AddWithValue("@window", DateTime.UtcNow.AddDays(-Math.Max(1.0, accessWindowDays)));
                }
                var decremented = decrementCommand.ExecuteNonQuery();

                // Delete records with weight 0 or less
                var deleteCommand = connection.CreateCommand();
                deleteCommand.CommandText = "DELETE FROM important_records WHERE weight <= 0";
                var deleted = deleteCommand.ExecuteNonQuery();

                transaction.Commit();

                Logger.Log($"Decremented {decremented} records by {decrementAmount}, deleted {deleted} expired records");
                return decremented;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to decrement all records: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Delete a record by ID
        /// </summary>
        /// <param name="id">The record ID</param>
        /// <returns>True if successful, false otherwise</returns>
        public bool DeleteRecord(int id)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM important_records WHERE id = @id";
                command.Parameters.AddWithValue("@id", id);

                var rowsAffected = command.ExecuteNonQuery();

                if (rowsAffected > 0)
                {
                    Logger.Log($"Deleted record #{id}");
                    return true;
                }
                else
                {
                    Logger.Log($"Record #{id} not found for deletion");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to delete record #{id}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Delete all records with weight = 0
        /// </summary>
        /// <returns>Number of records deleted</returns>
        public int DeleteExpiredRecords()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM important_records WHERE weight <= 0";

                var rowsAffected = command.ExecuteNonQuery();

                Logger.Log($"Deleted {rowsAffected} expired records");
                return rowsAffected;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to delete expired records: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Delete all records (clear all memory)
        /// </summary>
        /// <returns>Number of records deleted</returns>
        public int DeleteAllRecords()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM important_records";

                var rowsAffected = command.ExecuteNonQuery();

                Logger.Log($"Deleted all records, total: {rowsAffected}");
                return rowsAffected;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to delete all records: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Get all records (including those with weight 0)
        /// </summary>
        /// <returns>List of all records</returns>
        public List<ImportantRecord> GetAllRecords()
        {
            var records = new List<ImportantRecord>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = $@"
                    SELECT {RecordColumns}
                    FROM important_records
                    ORDER BY id DESC
                ";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    records.Add(ReadRecord(reader));
                }

                Logger.Log($"Retrieved {records.Count} total records");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get all records: {ex.Message}");
            }

            return records;
        }

        /// <summary>
        /// Enforce maximum records limit by removing records with lowest weight
        /// If weights are equal, older records are removed first
        /// </summary>
        /// <param name="maxLimit">Maximum number of records to keep</param>
        /// <returns>Number of records removed</returns>
        public int EnforceRecordsLimit(int maxLimit)
        {
            try
            {
                if (maxLimit <= 0)
                {
                    Logger.Log("Invalid max limit for records");
                    return 0;
                }

                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                // First, count total records
                var countCommand = connection.CreateCommand();
                countCommand.CommandText = "SELECT COUNT(*) FROM important_records";
                var totalRecords = Convert.ToInt32(countCommand.ExecuteScalar());

                if (totalRecords <= maxLimit)
                {
                    // No need to clean up
                    return 0;
                }

                var recordsToRemove = totalRecords - maxLimit;

                // Delete records with lowest weight, oldest first
                var deleteCommand = connection.CreateCommand();
                deleteCommand.CommandText = @"
                    DELETE FROM important_records
                    WHERE id IN (
                        SELECT id FROM important_records
                        ORDER BY weight ASC, created_at ASC
                        LIMIT @limit
                    )
                ";
                deleteCommand.Parameters.AddWithValue("@limit", recordsToRemove);
                var deleted = deleteCommand.ExecuteNonQuery();

                Logger.Log($"Enforced records limit: removed {deleted} records (limit: {maxLimit}, total was: {totalRecords})");
                return deleted;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to enforce records limit: {ex.Message}");
                return 0;
            }
        }

        public void Dispose()
        {
            // SQLite connections are automatically closed
        }
    }
}
