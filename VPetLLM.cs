using System.IO;
using System.Windows;
using System.Windows.Controls;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core;
using VPetLLM.Core.ChatCore;
using VPetLLM.Handlers;
using VPetLLM.UI.Windows;
using VPetLLM.Utils;

namespace VPetLLM
{
    public class VPetLLM : MainPlugin
    {
        public static VPetLLM? Instance { get; private set; }
        public Setting Settings;
        public IChatCore? ChatCore;
        public Windows.TalkBox? TalkBox;
        public ActionProcessor? ActionProcessor;
        public TouchInteractionHandler? TouchInteractionHandler;
        private System.Timers.Timer _syncTimer;
        public List<IVPetLLMPlugin> Plugins => PluginManager.Plugins;
        public List<FailedPlugin> FailedPlugins => PluginManager.FailedPlugins;
        public string PluginPath => PluginManager.PluginPath;
        public winSettingNew? SettingWindow;
        public TTSService? TTSService;

        public VPetLLM(IMainWindow mainwin) : base(mainwin)
        {
            Instance = this;
            Utils.Logger.Log("VPetLLM plugin constructor started.");
            Settings = new Setting(ExtensionValue.BaseDirectory);
            Utils.Logger.Log("Settings loaded.");
            var dllPath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var langPath = Path.Combine(dllPath, "VPetLLM_lang", "Language.json");
            LanguageHelper.LoadLanguages(langPath);
            PromptHelper.LoadPrompts(langPath);
            if (string.IsNullOrEmpty(Settings.Language))
            {
                var culture = System.Globalization.CultureInfo.CurrentUICulture.Name.ToLower();
                if (LanguageHelper.LanguageDisplayMap.ContainsKey(culture))
                {
                    Settings.Language = culture;
                }
                else
                {
                    Settings.Language = "en";
                }
            }
            ActionProcessor = new ActionProcessor(mainwin);
            switch (Settings.Provider)
            {
                case global::VPetLLM.Setting.LLMType.Ollama:
                    ChatCore = new OllamaChatCore(Settings.Ollama, Settings, mainwin, ActionProcessor);
                    Utils.Logger.Log("Chat core set to Ollama.");
                    break;
                case global::VPetLLM.Setting.LLMType.OpenAI:
                    ChatCore = new OpenAIChatCore(Settings.OpenAI, Settings, mainwin, ActionProcessor);
                    Utils.Logger.Log("Chat core set to OpenAI.");
                    break;
                case global::VPetLLM.Setting.LLMType.Gemini:
                    ChatCore = new GeminiChatCore(Settings.Gemini, Settings, mainwin, ActionProcessor);
                    Utils.Logger.Log("Chat core set to Gemini.");
                    break;
                case global::VPetLLM.Setting.LLMType.Free:
                    ChatCore = new FreeChatCore(Settings.Free, Settings, mainwin, ActionProcessor);
                    Utils.Logger.Log("Chat core set to Free.");
                    break;
            }
            // 加载聊天历史记录
            Utils.Logger.Log("VPetLLM plugin constructor finished.");

            _syncTimer = new System.Timers.Timer(5000); // 5 seconds
            _syncTimer.Elapsed += SyncNames;
            _syncTimer.AutoReset = true;
            _syncTimer.Enabled = true;

            // 初始化TTS服务
            TTSService = new TTSService(Settings.TTS, Settings.Proxy);

            LoadPlugins();
        }

