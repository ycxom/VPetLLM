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
        }

        public override void Responded(string text)
        {
            try
            {
                Task.Run(async () =>
                {
                    var response = await _plugin.ChatCore.Chat(text);
                    Application.Current.Dispatcher.Invoke(() => _plugin.MW.Main.Say(response));
                });
            }
            catch (Exception e)
            {
                Application.Current.Dispatcher.Invoke(() => _plugin.MW.Main.Say(e.Message));
            }
        }

        public override void Setting()
        {
            _plugin.Setting();
        }
    }
}