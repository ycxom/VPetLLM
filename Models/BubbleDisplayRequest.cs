namespace VPetLLM.Models
{
    /// <summary>
    /// Represents a bubble display request with all necessary parameters
    /// </summary>
    public class BubbleDisplayRequest
    {
        /// <summary>
        /// Text to display in the bubble
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// Animation name for the bubble display
        /// </summary>
        public string AnimationName { get; set; }

        /// <summary>
        /// Whether to force display (override current animation)
        /// </summary>
        public bool Force { get; set; }

        /// <summary>
        /// TTS options for this request
        /// </summary>
        public TTSOptions TTSOptions { get; set; }

        /// <summary>
        /// When this request was created
        /// </summary>
        public DateTime RequestTime { get; set; }

        /// <summary>
        /// Unique identifier for this request
        /// </summary>
        public string RequestId { get; set; }

        /// <summary>
        /// Priority of this request (higher values = higher priority)
        /// </summary>
        public int Priority { get; set; }

        public BubbleDisplayRequest()
        {
            RequestTime = DateTime.Now;
            RequestId = Guid.NewGuid().ToString();
            Priority = 0;
        }

        public BubbleDisplayRequest(string text, string animationName = null, bool force = false)
            : this()
        {
            Text = text;
            AnimationName = animationName;
            Force = force;
        }
    }
}