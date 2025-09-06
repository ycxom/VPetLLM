namespace VPetLLM.Core
{
    public interface IActionPlugin : IVPetLLMPlugin
    {
        void Invoke();
    }
}