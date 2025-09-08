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
        public event Action<string> OnSendMessage;


        public TalkBox(VPetLLM plugin) : base(plugin)
        {
            _plugin = plugin;
            _plugin.ChatCore.SetResponseHandler(HandleResponse);
            Logger.Log("TalkBox created.");
        }

        public void HandleNormalResponse(string message)
        {
            _plugin.MW.Main.Say(message);
        }
        public async void HandleResponse(string response)
        {

            if (!_plugin.Settings.EnableAction)
            {
                _plugin.MW.Main.Say(response);
                return;
            }

            // If action is enabled, process the actions
            Logger.Log($"Handling response for actions: {response}");
            var actionQueue = _plugin.ActionProcessor.Process(response, _plugin.Settings);
            Logger.Log($"Found {actionQueue.Count} actions.");

            if (actionQueue.Count == 0)
            {
                _plugin.MW.Main.Say(response);
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    foreach (var item in actionQueue)
                    {
                        Logger.Log($"Executing action: {item.Keyword}, value: {item.Value}");
                        // The SayHandler is now just for changing emotions, actual speech is handled above.
                        if (item.Handler is SayHandler)
                        {
                            var sayMatch = new Regex("\"(.*?)\"").Match(item.Value);
                            string sayText = "";
                            if (sayMatch.Success)
                            {
                                sayText = sayMatch.Groups[1].Value;
                                _plugin.MW.Main.Say(sayText);
                            }
                            var emotionMatch = new Regex(@",\s*(.*?)\)").Match(item.Value);
                            if (emotionMatch.Success)
                            {
                                var emotion = (IGameSave.ModeType)Enum.Parse(typeof(IGameSave.ModeType), emotionMatch.Groups[1].Value, true);
                                _plugin.MW.Core.Save.Mode = emotion;
                            }
                            await Task.Delay(Math.Max(2000, sayText.Length * 200));
                        }
                        else
                        {
                            if (string.IsNullOrEmpty(item.Value))
                                item.Handler.Execute(_plugin.MW);
                            else if (int.TryParse(item.Value, out int intValue))
                                item.Handler.Execute(intValue, _plugin.MW);
                            else
                                item.Handler.Execute(item.Value, _plugin.MW);
                            await Task.Delay(500);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"An error occurred while executing actions: {ex}");
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
                            // This part might need to be refactored similar to HandleResponse
                            item.Handler.Execute(item.Value, _plugin.MW);
                            await Task.Delay(500);
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