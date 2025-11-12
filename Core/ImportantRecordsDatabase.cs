using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using VPetLLM.Utils;

namespace VPetLLM.Core
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

                Logger.Log("Important records table initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to initialize important records table: {ex.Message}");
                throw;
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
                command.CommandText = @"
                    SELECT id, content, weight, created_at, updated_at
                    FROM important_records
                    WHERE id = @id
                ";
                command.Parameters.AddWithValue("@id", id);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    return new ImportantRecord
                    {
                        Id = reader.GetInt32(0),
                        Content = reader.GetString(1),
                        Weight = reader.GetDouble(2),
                        CreatedAt = reader.GetDateTime(3),
                        UpdatedAt = reader.GetDateTime(4)
                    };
                }

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
                command.CommandText = @"
                    SELECT id, content, weight, created_at, updated_at
                    FROM important_records
                    WHERE weight > 0
                    ORDER BY weight DESC, created_at DESC
                ";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    records.Add(new ImportantRecord
                    {
                        Id = reader.GetInt32(0),
                        Content = reader.GetString(1),
                        Weight = reader.GetDouble(2),
                        CreatedAt = reader.GetDateTime(3),
                        UpdatedAt = reader.GetDateTime(4)
                    });
                }

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
                if (record == null)
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
                if (record == null)
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
        public int DecrementAllRecords(double decrementAmount = 1.0)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                using var transaction = connection.BeginTransaction();

                // Decrement all weights
                var decrementCommand = connection.CreateCommand();
                decrementCommand.CommandText = @"
                    UPDATE important_records
                    SET weight = weight - @decrement, updated_at = @updated_at
                    WHERE weight > 0
                ";
                decrementCommand.Parameters.AddWithValue("@decrement", decrementAmount);
                decrementCommand.Parameters.AddWithValue("@updated_at", DateTime.UtcNow);
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
                command.CommandText = @"
                    SELECT id, content, weight, created_at, updated_at
                    FROM important_records
                    ORDER BY id DESC
                ";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    records.Add(new ImportantRecord
                    {
                        Id = reader.GetInt32(0),
                        Content = reader.GetString(1),
                        Weight = reader.GetDouble(2),
                        CreatedAt = reader.GetDateTime(3),
                        UpdatedAt = reader.GetDateTime(4)
                    });
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
