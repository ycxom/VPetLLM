using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers.State;

namespace VPetLLM.Handlers.Actions
{
    public class HealthHandler : IActionHandler
    {
        public string Keyword => "health";
        public ActionType ActionType => ActionType.State;
        public ActionCategory Category => ActionCategory.StateBased;
        public string Description => PromptHelper.Get("Handler_Health_Description", VPetLLM.Instance.Settings.PromptLanguage);

        public Task Execute(int value, IMainWindow mainWindow)
        {
            // 检查是否为默认插件
            if (VPetLLM.Instance?.IsVPetLLMDefaultPlugin() != true)
            {
                return Task.CompletedTask;
            }

            PetStateService.ChangeHealth(mainWindow, value);
            return Task.CompletedTask;
        }
        public Task Execute(string value, IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }
        public Task Execute(IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }
        public int GetAnimationDuration(string animationName) => 0;
    }
}