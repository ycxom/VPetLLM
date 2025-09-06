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
            Logger.Log($"Handling response: {response}");
            var actionQueue = _plugin.ActionProcessor.Process(response, _plugin.Settings);
            Logger.Log($"Found {actionQueue.Count} actions.");

            await Application.Current.Dispatcher.Invoke(async () =>
            {
                try
                {
                    foreach (var item in actionQueue)
                    {
                        Logger.Log($"Executing action: {item.Keyword}, value: {item.Value}");
                        if (item.Handler is SayHandler)
                        {
                            var match = new Regex("\"(.*?)\"").Match(item.Value);
                            if (match.Success)
                            {
                                var emotionMatch = new Regex(",(.*?)\\)").Match(item.Value);
                                var emotion = emotionMatch.Success ? (IGameSave.ModeType)Enum.Parse(typeof(IGameSave.ModeType), emotionMatch.Groups[1].Value, true) : IGameSave.ModeType.Nomal;
                                _plugin.MW.Core.Save.Mode = emotion;
                                _plugin.MW.Main.Say(match.Groups[1].Value);
                                await Task.Delay(match.Groups[1].Value.Length * 150);
                            }
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
               var response = await Task.Run(() => _plugin.ChatCore.Chat(text));
               HandleResponse(response);

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

                   await Application.Current.Dispatcher.Invoke(async () =>
                   {
                       foreach (var item in actionQueue)
                       {
                           if (item.Handler is SayHandler)
                           {
                               var match = new Regex("\"(.*?)\"").Match(item.Value);
                               if (match.Success)
                               {
                                   var emotionMatch = new Regex(",(.*?)\\)").Match(item.Value);
                                   var emotion = emotionMatch.Success ? (IGameSave.ModeType)Enum.Parse(typeof(IGameSave.ModeType), emotionMatch.Groups[1].Value, true) : IGameSave.ModeType.Nomal;
                                   _plugin.MW.Core.Save.Mode = emotion;
                                   _plugin.MW.Main.Say(match.Groups[1].Value);
                                   await Task.Delay(match.Groups[1].Value.Length * 150);
                               }
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