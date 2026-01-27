using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace VPetLLM.Infrastructure.Configuration;

/// <summary>
/// Manages database schema creation and versioning
/// </summary>
public class SchemaManager
{
    private const int CurrentSchemaVersion = 5;

    /// <summary>
    /// Create the initial database schema
    /// </summary>
    /// <param name="connection">SQLite connection</param>
    public void CreateSchema(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            // Create metadata table
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS metadata (
                        key TEXT PRIMARY KEY,
                        value TEXT NOT NULL
                    );
                ";
                cmd.ExecuteNonQuery();
            }

            // Create settings table
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS settings (
                        key TEXT PRIMARY KEY,
                        value TEXT NOT NULL,
                        type TEXT NOT NULL,
                        updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
                    );
                ";
                cmd.ExecuteNonQuery();
            }

            // Create index on settings type
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE INDEX IF NOT EXISTS idx_settings_type ON settings(type);
                ";
                cmd.ExecuteNonQuery();
            }

            // Create index on settings updated_at
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE INDEX IF NOT EXISTS idx_settings_updated ON settings(updated_at);
                ";
                cmd.ExecuteNonQuery();
            }

            // Create provider_nodes table
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS provider_nodes (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        provider_type TEXT NOT NULL,
                        node_data TEXT NOT NULL,
                        enabled INTEGER DEFAULT 1,
                        created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                        updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
                    );
                ";
                cmd.ExecuteNonQuery();
            }

            // Create index on provider_type
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE INDEX IF NOT EXISTS idx_provider_type ON provider_nodes(provider_type);
                ";
                cmd.ExecuteNonQuery();
            }

            // Create plugin_data table
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS plugin_data (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL,
                        type TEXT NOT NULL,
                        config_data TEXT NOT NULL,
                        enabled INTEGER DEFAULT 1,
                        display_order INTEGER DEFAULT 0,
                        created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                        updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
                    );
                ";
                cmd.ExecuteNonQuery();
            }

            // Create index on plugin type
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE INDEX IF NOT EXISTS idx_plugin_type ON plugin_data(type);
                ";
                cmd.ExecuteNonQuery();
            }

            // Create index on plugin display_order
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE INDEX IF NOT EXISTS idx_plugin_order ON plugin_data(display_order);
                ";
                cmd.ExecuteNonQuery();
            }

            // Set schema version
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO metadata (key, value) 
                    VALUES ('schema_version', @version);
                ";
                cmd.Parameters.AddWithValue("@version", CurrentSchemaVersion.ToString());
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
            Logger.Log($"Database schema created successfully (version {CurrentSchemaVersion})");
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            Logger.Log($"Failed to create database schema: {ex.Message}");
            throw new StorageException("Failed to create database schema", ex);
        }
    }

    /// <summary>
    /// Get the current schema version from the database
    /// </summary>
    /// <param name="connection">SQLite connection</param>
    /// <returns>Schema version number, or 0 if not found</returns>
    public int GetSchemaVersion(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT value FROM metadata WHERE key = 'schema_version';
            ";
            
            var result = cmd.ExecuteScalar();
            if (result != null && int.TryParse(result.ToString(), out int version))
            {
                return version;
            }
            
            return 0;
        }
        catch (SqliteException)
        {
            // Table doesn't exist yet
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to get schema version: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Upgrade the database schema from an older version
    /// </summary>
    /// <param name="connection">SQLite connection</param>
    /// <param name="fromVersion">Current schema version</param>
    public void UpgradeSchema(SqliteConnection connection, int fromVersion)
    {
        if (fromVersion >= CurrentSchemaVersion)
        {
            Logger.Log($"Schema is already at version {CurrentSchemaVersion}, no upgrade needed");
            return;
        }

        Logger.Log($"Upgrading schema from version {fromVersion} to {CurrentSchemaVersion}");

        using var transaction = connection.BeginTransaction();
        try
        {
            // Apply migrations based on version
            for (int version = fromVersion + 1; version <= CurrentSchemaVersion; version++)
            {
                ApplyMigration(connection, version);
            }

            // Update schema version
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    UPDATE metadata SET value = @version WHERE key = 'schema_version';
                ";
                cmd.Parameters.AddWithValue("@version", CurrentSchemaVersion.ToString());
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
            Logger.Log($"Schema upgraded successfully to version {CurrentSchemaVersion}");
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            Logger.Log($"Failed to upgrade schema: {ex.Message}");
            throw new StorageException($"Failed to upgrade schema from version {fromVersion}", ex);
        }
    }

    /// <summary>
    /// Apply a specific migration version
    /// </summary>
    /// <param name="connection">SQLite connection</param>
    /// <param name="toVersion">Target version to migrate to</param>
    private void ApplyMigration(SqliteConnection connection, int toVersion)
    {
        Logger.Log($"Applying migration to version {toVersion}");

        switch (toVersion)
        {
            case 1:
                // Version 1 is the initial schema, no migration needed
                break;
            
            case 2:
                ApplyMigrationV2(connection);
                break;
            
            case 3:
                ApplyMigrationV3(connection);
                break;
            
            case 4:
                ApplyMigrationV4(connection);
                break;
            
            case 5:
                ApplyMigrationV5(connection);
                break;
            
            default:
                throw new StorageException($"Unknown migration version: {toVersion}");
        }
    }

    /// <summary>
    /// Apply migration to version 2: Create instance-specific settings tables
    /// </summary>
    private void ApplyMigrationV2(SqliteConnection connection)
    {
        Logger.Log("Applying migration to version 2: Creating instance-specific settings structure");

        // 1. Create default instance table
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS settings_default (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    type TEXT NOT NULL,
                    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
                );
            ";
            cmd.ExecuteNonQuery();
            Logger.Log("Created settings_default table");
        }

        // 2. Migrate instance-specific settings from settings table to settings_default
        var instanceSpecificKeys = new[] { "AiName", "UserName", "Role", "Language", "PromptLanguage" };
        
        foreach (var key in instanceSpecificKeys)
        {
            try
            {
                // Check if key exists in settings table
                using var checkCmd = connection.CreateCommand();
                checkCmd.CommandText = "SELECT value, type FROM settings WHERE key = @key";
                checkCmd.Parameters.AddWithValue("@key", key);
                
                using var reader = checkCmd.ExecuteReader();
                if (reader.Read())
                {
                    var value = reader.GetString(0);
                    var type = reader.GetString(1);
                    reader.Close();

                    // Insert into settings_default
                    using var insertCmd = connection.CreateCommand();
                    insertCmd.CommandText = @"
                        INSERT OR REPLACE INTO settings_default (key, value, type, updated_at)
                        VALUES (@key, @value, @type, CURRENT_TIMESTAMP)
                    ";
                    insertCmd.Parameters.AddWithValue("@key", key);
                    insertCmd.Parameters.AddWithValue("@value", value);
                    insertCmd.Parameters.AddWithValue("@type", type);
                    insertCmd.ExecuteNonQuery();

                    // Delete from settings table
                    using var deleteCmd = connection.CreateCommand();
                    deleteCmd.CommandText = "DELETE FROM settings WHERE key = @key";
                    deleteCmd.Parameters.AddWithValue("@key", key);
                    deleteCmd.ExecuteNonQuery();

                    Logger.Log($"Migrated '{key}' from settings to settings_default");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Warning: Failed to migrate key '{key}': {ex.Message}");
                // Continue with other keys
            }
        }

        Logger.Log("Migration to version 2 completed");
    }

    /// <summary>
    /// Apply migration to version 3: Migrate provider nodes to provider_nodes table
    /// </summary>
    private void ApplyMigrationV3(SqliteConnection connection)
    {
        Logger.Log("Applying migration to version 3: Migrating provider nodes to provider_nodes table");

        try
        {
            // 1. Read OpenAI nodes from settings table
            MigrateProviderNodes(connection, "OpenAI");

            // 2. Read Gemini nodes from settings table
            MigrateProviderNodes(connection, "Gemini");

            Logger.Log("Migration to version 3 completed");
        }
        catch (Exception ex)
        {
            Logger.Log($"Warning: Migration to version 3 encountered errors: {ex.Message}");
            // Continue anyway - this is not critical
        }
    }

    /// <summary>
    /// Migrate provider nodes from settings table to provider_nodes table
    /// </summary>
    private void MigrateProviderNodes(SqliteConnection connection, string providerType)
    {
        try
        {
            // Read the provider setting from settings table
            using var readCmd = connection.CreateCommand();
            readCmd.CommandText = "SELECT value FROM settings WHERE key = @key";
            readCmd.Parameters.AddWithValue("@key", providerType);

            var settingJson = readCmd.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(settingJson))
            {
                Logger.Log($"No {providerType} settings found to migrate");
                return;
            }

            // Parse the setting JSON
            var setting = JObject.Parse(settingJson);
            
            // Determine the nodes array key based on provider type
            string nodesKey = providerType switch
            {
                "OpenAI" => "OpenAINodes",
                "Gemini" => "GeminiNodes",
                _ => $"{providerType}Nodes"
            };

            var nodesArray = setting[nodesKey] as JArray;
            if (nodesArray == null || nodesArray.Count == 0)
            {
                Logger.Log($"No {providerType} nodes found to migrate");
                return;
            }

            // Migrate each node to provider_nodes table
            var nodeService = new ProviderNodeService(connection);
            var migratedCount = 0;

            foreach (var nodeToken in nodesArray)
            {
                try
                {
                    var nodeJson = nodeToken.ToString();
                    var nodeId = nodeService.AddNode(providerType, nodeJson, enabled: true);
                    
                    if (nodeId > 0)
                    {
                        migratedCount++;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to migrate a {providerType} node: {ex.Message}");
                }
            }

            Logger.Log($"Migrated {migratedCount} {providerType} nodes to provider_nodes table");

            // Note: We keep the nodes in settings table for backward compatibility
            // They will be ignored when loading if provider_nodes table has data
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to migrate {providerType} nodes: {ex.Message}");
        }
    }

    /// <summary>
    /// Apply migration to version 4: Migrate plugin/tool data to plugin_data table
    /// </summary>
    private void ApplyMigrationV4(SqliteConnection connection)
    {
        Logger.Log("Applying migration to version 4: Migrating plugin/tool data to plugin_data table");

        try
        {
            // Read Tools list from settings table
            MigratePluginData(connection);

            Logger.Log("Migration to version 4 completed");
        }
        catch (Exception ex)
        {
            Logger.Log($"Warning: Migration to version 4 encountered errors: {ex.Message}");
            // Continue anyway - this is not critical
        }
    }

    /// <summary>
    /// Migrate plugin/tool data from settings table to plugin_data table
    /// </summary>
    private void MigratePluginData(SqliteConnection connection)
    {
        try
        {
            // Read the Tools setting from settings table
            using var readCmd = connection.CreateCommand();
            readCmd.CommandText = "SELECT value FROM settings WHERE key = @key";
            readCmd.Parameters.AddWithValue("@key", "Tools");

            var toolsJson = readCmd.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(toolsJson))
            {
                Logger.Log("No Tools settings found to migrate");
                return;
            }

            // Parse the tools JSON array
            var toolsArray = JArray.Parse(toolsJson);
            if (toolsArray.Count == 0)
            {
                Logger.Log("No tools found to migrate");
                return;
            }

            // Migrate each tool to plugin_data table
            var pluginService = new PluginDataService(connection);
            var migratedCount = 0;

            for (int i = 0; i < toolsArray.Count; i++)
            {
                try
                {
                    var toolToken = toolsArray[i];
                    var toolName = toolToken["Name"]?.ToString() ?? $"Tool{i + 1}";
                    var isEnabled = toolToken["IsEnabled"]?.ToObject<bool>() ?? true;
                    var toolJson = toolToken.ToString();
                    
                    var pluginId = pluginService.AddPlugin(
                        name: toolName,
                        type: "Tool",
                        configData: toolJson,
                        enabled: isEnabled,
                        displayOrder: i
                    );
                    
                    if (pluginId > 0)
                    {
                        migratedCount++;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to migrate a tool: {ex.Message}");
                }
            }

            Logger.Log($"Migrated {migratedCount} tools to plugin_data table");

            // Note: We keep the tools in settings table for backward compatibility
            // They will be ignored when loading if plugin_data table has data
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to migrate plugin data: {ex.Message}");
        }
    }

    /// <summary>
    /// Apply migration to version 5: Migrate plugin configuration files to plugin_data table
    /// </summary>
    private void ApplyMigrationV5(SqliteConnection connection)
    {
        Logger.Log("Applying migration to version 5: Migrating plugin configuration files to plugin_data table");
        MigratePluginConfigFiles(connection);
    }

    /// <summary>
    /// Migrate plugin configuration files to plugin_data table
    /// This method can be called independently to check for new plugin config files
    /// </summary>
    public void MigratePluginConfigFiles(SqliteConnection connection)
    {        try
        {
            // Get plugin data directory path
            var pluginDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "VPetLLM",
                "Plugin",
                "PluginData"
            );

            if (!Directory.Exists(pluginDataPath))
            {
                Logger.Log($"Plugin data directory not found: {pluginDataPath}");
                return;
            }

            var pluginService = new PluginDataService(connection);
            var migratedCount = 0;

            // Scan each plugin subdirectory
            var pluginDirs = Directory.GetDirectories(pluginDataPath);
            Logger.Log($"Found {pluginDirs.Length} plugin directories to scan");

            foreach (var pluginDir in pluginDirs)
            {
                try
                {
                    var pluginName = Path.GetFileName(pluginDir);
                    
                    // Try to find the plugin configuration file
                    // Common patterns: {PluginName}.json, {PluginName}Plugin.json, {PluginName}Settings.json
                    var possibleFiles = new[]
                    {
                        Path.Combine(pluginDir, $"{pluginName}.json"),
                        Path.Combine(pluginDir, $"{pluginName}Plugin.json"),
                        Path.Combine(pluginDir, $"{pluginName}Settings.json")
                    };

                    string configFile = null;
                    foreach (var file in possibleFiles)
                    {
                        if (File.Exists(file))
                        {
                            configFile = file;
                            break;
                        }
                    }

                    // If no match found, try to find any .json file (excluding .cache.json)
                    if (configFile == null)
                    {
                        var jsonFiles = Directory.GetFiles(pluginDir, "*.json")
                            .Where(f => !f.EndsWith(".cache.json", StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                        
                        if (jsonFiles.Length > 0)
                        {
                            configFile = jsonFiles[0];
                        }
                    }

                    if (configFile == null)
                    {
                        Logger.Log($"No configuration file found for plugin: {pluginName}");
                        continue;
                    }

                    // Read the configuration file
                    var configJson = File.ReadAllText(configFile);
                    if (string.IsNullOrWhiteSpace(configJson))
                    {
                        Logger.Log($"Empty configuration file for plugin: {pluginName}");
                        continue;
                    }

                    // Check if plugin config already exists in database
                    var existingPlugin = pluginService.GetPluginByName(pluginName, "Plugin");
                    int pluginId;
                    
                    if (existingPlugin != null)
                    {
                        // Update existing plugin config
                        pluginService.UpdatePlugin(existingPlugin.Id, configJson);
                        pluginId = existingPlugin.Id;
                        Logger.Log($"Updated plugin config: {pluginName} from {Path.GetFileName(configFile)}");
                    }
                    else
                    {
                        // Insert new plugin config
                        pluginId = pluginService.AddPlugin(
                            name: pluginName,
                            type: "Plugin",
                            configData: configJson,
                            enabled: true,
                            displayOrder: migratedCount
                        );
                        Logger.Log($"Migrated plugin config: {pluginName} from {Path.GetFileName(configFile)}");
                    }

                    if (pluginId > 0)
                    {
                        migratedCount++;
                        
                        // Delete the JSON file after successful migration
                        try
                        {
                            File.Delete(configFile);
                            Logger.Log($"Deleted migrated config file: {Path.GetFileName(configFile)}");
                        }
                        catch (Exception deleteEx)
                        {
                            Logger.Log($"Warning: Failed to delete config file {Path.GetFileName(configFile)}: {deleteEx.Message}");
                            // Continue anyway - file deletion is not critical
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to migrate plugin config from {Path.GetFileName(pluginDir)}: {ex.Message}");
                }
            }

            Logger.Log($"Migration to version 5 completed: Migrated {migratedCount} plugin configurations");
        }
        catch (Exception ex)
        {
            Logger.Log($"Warning: Migration to version 5 encountered errors: {ex.Message}");
            // Continue anyway - this is not critical
        }
    }
}
