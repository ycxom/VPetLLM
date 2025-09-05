using LinePutScript.Localization.WPF;
using System.Linq;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers
{
    public class BuyHandler : IActionHandler
    {
        public string Keyword => "buy";

        public void Execute(string itemName, IMainWindow mainWindow)
        {
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