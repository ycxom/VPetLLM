using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
        }

        public string Process(string response, Setting settings)
        {
            if (!settings.EnableAction) return response;

            var regex = new Regex(@"\[:(\w+)\((\w+)(?:\((.*?)\))?\)\]");
            var matches = regex.Matches(response);

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (Match match in matches)
                {
                    var keyword = match.Groups[2].Value.ToLower();
                    var valueStr = match.Groups[3].Value;

                    var handler = Handlers.FirstOrDefault(h => h.Keyword.ToLower() == keyword);
                    if (handler != null)
                    {
                        if (handler.Keyword == "buy" && !settings.EnableBuy) continue;
                        if (handler.Keyword == "action" && !settings.EnableActionExecution) continue;
                        if (handler.Keyword == "move" && !settings.EnableMove) continue;

                        if (string.IsNullOrEmpty(valueStr))
                        {
                            handler.Execute(_mainWindow);
                        }
                        else if (int.TryParse(valueStr, out int intValue))
                        {
                            handler.Execute(intValue, _mainWindow);
                        }
                        else
                        {
                            handler.Execute(valueStr, _mainWindow);
                        }
                    }
                }
            });

            return regex.Replace(response, "").Trim();
        }
    }
}