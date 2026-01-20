namespace VPetLLM.Core.Abstractions.Interfaces.Plugin
{
    /// <summary>
    /// Represents a plugin that can provide dynamic information to be included in the system prompt.
    /// </summary>
    public interface IDynamicInfoPlugin : IVPetLLMPlugin
    {
        /// <summary>
        /// Gets the dynamic information to be added to the system prompt.
        /// </summary>
        /// <returns>A string containing the dynamic information, or null/empty if there is none.</returns>
        string GetDynamicInfo();
    }
}