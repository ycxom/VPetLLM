using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using System.Collections.Concurrent;

namespace VPetLLM.Infrastructure.Configuration;

/// <summary>
/// SQLite-based settings storage implementation
/// </summary>
public class SQLiteSettingStorage : ISettingStorage
{
    private readonly string _databasePath;
    private readonly string _baseDirectory;
    private readonly string? _instanceId;
    private SqliteConnection? _connection;
    private readonly SchemaManager _schemaManager;
    private readonly BackupManager _backupManager;
    private readonly object _lock = new object();
    private readonly ConcurrentQueue<Action> _pendingWrites = new ConcurrentQueue<Action>();
    private readonly System.Threading.Timer? _flushTimer;

    // Instance-specific setting keys
    private static readonly HashSet<string> InstanceSpecificKeys = new HashSet<string>
    {
        "AiName",
        "UserName",
        "Role",
        "Language",
        "PromptLanguage"
    };

    public SQLiteSettingStorage(string baseDirectory, string? instanceId = null)
    {
        _baseDirectory = baseDirectory;
        _instanceId = instanceId;
        _databasePath = GetDatabasePath(baseDirectory);
        _schemaManager = new SchemaManager();
        _backupManager = new BackupManager(_databasePath);
        
        // Setup flush timer for batched writes (flush every 5 seconds)
        _flushTimer = new System.Threading.Timer(
            callback: _ => FlushPendingWrites(),
            state: null,
            dueTime: TimeSpan.FromSeconds(5),
            period: TimeSpan.FromSeconds(5)
        );
    }

    /// <summary>
    /// Get the database file path - all instances use the same database file
    /// </summary>
    private string GetDatabasePath(string baseDirectory)
    {
        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var vpetLLMPath = Path.Combine(documentsPath, "VPetLLM");
        return Path.Combine(vpetLLMPath, "settings.db");
    }

    /// <summary>
    /// Get the instance-specific table name
    /// </summary>
    private string GetInstanceTableName()
    {
        if (string.IsNullOrEmpty(_instanceId))
            return "settings_default";
        else
            return $"settings_{_instanceId}";
    }

    /// <summary>
    /// Check if a setting key is instance-specific
    /// </summary>
    private bool IsInstanceSpecificSetting(string key)
    {
        return InstanceSpecificKeys.Contains(key);
    }

    public bool Initialize(string? instanceId = null)
    {
        try
        {
            // Create directory if it doesn't exist
            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                Logger.Log($"Created settings directory: {directory}");
            }

            // Create connection string
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            // Open connection
            _connection = new SqliteConnection(connectionString);
            _connection.Open();

            Logger.Log($"SQLite database connection opened: {_databasePath}");

            // Check schema version
            var currentVersion = _schemaManager.GetSchemaVersion(_connection);
            
            if (currentVersion == 0)
            {
                // Create initial schema
                _schemaManager.CreateSchema(_connection);
            }
            else if (currentVersion < 1) // CurrentSchemaVersion
            {
                // Upgrade schema
                _schemaManager.UpgradeSchema(_connection, currentVersion);
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to initialize SQLite storage: {ex.Message}");
            Logger.Log($"Stack trace: {ex.StackTrace}");
            
            // Clean up connection if initialization failed
            _connection?.Dispose();
            _connection = null;
            
            return false;
        }
    }

