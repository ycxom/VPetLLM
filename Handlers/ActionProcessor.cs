using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VPet_Simulator.Core;
using System.Windows;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers
{
    public class ActionItem
    {
        public Action<IMainWindow> Action { get; set; }
        public bool IsBlocking { get; set; } // e.g., move
        public string Text { get; set; } // For say actions
        public IGameSave.ModeType Emotion { get; set; } // For say actions
        public ActionType Type { get; set; }
    }

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

        public List<ActionItem> Process(string response, Setting settings)
        {
            var actionQueue = new List<ActionItem>();
            if (!settings.EnableAction) return actionQueue;

            var regex = new Regex(@"\[:(\w+)\((\w+)(?:\((.*?)\))?\)\]");
            var matches = regex.Matches(response);
            var matchList = matches.Cast<Match>().ToList();

            var moveMatch = matchList.FirstOrDefault(m => m.Groups[2].Value.ToLower() == "move");
            if (moveMatch != null)
            {
                var handler = Handlers.First(h => h.Keyword == "move");
                actionQueue.Add(new ActionItem
                {
                    Action = (mw) => handler.Execute(moveMatch.Groups[3].Value, mw),
                    IsBlocking = true,
                    Type = ActionType.Body
                });
                return actionQueue;
            }

            foreach (var match in matchList)
            {
                var typeStr = match.Groups[1].Value.ToLower();
                var keyword = match.Groups[2].Value.ToLower();
                var valueStr = match.Groups[3].Value;
                var handler = Handlers.FirstOrDefault(h => h.Keyword.ToLower() == keyword);

                if (handler == null) continue;
                
                var actionType = handler.ActionType;

                if (actionType == ActionType.Talk && keyword == "say")
                {
                    string text;
                    string moodStr = "Nomal";
                    int firstQuote = valueStr.IndexOf('"');
                    int lastQuote = valueStr.LastIndexOf('"');
                    if (firstQuote != -1 && lastQuote > firstQuote)
                    {
                        text = valueStr.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                        int commaIndex = valueStr.IndexOf(',', lastQuote);
                        if (commaIndex != -1) moodStr = valueStr.Substring(commaIndex + 1).Trim();
                    } else continue;
                    
                    Enum.TryParse<IGameSave.ModeType>(moodStr, true, out var mood);

                    actionQueue.Add(new ActionItem { Text = text, Emotion = mood, Type = ActionType.Talk });
                }
                else
                {
                    actionQueue.Add(new ActionItem { Action = (mw) => handler.Execute(valueStr, mw), Type = actionType });
                }
            }
            return actionQueue;
        }
    }
}