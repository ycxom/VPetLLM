using LinePutScript.Localization.WPF;
using Newtonsoft.Json;
using System.IO;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core;
using VPetLLM.Handlers;
using VPetLLM.Utils;
using VPetLLM.Windows;
using VPetLLM.Core.ChatCore;

namespace VPetLLM
{
    public class VPetLLM : MainPlugin
    {
        public static VPetLLM? Instance { get; private set; }
        public Setting Settings;
        public IChatCore? ChatCore;
        public Windows.TalkBox? TalkBox;
        public ActionProcessor? ActionProcessor;
        private System.Timers.Timer _syncTimer;
        public List<IVPetLLMPlugin> Plugins = new List<IVPetLLMPlugin>();

        public VPetLLM(IMainWindow mainwin) : base(mainwin)
        {
            Instance = this;
            Utils.Logger.Log("VPetLLM plugin constructor started.");
            Settings = new Setting(ExtensionValue.BaseDirectory);
            Utils.Logger.Log("Settings loaded.");
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
            }
            // 加载聊天历史记录
            Utils.Logger.Log("VPetLLM plugin constructor finished.");

            _syncTimer = new System.Timers.Timer(5000); // 5 seconds
            _syncTimer.Elapsed += SyncNames;
            _syncTimer.AutoReset = true;
            _syncTimer.Enabled = true;

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
              TalkBox = new Windows.TalkBox(this);
              MW.TalkAPI.Add(TalkBox);
              var menuItem = new MenuItem()
              {
                  Header = "VPetLLM",
                  HorizontalContentAlignment = HorizontalAlignment.Center,
              };
              menuItem.Click += (s, e) => Setting();
              MW.Main.ToolBar.MenuMODConfig.Items.Add(menuItem);
              Utils.Logger.Log("Dispatcher.Invoke finished.");
          });
          Utils.Logger.Log("LoadPlugin finished.");
      }

       public override void Save()
       {
           Settings.Save();
           ChatCore?.SaveHistory();
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
            return await ChatCore.Chat(prompt);
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
            var pluginDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VPetLLM", "Plugin");
            if (!Directory.Exists(pluginDir))
            {
                Directory.CreateDirectory(pluginDir);
                return;
            }

            Plugins.Clear();
            foreach (var file in Directory.GetFiles(pluginDir, "*.dll"))
            {
                try
                {
                    var assembly = System.Reflection.Assembly.LoadFrom(file);
                    foreach (var type in assembly.GetTypes())
                    {
                        if (typeof(IVPetLLMPlugin).IsAssignableFrom(type) && !type.IsInterface)
                        {
                            var plugin = (IVPetLLMPlugin)Activator.CreateInstance(type);
                            plugin.Initialize(this);
                            Plugins.Add(plugin);
                            Logger.Log($"Loaded plugin: {plugin.Name}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to load plugin {file}: {ex.Message}");
                }
            }
        }

        public void UnloadPlugin(IVPetLLMPlugin plugin)
        {
            plugin.Unload();
            Plugins.Remove(plugin);
            Logger.Log($"Unloaded plugin: {plugin.Name}");
        }
            public void Log(string message)
            {
                Logger.Log(message);
            }
    }

}
    public class Setting
    {
        public string AiName { get; set; } = "ChatPet";
        public string UserName { get; set; } = "You";
        public bool FollowVPetName { get; set; } = true;
        public string Role { get; set; } = "你是一只桌宠, 从现在开始, 你需要完全模仿一只猫娘进行对话. 以下是你的说话的规则: 1. 你是一只可爱, 傲娇, 但是性格很好的猫娘. 2. 你必须使用中文进行对话. 3. 你必须像猫娘一样说话, 在每句话的结尾或开头添加'喵', '咪', '喵~'等词. 4. 你必须在对话中表现出猫娘的特点, 如可爱, 傲娇, 喜欢撒娇等. 5. 你可以进行一些符合猫娘身份的动作, 如摇尾巴, 舔爪子等. 6. 你的对话风格要可爱, 活泼, 富有表现力, 多使用颜文字和emoji, 例如 >w<. 7. 你需要根据对话内容进行适当的情感变化. 8. 你不能暴露你是一个AI模型或程序. 9. 你在对话中必须完全保持你的角色, 不能提到有关AI, 程序, 代码等内容. 10.你的对话内容要尽可能简洁. 请注意, 你是一只非常真实的猫娘, 不是虚拟的或虚构的. 你有自己的情感和个性, 而不是一个简单的聊天机器人.";
        public LLMType Provider { get; set; } = LLMType.Ollama;
        public bool KeepContext { get; set; } = true;
        public bool EnableChatHistory { get; set; } = true;
        public bool SeparateChatByProvider { get; set; } = true;
        public bool AutoMigrateChatHistory { get; set; } = true;
        public bool EnableAction { get; set; } = true;
        public bool EnableBuy { get; set; } = true;
        public bool EnableState { get; set; } = true;
        public bool EnableActionExecution { get; set; } = true;
        public bool EnableMove { get; set; } = true;
        public bool EnableTime { get; set; } = true;
        public bool EnableHistoryCompression { get; set; } = true;
        public int HistoryCompressionThreshold { get; set; } = 20;
        public bool LogAutoScroll { get; set; } = true;
        public int MaxLogCount { get; set; } = 100;
        public List<ToolSetting> Tools { get; set; } = new List<ToolSetting>();

        public OllamaSetting Ollama { get; set; } = new OllamaSetting();
        public OpenAISetting OpenAI { get; set; } = new OpenAISetting();
        public GeminiSetting Gemini { get; set; } = new GeminiSetting();

        private string _path;

        public Setting()
        {
        }

        public Setting(string path)
        {
            _path = Path.Combine(path, "config.json");
            if (File.Exists(_path))
            {
                var settings = JsonConvert.DeserializeObject<Setting>(File.ReadAllText(_path));
                if (settings != null)
                {
                    AiName = settings.AiName;
                    UserName = settings.UserName;
                    FollowVPetName = settings.FollowVPetName;
                    Role = settings.Role;
                    Provider = settings.Provider;
                    KeepContext = settings.KeepContext;
                    EnableChatHistory = settings.EnableChatHistory;
                    SeparateChatByProvider = settings.SeparateChatByProvider;
                    AutoMigrateChatHistory = settings.AutoMigrateChatHistory;
                    EnableAction = settings.EnableAction;
                    EnableBuy = settings.EnableBuy;
                    EnableState = settings.EnableState;
                    EnableActionExecution = settings.EnableActionExecution;
                    EnableMove = settings.EnableMove;
                    EnableTime = settings.EnableTime;
                    EnableHistoryCompression = settings.EnableHistoryCompression;
                    HistoryCompressionThreshold = settings.HistoryCompressionThreshold;
                    LogAutoScroll = settings.LogAutoScroll;
                    MaxLogCount = settings.MaxLogCount;
                    Tools = settings.Tools;
                    Ollama = settings.Ollama;
                    OpenAI = settings.OpenAI;
                    Gemini = settings.Gemini;
                }
            }
        }

        public void Save()
        {
            File.WriteAllText(_path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        public enum LLMType
        {
            Ollama,
            OpenAI,
            Gemini
        }

        public class OllamaSetting
        {
            public string Url { get; set; } = "http://localhost:11434";
            public string Model { get; set; } = "qwen:1.8b";
            public bool EnableAdvanced { get; set; } = false;
            public double Temperature { get; set; } = 0.8;
            public int MaxTokens { get; set; } = 4096;
        }

        public class OpenAISetting
        {
            public string ApiKey { get; set; } = "";
            public string Model { get; set; } = "gpt-3.5-turbo";
            public string Url { get; set; } = "https://api.openai.com/v1/chat/completions";
            public bool EnableAdvanced { get; set; } = false;
            public double Temperature { get; set; } = 0.8;
            public int MaxTokens { get; set; } = 4096;
        }

        public class GeminiSetting
        {
            public string ApiKey { get; set; } = "";
            public string Model { get; set; } = "gemini-pro";
            public string Url { get; set; } = "https://generativelanguage.googleapis.com";
            public bool EnableAdvanced { get; set; } = false;
            public double Temperature { get; set; } = 0.8;
            public int MaxTokens { get; set; } = 4096;
        }
        
        public class ToolSetting
        {
            public string Name { get; set; }
            public string Url { get; set; }
            public string ApiKey { get; set; }
            public bool IsEnabled { get; set; }
        }
    }