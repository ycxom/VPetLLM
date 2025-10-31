using System.Windows;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;
using static VPet_Simulator.Core.GraphInfo;

namespace VPetLLM.Handlers
{
    public class MoveHandler : IActionHandler
    {
        public string Keyword => "move";
        public ActionType ActionType => ActionType.Body;
        public string Description => PromptHelper.Get("Handler_Move_Description", VPetLLM.Instance.Settings.PromptLanguage);

        public Task Execute(string value, IMainWindow mainWindow)
        {
            Utils.Logger.Log($"MoveHandler executed with value: {value}");
            if (string.IsNullOrWhiteSpace(value))
            {
                // 直接调用Display方法显示移动动画，绕过可能失效的委托属性
                try
                {
                    mainWindow.Main.Display(GraphType.Move, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                    Logger.Log("MoveHandler: Move animation triggered successfully");
                }
                catch (Exception ex)
                {
                    Logger.Log($"MoveHandler: Failed to trigger move animation: {ex.Message}");
                }
                return Task.CompletedTask;
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
                    try
                    {
                        mainWindow.Main.Display(GraphType.Move, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                        mainWindow.Core.Controller.MoveWindows(x, y);
                        Logger.Log("MoveHandler: Move animation and position change successful");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"MoveHandler: Failed to trigger move animation: {ex.Message}, moving directly");
                        mainWindow.Core.Controller.MoveWindows(x, y);
                    }
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
                    try
                    {
                        mainWindow.Main.Display(GraphType.Move, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                        mainWindow.Core.Controller.MoveWindows(x, y);
                        Logger.Log("MoveHandler: Move animation and position change successful");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"MoveHandler: Failed to trigger move animation: {ex.Message}, moving directly");
                        mainWindow.Core.Controller.MoveWindows(x, y);
                    }
                }
            }
            else
            {
                try
                {
                    mainWindow.Main.Display(GraphType.Move, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                    Logger.Log("MoveHandler: Move animation triggered successfully");
                }
                catch (Exception ex)
                {
                    Logger.Log($"MoveHandler: Failed to trigger move animation: {ex.Message}");
                }
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
            try
            {
                mainWindow.Main.Display(GraphType.Move, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                Logger.Log("MoveHandler: Move animation triggered successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"MoveHandler: Failed to trigger move animation: {ex.Message}");
            }
            return Task.CompletedTask;
        }
        public int GetAnimationDuration(string animationName) => 0;
    }
}