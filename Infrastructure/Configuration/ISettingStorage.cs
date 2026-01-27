namespace VPetLLM.Infrastructure.Configuration;

/// <summary>
/// Interface for settings storage implementations (SQLite, JSON, etc.)
/// </summary>
public interface ISettingStorage
{
    /// <summary>
    /// Initialize the storage system
    /// </summary>
    /// <param name="instanceId">Optional instance ID for multi-instance support</param>
    /// <returns>True if initialization succeeded, false otherwise</returns>
    bool Initialize(string? instanceId = null);

    /// <summary>
    /// Load settings from storage
    /// </summary>
    /// <typeparam name="T">Type of settings object to load</typeparam>
    /// <returns>Loaded settings object or null if not found</returns>
    T? Load<T>() where T : class;

    /// <summary>
    /// Save settings to storage
    /// </summary>
    /// <typeparam name="T">Type of settings object to save</typeparam>
    /// <param name="settings">Settings object to save</param>
    void Save<T>(T settings) where T : class;

    /// <summary>
    /// Check if storage is available and accessible
    /// </summary>
    /// <returns>True if storage is available, false otherwise</returns>
    bool IsAvailable();

    /// <summary>
    /// Get the physical location of the storage
    /// </summary>
    /// <returns>Path to storage location</returns>
    string GetStorageLocation();
}
