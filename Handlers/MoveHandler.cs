using System;
using System.Linq;
using System.Windows;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;

namespace VPetLLM.Handlers
{
    public class MoveHandler : IActionHandler
    {
        public string Keyword => "move";
        public ActionType ActionType => ActionType.Body;
        public string Description => "通过 'move' 指令移动。可用参数: 'x,y' (移动到指定坐标), 'random' (随机移动)。添加 'flash' 参数可实现无动画的闪现移动。例如 '[:body(move(100,200))]' 或 '[:body(move(random,flash))]'。";

        public void Execute(string value, IMainWindow mainWindow)
        {
            Utils.Logger.Log($"MoveHandler executed with value: {value}");
            if (string.IsNullOrWhiteSpace(value))
            {
                mainWindow.Main.DisplayMove();
                return;
            }

            var parts = value.Split(',');
            bool flash = parts.Contains("flash");

            if (parts[0].ToLower() == "random")
            {
                var screenWidth = SystemParameters.PrimaryScreenWidth;
                var screenHeight = SystemParameters.PrimaryScreenHeight;
                var random = new Random();
                var x = random.NextDouble() * (screenWidth - mainWindow.PetGrid.ActualWidth);
                var y = random.NextDouble() * (screenHeight - mainWindow.PetGrid.ActualHeight);

                if (flash)
                {
                    mainWindow.Core.Controller.MoveWindows(x, y);
                }
                else
                {
                    mainWindow.Main.DisplayMove();
                    mainWindow.Core.Controller.MoveWindows(x, y);
                }
            }
            else if (double.TryParse(parts[0], out double x) && double.TryParse(parts[1], out double y))
            {
                if (flash)
                {
                    mainWindow.Core.Controller.MoveWindows(x, y);
                }
                else
                {
                    mainWindow.Main.DisplayMove();
                    mainWindow.Core.Controller.MoveWindows(x, y);
                }
            }
            else
            {
                mainWindow.Main.DisplayMove();
            }
        }

        public void Execute(int value, IMainWindow mainWindow)
        {
            // Not used for this handler
        }
        public void Execute(IMainWindow mainWindow)
        {
            mainWindow.Main.DisplayMove();
        }
    }
}