using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;

namespace VPetLLM.Handlers
{
    public class ActionProcessor
    {
        public List<IActionHandler> Handlers { get; } = new List<IActionHandler>();
        private readonly IMainWindow _mainWindow;

        public ActionProcessor(IMainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            RegisterHandlers();
        }

        public void RegisterHandlers()
        {
            Handlers.Clear();
            Handlers.Add(new HappyHandler());
            Handlers.Add(new HealthHandler());
            Handlers.Add(new ExpHandler());
            Handlers.Add(new BuyHandler());
            Handlers.Add(new ActionHandler());
            Handlers.Add(new MoveHandler());
            Handlers.Add(new SayHandler());
            Handlers.Add(new PluginHandler());
        }

        public List<HandlerAction> Process(string response, Setting settings)
        {
            var actions = new List<HandlerAction>();
            if (!settings.EnableAction) return actions;

            var regex = new Regex(@"\[:(.*?)\]");
            var matches = regex.Matches(response);
            Logger.Log($"ActionProcessor: Found {matches.Count} matches for response: {response}");

            foreach (Match match in matches)
            {
                var content = match.Groups[1].Value;
                var parts = content.Split(new[] { '(' }, 2);
                var command = parts[0].ToLower();
                var value = parts.Length > 1 && parts[1].Length > 0 ? parts[1].Substring(0, parts[1].Length - 1) : "";

                IActionHandler handler = Handlers.FirstOrDefault(h => h.Keyword.ToLower() == command);

                if (handler == null)
                {
                    var nestedMatch = new Regex(@"(\w+)\((.*)\)").Match(content);
                    if (nestedMatch.Success)
                    {
                        var outerCommand = nestedMatch.Groups[1].Value.ToLower();
                        var innerValue = nestedMatch.Groups[2].Value.Trim(')');
                        
                        handler = Handlers.FirstOrDefault(h => h.Keyword.ToLower() == outerCommand);
                        if(handler != null){
                            value = innerValue;
                        } else {
                             var innerParts = innerValue.Split(new[] { '(' }, 2);
                            var innerCommand = innerParts[0].ToLower();
                            handler = Handlers.FirstOrDefault(h => h.Keyword.ToLower() == innerCommand);
                            if(handler != null){
                                value = innerParts.Length > 1 ? innerParts[1].TrimEnd(')') : "";
                            }
                        }
                    }
                }

                if (handler == null)
                {
                     handler = Handlers.FirstOrDefault(h => h.Keyword == "plugin");
                     if (handler != null)
                     {
                        value = content;
                     }
                }

                if (handler == null)
                {
                    Logger.Log($"ActionProcessor: No handler found for command: {command}");
                    continue;
                }
                
                bool isEnabled = handler.ActionType switch
                {
                    ActionType.State => settings.EnableState,
                    ActionType.Body => (handler.Keyword.ToLower() == "move" && settings.EnableMove) || (handler.Keyword.ToLower() == "action" && settings.EnableActionExecution),
                    ActionType.Talk => true,
                    ActionType.Plugin => settings.EnablePlugin,
                    _ => false
                };
                if (handler.Keyword.ToLower() == "buy") isEnabled = settings.EnableBuy;

                if (!isEnabled)
                {
                    Logger.Log($"ActionProcessor: Handler '{handler.Keyword}' is disabled.");
                    continue;
                }

                actions.Add(new HandlerAction(handler.ActionType, handler.Keyword, value, handler));
            }
            return actions;
        }
    }

    public class HandlerAction
    {
        public ActionType Type { get; }
        public string Keyword { get; }
        public string Value { get; }
        public IActionHandler Handler { get; }

        public HandlerAction(ActionType type, string keyword, string value, IActionHandler handler)
        {
            Type = type;
            Keyword = keyword;
            Value = value;
            Handler = handler;
        }
    }
}