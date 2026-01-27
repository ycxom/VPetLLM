using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace VPetLLM.Infrastructure.Configuration;

/// <summary>
/// Service for managing plugin/tool data in the database
/// </summary>
public class PluginDataService
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new object();

    public PluginDataService(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>
    /// Get all plugins/tools of a specific type
    /// </summary>
    /// <param name="type">Plugin type (Tool, Plugin, etc.)</param>
    /// <param name="enabledOnly">If true, only return enabled items</param>
    /// <returns>List of plugin data</returns>
    public List<PluginData> GetPlugins(string type, bool enabledOnly = false)
    {
        lock (_lock)
        {
            try
            {
                var plugins = new List<PluginData>();

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = enabledOnly
                    ? "SELECT id, name, type, config_data, enabled, display_order, created_at, updated_at FROM plugin_data WHERE type = @type AND enabled = 1 ORDER BY display_order, id"
                    : "SELECT id, name, type, config_data, enabled, display_order, created_at, updated_at FROM plugin_data WHERE type = @type ORDER BY display_order, id";
                
                cmd.Parameters.AddWithValue("@type", type);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    plugins.Add(new PluginData
                    {
                        Id = reader.GetInt32(0),
                        Name = reader.GetString(1),
                        Type = reader.GetString(2),
                        ConfigData = reader.GetString(3),
                        Enabled = reader.GetInt32(4) == 1,
                        DisplayOrder = reader.GetInt32(5),
                        CreatedAt = reader.GetDateTime(6),
                        UpdatedAt = reader.GetDateTime(7)
                    });
                }

                return plugins;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get plugins for type '{type}': {ex.Message}");
                return new List<PluginData>();
            }
        }
    }

    /// <summary>
    /// Get all plugins/tools
    /// </summary>
    /// <param name="enabledOnly">If true, only return enabled items</param>
    /// <returns>List of all plugin data</returns>
    public List<PluginData> GetAllPlugins(bool enabledOnly = false)
    {
        lock (_lock)
        {
            try
            {
                var plugins = new List<PluginData>();

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = enabledOnly
                    ? "SELECT id, name, type, config_data, enabled, display_order, created_at, updated_at FROM plugin_data WHERE enabled = 1 ORDER BY type, display_order, id"
                    : "SELECT id, name, type, config_data, enabled, display_order, created_at, updated_at FROM plugin_data ORDER BY type, display_order, id";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    plugins.Add(new PluginData
                    {
                        Id = reader.GetInt32(0),
                        Name = reader.GetString(1),
                        Type = reader.GetString(2),
                        ConfigData = reader.GetString(3),
                        Enabled = reader.GetInt32(4) == 1,
                        DisplayOrder = reader.GetInt32(5),
                        CreatedAt = reader.GetDateTime(6),
                        UpdatedAt = reader.GetDateTime(7)
                    });
                }

                return plugins;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get all plugins: {ex.Message}");
                return new List<PluginData>();
            }
        }
    }

    /// <summary>
    /// Get a specific plugin/tool by ID
    /// </summary>
    /// <param name="id">Plugin ID</param>
    /// <returns>Plugin data or null if not found</returns>
    public PluginData? GetPluginById(int id)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT id, name, type, config_data, enabled, display_order, created_at, updated_at FROM plugin_data WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", id);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    return new PluginData
                    {
                        Id = reader.GetInt32(0),
                        Name = reader.GetString(1),
                        Type = reader.GetString(2),
                        ConfigData = reader.GetString(3),
                        Enabled = reader.GetInt32(4) == 1,
                        DisplayOrder = reader.GetInt32(5),
                        CreatedAt = reader.GetDateTime(6),
                        UpdatedAt = reader.GetDateTime(7)
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get plugin by ID {id}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Add a new plugin/tool
    /// </summary>
    /// <param name="name">Plugin name</param>
    /// <param name="type">Plugin type</param>
    /// <param name="configData">Configuration data as JSON string</param>
    /// <param name="enabled">Whether the plugin is enabled</param>
    /// <param name="displayOrder">Display order</param>
    /// <returns>ID of the newly created plugin, or -1 if failed</returns>
    public int AddPlugin(string name, string type, string configData, bool enabled = true, int displayOrder = 0)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO plugin_data (name, type, config_data, enabled, display_order, created_at, updated_at)
                    VALUES (@name, @type, @config_data, @enabled, @display_order, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                    SELECT last_insert_rowid();
                ";
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@type", type);
                cmd.Parameters.AddWithValue("@config_data", configData);
                cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("@display_order", displayOrder);

                var result = cmd.ExecuteScalar();
                var pluginId = Convert.ToInt32(result);

                Logger.Log($"Added new plugin '{name}' (type: {type}) with ID {pluginId}");
                return pluginId;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to add plugin '{name}': {ex.Message}");
                return -1;
            }
        }
    }

    /// <summary>
    /// Add a new plugin/tool from a typed object
    /// </summary>
    /// <typeparam name="T">Plugin configuration type</typeparam>
    /// <param name="name">Plugin name</param>
    /// <param name="type">Plugin type</param>
    /// <param name="config">Plugin configuration object</param>
    /// <param name="enabled">Whether the plugin is enabled</param>
    /// <param name="displayOrder">Display order</param>
    /// <returns>ID of the newly created plugin, or -1 if failed</returns>
    public int AddPlugin<T>(string name, string type, T config, bool enabled = true, int displayOrder = 0) where T : class
    {
        var configData = JsonConvert.SerializeObject(config);
        return AddPlugin(name, type, configData, enabled, displayOrder);
    }

    /// <summary>
    /// Update an existing plugin/tool
    /// </summary>
    /// <param name="id">Plugin ID</param>
    /// <param name="configData">Updated configuration data as JSON string</param>
    /// <returns>True if successful, false otherwise</returns>
    public bool UpdatePlugin(int id, string configData)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    UPDATE plugin_data 
                    SET config_data = @config_data, updated_at = CURRENT_TIMESTAMP
                    WHERE id = @id
                ";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@config_data", configData);

                var rowsAffected = cmd.ExecuteNonQuery();
                if (rowsAffected > 0)
                {
                    Logger.Log($"Updated plugin ID {id}");
                    return true;
                }

                Logger.Log($"Plugin ID {id} not found");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to update plugin ID {id}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Update an existing plugin/tool from a typed object
    /// </summary>
    /// <typeparam name="T">Plugin configuration type</typeparam>
    /// <param name="id">Plugin ID</param>
    /// <param name="config">Updated plugin configuration object</param>
    /// <returns>True if successful, false otherwise</returns>
    public bool UpdatePlugin<T>(int id, T config) where T : class
    {
        var configData = JsonConvert.SerializeObject(config);
        return UpdatePlugin(id, configData);
    }

    /// <summary>
    /// Enable a plugin/tool
    /// </summary>
    /// <param name="id">Plugin ID</param>
    /// <returns>True if successful, false otherwise</returns>
    public bool EnablePlugin(int id)
    {
        return SetPluginEnabled(id, true);
    }

    /// <summary>
    /// Disable a plugin/tool
    /// </summary>
    /// <param name="id">Plugin ID</param>
    /// <returns>True if successful, false otherwise</returns>
    public bool DisablePlugin(int id)
    {
        return SetPluginEnabled(id, false);
    }

    /// <summary>
    /// Set plugin enabled status
    /// </summary>
    private bool SetPluginEnabled(int id, bool enabled)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    UPDATE plugin_data 
                    SET enabled = @enabled, updated_at = CURRENT_TIMESTAMP
                    WHERE id = @id
                ";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);

                var rowsAffected = cmd.ExecuteNonQuery();
                if (rowsAffected > 0)
                {
                    Logger.Log($"{(enabled ? "Enabled" : "Disabled")} plugin ID {id}");
                    return true;
                }

                Logger.Log($"Plugin ID {id} not found");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to {(enabled ? "enable" : "disable")} plugin ID {id}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Delete a plugin/tool
    /// </summary>
    /// <param name="id">Plugin ID</param>
    /// <returns>True if successful, false otherwise</returns>
    public bool DeletePlugin(int id)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "DELETE FROM plugin_data WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", id);

                var rowsAffected = cmd.ExecuteNonQuery();
                if (rowsAffected > 0)
                {
                    Logger.Log($"Deleted plugin ID {id}");
                    return true;
                }

                Logger.Log($"Plugin ID {id} not found");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to delete plugin ID {id}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Get count of plugins/tools for a type
    /// </summary>
    /// <param name="type">Plugin type</param>
    /// <param name="enabledOnly">If true, only count enabled plugins</param>
    /// <returns>Number of plugins</returns>
    public int GetPluginCount(string type, bool enabledOnly = false)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = enabledOnly
                    ? "SELECT COUNT(*) FROM plugin_data WHERE type = @type AND enabled = 1"
                    : "SELECT COUNT(*) FROM plugin_data WHERE type = @type";
                
                cmd.Parameters.AddWithValue("@type", type);

                var result = cmd.ExecuteScalar();
                return Convert.ToInt32(result);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get plugin count for type '{type}': {ex.Message}");
                return 0;
            }
        }
    }

    /// <summary>
    /// Get a typed plugin configuration by ID
    /// </summary>
    /// <typeparam name="T">Plugin configuration type</typeparam>
    /// <param name="id">Plugin ID</param>
    /// <returns>Deserialized plugin configuration or null if not found</returns>
    public T? GetPluginConfig<T>(int id) where T : class
    {
        var plugin = GetPluginById(id);
        if (plugin == null)
            return null;

        try
        {
            return JsonConvert.DeserializeObject<T>(plugin.ConfigData);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to deserialize plugin config for ID {id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get all typed plugin configurations for a type
    /// </summary>
    /// <typeparam name="T">Plugin configuration type</typeparam>
    /// <param name="type">Plugin type</param>
    /// <param name="enabledOnly">If true, only return enabled plugins</param>
    /// <returns>List of deserialized plugin configurations</returns>
    public List<T> GetPluginConfigs<T>(string type, bool enabledOnly = false) where T : class
    {
        var plugins = GetPlugins(type, enabledOnly);
        var configs = new List<T>();

        foreach (var plugin in plugins)
        {
            try
            {
                var config = JsonConvert.DeserializeObject<T>(plugin.ConfigData);
                if (config != null)
                {
                    configs.Add(config);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to deserialize plugin config for ID {plugin.Id}: {ex.Message}");
            }
        }

        return configs;
    }

    /// <summary>
    /// Get a plugin by name
    /// </summary>
    /// <param name="name">Plugin name</param>
    /// <param name="type">Optional plugin type filter</param>
    /// <returns>Plugin data or null if not found</returns>
    public PluginData? GetPluginByName(string name, string? type = null)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                if (type != null)
                {
                    cmd.CommandText = "SELECT id, name, type, config_data, enabled, display_order, created_at, updated_at FROM plugin_data WHERE name = @name AND type = @type";
                    cmd.Parameters.AddWithValue("@name", name);
                    cmd.Parameters.AddWithValue("@type", type);
                }
                else
                {
                    cmd.CommandText = "SELECT id, name, type, config_data, enabled, display_order, created_at, updated_at FROM plugin_data WHERE name = @name";
                    cmd.Parameters.AddWithValue("@name", name);
                }

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    return new PluginData
                    {
                        Id = reader.GetInt32(0),
                        Name = reader.GetString(1),
                        Type = reader.GetString(2),
                        ConfigData = reader.GetString(3),
                        Enabled = reader.GetInt32(4) == 1,
                        DisplayOrder = reader.GetInt32(5),
                        CreatedAt = reader.GetDateTime(6),
                        UpdatedAt = reader.GetDateTime(7)
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get plugin by name '{name}': {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Get a typed plugin configuration by name
    /// </summary>
    /// <typeparam name="T">Plugin configuration type</typeparam>
    /// <param name="name">Plugin name</param>
    /// <param name="type">Optional plugin type filter</param>
    /// <returns>Deserialized plugin configuration or null if not found</returns>
    public T? GetPluginConfigByName<T>(string name, string? type = null) where T : class
    {
        var plugin = GetPluginByName(name, type);
        if (plugin == null)
            return null;

        try
        {
            return JsonConvert.DeserializeObject<T>(plugin.ConfigData);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to deserialize plugin config for '{name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Update or insert a plugin configuration by name
    /// </summary>
    /// <param name="name">Plugin name</param>
    /// <param name="type">Plugin type</param>
    /// <param name="configData">Configuration data as JSON string</param>
    /// <param name="enabled">Whether the plugin is enabled</param>
    /// <returns>Plugin ID (existing or newly created)</returns>
    public int UpsertPluginByName(string name, string type, string configData, bool enabled = true)
    {
        lock (_lock)
        {
            var existing = GetPluginByName(name, type);
            if (existing != null)
            {
                // Update existing
                UpdatePlugin(existing.Id, configData);
                return existing.Id;
            }
            else
            {
                // Insert new
                return AddPlugin(name, type, configData, enabled);
            }
        }
    }

    /// <summary>
    /// Update or insert a plugin configuration by name from a typed object
    /// </summary>
    /// <typeparam name="T">Plugin configuration type</typeparam>
    /// <param name="name">Plugin name</param>
    /// <param name="type">Plugin type</param>
    /// <param name="config">Plugin configuration object</param>
    /// <param name="enabled">Whether the plugin is enabled</param>
    /// <returns>Plugin ID (existing or newly created)</returns>
    public int UpsertPluginByName<T>(string name, string type, T config, bool enabled = true) where T : class
    {
        var configData = JsonConvert.SerializeObject(config);
        return UpsertPluginByName(name, type, configData, enabled);
    }
}
