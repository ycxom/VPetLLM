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

        public override string PluginName => "VPetLLM";
        
        // 测试方法：验证当前ChatCore实例类型
        public string GetCurrentChatCoreInfo()
        {
            return $"ChatCore Type: {ChatCore?.GetType().Name}, Hash: {ChatCore?.GetHashCode()}";
        }
    }
}