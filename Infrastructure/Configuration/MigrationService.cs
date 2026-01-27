using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VPetLLM.Infrastructure.Configuration;

/// <summary>
/// Service for migrating settings from JSON to SQLite
/// </summary>
public class MigrationService
{
    private readonly string _jsonPath;
    private readonly ISettingStorage _destination;

    public MigrationService(ISettingStorage source, ISettingStorage destination)
    {
        _jsonPath = source.GetStorageLocation();
        _destination = destination;
    }

    /// <summary>
    /// Migrate settings from JSON file to SQLite storage
    /// </summary>
    /// <returns>Migration result with statistics</returns>
    public SettingsMigrationResult Migrate()
    {
        var result = new SettingsMigrationResult
        {
            SourceLocation = _jsonPath,
            DestinationLocation = _destination.GetStorageLocation()
        };

        var stopwatch = Stopwatch.StartNew();

        try
        {
            Logger.Log($"Starting migration from {result.SourceLocation} to {result.DestinationLocation}");

            // Read JSON file directly
            if (!File.Exists(_jsonPath))
            {
                result.Errors.Add("JSON file not found");
                Logger.Log("Migration failed: JSON file not found");
                return result;
            }

            var jsonContent = File.ReadAllText(_jsonPath);
            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                result.Errors.Add("JSON file is empty");
                Logger.Log("Migration failed: JSON file is empty");
                return result;
            }

            Logger.Log($"Read {jsonContent.Length} characters from JSON file");

            // Parse JSON to JObject (doesn't require Setting constructor)
            var jsonObject = JObject.Parse(jsonContent);
            Logger.Log($"Parsed JSON with {jsonObject.Count} properties");

            // Create a temporary Setting instance for migration
            // We'll use reflection to set properties without calling constructor
            var tempSettings = CreateSettingFromJson(jsonObject);
            
            if (tempSettings == null)
            {
                result.Errors.Add("Failed to create settings object from JSON");
                Logger.Log("Migration failed: Could not create settings from JSON");
                return result;
            }

            // Save to SQLite
            _destination.Save(tempSettings);
            result.SettingsMigrated = jsonObject.Count;
            Logger.Log($"Saved {result.SettingsMigrated} settings to database");

            // Create backup of JSON file
            try
            {
                var backupPath = _jsonPath + ".backup";
                File.Copy(_jsonPath, backupPath, overwrite: true);
                Logger.Log($"Created backup of JSON file: {backupPath}");
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to create JSON backup: {ex.Message}");
                Logger.Log($"Warning: Failed to create JSON backup: {ex.Message}");
            }

            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;

            if (result.Success)
            {
                Logger.Log($"Migration completed successfully in {result.Duration.TotalSeconds:F2} seconds");
            }
            else
            {
                Logger.Log($"Migration completed with errors: {string.Join(", ", result.Errors)}");
            }

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
            result.Errors.Add($"Migration failed: {ex.Message}");
            Logger.Log($"Migration failed: {ex.Message}");
            Logger.Log($"Stack trace: {ex.StackTrace}");
            return result;
        }
    }

    /// <summary>
    /// Create a Setting object from JSON without calling constructor
    /// </summary>
    private Setting? CreateSettingFromJson(JObject jsonObject)
    {
        try
        {
            // Use JsonConvert.PopulateObject with a dummy path to avoid constructor issues
            // We create a minimal Setting instance first
            var dummyPath = Path.GetTempPath();
            var setting = new Setting(dummyPath);
            
            // Now populate it with JSON data
            JsonConvert.PopulateObject(jsonObject.ToString(), setting);
            
            Logger.Log("Successfully created Setting object from JSON");
            return setting;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to create Setting from JSON: {ex.Message}");
            return null;
        }
    }
}
