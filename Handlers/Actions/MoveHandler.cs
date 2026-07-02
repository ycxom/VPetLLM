using System.Windows;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core.Services;
using static VPet_Simulator.Core.GraphInfo;

namespace VPetLLM.Handlers.Actions
{
    public class MoveHandler : IActionHandler
    {
        public string Keyword => "move";
        public ActionType ActionType => ActionType.Body;
        public ActionCategory Category => ActionCategory.Interactive;
        public string Description => PromptHelper.Get("Handler_Move_Description", VPetLLM.Instance.Settings.PromptLanguage);

        /// <summary>
        /// 读取移动区域：自定义区域（经适配层）优先，否则回退主屏。
        /// </summary>
        private static void GetMoveArea(IMainWindow mainWindow, out double screenX, out double screenY, out double screenWidth, out double screenHeight)
        {
            screenX = 0;
            screenY = 0;
            try
            {
                if (VPetHostAdapter.TryGetCustomMoveArea(mainWindow, out var x, out var y, out var w, out var h))
                {
                    screenX = x;
                    screenY = y;
                    screenWidth = w;
                    screenHeight = h;
                    Logger.Log($"MoveHandler: Using custom area ({screenX:F0},{screenY:F0},{screenWidth:F0}x{screenHeight:F0})");
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"MoveHandler: Error getting screen border: {ex.Message}, using primary screen");
            }

            screenWidth = SystemParameters.PrimaryScreenWidth;
            screenHeight = SystemParameters.PrimaryScreenHeight;
        }

        public Task Execute(string value, IMainWindow mainWindow)
        {
            // 检查是否为默认插件
            if (VPetLLM.Instance?.IsVPetLLMDefaultPlugin() != true)
            {
                Logger.Log("MoveHandler: VPetLLM不是默认插件，忽略移动请求");
                return Task.CompletedTask;
            }

            Logger.Log($"MoveHandler executed with value: {value}");
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
                if (flash)
                {
                    // 闪现模式：计算随机位置并瞬移
                    GetMoveArea(mainWindow, out double screenX, out double screenY, out double screenWidth, out double screenHeight);

                    var petWidth = mainWindow.PetGrid.ActualWidth > 0 ? mainWindow.PetGrid.ActualWidth : 200;
                    var petHeight = mainWindow.PetGrid.ActualHeight > 0 ? mainWindow.PetGrid.ActualHeight : 200;

                    var random = new Random();
                    var targetX = screenX + random.NextDouble() * (screenWidth - petWidth);
                    var targetY = screenY + random.NextDouble() * (screenHeight - petHeight);

                    // 获取当前位置
                    double currentLeft = 0;
                    double currentTop = 0;
                    mainWindow.Dispatcher.Invoke(() =>
                    {
                        currentLeft = ((System.Windows.Window)mainWindow).Left;
                        currentTop = ((System.Windows.Window)mainWindow).Top;
                    });

                    var deltaX = (targetX - currentLeft) / mainWindow.Core.Controller.ZoomRatio;
                    var deltaY = (targetY - currentTop) / mainWindow.Core.Controller.ZoomRatio;

                    Logger.Log($"MoveHandler: Random flash to ({targetX:F0}, {targetY:F0})");

                    // 直接移动
                    mainWindow.Core.Controller.MoveWindows(deltaX, deltaY);

                    // 检查并修正位置
                    if (mainWindow.Core.Controller.CheckPosition())
                    {
                        mainWindow.Core.Controller.ResetPosition();
                    }
                }
                else
                {
                    // 使用 VPet 内置的 DisplayToMove 方法
                    Logger.Log("MoveHandler: Calling DisplayToMove");
                    try
                    {
                        if (!VPetHostAdapter.TryDisplayToMove(mainWindow))
                        {
                            mainWindow.Main.Display(GraphType.Move, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"MoveHandler: Error: {ex.Message}");
                        mainWindow.Main.Display(GraphType.Move, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                    }
                }
            }
            else if (double.TryParse(parts[0], out double targetX) && double.TryParse(parts[1], out double targetY))
            {
                GetMoveArea(mainWindow, out double screenX, out double screenY, out double screenWidth, out double screenHeight);

                var petWidth = mainWindow.PetGrid.ActualWidth > 0 ? mainWindow.PetGrid.ActualWidth : 200;
                var petHeight = mainWindow.PetGrid.ActualHeight > 0 ? mainWindow.PetGrid.ActualHeight : 200;

                // 限制目标位置在移动区域范围内
                targetX = Math.Max(screenX, Math.Min(targetX, screenX + screenWidth - petWidth));
                targetY = Math.Max(screenY, Math.Min(targetY, screenY + screenHeight - petHeight));

                Logger.Log($"MoveHandler: Moving to position ({targetX:F0}, {targetY:F0})");

                // 获取当前位置
                double currentLeft = 0;
                double currentTop = 0;
                mainWindow.Dispatcher.Invoke(() =>
                {
                    currentLeft = ((System.Windows.Window)mainWindow).Left;
                    currentTop = ((System.Windows.Window)mainWindow).Top;
                });

                // 计算移动距离（考虑缩放比例）
                var deltaX = (targetX - currentLeft) / mainWindow.Core.Controller.ZoomRatio;
                var deltaY = (targetY - currentTop) / mainWindow.Core.Controller.ZoomRatio;

                Logger.Log($"MoveHandler: Current=({currentLeft:F0}, {currentTop:F0}), Delta=({deltaX:F0}, {deltaY:F0})");

                // 直接调用 MoveWindows 移动
                mainWindow.Core.Controller.MoveWindows(deltaX, deltaY);

                Logger.Log("MoveHandler: Move completed");

                // 移动后检查并修正位置
                if (mainWindow.Core.Controller.CheckPosition())
                {
                    Logger.Log("MoveHandler: Position out of bounds, resetting");
                    mainWindow.Core.Controller.ResetPosition();
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