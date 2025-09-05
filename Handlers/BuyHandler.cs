using LinePutScript.Localization.WPF;
using System.Linq;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers
{
    public class BuyHandler : IActionHandler
    {
        public string Keyword => "buy";
        public ActionType ActionType => ActionType.State;
        public string Description => "通过 'buy' 指令购买物品。需提供物品名称作为参数。例如 '[:state(buy(奶茶))]'。";

        public void Execute(string itemName, IMainWindow mainWindow)
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
        }
        
        public void Execute(int value, IMainWindow mainWindow)
        {
            // Not used for this handler
        }
        public void Execute(IMainWindow mainWindow) { }
    }
}