namespace VPetLLM.Models
{
    /// <summary>
    /// Represents the current state of TTS processing
    /// </summary>
    public enum TTSState
    {
        /// <summary>
        /// TTS system is idle, not processing anything
        /// </summary>
        Idle,

        /// <summary>
        /// TTS is currently processing a request
        /// </summary>
        Processing,

        /// <summary>
        /// TTS is streaming audio output
        /// </summary>
        Streaming,

        /// <summary>
        /// TTS processing completed successfully
        /// </summary>
        Completed,

        /// <summary>
        /// TTS processing encountered an error
        /// </summary>
        Error,

        /// <summary>
        /// TTS processing was cancelled
        /// </summary>
        Cancelled,

        /// <summary>
        /// TTS processing timed out
        /// </summary>
        TimedOut
    }

    /// <summary>
    /// Detailed TTS state information
    /// </summary>
    public class TTSStateInfo
    {
        /// <summary>
        /// Current TTS state
        /// </summary>
        public TTSState State { get; set; }

        /// <summary>
        /// Text being processed
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// Progress percentage (0-100)
        /// </summary>
        public int ProgressPercent { get; set; }

        /// <summary>
        /// Error message if state is Error
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Audio file path if TTS completed successfully
        /// </summary>
        public string AudioFilePath { get; set; }

        /// <summary>
        /// Duration of the audio in milliseconds
        /// </summary>
        public int AudioDurationMs { get; set; }

        public TTSStateInfo()
        {
            State = TTSState.Idle;
            ProgressPercent = 0;
        }

        public TTSStateInfo(TTSState state, string text = null)
            : this()
        {
            State = state;
            Text = text;
        }
    }
}