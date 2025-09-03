using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM
{
    public class TalkBox : VPet_Simulator.Windows.Interface.TalkBox
    {
        public override string APIName { get; } = "VPetLLM";
        private readonly VPetLLM _plugin;

        public TalkBox(VPetLLM plugin) : base(plugin)
        {
            _plugin = plugin;
            Logger.Log("TalkBox created.");
        }

        public override async void Responded(string text)
        {
            Logger.Log($"Responded called with text: {text}");
            try
            {
                var response = await Task.Run(() => _plugin.ChatCore.Chat(text));
                Logger.Log($"Chat core responded: {response}");
                Application.Current.Dispatcher.Invoke(() => _plugin.MW.Main.Say(response));
            }
            catch (Exception e)
            {
                Logger.Log($"An error occurred in Responded: {e}");
                Application.Current.Dispatcher.Invoke(() => _plugin.MW.Main.Say(e.ToString()));
            }
        }

        public override void Setting()
        {
            _plugin.Setting();
        }
    }
}