using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// 增强的购买处理器，支持智能反馈和情感管理
    /// </summary>
    public class BuyHandler : IActionHandler
    {
        public string Keyword => "buy";
        public ActionType ActionType => ActionType.State;
        public string Description => PromptHelper.Get("Handler_Buy_Description", VPetLLM.Instance.Settings.PromptLanguage);

        private static readonly Dictionary<string, DateTime> _lastPurchaseTime = new();
        private static readonly TimeSpan _cooldownTime = TimeSpan.FromSeconds(3);

        public async Task Execute(string itemName, IMainWindow mainWindow)
        {
            Logger.Log($"BuyHandler: 开始处理购买请求 - 物品: {itemName}");

            // 检查冷却时间
            if (IsOnCooldown(itemName))
            {
                Logger.Log($"BuyHandler: 购买冷却中，跳过处理");
                return;
            }

            try
            {
                // 查找物品
                var food = FindItem(itemName, mainWindow);
                if (food == null)
                {
                    Logger.Log($"BuyHandler: 物品未找到: {itemName}");
                    return;
                }

                // 检查金钱
                if (mainWindow.Core.Save.Money < food.Price)
                {
                    Logger.Log($"BuyHandler: 金钱不足，需要${food.Price:F2}，只有${mainWindow.Core.Save.Money:F2}");
                    return;
                }

                // 执行购买
                await ExecutePurchase(food, mainWindow);
            }
            catch (Exception ex)
            {
                Logger.Log($"BuyHandler: 购买处理异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 查找物品
        /// </summary>
        private Food FindItem(string itemName, IMainWindow mainWindow)
        {
            // 精确匹配
            var food = mainWindow.Foods.FirstOrDefault(f =>
                string.Equals(f.Name, itemName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.TranslateName, itemName, StringComparison.OrdinalIgnoreCase));

            // 模糊匹配
            if (food == null)
            {
                food = mainWindow.Foods.FirstOrDefault(f =>
                    f.Name.ToLower().Contains(itemName.ToLower()) ||
                    f.TranslateName.ToLower().Contains(itemName.ToLower()));
            }

            Logger.Log($"BuyHandler: 物品查找结果 - {itemName} -> {food?.Name ?? "未找到"}");
            return food;
        }

        /// <summary>
        /// 执行购买流程
        /// </summary>
        private async Task ExecutePurchase(Food food, IMainWindow mainWindow)
        {
            Logger.Log($"BuyHandler: 开始执行购买 - {food.Name}, 价格: ${food.Price}");

            // 执行实际购买
            mainWindow.TakeItem(food);
            Logger.Log($"BuyHandler: 购买完成 - 剩余金钱: ${mainWindow.Core.Save.Money:F2}");

            // 处理智能购买互动
            await ProcessIntelligentPurchase(food);

            // 更新冷却时间
            UpdateCooldown(food.Name);
        }

        /// <summary>
        /// 处理智能购买互动
        /// </summary>
        private Task ProcessIntelligentPurchase(Food food)
        {
            try
            {
                // BuyHandler不再处理智能反馈，由事件系统处理
                Logger.Log($"BuyHandler: 购买完成，智能反馈由事件系统处理");
            }
            catch (Exception ex)
            {
                // 静默处理错误，不影响购买流程
                Logger.Log($"BuyHandler: 智能反馈异常: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// 检查是否在冷却时间内
        /// </summary>
        private bool IsOnCooldown(string itemName)
        {
            if (_lastPurchaseTime.TryGetValue(itemName, out var lastTime))
            {
                return DateTime.Now - lastTime < _cooldownTime;
            }
            return false;
        }

        /// <summary>
        /// 更新冷却时间
        /// </summary>
        private void UpdateCooldown(string itemName)
        {
            _lastPurchaseTime[itemName] = DateTime.Now;
        }

        public Task Execute(int value, IMainWindow mainWindow)
        {
            // Not used for this handler
            return Task.CompletedTask;
        }

        public Task Execute(IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }

        public int GetAnimationDuration(string animationName) => 0;
    }
}