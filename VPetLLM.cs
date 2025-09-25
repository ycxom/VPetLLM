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
            
            TTSService?.Dispose();
            TouchInteractionHandler?.Dispose();
            _syncTimer?.Stop();
            _syncTimer?.Dispose();
        }

        // 防抖动相关字段
        private static readonly Dictionary<string, DateTime> _lastPurchaseTime = new();
        private static readonly TimeSpan _debounceTime = TimeSpan.FromMilliseconds(500);

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

                // 防抖动检查
                if (IsOnDebounce(food.Name))
                {
                    Utils.Logger.Log($"Purchase event: {food.Name} is on debounce, skipping");
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

                // 更新防抖动时间
                UpdateDebounceTime(food.Name);

                // 直接处理购买反馈，不使用BuyHandler
                await ProcessPurchaseFeedback(food);
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Error handling purchase event: {ex.Message}");
                Utils.Logger.Log($"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 检查是否在防抖动时间内
        /// </summary>
        private bool IsOnDebounce(string itemName)
        {
            if (_lastPurchaseTime.TryGetValue(itemName, out var lastTime))
            {
                return DateTime.Now - lastTime < _debounceTime;
            }
            return false;
        }

        /// <summary>
        /// 更新防抖动时间
        /// </summary>
        private void UpdateDebounceTime(string itemName)
        {
            _lastPurchaseTime[itemName] = DateTime.Now;
        }

        /// <summary>
        /// 处理购买反馈
        /// </summary>
        private async Task ProcessPurchaseFeedback(Food food)
        {
            try
            {
                Utils.Logger.Log($"Processing purchase feedback for: {food.Name}");
                
                // 使用Prompt.json中的购买反馈提示词
                string promptKey = GetBuyFeedbackPromptKey(food);
                string message = PromptHelper.Get(promptKey, Settings.PromptLanguage);
                
                if (string.IsNullOrEmpty(message))
                {
                    // 如果没有找到对应的提示词，使用默认提示词
                    message = PromptHelper.Get("BuyFeedback_Default", Settings.PromptLanguage);
                }
                
                if (string.IsNullOrEmpty(message))
                {
                    // 如果还是没有找到，使用简单的默认消息
                    message = $"我刚刚收到了{food.Name}！";
                }
                else
                {
                    // 替换提示词中的占位符
                    message = message.Replace("{ItemName}", food.Name)
                                   .Replace("{ItemType}", GetItemType(food))
                                   .Replace("{ItemPrice}", food.Price.ToString("F2"))
                                   .Replace("{EmotionState}", GetCurrentEmotionState())
                                   .Replace("{CurrentHealth}", MW.Core.Save.Health.ToString("F0"))
                                   .Replace("{CurrentMood}", MW.Core.Save.Feeling.ToString("F0"))
                                   .Replace("{CurrentMoney}", MW.Core.Save.Money.ToString("F2"))
                                   .Replace("{CurrentHunger}", MW.Core.Save.StrengthFood.ToString("F0"))
                                   .Replace("{CurrentThirst}", MW.Core.Save.StrengthDrink.ToString("F0"));
                }
                
                Utils.Logger.Log($"Sending message to ChatCore: {message}");
                await ChatCore.Chat(message);
                Utils.Logger.Log("Purchase feedback sent successfully");
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Error in ProcessPurchaseFeedback: {ex.Message}");
                Utils.Logger.Log($"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 根据物品类型获取对应的购买反馈提示词键
        /// </summary>
        private string GetBuyFeedbackPromptKey(Food food)
        {
            // 根据物品类型返回对应的提示词键
            switch (food.Type)
            {
                case VPet_Simulator.Windows.Interface.Food.FoodType.Food:
                    return "BuyFeedback_Food";
                case VPet_Simulator.Windows.Interface.Food.FoodType.Drink:
                    return "BuyFeedback_Drink";
                case VPet_Simulator.Windows.Interface.Food.FoodType.Drug:
                    return "BuyFeedback_Drug";
                case VPet_Simulator.Windows.Interface.Food.FoodType.Gift:
                    return "BuyFeedback_Gift";
                default:
                    return "BuyFeedback_General";
            }
        }

        /// <summary>
        /// 获取物品类型的本地化名称
        /// </summary>
        private string GetItemType(Food food)
        {
            switch (food.Type)
            {
                case VPet_Simulator.Windows.Interface.Food.FoodType.Food:
                    return Settings.Language.StartsWith("zh") ? "食物" : "food";
                case VPet_Simulator.Windows.Interface.Food.FoodType.Drink:
                    return Settings.Language.StartsWith("zh") ? "饮料" : "drink";
                case VPet_Simulator.Windows.Interface.Food.FoodType.Drug:
                    return Settings.Language.StartsWith("zh") ? "药品" : "medicine";
                case VPet_Simulator.Windows.Interface.Food.FoodType.Gift:
                    return Settings.Language.StartsWith("zh") ? "礼物" : "gift";
                default:
                    return Settings.Language.StartsWith("zh") ? "物品" : "item";
            }
        }

        /// <summary>
        /// 获取当前情绪状态
        /// </summary>
        private string GetCurrentEmotionState()
        {
            var feeling = MW.Core.Save.Feeling;
            var health = MW.Core.Save.Health;
            
            if (Settings.Language.StartsWith("zh"))
            {
                if (health < 50) return "不舒服";
                if (feeling > 80) return "非常开心";
                if (feeling > 60) return "开心";
                if (feeling > 40) return "普通";
                if (feeling > 20) return "有点不开心";
                return "很不开心";
            }
            else
            {
                if (health < 50) return "unwell";
                if (feeling > 80) return "very happy";
                if (feeling > 60) return "happy";
                if (feeling > 40) return "normal";
                if (feeling > 20) return "a bit unhappy";
                return "very unhappy";
            }
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