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
            Logger.Log($"Current ChatCore instance: {_plugin.ChatCore?.GetType().Name}");
            Logger.Log($"Current ChatCore hash: {_plugin.ChatCore?.GetHashCode()}");
            
            try
            {
                var response = await Task.Run(() => _plugin.ChatCore.Chat(text));
                Logger.Log($"Chat core responded: {response}");
                var processedResponse = _plugin.ActionProcessor.Process(response);
                Application.Current.Dispatcher.Invoke(() => _plugin.MW.Main.Say(processedResponse));
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