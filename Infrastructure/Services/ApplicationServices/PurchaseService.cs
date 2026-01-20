using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils.Localization;
using Timer = System.Timers.Timer;

namespace VPetLLM.Infrastructure.Services.ApplicationServices
{
    /// <summary>
    /// 购买服务 - 使用新架构重�?
    /// </summary>
    public class PurchaseService : ServiceBase<PurchaseConfiguration>
    {
        private readonly VPetLLM _plugin;
        private readonly List<PurchaseItem> _pendingPurchases = new();
        private readonly object _purchaseLock = new();
        private Timer? _purchaseTimer;
        private static readonly TimeSpan BatchProcessTime = TimeSpan.FromMilliseconds(500);

        public override string ServiceName => "PurchaseService";
        public override Version Version => new Version(2, 0, 0);

        /// <summary>
        /// 待处理的购买数量
        /// </summary>
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

        /// <summary>
        /// 购买批次处理完成事件
        /// </summary>
        public event EventHandler<List<PurchaseItem>>? BatchProcessed;

        public PurchaseService(
            VPetLLM plugin,
            PurchaseConfiguration configuration,
            IStructuredLogger logger,
            IEventBus eventBus)
            : base(configuration, logger, eventBus)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        }

        protected override async Task OnInitializeAsync(CancellationToken cancellationToken)
        {
            LogInformation("Initializing purchase service");

            // 订阅配置变更事件
            await _eventBus.SubscribeAsync<ConfigurationChangedEvent<PurchaseConfiguration>>(OnConfigurationChanged);

            LogInformation("Purchase service initialized successfully");
        }

        protected override async Task OnStartAsync(CancellationToken cancellationToken)
        {
            LogInformation("Starting purchase service");
            await Task.CompletedTask;
            LogInformation("Purchase service started successfully");
        }

        protected override async Task OnStopAsync(CancellationToken cancellationToken)
        {
            LogInformation("Stopping purchase service");

            // 处理所有待处理的购�?
            await ProcessPurchaseBatchInternalAsync();

            // 清理定时�?
            _purchaseTimer?.Stop();
            _purchaseTimer?.Dispose();
            _purchaseTimer = null;

            LogInformation("Purchase service stopped successfully");
        }

        protected override async Task OnHealthCheckAsync(CancellationToken cancellationToken)
        {
            // 检查是否有积压的购�?
            if (PendingPurchaseCount > 10)
            {
                LogWarning($"High number of pending purchases: {PendingPurchaseCount}");
            }

            await Task.CompletedTask;
        }

        private async Task OnConfigurationChanged(ConfigurationChangedEvent<PurchaseConfiguration> evt)
        {
            LogInformation("Purchase configuration changed");
            await Task.CompletedTask;
        }

        /// <summary>
        /// 处理购买事件
        /// </summary>
        public void HandlePurchase(Food food, int count, string source)
        {
            try
            {
                if (food is null)
                {
                    LogWarning("Purchase event: food is null, skipping");
                    return;
                }

                if (!Configuration.EnableBuyFeedback)
                {
                    LogDebug("Purchase event: BuyFeedback is disabled, skipping");
                    return;
                }

                // 使用PurchaseSourceDetector模块检测购买来�?
                var purchaseSource = PurchaseSourceDetector.DetectPurchaseSource(source);
                string sourceDescription = PurchaseSourceDetector.GetPurchaseSourceDescription(purchaseSource);

                // 获取物品类型
                string itemType = GetItemType(food);
                LogInformation($"Purchase detected: {food.Name} x{count} from {sourceDescription} (type: {itemType})");

                // 判断是否应该触发AI反馈
                if (!PurchaseSourceDetector.ShouldTriggerAIFeedback(source))
                {
                    LogDebug($"Purchase from {sourceDescription} does not trigger AI feedback");
                    return;
                }

                if (_plugin.ChatCore is null)
                {
                    LogWarning("Purchase event: ChatCore is null, skipping");
                    return;
                }

                // 添加到批处理队列
                AddToPurchaseBatch(food, count, itemType, source);
            }
            catch (Exception ex)
            {
                LogError("Error handling purchase event", ex);
            }
        }

