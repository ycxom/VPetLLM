using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers
{
    public enum ActionType
    {
        State,
        Body,
        Talk,
        Plugin,
        Tool
    }

    public interface IActionHandler
    {
        string Keyword { get; }
        ActionType ActionType { get; }
        string Description { get; }
       Task Execute(int value, IMainWindow mainWindow);
      Task Execute(string value, IMainWindow mainWindow);
      Task Execute(IMainWindow mainWindow);
      int GetAnimationDuration(string animationName);
  }
}