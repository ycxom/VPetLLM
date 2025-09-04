using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers
{
    public class ActionProcessor
    {
        private readonly List<IActionHandler> _handlers = new List<IActionHandler>();
        private readonly IMainWindow _mainWindow;

        public ActionProcessor(IMainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            RegisterHandlers();
        }

        private void RegisterHandlers()
        {
            _handlers.Add(new HappyHandler());
            _handlers.Add(new HealthHandler());
            _handlers.Add(new ExpHandler());
            _handlers.Add(new BuyHandler());
        }

        public string Process(string response, Setting settings)
        {
            if (!settings.EnableAction) return response;

            var regex = new Regex(@"\[:(\w+)\((.+?)\)\]");
            var matches = regex.Matches(response);

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (Match match in matches)
                {
                    var keyword = match.Groups[1].Value.ToLower();
                    var valueStr = match.Groups[2].Value;

                    var handler = _handlers.FirstOrDefault(h => h.Keyword.ToLower() == keyword);
                    if (handler != null)
                    {
                        if (handler.Keyword == "buy" && !settings.EnableBuy) continue;
                        
                        if (int.TryParse(valueStr, out int intValue))
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