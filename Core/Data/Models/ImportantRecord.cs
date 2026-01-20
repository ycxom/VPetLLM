namespace VPetLLM.Core.Data.Models
{
    /// <summary>
    /// Represents an important record with lifecycle management
    /// </summary>
    public class ImportantRecord
    {
        /// <summary>
        /// Auto-incrementing unique identifier
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// The content of the important information
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Weight value (0-10), decreases with each conversation turn
        /// Stored as double for precise decay control, displayed as rounded integer
        /// </summary>
        public double Weight { get; set; }

        /// <summary>
        /// Display weight as rounded integer for user interface
        /// </summary>
        public int DisplayWeight => (int)Math.Round(Weight);

        /// <summary>
        /// Timestamp when the record was created
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Timestamp when the record was last updated
        /// </summary>
        public DateTime UpdatedAt { get; set; }
    }
}
