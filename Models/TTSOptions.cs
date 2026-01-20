namespace VPetLLM.Models
{
    /// <summary>
    /// Configuration options for TTS (Text-to-Speech) functionality
    /// </summary>
    public class TTSOptions
    {
        /// <summary>
        /// Whether TTS is enabled for this request
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Whether to use streaming TTS (SayInfoWithStream) or synchronous TTS (SayInfoWithOutStream)
        /// </summary>
        public bool UseStreaming { get; set; }

        /// <summary>
        /// Speech speed multiplier (1.0 = normal speed)
        /// </summary>
        public float Speed { get; set; } = 1.0f;

        /// <summary>
        /// Volume level (0.0 - 1.0)
        /// </summary>
        public float Volume { get; set; } = 1.0f;

        /// <summary>
        /// Voice identifier or name
        /// </summary>
        public string Voice { get; set; }

        /// <summary>
        /// Audio file path for pre-generated TTS
        /// </summary>
        public string AudioFilePath { get; set; }

        /// <summary>
        /// Whether to automatically hide bubble when TTS completes
        /// </summary>
        public bool AutoHideOnComplete { get; set; } = true;

        /// <summary>
        /// Maximum time to wait for TTS completion (in milliseconds)
        /// </summary>
        public int TimeoutMs { get; set; } = 30000; // 30 seconds

        public TTSOptions()
        {
            Enabled = false;
            UseStreaming = false;
        }

        public TTSOptions(bool enabled, bool useStreaming = false)
        {
            Enabled = enabled;
            UseStreaming = useStreaming;
        }
    }
}