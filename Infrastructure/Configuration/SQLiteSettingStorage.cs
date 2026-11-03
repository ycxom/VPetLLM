using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
    private bool _recovering;

    // Instance-specific setting keys
    private static readonly HashSet<string> InstanceSpecificKeys = new HashSet<string>
    {
        "AiName",
        "UserName",
        "Role",
        "Language",
        "PromptLanguage"
    };

    // Settings that should not be saved to settings table (managed elsewhere)
    private static readonly HashSet<string> ExcludedFromSettingsTable = new HashSet<string>
    {
        // Provider node lists are managed in provider_nodes table
        // We only save the parent objects (OpenAI, Gemini) for backward compatibility
        // but the node lists themselves are excluded
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

    /// <summary>
    /// Serialize provider settings (OpenAI/Gemini) without node lists
    /// Node lists are managed separately in provider_nodes table
    /// </summary>
    private string SerializeProviderSettingWithoutNodes(object value, string providerType)
    {
        try
        {
            // Create custom serializer settings to ignore node lists
            var settings = new JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
                {
                    IgnoreSerializableAttribute = false
                },
                Formatting = Formatting.None
            };

            // Serialize to JObject first
            var json = JsonConvert.SerializeObject(value, settings);
            var jObject = JObject.Parse(json);

            // Remove node lists and legacy single-node properties
            if (providerType == "OpenAI")
            {
                jObject.Remove("OpenAINodes");
                
                // Remove legacy single-node properties (向后兼容属性)
                // 这些属性在旧版本中用于单节点配置，现在由 provider_nodes 表管理
                jObject.Remove("ApiKey");
                jObject.Remove("Model");
                jObject.Remove("Url");
                jObject.Remove("Temperature");
                jObject.Remove("MaxTokens");
                jObject.Remove("EnableAdvanced");
                jObject.Remove("EnableStreaming");
                jObject.Remove("Enabled");
                jObject.Remove("Name");
            }
            else if (providerType == "Gemini")
            {
                jObject.Remove("GeminiNodes");
                
                // Remove legacy single-node properties
                jObject.Remove("ApiKey");
                jObject.Remove("Model");
                jObject.Remove("Url");
                jObject.Remove("Temperature");
                jObject.Remove("MaxTokens");
                jObject.Remove("EnableAdvanced");
                jObject.Remove("EnableStreaming");
            }

            return jObject.ToString(Formatting.None);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to serialize {providerType} without nodes: {ex.Message}");
            // Fallback to normal serialization
            return JsonConvert.SerializeObject(value);
        }
    }

    /// <summary>
    /// Get the database connection for advanced operations
    /// </summary>
    /// <returns>SQLite connection or null if not initialized</returns>
    public SqliteConnection? GetConnection()
    {
        return _connection;
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
            _connection = global::VPetLLM.Utils.Data.SQLiteHelper.OpenConnection(_databasePath);

            // Check schema version
            var currentVersion = _schemaManager.GetSchemaVersion(_connection);
            var targetVersion = SchemaManager.CurrentSchemaVersion;
            
            if (currentVersion == 0)
            {
                // Create initial schema
                _schemaManager.CreateSchema(_connection);
            }
            else if (currentVersion < targetVersion)
            {
                // Upgrade schema
                _schemaManager.UpgradeSchema(_connection, currentVersion);
            }

            // Repair databases that were stamped at the current version without ever
            // getting the tables the later migrations create (see EnsureSchemaComplete)
            _schemaManager.EnsureSchemaComplete(_connection);

            // Always check for plugin config files to migrate (similar to VPetLLM.json handling)
            // This ensures that if JSON files exist, they will be migrated/updated in database
            _schemaManager.MigratePluginConfigFiles(_connection);

            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to initialize SQLite storage: {ex.Message}");
            Logger.Log($"Stack trace: {ex.StackTrace}");
            
            // Clean up connection if initialization failed
            _connection?.Dispose();
            _connection = null;

            // 文件头坏了，打开连接那一步就报 SQLITE_NOTADB，走不到读设置时的那处恢复，得在这里接住
            for (var inner = ex; inner is not null; inner = inner.InnerException)
            {
                if (inner is SqliteException sqlite && IsDatabaseCorrupted(sqlite))
                {
                    Logger.Log("设置库已损坏，尝试从备份恢复");
                    return TryRecoverFromBackup();
                }
            }

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

                // Build a JSON object from both shared and instance-specific tables
                var jsonBuilder = new System.Text.StringBuilder();
                jsonBuilder.Append("{");

                var loadedCount = 0;
                var isFirst = true;

                // 1. Load shared settings from 'settings' table
                loadedCount += LoadFromTable("settings", jsonBuilder, ref isFirst);

                // 2. Load instance-specific settings from instance table
                var instanceTableName = GetInstanceTableName();
                
                // Check if instance table exists, create if not
                EnsureInstanceTableExists(instanceTableName);
                
                loadedCount += LoadFromTable(instanceTableName, jsonBuilder, ref isFirst);

                jsonBuilder.Append("}");

                if (loadedCount == 0)
                {
                    return null;
                }
                
                var json = jsonBuilder.ToString();
                
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
            if (_connection == null)
                return 0;

            var connection = _connection; // 使用局部变量避免可空类型问题
            using var cmd = connection.CreateCommand();
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
            if (_connection == null)
                return;

            var connection = _connection; // 使用局部变量避免可空类型问题
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"
                CREATE TABLE IF NOT EXISTS `{tableName}` (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    type TEXT NOT NULL,
                    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                )
            ";
            cmd.ExecuteNonQuery();
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
        // 恢复完会重新 Initialize；备份本身也坏的话那次初始化又会判成损坏，不拦住就无限套娃
        if (_recovering)
            return false;

        _recovering = true;
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
        finally
        {
            _recovering = false;
        }
    }

    /// <summary>
    /// 真要往库里写之前调：到了间隔就先备份一次。没改动的保存不调，也就不会每次都复制一遍整库。
    /// 要在开事务之前调；刚备份过的话直接返回，重复调很便宜。
    /// </summary>
    public void BackupBeforeWrite()
    {
        try
        {
            _backupManager.BackupIfDue();
        }
        catch (Exception ex)
        {
            Logger.Log($"Warning: Failed to create backup before save: {ex.Message}");
            // Continue with save even if backup fails
        }
    }

    private static Dictionary<string, (string Value, string Type)> ReadRows(SqliteConnection connection, string tableName)
    {
        var rows = new Dictionary<string, (string Value, string Type)>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT key, value, type FROM `{tableName}`";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows[reader.GetString(0)] = (reader.IsDBNull(1) ? "" : reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2));
        }
        return rows;
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

                // Ensure instance table exists
                var instanceTableName = GetInstanceTableName();
                EnsureInstanceTableExists(instanceTableName);

                var connection = _connection; // 使用局部变量避免可空类型问题

                // 宿主约 2 分钟调一次保存，绝大多数时候什么都没改：先和库里比，只写变了的行，一行没变就不碰库
                var rows = new List<(string Table, string Key, string Value, string Type)>();
                var properties = typeof(T).GetProperties();

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
                        // For OpenAI and Gemini settings, exclude node lists (managed in provider_nodes table)
                        // For Tools, exclude the list (managed in plugin_data table)
                        if (key == "OpenAI" || key == "Gemini")
                        {
                            serializedValue = SerializeProviderSettingWithoutNodes(value, key);
                        }
                        else if (key == "Tools")
                        {
                            // Skip Tools list - managed in plugin_data table
                            continue;
                        }
                        else
                        {
                            serializedValue = JsonConvert.SerializeObject(value);
                        }
                    }

                    // Determine which table to save to
                    var tableName = IsInstanceSpecificSetting(key) ? instanceTableName : "settings";
                    rows.Add((tableName, key, serializedValue, type));
                }

                var shared = ReadRows(connection, "settings");
                var instance = ReadRows(connection, instanceTableName);
                var changed = rows.Where(row =>
                    !(row.Table == "settings" ? shared : instance).TryGetValue(row.Key, out var stored)
                    || stored.Value != row.Value
                    || stored.Type != row.Type).ToList();
                if (changed.Count == 0)
                    return;

                BackupBeforeWrite();

                using var transaction = connection.BeginTransaction();
                try
                {
                    foreach (var row in changed)
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.CommandText = $@"
                            INSERT OR REPLACE INTO `{row.Table}` (key, value, type, updated_at)
                            VALUES (@key, @value, @type, CURRENT_TIMESTAMP)
                        ";
                        cmd.Parameters.AddWithValue("@key", row.Key);
                        cmd.Parameters.AddWithValue("@value", row.Value);
                        cmd.Parameters.AddWithValue("@type", row.Type);
                        cmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    Logger.Log($"设置已保存（{changed.Count} 项有改动）");
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

    // 库里还没加密的 API Key：  "ApiKey": "<非空且不以 dpapi1: 开头>"
    private static readonly System.Text.RegularExpressions.Regex PlaintextApiKeyPattern =
        new("\"ApiKey\"\\s*:\\s*\"(?!dpapi1:)(?!\")", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 扫描全部表的文本列，返回仍含明文 API Key 的 "表.列" 清单（空 = 库里没有明文）。
    /// 逐表逐列扫而不是只看 settings/provider_nodes/plugin_data：以后新增存密钥的表也不会漏
    /// </summary>
    private List<string> FindPlaintextSecretsLocked()
    {
        var hits = new List<string>();
        if (_connection == null)
            return hits;

        var tables = new List<string>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }

        foreach (var table in tables)
        {
            var columns = new List<string>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info(`{table}`)";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    columns.Add(reader.GetString(1));
            }

            foreach (var column in columns)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = $"SELECT `{column}` FROM `{table}` WHERE typeof(`{column}`) = 'text'";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (PlaintextApiKeyPattern.IsMatch(reader.GetString(0)))
                    {
                        hits.Add($"{table}.{column}");
                        break;
                    }
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// 密钥改成密文存储之后，清掉磁盘上残留的明文副本：
    /// 1. 数据库备份（保存前对整库的复制，加密之前的备份里是明文）
    /// 2. SQLite 改写行之后留在空闲页里的旧明文（VACUUM 重写整个库）和 WAL 里的旧页（checkpoint 截断）
    ///
    /// 前提是库里已经没有明文：先扫一遍确认，还有明文就一样都不动——
    /// 那时备份可能是唯一一份完整数据，不能在转换没成功的情况下删掉。
    /// 备份清理和 VACUUM 各自独立容错（VACUUM 要独占锁，别的实例持有连接时会失败）。
    /// </summary>
    /// <returns>库里确认没有明文并完成了清理</returns>
    public bool PurgeResidualPlaintext()
    {
        lock (_lock)
        {
            if (_connection == null)
                return false;

            try
            {
                var remaining = FindPlaintextSecretsLocked();
                if (remaining.Count > 0)
                {
                    Logger.Log($"数据库中仍有明文 API Key（{string.Join(", ", remaining)}），保留备份，不做 VACUUM");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"扫描明文 API Key 失败，保留备份: {ex.Message}");
                return false;
            }

            var purged = _backupManager.PurgeAllBackups();
            Logger.Log($"已清理 {purged} 份加密改造前的数据库备份");

            try
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "VACUUM;";
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    cmd.ExecuteNonQuery();
                }
                Logger.Log("已 VACUUM 并截断 WAL，旧明文页已清除");
            }
            catch (Exception ex)
            {
                Logger.Log($"VACUUM/WAL 截断失败（旧明文可能还留在数据库空闲页里；需要关掉所有 VPet 实例后手动 VACUUM）: {ex.Message}");
            }

            return true;
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

                var connection = _connection; // 使用局部变量避免可空类型问题
                Logger.Log("Running VACUUM operation to optimize database...");
                using var cmd = connection.CreateCommand();
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

}
