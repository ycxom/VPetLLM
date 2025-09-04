using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using VPet_Simulator.Windows.Interface;
using VPet_Simulator.Core;

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
                // 首先获取LLM增强的响应（基于宠物状态）
                var llmResponse = await _plugin.ProcessWithLLMAsync(text);
                if (llmResponse != null)
                {
                    Logger.Log("LLM enhanced response generated");
                    // 传递SayInfo对象的字符串表示
                    Application.Current.Dispatcher.Invoke(() => _plugin.MW.Main.Say(llmResponse.ToString(), "think", false, null));
                    return;
                }

                // 如果没有LLM响应，使用原有的ChatCore处理
                var response = await Task.Run(() => _plugin.ChatCore.Chat(text));
                Logger.Log($"Chat core responded: {response}");
                Application.Current.Dispatcher.Invoke(() => _plugin.MW.Main.Say(response, "think", false, null));
            }
            catch (Exception e)
            {
                Logger.Log($"An error occurred in Responded: {e}");
                Application.Current.Dispatcher.Invoke(() => _plugin.MW.Main.Say($"处理出错: {e.Message}", "think", false, null));
            }
        }

        public override void Setting()
        {
            _plugin.Setting();
        }
    }
}