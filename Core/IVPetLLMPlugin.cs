using System.Threading.Tasks;

namespace VPetLLM.Core
{
    public interface IVPetLLMPlugin
    {
        string Name { get; }
        string Description { get; }
        string Parameters { get; }
        bool Enabled { get; set; }
        string FilePath { get; set; }
        Task<string> Function(string arguments);
        void Initialize(VPetLLM plugin);
        void Unload();
        void Log(string message);
    }
}