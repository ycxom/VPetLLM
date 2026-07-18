using System.Globalization;
using System.Windows;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core.Services;

namespace VPetLLM.Handlers.Actions
{
    public class MoveHandler : IActionHandler
    {
        public string Keyword => "move";
        public ActionType ActionType => ActionType.Body;
        public ActionCategory Category => ActionCategory.Interactive;
        public string Description => PromptHelper.Get("Handler_Move_Description", VPetLLM.Instance.Settings.PromptLanguage);

        private static void GetMoveArea(
            IMainWindow mainWindow,
            out double screenX,
            out double screenY,
            out double screenWidth,
            out double screenHeight)
        {
            screenX = 0;
            screenY = 0;

            try
            {
                if (VPetHostAdapter.TryGetCustomMoveArea(mainWindow, out var x, out var y, out var width, out var height))
                {
                    screenX = x;
                    screenY = y;
                    screenWidth = width;
                    screenHeight = height;
                    Logger.Log($"MoveHandler: Using custom area ({screenX:F0},{screenY:F0},{screenWidth:F0}x{screenHeight:F0})");
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"MoveHandler: Error getting move area: {ex.Message}; using primary screen");
            }

            screenWidth = SystemParameters.PrimaryScreenWidth;
            screenHeight = SystemParameters.PrimaryScreenHeight;
        }

        private static bool CanMove(IMainWindow mainWindow)
        {
            if (VPetLLM.Instance?.IsVPetLLMDefaultPlugin() != true)
            {
                Logger.Log("MoveHandler: VPetLLM is not the default plugin; ignoring move request");
                return false;
            }

            if (mainWindow?.Main is null || mainWindow.Core?.Controller is null)
            {
                Logger.Log("MoveHandler: VPet main window is not ready");
                return false;
            }

            if (mainWindow.Set?.AllowMove != true)
            {
                Logger.Log("MoveHandler: VPet host movement is disabled; ignoring move request");
                return false;
            }

            var displayType = mainWindow.Main.DisplayType;
            if (displayType is not null && VPetMovementPolicy.IsAnimationProtected(displayType.Type))
            {
                Logger.Log($"MoveHandler: VPet is in protected host animation ({displayType.Type}); ignoring move request");
                return false;
            }

            return true;
        }

        private static bool TryGetWindowMetrics(
            IMainWindow mainWindow,
            out double left,
            out double top,
            out double width,
            out double height)
        {
            left = top = width = height = 0;
            if (mainWindow is not Window window)
            {
                Logger.Log("MoveHandler: Main window does not derive from System.Windows.Window");
                return false;
            }

            double currentLeft = 0;
            double currentTop = 0;
            double actualWidth = 0;
            double actualHeight = 0;
            mainWindow.Dispatcher.Invoke(() =>
            {
                currentLeft = window.Left;
                currentTop = window.Top;
                actualWidth = window.ActualWidth;
                actualHeight = window.ActualHeight;
            });

            left = currentLeft;
            top = currentTop;
            width = actualWidth;
            height = actualHeight;

            var zoom = Math.Max(0.01, mainWindow.Core.Controller.ZoomRatio);
            if (width <= 0) width = 500 * zoom;
            if (height <= 0) height = 500 * zoom;
            return true;
        }

        private static bool TryStartNativeMove(IMainWindow mainWindow)
        {
            if (!CanMove(mainWindow)) return false;

            try
            {
                var started = VPetHostAdapter.TryDisplayToMove(mainWindow);
                Logger.Log(started
                    ? "MoveHandler: Native VPet move started"
                    : "MoveHandler: No eligible native VPet move is available");
                return started;
            }
            catch (Exception ex)
            {
                Logger.Log($"MoveHandler: Native move failed: {ex.Message}");
                return false;
            }
        }

        private static bool TryMoveWindowTo(IMainWindow mainWindow, double targetX, double targetY)
        {
            if (!CanMove(mainWindow)) return false;
            if (!TryGetWindowMetrics(mainWindow, out var currentLeft, out var currentTop, out var windowWidth, out var windowHeight))
                return false;

            GetMoveArea(mainWindow, out var areaX, out var areaY, out var areaWidth, out var areaHeight);
            targetX = VPetMovementPolicy.ClampWindowCoordinate(targetX, areaX, areaWidth, windowWidth);
            targetY = VPetMovementPolicy.ClampWindowCoordinate(targetY, areaY, areaHeight, windowHeight);

            var zoom = Math.Max(0.01, mainWindow.Core.Controller.ZoomRatio);
            var deltaX = (targetX - currentLeft) / zoom;
            var deltaY = (targetY - currentTop) / zoom;

            Logger.Log($"MoveHandler: Current=({currentLeft:F0},{currentTop:F0}), Target=({targetX:F0},{targetY:F0}), Delta=({deltaX:F0},{deltaY:F0})");
            mainWindow.Core.Controller.MoveWindows(deltaX, deltaY);

            // Keep the host as the final authority in case its active screen changed
            // between reading the metrics and applying the displacement.
            if (mainWindow.Core.Controller.CheckPosition())
            {
                Logger.Log("MoveHandler: Host reported an out-of-bounds position; resetting once");
                mainWindow.Core.Controller.ResetPosition();
            }

            return true;
        }

        private static bool TryParseCoordinate(string value, out double coordinate)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out coordinate)
                || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate);
        }

        public Task Execute(string value, IMainWindow mainWindow)
        {
            Logger.Log($"MoveHandler executed with value: {value}");
            if (string.IsNullOrWhiteSpace(value))
            {
                TryStartNativeMove(mainWindow);
                return Task.CompletedTask;
            }

            var parts = value.Split(',').Select(part => part.Trim()).ToArray();
            var flash = parts.Any(part => part.Equals("flash", StringComparison.OrdinalIgnoreCase));

            if (parts[0].Equals("random", StringComparison.OrdinalIgnoreCase))
            {
                if (!flash)
                {
                    TryStartNativeMove(mainWindow);
                    return Task.CompletedTask;
                }

                if (!CanMove(mainWindow)
                    || !TryGetWindowMetrics(mainWindow, out _, out _, out var windowWidth, out var windowHeight))
                {
                    return Task.CompletedTask;
                }

                GetMoveArea(mainWindow, out var areaX, out var areaY, out var areaWidth, out var areaHeight);
                var usableWidth = Math.Max(0, areaWidth - windowWidth);
                var usableHeight = Math.Max(0, areaHeight - windowHeight);
                var targetX = areaX + Random.Shared.NextDouble() * usableWidth;
                var targetY = areaY + Random.Shared.NextDouble() * usableHeight;
                TryMoveWindowTo(mainWindow, targetX, targetY);
            }
            else if (parts.Length >= 2
                && TryParseCoordinate(parts[0], out var targetX)
                && TryParseCoordinate(parts[1], out var targetY))
            {
                TryMoveWindowTo(mainWindow, targetX, targetY);
            }
            else
            {
                Logger.Log($"MoveHandler: Invalid move parameters '{value}'; starting a native move instead");
                TryStartNativeMove(mainWindow);
            }

            return Task.CompletedTask;
        }

        public Task Execute(int value, IMainWindow mainWindow)
        {
            TryStartNativeMove(mainWindow);
            return Task.CompletedTask;
        }

        public Task Execute(IMainWindow mainWindow)
        {
            TryStartNativeMove(mainWindow);
            return Task.CompletedTask;
        }

        public int GetAnimationDuration(string animationName) => 0;
    }
}
