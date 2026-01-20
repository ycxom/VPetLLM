using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers.Actions
{
    /// <summary>
    /// 物品使用处理器 - 使用桌宠背包中的物品
    /// 直接使用 VPet 原生 Item.Use() 和 Item.Consume() API
    /// </summary>
    public class UseItemHandler : IActionHandler
    {
        public string Keyword => "use_item";
        public ActionType ActionType => ActionType.State;
        public ActionCategory Category => ActionCategory.StateBased;
        public string Description => PromptHelper.Get("Handler_UseItem_Description", VPetLLM.Instance.Settings.PromptLanguage);

        // 物品搜索服务（延迟初始化）
        private static FoodSearchService _foodSearchService;

        /// <summary>
        /// 获取或创建物品搜索服务
        /// </summary>
        private FoodSearchService GetFoodSearchService(IMainWindow mainWindow)
        {
            if (_foodSearchService is null)
            {
                _foodSearchService = new FoodSearchService(mainWindow);
            }
            return _foodSearchService;
        }

        /// <summary>
        /// 执行物品使用操作
        /// </summary>
        public async Task Execute(string itemName, IMainWindow mainWindow)
        {
            // 检查是否为默认插件
            if (VPetLLM.Instance?.IsVPetLLMDefaultPlugin() != true)
            {
                Logger.Log("UseItemHandler: VPetLLM不是默认插件，忽略使用请求");
                return;
            }

            Logger.Log($"UseItemHandler: 开始处理物品使用请求 - 物品: {itemName}");

            try
            {
                // 在物品栏中查找物品
                var searchService = GetFoodSearchService(mainWindow);
                var itemInfo = searchService.FindItemInInventory(itemName);

                if (itemInfo is null)
                {
                    Logger.Log($"UseItemHandler: 物品未在背包中找到: {itemName}");
                    return;
                }

                // 检查物品是否可用
                if (!itemInfo.CanUse)
                {
                    Logger.Log($"UseItemHandler: 物品不可用: {itemName} (CanUse=false)");
                    return;
                }

                // 获取原始 Item 对象
                var item = itemInfo.OriginalItem as Item;
                if (item is null)
                {
                    Logger.Log($"UseItemHandler: 无法获取 Item 对象: {itemName}");
                    return;
                }

                // 播放使用动画
                await PlayUseAnimationAsync(mainWindow, item);

                // 直接调用 Item.Use() 方法
                item.Use();

                Logger.Log($"UseItemHandler: 物品使用成功 - {item.Name} (type: {item.ItemType})");
            }
            catch (Exception ex)
            {
                Logger.Log($"UseItemHandler: 物品使用异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 使用指定数量的物品
        /// </summary>
        public async Task ExecuteWithCount(string itemName, int count, IMainWindow mainWindow)
        {
            if (count <= 0) count = 1;

            Logger.Log($"UseItemHandler: 开始处理物品使用请求 - 物品: {itemName}, 数量: {count}");

            try
            {
                var searchService = GetFoodSearchService(mainWindow);
                var itemInfo = searchService.FindItemInInventory(itemName);

                if (itemInfo is null)
                {
                    Logger.Log($"UseItemHandler: 物品未在背包中找到: {itemName}");
                    return;
                }

                if (!itemInfo.CanUse)
                {
                    Logger.Log($"UseItemHandler: 物品不可用: {itemName}");
                    return;
                }

                var item = itemInfo.OriginalItem as Item;
                if (item is null)
                {
                    Logger.Log($"UseItemHandler: 无法获取 Item 对象: {itemName}");
                    return;
                }

                // 限制使用数量不超过拥有数量
                int actualCount = Math.Min(count, item.Count);

                // 播放使用动画
                await PlayUseAnimationAsync(mainWindow, item);

                // 使用物品（多次调用 Use）
                for (int i = 0; i < actualCount; i++)
                {
                    item.Use();
                }

                Logger.Log($"UseItemHandler: 物品使用成功 - {item.Name} x{actualCount}");
            }
            catch (Exception ex)
            {
                Logger.Log($"UseItemHandler: 物品使用异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 消耗物品（减少数量，不触发使用效果）
        /// </summary>
        public void ConsumeItem(string itemName, int count, IMainWindow mainWindow)
        {
            if (count <= 0) count = 1;

            Logger.Log($"UseItemHandler: 开始消耗物品 - 物品: {itemName}, 数量: {count}");

            try
            {
                var searchService = GetFoodSearchService(mainWindow);
                var itemInfo = searchService.FindItemInInventory(itemName);

                if (itemInfo is null)
                {
                    Logger.Log($"UseItemHandler: 物品未在背包中找到: {itemName}");
                    return;
                }

                var item = itemInfo.OriginalItem as Item;
                if (item is null)
                {
                    Logger.Log($"UseItemHandler: 无法获取 Item 对象: {itemName}");
                    return;
                }

                // 直接调用 Item.Consume() 方法
                item.Consume(mainWindow, count);

                Logger.Log($"UseItemHandler: 物品消耗成功 - {item.Name} x{count}");
            }
            catch (Exception ex)
            {
                Logger.Log($"UseItemHandler: 物品消耗异常: {ex.Message}");
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
        /// 播放使用动画
        /// </summary>
        private async Task PlayUseAnimationAsync(IMainWindow mainWindow, Item item)
        {
            try
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // 根据物品类型选择动画
                        string graphName = "gift";  // 默认使用礼物动画

                        // 如果是 Food 类型，获取对应的动画
                        if (item is Food food)
                        {
                            graphName = food.GetGraph();
                        }

                        Logger.Log($"UseItemHandler: 播放使用动画 - {graphName}");
                        mainWindow.DisplayFoodAnimation(graphName, item.ImageSource);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"UseItemHandler: 播放动画失败: {ex.Message}");
                    }
                });

                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                Logger.Log($"UseItemHandler: 播放动画异常: {ex.Message}");
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