    public T? Load<T>() where T : class
    {
        var json = LoadAsJson();
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<T>(json);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to deserialize settings: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Load settings as JSON string (for PopulateObject usage)
    /// Loads from both shared settings table and instance-specific table
    /// </summary>
    public string? LoadAsJson()
    {
        lock (_lock)
        {
            try
            {
                if (_connection == null)
                {
                    throw new StorageException("Database connection is not initialized");
                }

                Logger.Log($"Loading settings from database as JSON for instance: '{_instanceId ?? "default"}'");

                // Build a JSON object from both shared and instance-specific tables
                var jsonBuilder = new System.Text.StringBuilder();
                jsonBuilder.Append("{");

                var loadedCount = 0;
                var isFirst = true;

                // 1. Load shared settings from 'settings' table
                Logger.Log("Loading shared settings from 'settings' table...");
                loadedCount += LoadFromTable("settings", jsonBuilder, ref isFirst);

                // 2. Load instance-specific settings from instance table
                var instanceTableName = GetInstanceTableName();
                Logger.Log($"Loading instance-specific settings from '{instanceTableName}' table...");
                
                // Check if instance table exists, create if not
                EnsureInstanceTableExists(instanceTableName);
                
                loadedCount += LoadFromTable(instanceTableName, jsonBuilder, ref isFirst);

                jsonBuilder.Append("}");

                if (loadedCount == 0)
                {
                    Logger.Log("No settings found in database");
                    return null;
                }

                Logger.Log($"Loaded {loadedCount} settings from database (shared + instance-specific)");
                
                var json = jsonBuilder.ToString();
                Logger.Log($"Reconstructed JSON length: {json.Length} characters");
                
                return json;
            }
            catch (SqliteException ex)
            {
                Logger.Log($"Database error while loading settings: {ex.Message}");
                
                // Check if database is corrupted
                if (IsDatabaseCorrupted(ex))
                {
                    Logger.Log("Database appears to be corrupted, attempting recovery...");
                    if (TryRecoverFromBackup())
                    {
                        Logger.Log("Database recovered from backup, retrying load...");
                        // Retry load after recovery
                        return LoadAsJson();
                    }
                    else
                    {
                        Logger.Log("Failed to recover database from backup");
                    }
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to load settings from database: {ex.Message}");
                Logger.Log($"Stack trace: {ex.StackTrace}");
                return null;
            }
        }
    }

    /// <summary>
    /// Load settings from a specific table and append to JSON builder
    /// </summary>
    private int LoadFromTable(string tableName, System.Text.StringBuilder jsonBuilder, ref bool isFirst)
    {
        var loadedCount = 0;

        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = $"SELECT key, value, type FROM `{tableName}`";

            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var key = reader.GetString(0);
                var value = reader.GetString(1);
                var type = reader.GetString(2);

                try
                {
                    if (!isFirst)
                    {
                        jsonBuilder.Append(",");
                    }
                    isFirst = false;

                    // Add property to JSON
                    jsonBuilder.Append($"\"{key}\":");
                    
                    if (type.StartsWith("System."))
                    {
                        // Simple type - add as JSON value
                        if (type == "System.String")
                        {
                            // Escape string value
                            var escapedValue = JsonConvert.ToString(value);
                            jsonBuilder.Append(escapedValue);
                        }
                        else if (type == "System.Boolean")
                        {
                            jsonBuilder.Append(value.ToLower());
                        }
                        else
                        {
                            jsonBuilder.Append(value);
                        }
                    }
                    else
                    {
                        // Complex type - value is already JSON, append directly
                        jsonBuilder.Append(value);
                    }
                    
                    loadedCount++;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to process setting '{key}' from table '{tableName}': {ex.Message}");
                }
            }
        }
        catch (SqliteException ex)
        {
            // Table might not exist yet
            if (ex.SqliteErrorCode == 1) // SQLITE_ERROR - no such table
            {
                Logger.Log($"Table '{tableName}' does not exist yet, skipping...");
            }
            else
            {
                throw;
            }
        }

        return loadedCount;
    }

