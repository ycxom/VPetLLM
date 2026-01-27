using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace VPetLLM.Infrastructure.Configuration;

/// <summary>
/// Service for managing provider nodes in the database
/// </summary>
public class ProviderNodeService
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new object();

    public ProviderNodeService(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>
    /// Get all nodes for a specific provider type
    /// </summary>
    /// <param name="providerType">Provider type (OpenAI, Gemini, Ollama, etc.)</param>
    /// <param name="enabledOnly">If true, only return enabled nodes</param>
    /// <returns>List of provider nodes</returns>
    public List<ProviderNode> GetNodes(string providerType, bool enabledOnly = true)
    {
        lock (_lock)
        {
            try
            {
                var nodes = new List<ProviderNode>();

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = enabledOnly
                    ? "SELECT id, provider_type, node_data, enabled, created_at, updated_at FROM provider_nodes WHERE provider_type = @type AND enabled = 1 ORDER BY id"
                    : "SELECT id, provider_type, node_data, enabled, created_at, updated_at FROM provider_nodes WHERE provider_type = @type ORDER BY id";
                
                cmd.Parameters.AddWithValue("@type", providerType);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    nodes.Add(new ProviderNode
                    {
                        Id = reader.GetInt32(0),
                        ProviderType = reader.GetString(1),
                        NodeData = reader.GetString(2),
                        Enabled = reader.GetInt32(3) == 1,
                        CreatedAt = reader.GetDateTime(4),
                        UpdatedAt = reader.GetDateTime(5)
                    });
                }

                return nodes;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get nodes for provider '{providerType}': {ex.Message}");
                return new List<ProviderNode>();
            }
        }
    }

    /// <summary>
    /// Get a specific node by ID
    /// </summary>
    /// <param name="id">Node ID</param>
    /// <returns>Provider node or null if not found</returns>
    public ProviderNode? GetNodeById(int id)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT id, provider_type, node_data, enabled, created_at, updated_at FROM provider_nodes WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", id);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    return new ProviderNode
                    {
                        Id = reader.GetInt32(0),
                        ProviderType = reader.GetString(1),
                        NodeData = reader.GetString(2),
                        Enabled = reader.GetInt32(3) == 1,
                        CreatedAt = reader.GetDateTime(4),
                        UpdatedAt = reader.GetDateTime(5)
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get node by ID {id}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Add a new node
    /// </summary>
    /// <param name="providerType">Provider type</param>
    /// <param name="nodeData">Node configuration as JSON string</param>
    /// <param name="enabled">Whether the node is enabled</param>
    /// <returns>ID of the newly created node, or -1 if failed</returns>
    public int AddNode(string providerType, string nodeData, bool enabled = true)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO provider_nodes (provider_type, node_data, enabled, created_at, updated_at)
                    VALUES (@type, @data, @enabled, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                    SELECT last_insert_rowid();
                ";
                cmd.Parameters.AddWithValue("@type", providerType);
                cmd.Parameters.AddWithValue("@data", nodeData);
                cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);

                var result = cmd.ExecuteScalar();
                var nodeId = Convert.ToInt32(result);

                Logger.Log($"Added new node for provider '{providerType}' with ID {nodeId}");
                return nodeId;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to add node for provider '{providerType}': {ex.Message}");
                return -1;
            }
        }
    }

    /// <summary>
    /// Add a new node from a typed object
    /// </summary>
    /// <typeparam name="T">Node configuration type</typeparam>
    /// <param name="providerType">Provider type</param>
    /// <param name="nodeConfig">Node configuration object</param>
    /// <param name="enabled">Whether the node is enabled</param>
    /// <returns>ID of the newly created node, or -1 if failed</returns>
    public int AddNode<T>(string providerType, T nodeConfig, bool enabled = true) where T : class
    {
        var nodeData = JsonConvert.SerializeObject(nodeConfig);
        return AddNode(providerType, nodeData, enabled);
    }

    /// <summary>
    /// Update an existing node
    /// </summary>
    /// <param name="id">Node ID</param>
    /// <param name="nodeData">Updated node configuration as JSON string</param>
    /// <returns>True if successful, false otherwise</returns>
    public bool UpdateNode(int id, string nodeData)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    UPDATE provider_nodes 
                    SET node_data = @data, updated_at = CURRENT_TIMESTAMP
                    WHERE id = @id
                ";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@data", nodeData);

                var rowsAffected = cmd.ExecuteNonQuery();
                if (rowsAffected > 0)
                {
                    Logger.Log($"Updated node ID {id}");
                    return true;
                }

                Logger.Log($"Node ID {id} not found");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to update node ID {id}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Update an existing node from a typed object
    /// </summary>
    /// <typeparam name="T">Node configuration type</typeparam>
    /// <param name="id">Node ID</param>
    /// <param name="nodeConfig">Updated node configuration object</param>
    /// <returns>True if successful, false otherwise</returns>
    public bool UpdateNode<T>(int id, T nodeConfig) where T : class
    {
        var nodeData = JsonConvert.SerializeObject(nodeConfig);
        return UpdateNode(id, nodeData);
    }

    /// <summary>
    /// Enable a node
    /// </summary>
    /// <param name="id">Node ID</param>
    /// <returns>True if successful, false otherwise</returns>
    public bool EnableNode(int id)
    {
        return SetNodeEnabled(id, true);
    }

    /// <summary>
    /// Disable a node
    /// </summary>
    /// <param name="id">Node ID</param>
    /// <returns>True if successful, false otherwise</returns>
    public bool DisableNode(int id)
    {
        return SetNodeEnabled(id, false);
    }

    /// <summary>
    /// Set node enabled status
    /// </summary>
    private bool SetNodeEnabled(int id, bool enabled)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    UPDATE provider_nodes 
                    SET enabled = @enabled, updated_at = CURRENT_TIMESTAMP
                    WHERE id = @id
                ";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);

                var rowsAffected = cmd.ExecuteNonQuery();
                if (rowsAffected > 0)
                {
                    Logger.Log($"{(enabled ? "Enabled" : "Disabled")} node ID {id}");
                    return true;
                }

                Logger.Log($"Node ID {id} not found");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to {(enabled ? "enable" : "disable")} node ID {id}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Delete a node
    /// </summary>
    /// <param name="id">Node ID</param>
    /// <returns>True if successful, false otherwise</returns>
    public bool DeleteNode(int id)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "DELETE FROM provider_nodes WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", id);

                var rowsAffected = cmd.ExecuteNonQuery();
                if (rowsAffected > 0)
                {
                    Logger.Log($"Deleted node ID {id}");
                    return true;
                }

                Logger.Log($"Node ID {id} not found");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to delete node ID {id}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Get count of nodes for a provider type
    /// </summary>
    /// <param name="providerType">Provider type</param>
    /// <param name="enabledOnly">If true, only count enabled nodes</param>
    /// <returns>Number of nodes</returns>
    public int GetNodeCount(string providerType, bool enabledOnly = true)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = enabledOnly
                    ? "SELECT COUNT(*) FROM provider_nodes WHERE provider_type = @type AND enabled = 1"
                    : "SELECT COUNT(*) FROM provider_nodes WHERE provider_type = @type";
                
                cmd.Parameters.AddWithValue("@type", providerType);

                var result = cmd.ExecuteScalar();
                return Convert.ToInt32(result);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get node count for provider '{providerType}': {ex.Message}");
                return 0;
            }
        }
    }

    /// <summary>
    /// Get a typed node configuration by ID
    /// </summary>
    /// <typeparam name="T">Node configuration type</typeparam>
    /// <param name="id">Node ID</param>
    /// <returns>Deserialized node configuration or null if not found</returns>
    public T? GetNodeConfig<T>(int id) where T : class
    {
        var node = GetNodeById(id);
        if (node == null)
            return null;

        try
        {
            return JsonConvert.DeserializeObject<T>(node.NodeData);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to deserialize node config for ID {id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get all typed node configurations for a provider
    /// </summary>
    /// <typeparam name="T">Node configuration type</typeparam>
    /// <param name="providerType">Provider type</param>
    /// <param name="enabledOnly">If true, only return enabled nodes</param>
    /// <returns>List of deserialized node configurations</returns>
    public List<T> GetNodeConfigs<T>(string providerType, bool enabledOnly = true) where T : class
    {
        var nodes = GetNodes(providerType, enabledOnly);
        var configs = new List<T>();

        foreach (var node in nodes)
        {
            try
            {
                var config = JsonConvert.DeserializeObject<T>(node.NodeData);
                if (config != null)
                {
                    configs.Add(config);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to deserialize node config for ID {node.Id}: {ex.Message}");
            }
        }

        return configs;
    }
}
