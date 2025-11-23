using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// 简化版购买处理器 - 使用向量检索和模糊搜索优化
    /// </summary>
    public class BuyHandler : IActionHandler
    {
        public string Keyword => "buy";
        public ActionType ActionType => ActionType.State;
        public string Description => PromptHelper.Get("Handler_Buy_Description", VPetLLM.Instance.Settings.PromptLanguage);

        private const string VPetLLMSource = "vpetllm";
        private const string FriendGiftSource = "friendgift";
        
        // 食物搜索服务（延迟初始化）
        private static FoodSearchService _foodSearchService;

        /// <summary>
        /// 获取或创建食物搜索服务
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

                // 播放购买动画
                await PlayBuyAnimationAsync(mainWindow, food);

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
                // 使用智能搜索服务查找物品
                var searchService = GetFoodSearchService(mainWindow);
                var food = searchService.SearchFood(itemName);

                if (food == null)
                {
                    Logger.Log($"BuyHandler: 朋友赠送物品未找到: {itemName}");
                    return;
                }

                // 播放购买动画（赠送也使用相同动画）
                await PlayBuyAnimationAsync(mainWindow, food);

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
                // 使用智能搜索服务查找物品
                var searchService = GetFoodSearchService(mainWindow);
                var food = searchService.SearchFood(itemName);

                if (food == null)
                {
                    Logger.Log($"BuyHandler: 朋友代购物品未找到: {itemName}");
                    return;
                }

                // 播放购买动画
                await PlayBuyAnimationAsync(mainWindow, food);

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
        /// 播放购买物品动画（使用VPet原生的DisplayFoodAnimation方法）
        /// </summary>
        private async Task PlayBuyAnimationAsync(IMainWindow mainWindow, Food food)
        {
            try
            {
                Logger.Log($"BuyHandler: 开始播放购买动画 - 物品: {food.Name}, 类型: {food.Type}");

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // 使用VPet原生的DisplayFoodAnimation方法
                        // 这个方法会自动处理动画播放、状态切换等逻辑
                        string graphName = food.GetGraph(); // 获取动画名称（eat/drink/gift）
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