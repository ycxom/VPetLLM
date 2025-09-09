using System;
using System.Collections.Generic;
using System.Linq;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers;
using VPetLLM.Utils;

namespace VPetLLM.Core
{
    public class SystemMessageProvider
    {
        private readonly Setting _settings;
        private readonly IMainWindow _mainWindow;
        private readonly ActionProcessor _actionProcessor;

        public SystemMessageProvider(Setting settings, IMainWindow mainWindow, ActionProcessor actionProcessor)
        {
            _settings = settings;
            _mainWindow = mainWindow;
            _actionProcessor = actionProcessor;
        }

        public string GetSystemMessage()
        {
            if (_settings == null || _mainWindow == null || _actionProcessor == null) return "";

           var lang = _settings.PromptLanguage;
           var parts = new List<string>
           {
               PromptHelper.Get("Role", lang)
                           .Replace("{AiName}", _settings.AiName)
                           .Replace("{UserName}", _settings.UserName),
              _settings.Role
           };

           if (_settings.EnableAction)
           {
               parts.Add(PromptHelper.Get("Character_Setting", lang));

               if (_settings.EnableState)
               {
                   var core = _mainWindow.Core;
                   var status = PromptHelper.Get("Status_Prefix", lang)
                       .Replace("{Level}", core.Save.Level.ToString())
                       .Replace("{Money:F2}", core.Save.Money.ToString("F2"))
                       .Replace("{Strength:F0}", core.Save.Strength.ToString("F0"))
                       .Replace("{StrengthMax:F0}", core.Save.StrengthMax.ToString("F0"))
                       .Replace("{Health:F0}", core.Save.Health.ToString("F0"))
                       .Replace("{Feeling:F0}", core.Save.Feeling.ToString("F0"))
                       .Replace("{FeelingMax:F0}", core.Save.FeelingMax.ToString("F0"))
                       .Replace("{Likability:F0}", core.Save.Likability.ToString("F0"))
                       .Replace("{LikabilityMax:F0}", core.Save.LikabilityMax.ToString("F0"))
                       .Replace("{StrengthFood:F0}", core.Save.StrengthFood.ToString("F0"))
                       .Replace("{StrengthDrink:F0}", core.Save.StrengthDrink.ToString("F0"));

                   if (_settings.EnableTime)
                   {
                       status += PromptHelper.Get("Time_Prefix", lang)
                                   .Replace("{CurrentTime:yyyy-MM-dd HH:mm:ss}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                   }
                   parts.Add(status);
               }

               var instructions = new List<string>();
               foreach (var handler in _actionProcessor.Handlers)
               {
                   bool isEnabled = handler.ActionType switch
                   {
                       ActionType.State => (_settings.EnableBuy && handler.Keyword == "buy") || (_settings.EnableAction && handler.Keyword != "buy"),
                       ActionType.Body => (_settings.EnableActionExecution && handler.Keyword == "action") || (_settings.EnableMove && handler.Keyword == "move"),
                       ActionType.Talk => true,
                       _ => false
                   };

                   if (isEnabled)
                   {
                       instructions.Add(handler.Description);
                   }
               }

               if (instructions.Any())
               {
                   parts.Add(PromptHelper.Get("Available_Commands_Prefix", lang)
                               .Replace("{CommandList}", string.Join("\n", instructions)));
                if (_settings.EnableActionExecution)
                {
                    parts.Add(PromptHelper.Get("Available_Animations_Prefix", lang)
                                .Replace("{AnimationList}", string.Join(", ", VPetLLM.Instance.GetAvailableAnimations())));
                    parts.Add(PromptHelper.Get("Available_Say_Animations_Prefix", lang)
                                .Replace("{SayAnimationList}", string.Join(", ", VPetLLM.Instance.GetAvailableSayAnimations())));
                }
               }

               if (_settings.EnableBuy)
               {
                   var items = string.Join(",", _mainWindow.Foods.Select(f => f.Name));
                   parts.Add(PromptHelper.Get("Available_Items_Prefix", lang)
                               .Replace("{ItemList}", items));
               }
           }

           if (_settings.EnablePlugin && VPetLLM.Instance.Plugins.Any(p => p.Enabled))
           {
               var pluginDescriptions = VPetLLM.Instance.Plugins.Where(p => p.Enabled).Select(p => $"{p.Name}: {p.Description} {p.Examples}");
               parts.Add("Available Plugins (all plugins must be called in the format `[:plugin(plugin_name(parameters))]`):\n" + string.Join("\n", pluginDescriptions));
           }

           var systemMessage = string.Join("\n", parts);
        //    Logger.Log("System Message Generated:\n" + systemMessage); // System Role 查看用的
           return systemMessage;
       }

        public void AddPlugin(IVPetLLMPlugin plugin)
        {
            // No action needed here for now, as GetSystemMessage dynamically fetches the list
        }

        public void RemovePlugin(IVPetLLMPlugin plugin)
        {
            // No action needed here for now, as GetSystemMessage dynamically fetches the list
        }
    }
}