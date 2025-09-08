namespace VPetLLM.Core
{
    public interface IActionPlugin : IVPetLLMPlugin
    {
        System.Threading.Tasks.Task<string> Function(string arguments);
    }
}