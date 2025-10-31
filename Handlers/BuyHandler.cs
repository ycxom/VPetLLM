using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// 简化版购买处理器 - 直接使用VPet原生接口
    /// </summary>
    public class BuyHandler : IActionHandler
    {
        public string Keyword => "buy";
        public ActionType ActionType => ActionType.State;
        public string Description => PromptHelper.Get("Handler_Buy_Description", VPetLLM.Instance.Settings.PromptLanguage);

        private const string VPetLLMSource = "vpetllm";
        private const string FriendGiftSource = "friendgift";

        public async Task Execute(string itemName, IMainWindow mainWindow)
        {
            Logger.Log($"BuyHandler: 开始处理购买请求 - 物品: {itemName}");

            try
            {
                // 查找物品
                var food = mainWindow.Foods.FirstOrDefault(f =>
                    string.Equals(f.Name, itemName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(f.TranslateName, itemName, StringComparison.OrdinalIgnoreCase));

                if (food == null)
                {
                    Logger.Log($"BuyHandler: 物品未找到: {itemName}");
                    return;
                }

                // 执行购买并标记来源为VPetLLM
                mainWindow.TakeItem(food);
                TryInvokeTakeItemHandle(mainWindow, food, 1, VPetLLMSource);
                
                Logger.Log($"BuyHandler: 购买完成 - {food.Name}, 剩余金钱: ${mainWindow.Core.Save.Money:F2}");
            }
            catch (Exception ex)
            {
                Logger.Log($"BuyHandler: 购买处理异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 朋友赠送物品
        /// </summary>
        public async Task ExecuteFriendGift(string itemName, IMainWindow mainWindow, string friendName = "朋友")
        {
            Logger.Log($"BuyHandler: 开始处理朋友赠送请求 - 物品: {itemName}, 赠送者: {friendName}");

            try
            {
                // 查找物品
                var food = mainWindow.Foods.FirstOrDefault(f =>
                    string.Equals(f.Name, itemName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(f.TranslateName, itemName, StringComparison.OrdinalIgnoreCase));

                if (food == null)
                {
                    Logger.Log($"BuyHandler: 朋友赠送物品未找到: {itemName}");
                    return;
                }

                // 朋友赠送不扣钱，只添加物品并标记来源
                TryInvokeTakeItemHandle(mainWindow, food, 1, FriendGiftSource);
                
                Logger.Log($"BuyHandler: 朋友赠送完成 - {food.Name} (来自 {friendName}), 不扣金钱");
            }
            catch (Exception ex)
            {
                Logger.Log($"BuyHandler: 朋友赠送处理异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 朋友代为购买
        /// </summary>
        public async Task ExecuteFriendBuy(string itemName, IMainWindow mainWindow, string friendName = "朋友")
        {
            Logger.Log($"BuyHandler: 开始处理朋友代购请求 - 物品: {itemName}, 代购者: {friendName}");

            try
            {
                // 查找物品
                var food = mainWindow.Foods.FirstOrDefault(f =>
                    string.Equals(f.Name, itemName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(f.TranslateName, itemName, StringComparison.OrdinalIgnoreCase));

                if (food == null)
                {
                    Logger.Log($"BuyHandler: 朋友代购物品未找到: {itemName}");
                    return;
                }

                // 朋友代为购买，扣朋友的钱，不扣当前用户的钱
                // 这里模拟朋友支付，实际游戏中可能需要更复杂的逻辑
                TryInvokeTakeItemHandle(mainWindow, food, 1, $"friendbuy_{friendName}");
                
                Logger.Log($"BuyHandler: 朋友代购完成 - {food.Name} (由 {friendName} 支付), 不扣当前用户金钱");
            }
            catch (Exception ex)
            {
                Logger.Log($"BuyHandler: 朋友代购处理异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 动态调用 VPet 原生 TakeItemHandle 接口（向后兼容）
        /// </summary>
        private void TryInvokeTakeItemHandle(IMainWindow mainWindow, Food food, int count, string source)
        {
            try
            {
                var takeItemHandleMethod = mainWindow.GetType().GetMethod("TakeItemHandle");
                if (takeItemHandleMethod != null)
                {
                    takeItemHandleMethod.Invoke(mainWindow, new object[] { food, count, source });
                    Logger.Log($"BuyHandler: 成功调用 TakeItemHandle - 物品: {food.Name}, 来源: {source}");
                }
                else
                {
                    Logger.Log($"BuyHandler: 当前VPet版本不支持TakeItemHandle接口");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"BuyHandler: 调用 TakeItemHandle 失败: {ex.Message}");
            }
        }

        public Task Execute(int value, IMainWindow mainWindow)
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