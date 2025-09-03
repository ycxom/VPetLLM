using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM
{
    public class TalkBox : VPet_Simulator.Windows.Interface.TalkBox
    {
        private readonly VPetLLM _plugin;

        public TalkBox(VPetLLM plugin) : base(plugin)
        {
            _plugin = plugin;
            APIName = "VPetLLM";
        }

        public override void Responded(string text)
        {
            try
            {
                Task.Run(async () =>
                {
                    var response = await _plugin.ChatCore.Chat(text);
                    DisplayThinkToSay(response);
                });
            }
            catch (Exception e)
            {
                DisplayThinkToSay(e.Message);
            }
        }

        public override void Setting()
        {
            _plugin.Setting();
        }
    }
}