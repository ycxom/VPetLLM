using LinePutScript.Localization.WPF;
using Newtonsoft.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core;

namespace VPetLLM
{
    public class VPetLLM : MainPlugin
    {
        public Setting Settings;
        public IChatCore? ChatCore;
        public TalkBox? TalkBox;

        public VPetLLM(IMainWindow mainwin) : base(mainwin)
        {
            Logger.Log("VPetLLM plugin constructor started.");
            Settings = new Setting(ExtensionValue.BaseDirectory);
            Logger.Log("Settings loaded.");
            switch (Settings.Provider)
            {
                case global::VPetLLM.Setting.LLMType.Ollama:
                    ChatCore = new OllamaChatCore(Settings.Ollama, Settings);
                    Logger.Log("Chat core set to Ollama.");
                    break;
                case global::VPetLLM.Setting.LLMType.OpenAI:
                    ChatCore = new OpenAIChatCore(Settings.OpenAI, Settings);
                    Logger.Log("Chat core set to OpenAI.");
                    break;
                case global::VPetLLM.Setting.LLMType.Gemini:
                    ChatCore = new GeminiChatCore(Settings.Gemini, Settings);
                    Logger.Log("Chat core set to Gemini.");
                    break;
            }
            Logger.Log("VPetLLM plugin constructor finished.");
        }

        public override void LoadPlugin()
        {
            Logger.Log("LoadPlugin started.");
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

        public override void Setting()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var settingWindow = new winSettingNew(this);
                settingWindow.Show();
            });
        }

        public override string PluginName => "VPetLLM";
    }
}