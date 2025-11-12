using System;

namespace VPetLLM.Core
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
        /// </summary>
        public int Weight { get; set; }
        
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
