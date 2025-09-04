using LinePutScript.Localization.WPF;
using Newtonsoft.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core;
using VPetLLM.Handlers;

namespace VPetLLM
{
    public class VPetLLM : MainPlugin
    {
        public static VPetLLM? Instance { get; private set; }
        public Setting Settings;
        public IChatCore? ChatCore;
        public TalkBox? TalkBox;
        public ActionProcessor? ActionProcessor;

        public VPetLLM(IMainWindow mainwin) : base(mainwin)
        {
            Instance = this;
            Logger.Log("VPetLLM plugin constructor started.");
            Settings = new Setting(ExtensionValue.BaseDirectory);
            Logger.Log("Settings loaded.");
            switch (Settings.Provider)
            {
                case global::VPetLLM.Setting.LLMType.Ollama:
                    ChatCore = new OllamaChatCore(Settings.Ollama, Settings, mainwin);
                    Logger.Log("Chat core set to Ollama.");
                    break;
                case global::VPetLLM.Setting.LLMType.OpenAI:
                    ChatCore = new OpenAIChatCore(Settings.OpenAI, Settings, mainwin);
                    Logger.Log("Chat core set to OpenAI.");
                    break;
                case global::VPetLLM.Setting.LLMType.Gemini:
                    ChatCore = new GeminiChatCore(Settings.Gemini, Settings, mainwin);
                    Logger.Log("Chat core set to Gemini.");
                    break;
            }
            // 加载聊天历史记录
            ChatCore?.LoadHistory();
            Logger.Log("VPetLLM plugin constructor finished.");
        }

        public override void LoadPlugin()
        {
            Logger.Log("LoadPlugin started.");
            ActionProcessor = new ActionProcessor(MW);
            Application.Current.Dispatcher.Invoke(() =>
            {
                Logger.Log("Dispatcher.Invoke started.");
                TalkBox = new TalkBox(this);
                MW.TalkAPI.Add(TalkBox);
                var menuItem = new MenuItem()
                {
                    Header = "VPetLLM",
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                menuItem.Click += (s, e) => Setting();
                MW.Main.ToolBar.MenuMODConfig.Items.Add(menuItem);
                Logger.Log("Dispatcher.Invoke finished.");
            });
            Logger.Log("LoadPlugin finished.");
        }

        public override void Save()
        {
            Settings.Save();
        }

        public void UpdateChatCore(IChatCore newChatCore)
        {
            // 首先更新ChatCore字段
            ChatCore = newChatCore;
            Logger.Log($"ChatCore updated to: {ChatCore?.GetType().Name}");
            
            // 重新加载聊天历史记录
            ChatCore?.LoadHistory();
            Logger.Log("Chat history reloaded");
            
            // 重新创建TalkBox以确保使用新的ChatCore实例
            // 使用InvokeAsync并等待完成，确保热重载生效
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (TalkBox != null)
                {
                    MW.TalkAPI.Remove(TalkBox);
                    Logger.Log("Old TalkBox removed from TalkAPI");
                }
                TalkBox = new TalkBox(this);
                MW.TalkAPI.Add(TalkBox);
                Logger.Log("New TalkBox added to TalkAPI");
                
                // 验证新的TalkBox是否使用正确的ChatCore实例
                Logger.Log($"New TalkBox should use ChatCore: {ChatCore?.GetType().Name}");
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

        public override string PluginName => "VPetLLM";
        
        // 测试方法：验证当前ChatCore实例类型
        public string GetCurrentChatCoreInfo()
        {
            return $"ChatCore Type: {ChatCore?.GetType().Name}, Hash: {ChatCore?.GetHashCode()}";
        }
    }
}