namespace VPetLLM.Core.Abstractions.Interfaces.Plugin
{
    public interface IActionPlugin : IVPetLLMPlugin
    {
        System.Threading.Tasks.Task<string> Function(string arguments);
    }
}