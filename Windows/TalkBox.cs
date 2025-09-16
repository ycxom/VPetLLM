using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Windows;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers;
using VPetLLM.Utils;

namespace VPetLLM.Windows
{
    public class TalkBox : VPet_Simulator.Windows.Interface.TalkBox
    {
        public override string APIName { get; } = "VPetLLM";
        private readonly VPetLLM _plugin;
        private readonly SmartMessageProcessor _messageProcessor;
        public event Action<string> OnSendMessage;


        public TalkBox(VPetLLM plugin) : base(plugin)
        {
            _plugin = plugin;
            _messageProcessor = new SmartMessageProcessor(_plugin);
            _plugin.ChatCore.SetResponseHandler(HandleResponse);
            Logger.Log("TalkBox created.");
        }

        public void HandleNormalResponse(string message)
        {
            _plugin.MW.Main.Say(message);
        }
        public async void HandleResponse(string response)
        {
            Logger.Log($"HandleResponse: 收到AI回复: {response}");

            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    // 使用智能消息处理器处理回复
                    await _messageProcessor.ProcessMessageAsync(response);
                }
                catch (Exception ex)
                {
                    Logger.Log($"HandleResponse: 处理AI回复时发生错误: {ex.Message}");
                    // 发生错误时回退到简单显示
                    _plugin.MW.Main.Say(response);
                }
            });
        }
        public override async void Responded(string text)
        {
            OnSendMessage?.Invoke(text);
            Logger.Log($"Responded called with text: {text}");
            try
            {
                Logger.Log("Calling ChatCore.Chat...");
                await Task.Run(() => _plugin.ChatCore.Chat(text));

                Logger.Log("Processing tools...");
                await ProcessTools(text);
                Logger.Log("Responded finished.");
            }
            catch (Exception e)
            {
                Logger.Log($"An error occurred in Responded: {e}");
                await Application.Current.Dispatcher.InvokeAsync(() => _plugin.MW.Main.Say(e.ToString()));
            }
        }

        private async Task ProcessTools(string text)
        {
            if (_plugin.Settings.Tools == null) return;

            foreach (var tool in _plugin.Settings.Tools)
            {
                if (!tool.IsEnabled) continue;

                var client = new HttpClient();
                var requestData = new { prompt = text };
                var content = new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json");

                try
                {
                    var response = await client.PostAsync(tool.Url, content);
                    response.EnsureSuccessStatusCode();
                    var responseString = await response.Content.ReadAsStringAsync();
                    var actionQueue = _plugin.ActionProcessor.Process(responseString, _plugin.Settings);

                    await Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        foreach (var item in actionQueue)
                        {
                           await item.Handler.Execute(item.Value, _plugin.MW);
                        }
                    });
                }
                catch (Exception e)
                {
                    Logger.Log($"An error occurred in ProcessTools: {e}");
                }
            }
        }

        public override void Setting()
        {
            _plugin.Setting();
        }
    }
}