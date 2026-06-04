namespace VPetLLM.Core.Data.Models
{
    /// <summary>
    /// Represents a user-defined skill that the AI can create, modify, delete, and invoke.
    /// Skills are data-driven: they execute by expanding their ActionTemplate into existing command format.
    /// </summary>
    public class Skill
    {
        /// <summary>
        /// Auto-incrementing unique identifier
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Unique skill name (used as the identifier in commands, e.g. "GreetMorning")
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable description of what the skill does (for the AI to understand)
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Hint about when this skill should be triggered (helps the AI decide when to use it)
        /// </summary>
        public string TriggerHint { get; set; } = string.Empty;

        /// <summary>
        /// The action template using existing command format.
        /// When invoked, the ActionProcessor will parse and execute these commands.
        /// Example: "&lt;|say_begin|&gt; \"Good morning!\" &lt;|say_end|&gt;&lt;|plugin_Weather_begin|&gt; city(Beijing) &lt;|plugin_Weather_end|&gt;"
        /// Supports {param} placeholders that are substituted at call time.
        /// </summary>
        public string ActionTemplate { get; set; } = string.Empty;

        /// <summary>
        /// Whether this skill is currently enabled
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Number of times this skill has been used (for tracking effectiveness)
        /// </summary>
        public int UseCount { get; set; }

        /// <summary>
        /// Timestamp when the skill was created
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Timestamp when the skill was last modified
        /// </summary>
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// Timestamp when the skill was last used (null if never)
        /// </summary>
        public DateTime? LastUsed { get; set; }
    }
}
