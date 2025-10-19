using System;
using System.Diagnostics;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Utils
{
    /// <summary>
    /// 购买来源检测器 - 用于区分用户购买和VPet自动购物
    /// </summary>
    public static class PurchaseSourceDetector
    {
        private static DateTime _lastAutoBuyTime = DateTime.MinValue;
        private static readonly TimeSpan AutoBuyDetectionWindow = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// 检测购买是否来自VPet的自动购物功能
        /// </summary>
        /// <param name="mainWindow">主窗口</param>
        /// <returns>true表示是自动购物，false表示是用户购买</returns>
        public static bool IsAutoBuy(IMainWindow mainWindow)
        {
            try
            {
                // 方法1：检查调用堆栈
                if (IsCalledFromAutoBuy())
                {
                    Logger.Log("PurchaseSourceDetector: 检测到自动购物（通过调用堆栈）");
                    return true;
                }

                // 方法2：检查是否在自动购物时间窗口内
                if (DateTime.Now - _lastAutoBuyTime < AutoBuyDetectionWindow)
                {
                    Logger.Log("PurchaseSourceDetector: 检测到自动购物（通过时间窗口）");
                    return true;
                }

                // 方法3：检查VPet的动画状态（辅助参考）
                // 自动购物通常发生在VPet处于特定状态时
                if (mainWindow?.Main != null)
                {
                    var displayType = mainWindow.Main.DisplayType;
                    
                    // 如果VPet正在显示食物相关动画，可能是自动购物
                    // 因为自动购物会立即调用 Main.Display(item.GetGraph(), ...)
                    if (displayType != null && 
                        (displayType.Name == "eat" || 
                         displayType.Name == "drink"))
                    {
                        // 这个检查不太可靠，因为用户购买后也会显示吃的动画
                        // 所以我们只在其他条件都不满足时才使用这个作为参考
                    }
                }

                // 默认认为是用户购买
                Logger.Log("PurchaseSourceDetector: 检测为用户购买");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"PurchaseSourceDetector: 检测失败: {ex.Message}");
                // 出错时默认认为是用户购买，避免误判
                return false;
            }
        }

        /// <summary>
        /// 通过检查调用堆栈判断是否来自自动购物
        /// </summary>
        private static bool IsCalledFromAutoBuy()
        {
            try
            {
                var stackTrace = new StackTrace();
                var frames = stackTrace.GetFrames();

                if (frames == null)
                    return false;

                foreach (var frame in frames)
                {
                    var method = frame.GetMethod();
                    if (method == null)
                        continue;

                    var methodName = method.Name;
                    var className = method.DeclaringType?.Name;

                    // 检查是否来自自动购物相关的方法
                    // 根据MainWindow.cs的代码，自动购物在 AutoBuy 方法中
                    if (methodName != null && 
                        (methodName.Contains("AutoBuy") || 
                         methodName.Contains("AutoGift") ||
                         methodName.Contains("lowstrength")))
                    {
                        return true;
                    }

                    // 检查类名
                    if (className != null && className == "MainWindow")
                    {
                        // 如果方法名包含特定关键字
                        if (methodName != null &&
                            (methodName.Contains("Timer") ||
                             methodName.Contains("Auto")))
                        {
                            // 可能是自动购物
                            // 但需要更精确的判断
                        }
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 标记自动购物时间（由VPet主程序调用，如果可能的话）
        /// </summary>
        public static void MarkAutoBuyTime()
        {
            _lastAutoBuyTime = DateTime.Now;
            Logger.Log("PurchaseSourceDetector: 标记自动购物时间");
        }

        /// <summary>
        /// 获取购买来源的描述（用于日志）
        /// </summary>
        public static string GetPurchaseSourceDescription(bool isAutoBuy)
        {
            return isAutoBuy ? "VPet自动购物" : "用户手动购买";
        }
    }
}
