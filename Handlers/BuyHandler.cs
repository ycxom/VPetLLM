using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// 购买处理器 - 直接使用 Food.ItemType 属性和 IMainWindow.TakeItemHandle 方法
    /// 使用向量检索和模糊搜索优化
    /// </summary>
    public class BuyHandler : IActionHandler
    {
        public string Keyword => "buy";
        public ActionType ActionType => ActionType.State;
        public ActionCategory Category => ActionCategory.StateBased;
        public string Description => PromptHelper.Get("Handler_Buy_Description", VPetLLM.Instance.Settings.PromptLanguage);

        private const string VPetLLMSource = "vpetllm";
        private const string FriendGiftSource = "friendgift";
        
        // 物品搜索服务（延迟初始化）
        private static FoodSearchService _foodSearchService;

        /// <summary>
        /// 获取或创建物品搜索服务
        /// </summary>
        private FoodSearchService GetFoodSearchService(IMainWindow mainWindow)
        {
            if (_foodSearchService == null)
            {
                _foodSearchService = new FoodSearchService(mainWindow);
            }
            return _foodSearchService;
        }

        public async Task Execute(string itemName, IMainWindow mainWindow)
        {
            // 检查是否为默认插件
            if (VPetLLM.Instance?.IsVPetLLMDefaultPlugin() != true)
            {
                Logger.Log("BuyHandler: VPetLLM不是默认插件，忽略购买请求");
                return;
            }

            Logger.Log($"BuyHandler: 开始处理购买请求 - 物品: {itemName}");

            try
            {
                // 使用智能搜索服务查找物品
                var searchService = GetFoodSearchService(mainWindow);
                var food = searchService.SearchFood(itemName);

                if (food == null)
                {
                    Logger.Log($"BuyHandler: 物品未找到: {itemName}");
                    return;
                }

                // 检查物品是否可用
                if (!food.CanUse)
                {
                    Logger.Log($"BuyHandler: 物品不可用: {itemName}");
                    return;
                }

                // 直接使用 Food.ItemType 属性
                string itemType = food.ItemType;
                
                // 播放购买动画
                await PlayBuyAnimationAsync(mainWindow, food, itemType);
                
                // 使用物品
                mainWindow.TakeItem(food);
                
                // 直接调用 IMainWindow.TakeItemHandle 方法
                mainWindow.TakeItemHandle(food, 1, VPetLLMSource);
                
                Logger.Log($"BuyHandler: 购买完成 - {food.Name} (type: {itemType}), 剩余金钱: ${mainWindow.Core.Save.Money:F2}");
            }
            catch (Exception ex)
            {
                Logger.Log($"BuyHandler: 购买处理异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取物品栏摘要（供 AI 使用）
        /// </summary>
        public string GetInventorySummary(IMainWindow mainWindow, string language = "zh")
        {
            var searchService = GetFoodSearchService(mainWindow);
            return searchService.GetInventorySummary(language);
        }

        /// <summary>
        /// 检查桌宠是否拥有某物品
        /// </summary>
        public bool HasItemInInventory(IMainWindow mainWindow, string itemName)
        {
            var searchService = GetFoodSearchService(mainWindow);
            return searchService.HasItemInInventory(itemName);
        }

        /// <summary>
        /// 朋友赠送物品
        /// </summary>
        public async Task ExecuteFriendGift(string itemName, IMainWindow mainWindow, string friendName = "朋友")
        {
            Logger.Log($"BuyHandler: 开始处理朋友赠送请求 - 物品: {itemName}, 赠送者: {friendName}");

            try
            {
                // 使用智能搜索服务查找物品
                var searchService = GetFoodSearchService(mainWindow);
                var food = searchService.SearchFood(itemName);

                if (food == null)
                {
                    Logger.Log($"BuyHandler: 朋友赠送物品未找到: {itemName}");
                    return;
                }

                // 直接使用 Food.ItemType 属性
                string itemType = food.ItemType;
                
                // 播放动画
                await PlayBuyAnimationAsync(mainWindow, food, itemType);

                // 朋友赠送不扣钱，只添加物品并标记来源
                mainWindow.TakeItemHandle(food, 1, FriendGiftSource);
                
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
                // 使用智能搜索服务查找物品
                var searchService = GetFoodSearchService(mainWindow);
                var food = searchService.SearchFood(itemName);

                if (food == null)
                {
                    Logger.Log($"BuyHandler: 朋友代购物品未找到: {itemName}");
                    return;
                }

                // 直接使用 Food.ItemType 属性
                string itemType = food.ItemType;
                
                // 播放动画
                await PlayBuyAnimationAsync(mainWindow, food, itemType);

                // 朋友代为购买，扣朋友的钱，不扣当前用户的钱
                mainWindow.TakeItemHandle(food, 1, $"friendbuy_{friendName}");
                
                Logger.Log($"BuyHandler: 朋友代购完成 - {food.Name} (由 {friendName} 支付), 不扣当前用户金钱");
            }
            catch (Exception ex)
            {
                Logger.Log($"BuyHandler: 朋友代购处理异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 播放购买动画（根据物品类型选择动画）
        /// </summary>
        private async Task PlayBuyAnimationAsync(IMainWindow mainWindow, Food food, string itemType)
        {
            try
            {
                Logger.Log($"BuyHandler: 开始播放购买动画 - 物品: {food.Name}, 类型: {itemType}");

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // 根据物品类型选择动画
                        string graphName;
                        if (itemType == "Food")
                        {
                            // 食物类型使用原生的 GetGraph 方法
                            graphName = food.GetGraph();
                        }
                        else
                        {
                            // 其他类型（Tool, Toy, Item）使用礼物动画
                            graphName = "gift";
                        }
                        
                        Logger.Log($"BuyHandler: 调用DisplayFoodAnimation - 动画: {graphName}");
                        mainWindow.DisplayFoodAnimation(graphName, food.ImageSource);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"BuyHandler: 播放购买动画失败: {ex.Message}");
                    }
                });

                // 短暂延迟，让动画开始播放
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                Logger.Log($"BuyHandler: 播放购买动画异常: {ex.Message}");
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
