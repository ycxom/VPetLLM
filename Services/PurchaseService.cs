using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;

namespace VPetLLM.Services
{
    /// <summary>
    /// 购买事件服务实现
    /// </summary>
    public class PurchaseService : IPurchaseService
    {
        private readonly VPetLLM _plugin;
        private readonly Setting _settings;
        private readonly List<PurchaseItem> _pendingPurchases = new();
        private readonly object _purchaseLock = new();
        private System.Timers.Timer? _purchaseTimer;
        private static readonly TimeSpan _batchProcessTime = TimeSpan.FromMilliseconds(500);
        private bool _disposed;

        /// <inheritdoc/>
        public int PendingPurchaseCount
        {
            get
            {
                lock (_purchaseLock)
                {
                    return _pendingPurchases.Count;
                }
            }
        }

        /// <inheritdoc/>
        public event EventHandler<List<PurchaseItem>>? BatchProcessed;

        public PurchaseService(VPetLLM plugin, Setting settings)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <inheritdoc/>
        public void HandlePurchase(Food food, int count, string source)
        {
            try
            {
                if (food == null)
                {
                    Logger.Log("Purchase event: food is null, skipping");
                    return;
                }

                if (!_settings.EnableBuyFeedback)
                {
                    Logger.Log("Purchase event: BuyFeedback is disabled, skipping");
                    return;
                }

                // 使用PurchaseSourceDetector模块检测购买来源
                var purchaseSource = PurchaseSourceDetector.DetectPurchaseSource(source);
                string sourceDescription = PurchaseSourceDetector.GetPurchaseSourceDescription(purchaseSource);
                Logger.Log($"Purchase source detected: {sourceDescription} (from: {source})");

                // 判断是否应该触发AI反馈
                if (!PurchaseSourceDetector.ShouldTriggerAIFeedback(source))
                {
                    Logger.Log($"Purchase event: {sourceDescription}，跳过AI反馈");
                    return;
                }

                if (_plugin.ChatCore == null)
                {
                    Logger.Log("Purchase event: ChatCore is null, skipping");
                    return;
                }

                // 添加到批处理队列
                AddToPurchaseBatch(food, count);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling purchase event: {ex.Message}");
                Logger.Log($"Stack trace: {ex.StackTrace}");
            }
        }

        private void AddToPurchaseBatch(Food food, int count = 1)
        {
            lock (_purchaseLock)
            {
                var existingItem = _pendingPurchases.FirstOrDefault(p => p.Name == food.Name);
                if (existingItem != null)
                {
                    existingItem.Quantity += count;
                    existingItem.PurchaseTime = DateTime.Now;
                    Logger.Log($"Updated purchase quantity for {food.Name}: {existingItem.Quantity}");
                }
                else
                {
                    _pendingPurchases.Add(new PurchaseItem
                    {
                        Name = food.Name,
                        Type = food.Type,
                        Price = food.Price,
                        PurchaseTime = DateTime.Now,
                        Quantity = count
                    });
                    Logger.Log($"Added new purchase item: {food.Name} x{count}");
                }

                ResetPurchaseTimer();
            }
        }

        private void ResetPurchaseTimer()
        {
            _purchaseTimer?.Stop();
            _purchaseTimer?.Dispose();

            _purchaseTimer = new System.Timers.Timer(_batchProcessTime.TotalMilliseconds);
            _purchaseTimer.Elapsed += async (sender, e) => await ProcessPurchaseBatchInternal();
            _purchaseTimer.AutoReset = false;
            _purchaseTimer.Start();

            Logger.Log($"Purchase timer reset, will process batch in {_batchProcessTime.TotalMilliseconds}ms");
        }

        /// <inheritdoc/>
        public void ProcessPendingPurchases()
        {
            _ = ProcessPurchaseBatchInternal();
        }

        private async Task ProcessPurchaseBatchInternal()
        {
            List<PurchaseItem> itemsToProcess;

            lock (_purchaseLock)
            {
                if (_pendingPurchases.Count == 0)
                {
                    Logger.Log("No pending purchases to process");
                    return;
                }

                itemsToProcess = new List<PurchaseItem>(_pendingPurchases);
                _pendingPurchases.Clear();
                Logger.Log($"Processing batch of {itemsToProcess.Count} purchase items");
            }

            try
            {
                await ProcessBatchPurchaseFeedback(itemsToProcess);
                BatchProcessed?.Invoke(this, itemsToProcess);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error processing purchase batch: {ex.Message}");
                Logger.Log($"Stack trace: {ex.StackTrace}");
            }
        }

