namespace VPetLLM.Infrastructure.Configuration;

/// <summary>
/// Represents plugin/tool data stored in the database
/// </summary>
public class PluginData
{
    /// <summary>
    /// Unique identifier for the plugin/tool
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Plugin/Tool name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Plugin/Tool type (Tool, Plugin, etc.)
    /// </summary>
    public string Type { get; set; } = "Tool";

    /// <summary>
    /// Plugin/Tool configuration data as JSON string
    /// </summary>
    public string ConfigData { get; set; } = string.Empty;

    /// <summary>
    /// Whether the plugin/tool is enabled (1) or disabled (0)
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Display order
    /// </summary>
    public int DisplayOrder { get; set; } = 0;

    /// <summary>
    /// When the plugin/tool was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the plugin/tool was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}
