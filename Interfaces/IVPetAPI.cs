namespace VPetLLM.Interfaces
{
    /// <summary>
    /// Interface for VPet API abstraction
    /// Provides a clean abstraction over VPet's bubble display functionality
    /// </summary>
    public interface IVPetAPI
    {
        /// <summary>
        /// Display text bubble using VPet's Say method
        /// </summary>
        /// <param name="text">Text to display</param>
        /// <param name="graphname">Animation name (optional)</param>
        /// <param name="force">Whether to force display</param>
        void Say(string text, string graphname = null, bool force = false);

        /// <summary>
        /// Display text bubble using SayInfoWithOutStream for synchronous operation
        /// </summary>
        /// <param name="text">Text to display</param>
        /// <param name="graphname">Animation name (optional)</param>
        /// <returns>Task representing the async operation</returns>
        Task SayInfoWithOutStreamAsync(string text, string graphname = null);

        /// <summary>
        /// Display text bubble using SayInfoWithStream for asynchronous streaming
        /// </summary>
        /// <param name="text">Text to display</param>
        /// <param name="graphname">Animation name (optional)</param>
        /// <returns>Task representing the async operation</returns>
        Task SayInfoWithStreamAsync(string text, string graphname = null);

        /// <summary>
        /// Check if VPet API is available and ready
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Get the main window instance
        /// </summary>
        object GetMainWindow();
    }
}