    /// <summary>
    /// Ensure instance-specific table exists, create if not
    /// </summary>
    private void EnsureInstanceTableExists(string tableName)
    {
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = $@"
                CREATE TABLE IF NOT EXISTS `{tableName}` (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    type TEXT NOT NULL,
                    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                )
            ";
            cmd.ExecuteNonQuery();
            Logger.Log($"Ensured table '{tableName}' exists");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to create table '{tableName}': {ex.Message}");
        }
    }

    private bool IsDatabaseCorrupted(SqliteException ex)
    {
        // Check for common corruption error codes
        return ex.SqliteErrorCode == 11 || // SQLITE_CORRUPT
               ex.SqliteErrorCode == 26 || // SQLITE_NOTADB
               ex.Message.Contains("corrupt", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryRecoverFromBackup()
    {
        try
        {
            // Close current connection
            _connection?.Dispose();
            _connection = null;

            // Get most recent backup
            var backupPath = _backupManager.GetMostRecentBackup();
            if (string.IsNullOrEmpty(backupPath))
            {
                Logger.Log("No backup available for recovery");
                return false;
            }

            // Restore from backup
            if (_backupManager.RestoreFromBackup(backupPath))
            {
                // Reinitialize connection
                return Initialize();
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to recover from backup: {ex.Message}");
            return false;
        }
    }

    public void Save<T>(T settings) where T : class
    {
        lock (_lock)
        {
            try
            {
                if (_connection == null)
                {
                    throw new StorageException("Database connection is not initialized");
                }

                // Create backup before saving
                try
                {
                    _backupManager.CreateBackup();
                }
                catch (Exception ex)
                {
                    Logger.Log($"Warning: Failed to create backup before save: {ex.Message}");
                    // Continue with save even if backup fails
                }

                // Ensure instance table exists
                var instanceTableName = GetInstanceTableName();
                EnsureInstanceTableExists(instanceTableName);

                using var transaction = _connection.BeginTransaction();
                try
                {
                    var properties = typeof(T).GetProperties();
                    var savedCount = 0;
                    var sharedCount = 0;
                    var instanceCount = 0;

                    foreach (var property in properties)
                    {
                        if (!property.CanRead)
                            continue;

                        var key = property.Name;
                        var value = property.GetValue(settings);
                        
                        if (value == null)
                            continue;

                        var type = property.PropertyType.FullName ?? property.PropertyType.Name;
                        string serializedValue;

                        // Serialize based on type
                        if (property.PropertyType.IsPrimitive || 
                            property.PropertyType == typeof(string) ||
                            property.PropertyType == typeof(decimal) ||
                            property.PropertyType == typeof(DateTime))
                        {
                            serializedValue = value.ToString() ?? "";
                        }
                        else
                        {
                            // Complex type - serialize to JSON
                            serializedValue = JsonConvert.SerializeObject(value);
                        }

                        // Determine which table to save to
                        var tableName = IsInstanceSpecificSetting(key) ? instanceTableName : "settings";

                        using var cmd = _connection.CreateCommand();
                        cmd.CommandText = $@"
                            INSERT OR REPLACE INTO `{tableName}` (key, value, type, updated_at)
                            VALUES (@key, @value, @type, CURRENT_TIMESTAMP)
                        ";
                        cmd.Parameters.AddWithValue("@key", key);
                        cmd.Parameters.AddWithValue("@value", serializedValue);
                        cmd.Parameters.AddWithValue("@type", type);
                        cmd.ExecuteNonQuery();
                        
                        savedCount++;
                        if (IsInstanceSpecificSetting(key))
                            instanceCount++;
                        else
                            sharedCount++;
                    }

                    transaction.Commit();
                    Logger.Log($"Saved {savedCount} settings to database (shared: {sharedCount}, instance: {instanceCount})");
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    Logger.Log($"Failed to save settings, transaction rolled back: {ex.Message}");
                    throw;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save settings to database: {ex.Message}");
                throw new StorageException("Failed to save settings to database", ex);
            }
        }
    }

    public bool IsAvailable()
    {
        return _connection != null && _connection.State == System.Data.ConnectionState.Open;
    }

    public string GetStorageLocation()
    {
        return _databasePath;
    }

    public void Dispose()
    {
        // Flush any pending writes before disposing
        FlushPendingWrites();
        
        _flushTimer?.Dispose();
        _connection?.Dispose();
        _connection = null;
    }

    private void FlushPendingWrites()
    {
        if (_pendingWrites.IsEmpty)
            return;

        lock (_lock)
        {
            try
            {
                while (_pendingWrites.TryDequeue(out var writeAction))
                {
                    writeAction?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error flushing pending writes: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Run VACUUM operation to optimize database
    /// </summary>
    public void Vacuum()
    {
        lock (_lock)
        {
            try
            {
                if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
                {
                    Logger.Log("Cannot run VACUUM: database connection is not open");
                    return;
                }

                Logger.Log("Running VACUUM operation to optimize database...");
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "VACUUM;";
                cmd.ExecuteNonQuery();
                Logger.Log("VACUUM operation completed successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to run VACUUM operation: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Check if database needs maintenance based on file size
    /// </summary>
    private bool NeedsMaintenance()
    {
        try
        {
            if (!File.Exists(_databasePath))
                return false;

            var fileInfo = new FileInfo(_databasePath);
            // Run VACUUM if database is larger than 10MB
            return fileInfo.Length > 10 * 1024 * 1024;
        }
        catch
        {
            return false;
        }
    }
}
