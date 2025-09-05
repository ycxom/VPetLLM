using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers;

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
                
                var actionQueue = _plugin.ActionProcessor.Process(response, _plugin.Settings);

                await Application.Current.Dispatcher.Invoke(async () =>
                {
                    if (actionQueue.Any(a => a.IsBlocking))
                    {
                        actionQueue.First(a => a.IsBlocking).Action.Invoke(_plugin.MW);
                        return;
                    }

                    foreach (var item in actionQueue)
                    {
                        if (item.Type == ActionType.Talk)
                        {
                            _plugin.MW.Core.Save.Mode = item.Emotion;
                            _plugin.MW.Main.Say(item.Text);
                            // 等待说话动画完成，这里用一个估算的时间
                            // 实际项目中可能需要更精确的事件来同步
                            await Task.Delay(item.Text.Length * 150);
                        }
                        else
                        {
                            item.Action.Invoke(_plugin.MW);
                            // 为非说话动作添加一个小的延迟
                            await Task.Delay(500);
                        }
                    }
                });
            }
            catch (Exception e)
            {
                Logger.Log($"An error occurred in Responded: {e}");
                await Application.Current.Dispatcher.InvokeAsync(() => _plugin.MW.Main.Say(e.ToString()));
            }
        }

        public override void Setting()
        {
            _plugin.Setting();
        }
    }
}