using System.Threading.Tasks;

namespace VPetLLM.Core
{
    public interface IVPetLLMPlugin
    {
        string Name { get; }
        string Author { get; }
        string Description { get; }
        string Parameters { get; }
        string Examples { get; }
        bool Enabled { get; set; }
        string FilePath { get; set; }

        void Initialize(VPetLLM plugin);
        void Unload();
    }
}