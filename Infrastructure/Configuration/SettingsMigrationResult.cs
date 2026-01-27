namespace VPetLLM.Infrastructure.Configuration;

/// <summary>
/// Result of a settings storage migration operation
/// </summary>
public class SettingsMigrationResult
{
    /// <summary>
    /// Number of settings successfully migrated
    /// </summary>
    public int SettingsMigrated { get; set; }

    /// <summary>
    /// List of errors encountered during migration
    /// </summary>
    public List<string> Errors { get; set; } = new List<string>();

    /// <summary>
    /// Duration of the migration operation
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Whether the migration was successful
    /// </summary>
    public bool Success => Errors.Count == 0;

    /// <summary>
    /// Source storage location
    /// </summary>
    public string? SourceLocation { get; set; }

    /// <summary>
    /// Destination storage location
    /// </summary>
    public string? DestinationLocation { get; set; }
}