        private Task ProcessBatchPurchaseFeedback(List<PurchaseItem> purchases)
        {
            try
            {
                Logger.Log($"Processing batch purchase feedback for {purchases.Count} items");

                string message = BuildBatchPurchaseMessage(purchases);

                Logger.Log($"Sending batch message to aggregator: {message}");
                ResultAggregator.Enqueue($"[System.BuyFeedback: \"{message}\"]");
                Logger.Log("Batch purchase feedback enqueued for aggregation");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error in ProcessBatchPurchaseFeedback: {ex.Message}");
                Logger.Log($"Stack trace: {ex.StackTrace}");
            }
            return Task.CompletedTask;
        }

        private string BuildBatchPurchaseMessage(List<PurchaseItem> purchases)
        {
            string template = PromptHelper.Get("BuyFeedback_Batch", _settings.PromptLanguage);

            if (string.IsNullOrEmpty(template) || template.Contains("[Prompt Missing"))
            {
                template = PromptHelper.Get("BuyFeedback_Default", _settings.PromptLanguage);
                if (string.IsNullOrEmpty(template) || template.Contains("[Prompt Missing"))
                {
                    return "";
                }

                var firstItem = purchases[0];
                return template
                    .Replace("{ItemName}", firstItem.Name)
                    .Replace("{ItemType}", GetItemTypeName(firstItem.Type))
                    .Replace("{ItemPrice}", firstItem.Price.ToString("F2"))
                    .Replace("{EmotionState}", GetCurrentEmotionState());
            }

            var itemList = new List<string>();
            double totalPrice = 0;
            var itemsByType = purchases.GroupBy(p => p.Type).ToList();

            foreach (var purchase in purchases)
            {
                string itemDesc = purchase.Quantity > 1
                    ? $"{purchase.Name} x{purchase.Quantity}"
                    : purchase.Name;
                itemList.Add(itemDesc);
                totalPrice += purchase.Price * purchase.Quantity;
            }

            var typeStats = new List<string>();
            foreach (var typeGroup in itemsByType)
            {
                int totalQuantity = typeGroup.Sum(p => p.Quantity);
                string typeName = GetItemTypeName(typeGroup.Key);
                typeStats.Add($"{typeName}({totalQuantity}个)");
            }

            string separator = _settings.PromptLanguage.StartsWith("zh") ? "、" : ", ";
            string message = template
                .Replace("{ItemCount}", purchases.Count.ToString())
                .Replace("{ItemList}", string.Join(separator, itemList))
                .Replace("{TotalPrice}", totalPrice.ToString("F2"))
                .Replace("{TypeStats}", string.Join(separator, typeStats))
                .Replace("{EmotionState}", GetCurrentEmotionState())
                .Replace("{CurrentHealth}", $"{(_plugin.MW.Core.Save.Health / 100.0 * 100):F0}%")
                .Replace("{CurrentMood}", $"{(_plugin.MW.Core.Save.Feeling / _plugin.MW.Core.Save.FeelingMax * 100):F0}%")
                .Replace("{CurrentMoney}", _plugin.MW.Core.Save.Money.ToString("F2"))
                .Replace("{CurrentHunger}", $"{(_plugin.MW.Core.Save.StrengthFood / _plugin.MW.Core.Save.StrengthMax * 100):F0}%")
                .Replace("{CurrentThirst}", $"{(_plugin.MW.Core.Save.StrengthDrink / _plugin.MW.Core.Save.StrengthMax * 100):F0}%")
                .Replace("{PurchaseTime}", DateTime.Now.ToString("HH:mm:ss"));

            return message;
        }

        private string GetItemTypeName(Food.FoodType type)
        {
            string key = type switch
            {
                Food.FoodType.Food => "ItemType.Food",
                Food.FoodType.Drink => "ItemType.Drink",
                Food.FoodType.Drug => "ItemType.Drug",
                Food.FoodType.Gift => "ItemType.Gift",
                _ => "ItemType.General"
            };

            string result = LanguageHelper.Get(key, _settings.Language);
            return string.IsNullOrEmpty(result) || result.Contains("[") ? type.ToString().ToLower() : result;
        }

        private string GetCurrentEmotionState()
        {
            var feeling = _plugin.MW.Core.Save.Feeling;
            var health = _plugin.MW.Core.Save.Health;

            string key;
            if (health < 50)
                key = "EmotionState.Unwell";
            else if (feeling > 80)
                key = "EmotionState.VeryHappy";
            else if (feeling > 60)
                key = "EmotionState.Happy";
            else if (feeling > 40)
                key = "EmotionState.Normal";
            else if (feeling > 20)
                key = "EmotionState.Unhappy";
            else
                key = "EmotionState.VeryUnhappy";

            string result = LanguageHelper.Get(key, _settings.Language);
            return string.IsNullOrEmpty(result) || result.Contains("[") ? "normal" : result;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _purchaseTimer?.Stop();
            _purchaseTimer?.Dispose();
            _purchaseTimer = null;
        }
    }
}
