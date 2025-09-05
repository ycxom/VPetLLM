using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VPet_Simulator.Core;
using System.Windows;
using VPet_Simulator.Windows.Interface;

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
        }

        public List<HandlerAction> Process(string response, Setting settings)
        {
            var actions = new List<HandlerAction>();
            if (!settings.EnableAction) return actions;

            var regex = new Regex(@"\[:(\w+)\((\w+)(?:\((.*?)\))?\)\]");
            var matches = regex.Matches(response);

            foreach (Match match in matches)
            {
                var keyword = match.Groups[2].Value.ToLower();
                var valueStr = match.Groups[3].Value;
                var handler = Handlers.FirstOrDefault(h => h.Keyword.ToLower() == keyword);

                if (handler == null) continue;

                bool isEnabled = handler.ActionType switch
                {
                    ActionType.State => settings.EnableState,
                    ActionType.Body => (handler.Keyword == "move" && settings.EnableMove) || (handler.Keyword == "action" && settings.EnableActionExecution),
                    ActionType.Talk => true,
                    _ => false
                };
                if (handler.Keyword == "buy") isEnabled = settings.EnableBuy;

                if (!isEnabled) continue;

                actions.Add(new HandlerAction(handler.ActionType, handler.Keyword, valueStr, handler));
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