using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers.State;

namespace VPetLLM.Handlers.Actions
{
    public class ExpHandler : IActionHandler
    {
        public string Keyword => "exp";
        public ActionType ActionType => ActionType.State;
        public ActionCategory Category => ActionCategory.StateBased;
        public string Description => PromptHelper.Get("Handler_Exp_Description", VPetLLM.Instance.Settings.PromptLanguage);

        public Task Execute(int value, IMainWindow mainWindow)
        {
            PetStateService.ChangeExp(mainWindow, value);
            return Task.CompletedTask;
        }
        public Task Execute(string value, IMainWindow mainWindow)
        {
            if (double.TryParse(value, out double doubleValue))
            {
                PetStateService.ChangeExp(mainWindow, doubleValue);
            }
            return Task.CompletedTask;
        }
        public Task Execute(IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }
        public int GetAnimationDuration(string animationName) => 0;
    }
}