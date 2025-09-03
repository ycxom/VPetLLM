using LinePutScript.Localization.WPF;
using Newtonsoft.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM
{
    public class VPetLLM : MainPlugin
    {
        public Setting Settings;
        public IChatCore ChatCore;
        public readonly TalkBox TalkBox;

        public VPetLLM(IMainWindow mainwin) : base(mainwin)
        {
            Settings = new Setting(ExtensionValue.BaseDirectory);
            TalkBox = new TalkBox(this);
            switch (Settings.Provider)
            {
                case Setting.LLMType.Ollama:
                    ChatCore = new OllamaChatCore(Settings.Ollama);
                    break;
                case Setting.LLMType.OpenAI:
                    ChatCore = new OpenAIChatCore(Settings.OpenAI);
                    break;
                case Setting.LLMType.Gemini:
                    ChatCore = new GeminiChatCore(Settings.Gemini);
                    break;
            }
        }

        public override void LoadPlugin()
        {
            MW.TalkAPI.Add(TalkBox);
            var menuItem = new MenuItem()
            {
                Header = "VPetLLM",
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            menuItem.Click += (s, e) => Setting();
            MW.Main.ToolBar.MenuMODConfig.Items.Add(menuItem);
        }

        public override void Save()
        {
            Settings.Save();
        }

        public override void Setting()
        {
            var settingWindow = new winSetting(this);
            settingWindow.Show();
        }

        public override string PluginName => "VPetLLM";
    }
}