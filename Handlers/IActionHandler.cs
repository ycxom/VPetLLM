using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers
{
    public enum ActionType
    {
        State,
        Body
    }

    public interface IActionHandler
    {
        string Keyword { get; }
        ActionType ActionType { get; }
        string Description { get; }
        void Execute(int value, IMainWindow mainWindow);
        void Execute(string value, IMainWindow mainWindow);
        void Execute(IMainWindow mainWindow);
    }
}