using LinePutScript.Localization.WPF;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;

namespace VPetLLM.Handlers
{
    public class BuyHandler : IActionHandler
    {
        public string Keyword => "buy";
        public ActionType ActionType => ActionType.State;
        public string Description => PromptHelper.Get("Handler_Buy_Description", VPetLLM.Instance.Settings.PromptLanguage);

        public Task Execute(string itemName, IMainWindow mainWindow)
        {
            Logger.Log($"BuyHandler executed with value: {itemName}");
            var food = mainWindow.Foods.FirstOrDefault(f => f.Name.ToLower() == itemName.ToLower());
            if (food != null)
            {
                if (mainWindow.Core.Save.Money >= food.Price)
                {
                    mainWindow.TakeItem(food);
                    mainWindow.Main.Say($"{food.Name} 真好吃！".Translate());
                }
                else
                {
                    mainWindow.Main.Say("钱不够买 ".Translate() + food.Name);
                }
            }
            else
            {
                mainWindow.Main.Say("好像没有找到 ".Translate() + itemName);
            }
            return Task.CompletedTask;
        }

        public Task Execute(int value, IMainWindow mainWindow)
        {
            // Not used for this handler
            return Task.CompletedTask;
        }
        public Task Execute(IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }
        public int GetAnimationDuration(string animationName) => 0;
    }
}