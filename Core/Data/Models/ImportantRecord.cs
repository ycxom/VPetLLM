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

        /// <summary>
        /// 被记忆检索命中的累计次数。命中越多，权重衰减越慢。
        /// </summary>
        public int AccessCount { get; set; }

        /// <summary>
        /// 最后一次被记忆检索命中的时间。null 表示从未被召回过。
        /// </summary>
        public DateTime? LastAccessAt { get; set; }

        /// <summary>
        /// 排序用的时间基准：取「最后召回」与「创建」中较晚的一个，
        /// 使被频繁提起的记忆在新鲜度维度上不会显得陈旧。
        /// </summary>
        public DateTime ReferenceTime => LastAccessAt is DateTime t && t > CreatedAt ? t : CreatedAt;
    }
}
