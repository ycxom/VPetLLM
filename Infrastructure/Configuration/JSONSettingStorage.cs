using Newtonsoft.Json;

namespace VPetLLM.Infrastructure.Configuration;

/// <summary>
/// JSON-based settings storage implementation (fallback)
/// </summary>
public class JSONSettingStorage : ISettingStorage
{
    private readonly string _jsonPath;

    /// <summary>
    /// Create JSON storage with full file path
    /// </summary>
    /// <param name="jsonFilePath">Full path to the JSON file (e.g., "C:\path\to\VPetLLM.json")</param>
    public JSONSettingStorage(string jsonFilePath)
    {
        Logger.Log($"JSONSettingStorage constructor called with: {jsonFilePath ?? "NULL"}");
        
        // If the path ends with .json, use it directly; otherwise append VPetLLM.json
        if (jsonFilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            _jsonPath = jsonFilePath;
            Logger.Log($"Using path as-is (ends with .json): {_jsonPath}");
        }
        else
        {
            _jsonPath = Path.Combine(jsonFilePath, "VPetLLM.json");
            Logger.Log($"Appending VPetLLM.json to path: {_jsonPath}");
        }
    }

    public bool Initialize(string? instanceId = null)
    {
        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(_jsonPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Logger.Log($"JSON storage initialized: {_jsonPath}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to initialize JSON storage: {ex.Message}");
            return false;
        }
    }

    public T? Load<T>() where T : class
    {
        try
        {
            Logger.Log($"JSONSettingStorage.Load: Attempting to load from: {_jsonPath}");
            
            if (!File.Exists(_jsonPath))
            {
                Logger.Log($"JSON file not found: {_jsonPath}");
                return null;
            }

            Logger.Log($"JSON file exists, reading content...");
            var json = File.ReadAllText(_jsonPath);
            
            if (string.IsNullOrWhiteSpace(json))
            {
                Logger.Log($"JSON file is empty: {_jsonPath}");
                return null;
            }

            Logger.Log($"JSON content length: {json.Length} characters, deserializing...");
            var settings = JsonConvert.DeserializeObject<T>(json);
            
            if (settings == null)
            {
                Logger.Log($"Deserialization returned null");
                return null;
            }
            
            Logger.Log($"Loaded settings from JSON: {_jsonPath}");
            return settings;
        }
        catch (JsonException ex)
        {
            Logger.Log($"Failed to parse JSON settings: {ex.Message}");
            Logger.Log($"Stack trace: {ex.StackTrace}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to load JSON settings: {ex.Message}");
            Logger.Log($"Stack trace: {ex.StackTrace}");
            return null;
        }
    }

    public void Save<T>(T settings) where T : class
    {
        try
        {
            var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
            File.WriteAllText(_jsonPath, json);
            
            Logger.Log($"Saved settings to JSON: {_jsonPath}");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to save JSON settings: {ex.Message}");
            throw new StorageException("Failed to save settings to JSON", ex);
        }
    }

    public bool IsAvailable()
    {
        try
        {
            var directory = Path.GetDirectoryName(_jsonPath);
            return !string.IsNullOrEmpty(directory) && Directory.Exists(directory);
        }
        catch
        {
            return false;
        }
    }

    public string GetStorageLocation()
    {
        return _jsonPath;
    }
}
