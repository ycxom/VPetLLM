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

        private void RegisterHandlers()
        {
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

            var regex = new Regex(@"\[:(plugin)\(((\w+)(?:\((.*)\))?)\)\]|\[:(\w+)\((.*?)\)\]");
            var matches = regex.Matches(response);
            Logger.Log($"ActionProcessor: Found {matches.Count} matches for response: {response}");

            foreach (Match match in matches)
            {
                string type, fullValue, keyword, value;

                if (match.Groups[1].Success && match.Groups[1].Value == "plugin")
                {
                    type = "plugin";
                    fullValue = match.Groups[2].Value;
                    keyword = match.Groups[3].Value.ToLower();
                    value = match.Groups[4].Value;
                }
                else
                {
                    type = match.Groups[5].Value.ToLower();
                    fullValue = match.Groups[6].Value;
                    keyword = type;
                    value = fullValue;

                    if (type != "plugin")
                    {
                        var valueMatch = new Regex(@"(\w+)(?:\((.*)\))?").Match(fullValue);
                        if (valueMatch.Success)
                        {
                            keyword = valueMatch.Groups[1].Value.ToLower();
                            value = valueMatch.Groups[2].Value;
                        }
                    }
                }

                IActionHandler handler = Handlers.FirstOrDefault(h => h.Keyword.ToLower() == keyword);

                if (handler == null)
                {
                    // Fallback for type-based handlers like 'plugin'
                    handler = Handlers.FirstOrDefault(h => h.Keyword.ToLower() == type);
                    if (handler == null)
                    {
                        // If no handler is found, check if it's a plugin name
                        var plugin = VPetLLM.Instance.Plugins.Find(p => p.Name.Replace(" ", "_").ToLower() == keyword);
                        if (plugin != null)
                        {
                            handler = Handlers.FirstOrDefault(h => h.Keyword.ToLower() == "plugin");
                            if (handler != null)
                            {
                                value = $"{keyword}({value})";
                            }
                        }

                        if (handler == null)
                        {
                            Logger.Log($"ActionProcessor: No handler or plugin found for type or keyword: {type}/{keyword}");
                            continue;
                        }
                    }
                    else
                    {
                        value = fullValue;
                    }
                }

                bool isEnabled = handler.ActionType switch
                {
                    ActionType.State => settings.EnableState,
                    ActionType.Body => (handler.Keyword.ToLower() == "move" && settings.EnableMove) || (handler.Keyword.ToLower() == "action" && settings.EnableActionExecution),
                    ActionType.Talk => true,
                    ActionType.Plugin => true,
                    _ => false
                };
                if (handler.Keyword.ToLower() == "buy") isEnabled = settings.EnableBuy;

                if (!isEnabled)
                {
                    Logger.Log($"ActionProcessor: Handler '{handler.Keyword}' is disabled.");
                    continue;
                }
                
                actions.Add(new HandlerAction(handler.ActionType, keyword, value, handler));
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