using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers
{
    public interface IActionHandler
    {
        string Keyword { get; }
        void Execute(int value, IMainWindow mainWindow);
        void Execute(string value, IMainWindow mainWindow);
    }
}