        /// <summary>
        /// 获取物品类型（兼容新旧版本）
        /// </summary>
        private string GetItemType(Food food)
        {
            try
            {
                // 尝试获取 ItemType 属性（新版�?VPet�?
                var itemTypeProperty = food.GetType().GetProperty("ItemType");
                if (itemTypeProperty is not null)
                {
                    var value = itemTypeProperty.GetValue(food) as string;
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }
            catch (Exception ex)
            {
                LogWarning($"GetItemType reflection failed: {ex.Message}");
            }

            // 默认返回 "Food"
            return "Food";
        }

        private void AddToPurchaseBatch(Food food, int count, string itemType, string source)
        {
            lock (_purchaseLock)
            {
                var existingItem = _pendingPurchases.FirstOrDefault(p => p.Name == food.Name && p.ItemType == itemType);
                if (existingItem is not null)
                {
                    existingItem.Quantity += count;
                    existingItem.PurchaseTime = DateTime.Now;
                    LogDebug($"Updated purchase quantity for {food.Name}: {existingItem.Quantity}");
                }
                else
                {
                    var purchaseItem = new PurchaseItem
                    {
                        Name = food.Name,
                        ItemType = itemType,
                        FoodType = food.Type,
                        Price = food.Price,
                        PurchaseTime = DateTime.Now,
                        Quantity = count,
                        Description = food.Desc ?? "",
                        OriginalFood = food,
                        Source = source
                    };

                    _pendingPurchases.Add(purchaseItem);
                    LogDebug($"Added new purchase item: {food.Name} x{count} (type: {itemType})");
                }

                ResetPurchaseTimer();
            }
        }

        private void ResetPurchaseTimer()
        {
            _purchaseTimer?.Stop();
            _purchaseTimer?.Dispose();

            _purchaseTimer = new Timer(BatchProcessTime.TotalMilliseconds);
            _purchaseTimer.Elapsed += async (sender, e) => await ProcessPurchaseBatchInternalAsync();
            _purchaseTimer.AutoReset = false;
            _purchaseTimer.Start();

            LogDebug($"Purchase timer reset, will process batch in {BatchProcessTime.TotalMilliseconds}ms");
        }

        /// <summary>
        /// 处理待处理的购买批次
        /// </summary>
        public async Task ProcessPendingPurchasesAsync()
        {
            await ProcessPurchaseBatchInternalAsync();
        }

        private async Task ProcessPurchaseBatchInternalAsync()
        {
            List<PurchaseItem> itemsToProcess;

            lock (_purchaseLock)
            {
                if (_pendingPurchases.Count == 0)
                {
                    LogDebug("No pending purchases to process");
                    return;
                }

                itemsToProcess = new List<PurchaseItem>(_pendingPurchases);
                _pendingPurchases.Clear();
                LogInformation($"Processing batch of {itemsToProcess.Count} purchase items");
            }

            try
            {
                await ProcessBatchPurchaseFeedback(itemsToProcess);

                // 触发批次处理完成事件
                BatchProcessed?.Invoke(this, itemsToProcess);

                // 发布事件总线事件
                await _eventBus.PublishAsync(new PurchaseBatchProcessedEvent
                {
                    Items = itemsToProcess,
                    TotalCount = itemsToProcess.Sum(i => i.Quantity),
                    TotalPrice = itemsToProcess.Sum(i => i.Price * i.Quantity),
                    ProcessTime = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                LogError("Error processing purchase batch", ex);
            }
        }

        private Task ProcessBatchPurchaseFeedback(List<PurchaseItem> purchases)
        {
            try
            {
                LogInformation($"Processing batch purchase feedback for {purchases.Count} items");

                string message = BuildBatchPurchaseMessage(purchases);

                if (!string.IsNullOrEmpty(message))
                {
                    LogDebug($"Sending batch message to aggregator: {message}");
                    ResultAggregator.Enqueue($"[System.BuyFeedback: \"{message}\"]");
                    LogInformation("Batch purchase feedback enqueued for aggregation");
                }
            }
            catch (Exception ex)
            {
                LogError("Error in ProcessBatchPurchaseFeedback", ex);
            }
            return Task.CompletedTask;
        }

        private string BuildBatchPurchaseMessage(List<PurchaseItem> purchases)
        {
            string template = PromptHelper.Get("BuyFeedback_Batch", _plugin.Settings.PromptLanguage);

            if (string.IsNullOrEmpty(template) || template.Contains("[Prompt Missing"))
            {
                template = PromptHelper.Get("BuyFeedback_Default", _plugin.Settings.PromptLanguage);
                if (string.IsNullOrEmpty(template) || template.Contains("[Prompt Missing"))
                {
                    return "";
                }

                var firstItem = purchases[0];
                return template
                    .Replace("{ItemName}", firstItem.Name)
                    .Replace("{ItemType}", GetItemTypeName(firstItem.ItemType, firstItem.FoodType))
                    .Replace("{ItemPrice}", firstItem.Price.ToString("F2"))
                    .Replace("{EmotionState}", GetCurrentEmotionState());
            }

            var itemList = new List<string>();
            double totalPrice = 0;
            var itemsByType = purchases.GroupBy(p => p.ItemType).ToList();

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
                string typeName = GetItemTypeName(typeGroup.Key, typeGroup.First().FoodType);
                typeStats.Add($"{typeName}({totalQuantity})");
            }

            string separator = _plugin.Settings.PromptLanguage.StartsWith("zh") ? "、" : ", ";
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

        /// <summary>
        /// 获取物品类型名称（支持通用 Item 系统�?
        /// </summary>
        private string GetItemTypeName(string itemType, Food.FoodType? foodType = null)
        {
            // 如果�?Food 类型且有具体�?FoodType，使用更详细的分�?
            if (itemType == "Food" && foodType.HasValue)
            {
                string key = foodType.Value switch
                {
                    Food.FoodType.Food => "ItemType.Food",
                    Food.FoodType.Drink => "ItemType.Drink",
                    Food.FoodType.Drug => "ItemType.Drug",
                    Food.FoodType.Gift => "ItemType.Gift",
                    _ => "ItemType.General"
                };

                string result = LanguageHelper.Get(key, _plugin.Settings.Language);
                if (!string.IsNullOrEmpty(result) && !result.Contains("["))
                {
                    return result;
                }
                return foodType.Value.ToString().ToLower();
            }

            // 通用 Item 类型处理
            string itemKey = itemType switch
            {
                "Food" => "ItemType.Food",
                "Tool" => "ItemType.Tool",
                "Toy" => "ItemType.Toy",
                "Item" => "ItemType.Item",
                _ => "ItemType.General"
            };

            string itemResult = LanguageHelper.Get(itemKey, _plugin.Settings.Language);
            if (!string.IsNullOrEmpty(itemResult) && !itemResult.Contains("["))
            {
                return itemResult;
            }

            // 降级：返回原始类型名
            return itemType.ToLower();
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

            string result = LanguageHelper.Get(key, _plugin.Settings.Language);
            return string.IsNullOrEmpty(result) || result.Contains("[") ? "normal" : result;
        }

        protected override void OnDispose()
        {
            {
                _purchaseTimer?.Stop();
                _purchaseTimer?.Dispose();
                _purchaseTimer = null;
            }

            // Disposed by base class
        }
    }

    /// <summary>
    /// 购买物品信息
    /// </summary>
    public class PurchaseItem
    {
        public string Name { get; set; } = "";
        public string ItemType { get; set; } = "Food";
        public Food.FoodType? FoodType { get; set; }
        public double Price { get; set; }
        public DateTime PurchaseTime { get; set; }
        public int Quantity { get; set; } = 1;
        public string Description { get; set; } = "";
        public Food? OriginalFood { get; set; }
        public string Source { get; set; } = "";
    }

    /// <summary>
    /// 购买批次处理完成事件
    /// </summary>
    public class PurchaseBatchProcessedEvent
    {
        public List<PurchaseItem> Items { get; set; } = new();
        public int TotalCount { get; set; }
        public double TotalPrice { get; set; }
        public DateTime ProcessTime { get; set; }
    }

    /// <summary>
    /// 购买配置
    /// </summary>
    public class PurchaseConfiguration : IConfiguration
    {
        public string ConfigurationName => "PurchaseConfiguration";
        public Version Version => new Version(1, 0, 0);
        public DateTime LastModified { get; set; } = DateTime.Now;
        public bool IsModified { get; set; }

        public bool EnableBuyFeedback { get; set; } = true;
        public int BatchProcessDelayMs { get; set; } = 500;

        public IConfiguration Clone()
        {
            return new PurchaseConfiguration
            {
                EnableBuyFeedback = this.EnableBuyFeedback,
                BatchProcessDelayMs = this.BatchProcessDelayMs,
                LastModified = this.LastModified,
                IsModified = this.IsModified
            };
        }

        public void Merge(IConfiguration other)
        {
            if (other is PurchaseConfiguration config)
            {
                EnableBuyFeedback = config.EnableBuyFeedback;
                BatchProcessDelayMs = config.BatchProcessDelayMs;
                IsModified = true;
                LastModified = DateTime.Now;
            }
        }

        public void ResetToDefaults()
        {
            EnableBuyFeedback = true;
            BatchProcessDelayMs = 500;
            IsModified = true;
            LastModified = DateTime.Now;
        }

        public SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (BatchProcessDelayMs < 0)
            {
                result.IsValid = false;
                result.Errors.Add("BatchProcessDelayMs must be non-negative");
            }

            return result;
        }
    }
}
