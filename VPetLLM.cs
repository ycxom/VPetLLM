using LinePutScript.Localization.WPF;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core;
using VPetLLM.Core.ChatCore;
using VPetLLM.Handlers;
using VPetLLM.Utils;
using VPetLLM.Windows;

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
        public string PluginPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VPetLLM", "Plugin");
        public winSettingNew? SettingWindow;
        private readonly Dictionary<string, AssemblyLoadContext> _pluginContexts = new();

        public VPetLLM(IMainWindow mainwin) : base(mainwin)
        {
            Instance = this;
            Utils.Logger.Log("VPetLLM plugin constructor started.");
            Settings = new Setting(ExtensionValue.BaseDirectory);
            Utils.Logger.Log("Settings loaded.");
            var dllPath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var langPath = Path.Combine(dllPath, "VPetLLM_lang", "Language.json");
            LanguageHelper.LoadLanguages(langPath);
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
            var pluginDir = PluginPath;
            if (!Directory.Exists(pluginDir))
            {
                Directory.CreateDirectory(pluginDir);
                return;
            }

            var configFile = Path.Combine(pluginDir, "plugins.json");
            var pluginStates = new Dictionary<string, bool>();
            if (File.Exists(configFile))
            {
                pluginStates = JsonConvert.DeserializeObject<Dictionary<string, bool>>(File.ReadAllText(configFile));
            }
            foreach (var p in Plugins.ToList())
            {
                UnloadPlugin(p);
            }
            Plugins.Clear();
            _pluginContexts.Clear();
            foreach (var file in Directory.GetFiles(pluginDir, "*.dll"))
            {
                try
                {
                    var context = new AssemblyLoadContext(Path.GetFileNameWithoutExtension(file), isCollectible: true);
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
                    var assembly = context.LoadFromStream(fs);
                    _pluginContexts[file] = context;
                    foreach (var type in assembly.GetTypes())
                    {
                        if (typeof(IVPetLLMPlugin).IsAssignableFrom(type) && !type.IsInterface)
                        {
                            var plugin = (IVPetLLMPlugin)Activator.CreateInstance(type);
                            plugin.FilePath = file;
                            plugin.Enabled = pluginStates.TryGetValue(plugin.Name, out var enabled) ? enabled : true;
                            plugin.Initialize(this);
                            Plugins.Add(plugin);
                            if (plugin.Enabled)
                            {
                                ChatCore?.AddPlugin(plugin);
                            }
                            Logger.Log($"Loaded plugin: {plugin.Name}, Enabled: {plugin.Enabled}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to load plugin {file}: {ex.Message}");
                }
            }
        }

        public void SavePluginStates()
        {
            var pluginDir = PluginPath;
            var configFile = Path.Combine(pluginDir, "plugins.json");
            var pluginStates = Plugins.ToDictionary(p => p.Name, p => p.Enabled);
            File.WriteAllText(configFile, JsonConvert.SerializeObject(pluginStates, Formatting.Indented));
        }

        public void UnloadPlugin(IVPetLLMPlugin plugin)
        {
            if (ChatCore != null)
            {
                ChatCore.RemovePlugin(plugin);
            }
            plugin.Unload();
            if (_pluginContexts.TryGetValue(plugin.FilePath, out var context))
            {
                context.Unload();
                _pluginContexts.Remove(plugin.FilePath);
                Logger.Log($"Unloading AssemblyLoadContext for {plugin.Name}");
            }
            Plugins.Remove(plugin);
            Logger.Log($"Unloaded plugin: {plugin.Name}");
        }
        public void Log(string message)
        {
            Logger.Log(message);
        }

        public void ImportPlugin(string filePath)
        {
            var pluginDir = PluginPath;
            if (!Directory.Exists(pluginDir))
            {
                Directory.CreateDirectory(pluginDir);
            }

            var fileName = Path.GetFileName(filePath);
            var destPath = Path.Combine(pluginDir, fileName);

            try
            {
                File.Copy(filePath, destPath, true);
                Logger.Log($"Imported plugin: {fileName}");
                LoadPlugins();
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to import plugin {fileName}: {ex.Message}");
            }
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

        public IEnumerable<string> GetAvailableAnimations()
        {
            return MW.Main.Core.Graph.GraphsList.Keys;
        }
    }
}