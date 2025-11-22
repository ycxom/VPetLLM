using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VPetLLM.Utils;

namespace VPetLLM.Core
{
    /// <summary>
    /// Manages the lifecycle and injection of important records
    /// </summary>
    public class RecordManager
    {
        private readonly ImportantRecordsDatabase _database;
        private readonly Setting _settings;
        private readonly string _providerName;

        public RecordManager(Setting settings, string providerName)
        {
            _settings = settings;
            _providerName = providerName;
            _database = new ImportantRecordsDatabase(GetDatabasePath());
        }

        /// <summary>
        /// Create a new record from LLM command
        /// </summary>
        /// <param name="content">The content to record</param>
        /// <param name="weight">Initial weight (0-10)</param>
        /// <returns>The ID of the created record, or -1 on failure</returns>
        public int CreateRecord(string content, int weight)
        {
            try
            {
                // Validate content length
                if (!string.IsNullOrWhiteSpace(content) && 
                    _settings.Records != null && 
                    content.Length > _settings.Records.MaxRecordContentLength)
                {
                    Logger.Log($"Record content exceeds maximum length ({_settings.Records.MaxRecordContentLength}), truncating");
                    content = content.Substring(0, _settings.Records.MaxRecordContentLength);
                }

                var recordId = _database.CreateRecord(content, weight);
                
                if (recordId > 0)
                {
                    Logger.Log($"RecordManager: Created record #{recordId} with weight {weight}");
                    
                    // Enforce records limit after creating new record
                    if (_settings.Records != null && _settings.Records.MaxRecordsLimit > 0)
                    {
                        var removed = _database.EnforceRecordsLimit(_settings.Records.MaxRecordsLimit);
                        if (removed > 0)
                        {
                            Logger.Log($"RecordManager: Enforced limit of {_settings.Records.MaxRecordsLimit} records, removed {removed} old records");
                        }
                    }
                }
                
                return recordId;
            }
            catch (Exception ex)
            {
                Logger.Log($"RecordManager: Failed to create record: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Modify record weight by ID
        /// </summary>
        /// <param name="id">The record ID</param>
        /// <param name="delta">Amount to add (positive) or subtract (negative)</param>
        /// <returns>True if successful, false otherwise</returns>
        public bool ModifyRecordWeight(int id, int delta)
        {
            try
            {
                if (delta > 0)
                {
                    return _database.IncrementRecordWeight(id, delta);
                }
                else if (delta < 0)
                {
                    return _database.DecrementRecordWeight(id, Math.Abs(delta));
                }
                else
                {
                    Logger.Log($"RecordManager: Weight delta is zero, no modification needed for record #{id}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"RecordManager: Failed to modify record #{id} weight: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Called on each conversation turn to decrement weights
        /// </summary>
        public void OnConversationTurn()
        {
            try
            {
                // Check if auto-decrement is enabled
                if (_settings.Records == null || !_settings.Records.AutoDecrementWeights)
                {
                    return;
                }

                // Calculate decrement amount based on decay turns setting
                // If WeightDecayTurns = 1, decrement by 1.0 (default behavior)
                // If WeightDecayTurns = 3, decrement by 1/3 ≈ 0.333 (takes 3 turns to lose 1 weight)
                var decayTurns = Math.Max(1, _settings.Records.WeightDecayTurns);
                var decrementAmount = 1.0 / decayTurns;
                
                var decremented = _database.DecrementAllRecords(decrementAmount);
                
                if (decremented > 0)
                {
                    Logger.Log($"RecordManager: Decremented {decremented} records by {decrementAmount:F3} on conversation turn (decay turns: {decayTurns})");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"RecordManager: Failed to decrement records on conversation turn: {ex.Message}");
            }
        }

        /// <summary>
        /// Get formatted records for context injection
        /// </summary>
        /// <returns>Formatted string containing all active records</returns>
        public string GetRecordsContext()
        {
            try
            {
                var records = _database.GetActiveRecords();
                
                if (records == null || records.Count == 0)
                {
                    return string.Empty;
                }

                // Limit number of records if configured
                if (_settings.Records != null && _settings.Records.MaxRecordsInContext > 0)
                {
                    records = records.Take(_settings.Records.MaxRecordsInContext).ToList();
                }

                var sb = new StringBuilder();
                sb.AppendLine("[IMPORTANT RECORDS]");
                sb.AppendLine("The following are important things you should remember:");
                sb.AppendLine();

                foreach (var record in records)
                {
                    sb.AppendLine($"#{record.Id} (Weight: {record.DisplayWeight}): {record.Content}");
                }

                sb.AppendLine();
                sb.AppendLine("[END IMPORTANT RECORDS]");

                return sb.ToString();
            }
            catch (Exception ex)
            {
                Logger.Log($"RecordManager: Failed to get records context: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Inject records into message history at first user position
        /// </summary>
        /// <param name="history">The message history</param>
        /// <returns>Modified history with records injected</returns>
        public List<Message> InjectRecordsIntoHistory(List<Message> history)
        {
            try
            {
                // Check if records system is enabled
                if (_settings.Records == null || !_settings.Records.EnableRecords)
                {
                    return history;
                }

                // Get records context
                var recordsContext = GetRecordsContext();
                
                if (string.IsNullOrWhiteSpace(recordsContext))
                {
                    return history;
                }

                // Find the first user message position
                var firstUserIndex = history.FindIndex(m => m.Role == "user");
                
                if (firstUserIndex == -1)
                {
                    // No user messages, cannot inject
                    Logger.Log("RecordManager: No user messages found, cannot inject records");
                    return history;
                }

                // Create a new list with records injected
                var modifiedHistory = new List<Message>(history);
                
                // Create a new message with records context prepended to the first user message
                var firstUserMessage = modifiedHistory[firstUserIndex];
                var modifiedContent = recordsContext + "\n" + firstUserMessage.Content;
                
                modifiedHistory[firstUserIndex] = new Message
                {
                    Role = firstUserMessage.Role,
                    Content = modifiedContent,
                    UnixTime = firstUserMessage.UnixTime,
                    StatusInfo = firstUserMessage.StatusInfo
                };

                Logger.Log($"RecordManager: Injected records into first user message at index {firstUserIndex}");
                
                return modifiedHistory;
            }
            catch (Exception ex)
            {
                Logger.Log($"RecordManager: Failed to inject records into history: {ex.Message}");
                return history;
            }
        }

        /// <summary>
        /// Clear all records
        /// </summary>
        /// <returns>Number of records deleted</returns>
        public int ClearAllRecords()
        {
            try
            {
                var deleted = _database.DeleteAllRecords();
                Logger.Log($"RecordManager: Cleared all records, total: {deleted}");
                return deleted;
            }
            catch (Exception ex)
            {
                Logger.Log($"RecordManager: Failed to clear all records: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Get all records for editing
        /// </summary>
        /// <returns>List of all records</returns>
        public List<ImportantRecord> GetAllRecordsForEditing()
        {
            try
            {
                return _database.GetAllRecords();
            }
            catch (Exception ex)
            {
                Logger.Log($"RecordManager: Failed to get all records for editing: {ex.Message}");
                return new List<ImportantRecord>();
            }
        }

        /// <summary>
        /// Update a record
        /// </summary>
        public bool UpdateRecord(ImportantRecord record)
        {
            try
            {
                return _database.UpdateRecord(record.Id, record.Content, record.Weight);
            }
            catch (Exception ex)
            {
                Logger.Log($"RecordManager: Failed to update record: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get the database path
        /// </summary>
        private string GetDatabasePath()
        {
            var docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dataPath = Path.Combine(docPath, "VPetLLM", "Chat");
            
            if (!Directory.Exists(dataPath))
            {
                Directory.CreateDirectory(dataPath);
            }

            return Path.Combine(dataPath, "chat_history.db");
        }
    }
}
