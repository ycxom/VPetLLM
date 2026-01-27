using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace VPetLLM.Infrastructure.Configuration;

/// <summary>
/// Helper class for loading and saving plugin configurations
/// Provides unified access to plugin configs stored in database or JSON files
/// </summary>
public static class PluginConfigHelper
{
    private static readonly string DatabasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "VPetLLM",
        "settings.db"
    );

    /// <summary>
    /// Load plugin configuration from database
    /// </summary>
    /// <typeparam name="T">Plugin settings type</typeparam>
    /// <param name="pluginName">Plugin name</param>
    /// <returns>Plugin settings, or default if not found</returns>
    public static T Load<T>(string pluginName) where T : class, new()
    {
        // Load from database
        try
        {
            if (File.Exists(DatabasePath))
            {
                using var connection = new SqliteConnection($"Data Source={DatabasePath}");
                connection.Open();

                var pluginService = new PluginDataService(connection);
                var config = pluginService.GetPluginConfigByName<T>(pluginName, "Plugin");

                if (config != null)
                {
                    Logger.Log($"{pluginName}: Settings loaded from database");
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"{pluginName}: Failed to load settings from database: {ex.Message}");
        }

        // Return default settings
        Logger.Log($"{pluginName}: Using default settings");
        return new T();
    }

    /// <summary>
    /// Save plugin configuration to database
    /// </summary>
    /// <typeparam name="T">Plugin settings type</typeparam>
    /// <param name="pluginName">Plugin name</param>
    /// <param name="config">Plugin settings</param>
    /// <returns>True if successful</returns>
    public static bool Save<T>(string pluginName, T config) where T : class
    {
        // Save to database
        try
        {
            if (!File.Exists(DatabasePath))
            {
                Logger.Log($"{pluginName}: Database not found, cannot save settings");
                return false;
            }

            using var connection = new SqliteConnection($"Data Source={DatabasePath}");
            connection.Open();

            var pluginService = new PluginDataService(connection);
            pluginService.UpsertPluginByName(pluginName, "Plugin", config, enabled: true);

            Logger.Log($"{pluginName}: Settings saved to database");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"{pluginName}: Failed to save settings to database: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Check if plugin configuration exists in database
    /// </summary>
    /// <param name="pluginName">Plugin name</param>
    /// <returns>True if exists</returns>
    public static bool ExistsInDatabase(string pluginName)
    {
        try
        {
            if (!File.Exists(DatabasePath))
                return false;

            using var connection = new SqliteConnection($"Data Source={DatabasePath}");
            connection.Open();

            var pluginService = new PluginDataService(connection);
            var plugin = pluginService.GetPluginByName(pluginName, "Plugin");

            return plugin != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get the database path
    /// </summary>
    public static string GetDatabasePath() => DatabasePath;
}
