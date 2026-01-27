using Microsoft.Data.Sqlite;

namespace VPetLLM.Infrastructure.Configuration;

/// <summary>
/// Manages database schema creation and versioning
/// </summary>
public class SchemaManager
{
    private const int CurrentSchemaVersion = 2;

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
}
