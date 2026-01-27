namespace VPetLLM.Infrastructure.Configuration;

/// <summary>
/// Represents a provider node stored in the database
/// </summary>
public class ProviderNode
{
    /// <summary>
    /// Unique identifier for the node
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Provider type (OpenAI, Gemini, Ollama, etc.)
    /// </summary>
    public string ProviderType { get; set; } = string.Empty;

    /// <summary>
    /// Node configuration data as JSON string
    /// </summary>
    public string NodeData { get; set; } = string.Empty;

    /// <summary>
    /// Whether the node is enabled (1) or disabled (0)
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When the node was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the node was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}