        private void SyncNames(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (Settings.FollowVPetName)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Settings.AiName = MW.Core.Save.Name;
                    Settings.UserName = MW.Core.Save.HostName;
                });
            }
        }

        public override void LoadPlugin()
        {
            Utils.Logger.Log("LoadPlugin started.");
            ChatCore?.LoadHistory();
            Application.Current.Dispatcher.Invoke(() =>
            {
                Utils.Logger.Log("Dispatcher.Invoke started.");
                if (TalkBox != null)
                {
                    MW.TalkAPI.Remove(TalkBox);
                }
                TalkBox = new Windows.TalkBox(this);
                MW.TalkAPI.Add(TalkBox);
                var menuItem = new MenuItem()
                {
                    Header = "VPetLLM",
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                menuItem.Click += (s, e) => Setting();
                MW.Main.ToolBar.MenuMODConfig.Items.Add(menuItem);
                
                // 在LoadPlugin阶段初始化TouchInteractionHandler，确保Main窗口已经完全加载
                InitializeTouchInteractionHandler();
                
                // 监听购买事件
                MW.Event_TakeItem += OnTakeItem;
                Utils.Logger.Log("Purchase event listener registered.");
                
                Utils.Logger.Log("Dispatcher.Invoke finished.");
            });
            Utils.Logger.Log("LoadPlugin finished.");
        }

        public override void Save()
        {
            Settings.Save();
            ChatCore?.SaveHistory();
            SavePluginStates();
        }

        public void Dispose()
        {
            // 取消事件监听
            if (MW != null)
            {
                MW.Event_TakeItem -= OnTakeItem;
            }
            
            // 清理购买计时器
            _purchaseTimer?.Stop();
            _purchaseTimer?.Dispose();
            
            TTSService?.Dispose();
            TouchInteractionHandler?.Dispose();
            _syncTimer?.Stop();
            _syncTimer?.Dispose();
        }

        // 购买批处理相关字段
        private readonly List<PurchaseItem> _pendingPurchases = new();
        private readonly object _purchaseLock = new object();
        private System.Timers.Timer? _purchaseTimer;
        private static readonly TimeSpan _batchProcessTime = TimeSpan.FromMilliseconds(1000); // 1秒批处理时间

        /// <summary>
        /// 购买物品信息
        /// </summary>
        public class PurchaseItem
        {
            public string Name { get; set; } = "";
            public VPet_Simulator.Windows.Interface.Food.FoodType Type { get; set; }
            public double Price { get; set; }
            public DateTime PurchaseTime { get; set; }
            public int Quantity { get; set; } = 1;
        }

        /// <summary>
        /// 处理购买事件
        /// </summary>
        private async void OnTakeItem(Food food)
        {
            try
            {
                Utils.Logger.Log($"Purchase event detected: {food?.Name ?? "Unknown"}");
                
                if (food == null)
                {
                    Utils.Logger.Log("Purchase event: food is null, skipping");
                    return;
                }

                if (MW == null)
                {
                    Utils.Logger.Log("Purchase event: MW is null, skipping");
                    return;
                }

                if (!Settings.EnableBuyFeedback)
                {
                    Utils.Logger.Log("Purchase event: BuyFeedback is disabled, skipping");
                    return;
                }

                if (ChatCore == null)
                {
                    Utils.Logger.Log("Purchase event: ChatCore is null, skipping");
                    return;
                }

                // 添加到批处理队列
                AddToPurchaseBatch(food);
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Error handling purchase event: {ex.Message}");
                Utils.Logger.Log($"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 添加购买物品到批处理队列
        /// </summary>
        private void AddToPurchaseBatch(Food food)
        {
            lock (_purchaseLock)
            {
                // 检查是否已存在相同物品
                var existingItem = _pendingPurchases.FirstOrDefault(p => p.Name == food.Name);
                if (existingItem != null)
                {
                    // 增加数量
                    existingItem.Quantity++;
                    existingItem.PurchaseTime = DateTime.Now; // 更新最后购买时间
                    Utils.Logger.Log($"Updated purchase quantity for {food.Name}: {existingItem.Quantity}");
                }
                else
                {
                    // 添加新物品
                    _pendingPurchases.Add(new PurchaseItem
                    {
                        Name = food.Name,
                        Type = food.Type,
                        Price = food.Price,
                        PurchaseTime = DateTime.Now,
                        Quantity = 1
                    });
                    Utils.Logger.Log($"Added new purchase item: {food.Name}");
                }

                // 重置或启动计时器
                ResetPurchaseTimer();
            }
        }

        /// <summary>
        /// 重置购买处理计时器
        /// </summary>
        private void ResetPurchaseTimer()
        {
            // 停止现有计时器
            _purchaseTimer?.Stop();
            _purchaseTimer?.Dispose();

            // 创建新计时器
            _purchaseTimer = new System.Timers.Timer(_batchProcessTime.TotalMilliseconds);
            _purchaseTimer.Elapsed += async (sender, e) => await ProcessPurchaseBatch();
            _purchaseTimer.AutoReset = false; // 只执行一次
            _purchaseTimer.Start();

            Utils.Logger.Log($"Purchase timer reset, will process batch in {_batchProcessTime.TotalMilliseconds}ms");
        }

        /// <summary>
        /// 处理购买批次
        /// </summary>
        private async Task ProcessPurchaseBatch()
        {
            List<PurchaseItem> itemsToProcess;
            
            lock (_purchaseLock)
            {
                if (_pendingPurchases.Count == 0)
                {
                    Utils.Logger.Log("No pending purchases to process");
                    return;
                }

                // 复制待处理列表并清空原列表
                itemsToProcess = new List<PurchaseItem>(_pendingPurchases);
                _pendingPurchases.Clear();
                Utils.Logger.Log($"Processing batch of {itemsToProcess.Count} purchase items");
            }

            try
            {
                await ProcessBatchPurchaseFeedback(itemsToProcess);
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Error processing purchase batch: {ex.Message}");
                Utils.Logger.Log($"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 处理批量购买反馈
        /// </summary>
        private async Task ProcessBatchPurchaseFeedback(List<PurchaseItem> purchases)
        {
            try
            {
                Utils.Logger.Log($"Processing batch purchase feedback for {purchases.Count} items");
                
                // 构建批量购买消息
                string message = BuildBatchPurchaseMessage(purchases);
                
                Utils.Logger.Log($"Sending batch message to aggregator: {message}");
                // 非用户触发的系统反馈：进入2秒聚合，再统一回灌给AI，避免连续唤起
                ResultAggregator.Enqueue($"[System.BuyFeedback: \"{message}\"]");
                Utils.Logger.Log("Batch purchase feedback enqueued for aggregation");
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Error in ProcessBatchPurchaseFeedback: {ex.Message}");
                Utils.Logger.Log($"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 构建批量购买消息
        /// </summary>
        private string BuildBatchPurchaseMessage(List<PurchaseItem> purchases)
        {
            // 获取批量购买提示词
            string template = PromptHelper.Get("BuyFeedback_Batch", Settings.PromptLanguage);
            
            if (string.IsNullOrEmpty(template) || template.Contains("[Prompt Missing"))
            {
                // 如果没有找到批量模板，使用默认购买提示词
                template = PromptHelper.Get("BuyFeedback_Default", Settings.PromptLanguage);
                if (string.IsNullOrEmpty(template) || template.Contains("[Prompt Missing"))
                {
                    // 如果连默认提示词都没有，直接返回空字符串让AI自行判断
                    return "";
                }
                
                // 使用第一个物品的信息填充默认模板
                var firstItem = purchases[0];
                return template
                    .Replace("{ItemName}", firstItem.Name)
                    .Replace("{ItemType}", GetItemTypeName(firstItem.Type))
                    .Replace("{ItemPrice}", firstItem.Price.ToString("F2"))
                    .Replace("{EmotionState}", GetCurrentEmotionState());
            }

            // 构建所有占位符的替换值
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

            // 构建类型统计
            var typeStats = new List<string>();
            foreach (var typeGroup in itemsByType)
            {
                int totalQuantity = typeGroup.Sum(p => p.Quantity);
                string typeName = GetItemTypeName(typeGroup.Key);
                typeStats.Add($"{typeName}({totalQuantity}个)");
            }

            // 替换所有占位符
            string separator = Settings.PromptLanguage.StartsWith("zh") ? "、" : ", ";
            string message = template
                .Replace("{ItemCount}", purchases.Count.ToString())
                .Replace("{ItemList}", string.Join(separator, itemList))
                .Replace("{TotalPrice}", totalPrice.ToString("F2"))
                .Replace("{TypeStats}", string.Join(separator, typeStats))
                .Replace("{EmotionState}", GetCurrentEmotionState())
                .Replace("{CurrentHealth}", $"{(MW.Core.Save.Health / 100.0 * 100):F0}%")
                .Replace("{CurrentMood}", $"{(MW.Core.Save.Feeling / MW.Core.Save.FeelingMax * 100):F0}%")
                .Replace("{CurrentMoney}", MW.Core.Save.Money.ToString("F2"))
                .Replace("{CurrentHunger}", $"{(MW.Core.Save.StrengthFood / MW.Core.Save.StrengthMax * 100):F0}%")
                .Replace("{CurrentThirst}", $"{(MW.Core.Save.StrengthDrink / MW.Core.Save.StrengthMax * 100):F0}%")
                .Replace("{PurchaseTime}", DateTime.Now.ToString("HH:mm:ss"));

            return message;
        }

        /// <summary>
        /// 构建默认批量消息（已废弃，不应该被调用）
        /// </summary>
        private string BuildDefaultBatchMessage(List<PurchaseItem> purchases)
        {
            // 这个方法不应该被调用，因为我们已经移除了对它的调用
            // 如果被调用，返回空字符串让AI自行处理
            return "";
        }

        /// <summary>
        /// 获取物品类型名称
        /// </summary>
        private string GetItemTypeName(VPet_Simulator.Windows.Interface.Food.FoodType type)
        {
            string key = type switch
            {
                VPet_Simulator.Windows.Interface.Food.FoodType.Food => "ItemType.Food",
                VPet_Simulator.Windows.Interface.Food.FoodType.Drink => "ItemType.Drink", 
                VPet_Simulator.Windows.Interface.Food.FoodType.Drug => "ItemType.Drug",
                VPet_Simulator.Windows.Interface.Food.FoodType.Gift => "ItemType.Gift",
                _ => "ItemType.General"
            };
            
            string result = LanguageHelper.Get(key, Settings.Language);
            return string.IsNullOrEmpty(result) || result.Contains("[") ? type.ToString().ToLower() : result;
        }

        /// <summary>
        /// 获取当前情绪状态
        /// </summary>
        private string GetCurrentEmotionState()
        {
            var feeling = MW.Core.Save.Feeling;
            var health = MW.Core.Save.Health;
            
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
            
            string result = LanguageHelper.Get(key, Settings.Language);
            return string.IsNullOrEmpty(result) || result.Contains("[") ? "normal" : result;
        }

        public void UpdateChatCore(IChatCore newChatCore)
        {
            // 首先更新ChatCore字段
            ChatCore = newChatCore;
            Utils.Logger.Log($"ChatCore updated to: {ChatCore?.GetType().Name}");

            // 重新加载聊天历史记录
            ChatCore?.LoadHistory();
            Utils.Logger.Log("Chat history reloaded");

            // 重新创建TalkBox以确保使用新的ChatCore实例
            // 使用InvokeAsync并等待完成，确保热重载生效
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (TalkBox != null)
                {
                    MW.TalkAPI.Remove(TalkBox);
                    Utils.Logger.Log("Old TalkBox removed from TalkAPI");
                }
                TalkBox = new Windows.TalkBox(this);
                MW.TalkAPI.Add(TalkBox);
                Utils.Logger.Log("New TalkBox added to TalkAPI");

                // 验证新的TalkBox是否使用正确的ChatCore实例
                Utils.Logger.Log($"New TalkBox should use ChatCore: {ChatCore?.GetType().Name}");
            }).Wait(); // 等待操作完成
        }

        public override void Setting()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var settingWindow = new winSettingNew(this);
                settingWindow.Show();
            });
        }

        public void RefreshPluginList()
        {
            SettingWindow?.RefreshPluginList();
        }

        public void ResetSettings()
        {
            Settings = new Setting(ExtensionValue.BaseDirectory);
            Settings.Save();
        }


        public override string PluginName => "VPetLLM";


        public async Task<string> SendChat(string prompt)
        {
            if (ChatCore == null)
            {
                return "错误：聊天核心未初始化。";
            }
            PromptHelper.ReloadPrompts();
            var response = await ChatCore.Chat(prompt);

            // 注意：TTS和动作处理现在由SmartMessageProcessor在HandleResponse中统一处理
            // 这里只返回原始回复，不再单独处理TTS

            return response;
        }

        public List<Message> GetChatHistory()
        {
            return ChatCore?.GetChatHistory() ?? new List<Message>();
        }

        public void SetChatHistory(List<Message> history)
        {
            ChatCore?.SetChatHistory(history);
        }

        public void ClearChatHistory()
        {
            ChatCore?.ClearContext();
        }
        // 测试方法：验证当前ChatCore实例类型
        public string GetCurrentChatCoreInfo()
        {
            return $"ChatCore Type: {ChatCore?.GetType().Name}, Hash: {ChatCore?.GetHashCode()}";
        }

        public void LoadPlugins()
        {
            PluginManager.LoadPlugins(this.ChatCore);
        }

        public async Task<bool> UpdatePlugin(string pluginFilePath)
        {
            return await PluginManager.UpdatePlugin(pluginFilePath, this.ChatCore);
        }

        public void SavePluginStates()
        {
            PluginManager.SavePluginStates();
        }

        public async Task<bool> UnloadAndTryDeletePlugin(IVPetLLMPlugin plugin)
        {
            return await PluginManager.UnloadAndTryDeletePlugin(plugin, this.ChatCore);
        }

        public void ImportPlugin(string filePath)
        {
            PluginManager.ImportPlugin(filePath);
            LoadPlugins();
            RefreshPluginList();
        }

        public async Task<bool> DeletePluginFile(string pluginFilePath)
        {
            return await PluginManager.DeletePluginFile(pluginFilePath);
        }

        public void Log(string message)
        {
            Logger.Log(message);
        }

        public void UpdateSystemMessage()
        {
            if (ChatCore != null)
            {
                // The system message is fetched dynamically, so we just need to ensure the chat core is aware of changes.
                // This can be a no-op if the system message is always fetched fresh,
                // but it's good practice to have a method for explicit updates.
            }
        }

        public void UpdateActionProcessor()
        {
            ActionProcessor?.RegisterHandlers();
        }

        public void UpdateTTSService()
        {
            TTSService?.UpdateSettings(Settings.TTS, Settings.Proxy);
            // Logger.Log("TTS服务设置已更新");
        }

        /// <summary>
        /// 初始化触摸交互处理器
        /// </summary>
        private void InitializeTouchInteractionHandler()
        {
            try
            {
                Logger.Log("开始初始化TouchInteractionHandler...");
                
                // 检查必要的组件是否已准备好
                if (MW?.Main == null)
                {
                    Logger.Log("错误：MW.Main为null，无法初始化TouchInteractionHandler");
                    return;
                }
                
                Logger.Log("MW.Main已准备好，创建TouchInteractionHandler...");
                TouchInteractionHandler = new TouchInteractionHandler(this);
                Logger.Log("TouchInteractionHandler初始化成功");
            }
            catch (Exception ex)
            {
                Logger.Log($"初始化TouchInteractionHandler时发生错误: {ex.Message}");
                Logger.Log($"错误堆栈: {ex.StackTrace}");
                TouchInteractionHandler = null;
            }
        }

        public async Task PlayTTSAsync(string text)
        {
            if (Settings.TTS.IsEnabled && TTSService != null && !string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    await TTSService.PlayTextAsync(text);
                }
                catch (Exception ex)
                {
                    Logger.Log($"TTS播放失败: {ex.Message}");
                }
            }
        }

        public IEnumerable<string> GetAvailableAnimations()
        {
            return MW.Main.Core.Graph.GraphsList.Keys;
        }
        public IEnumerable<string> GetAvailableSayAnimations()
        {
            if (MW.Main.Core.Graph.GraphsName.TryGetValue(VPet_Simulator.Core.GraphInfo.GraphType.Say, out var gl))
            {
                return gl;
            }
            return new List<string>();
        }
    }
}