using System.Text.RegularExpressions;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;

namespace VPetLLM.Handlers
{
    public class ActionProcessor
    {
        public List<IActionHandler> Handlers { get; } = new List<IActionHandler>();
        private readonly IMainWindow _mainWindow;
        private Core.RecordManager _recordManager;

        public ActionProcessor(IMainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            RegisterHandlers();
        }

        public void SetRecordManager(Core.RecordManager recordManager)
        {
            _recordManager = recordManager;
            // Re-register handlers to include RecordCommandHandler
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
            
            // Add RecordCommandHandler if RecordManager is available
            if (_recordManager != null)
            {
                Handlers.Add(new RecordCommandHandler(_recordManager));
                Handlers.Add(new RecordModifyCommandHandler(_recordManager));
            }
        }

        public List<HandlerAction> Process(string response, Setting settings)
        {
            var actions = new List<HandlerAction>();
            if (!settings.EnableAction) return actions;

            // Detect and reject legacy format
            var format = CommandFormatParser.DetectFormat(response);
            if (format == CommandFormat.Legacy)
            {
                Logger.Log($"ActionProcessor: REJECTED - Legacy format detected. Please use new format: <|command_type_begin|> parameters <|command_type_end|>");
                // Legacy format is rejected, Parse() will log detailed warning
            }

            // Parse commands (only new format will be parsed)
            var commands = CommandFormatParser.Parse(response);
            
            Logger.Log($"ActionProcessor: Detected format: {format}, found {commands.Count} commands in new format");

            foreach (var command in commands)
            {
                string actionType = command.CommandType.ToLower();
                string value = command.Parameters;

                // Check for new plugin format: plugin_PluginName
                string pluginName = null;
                if (actionType.StartsWith("plugin_"))
                {
                    pluginName = actionType.Substring(7); // Remove "plugin_" prefix
                    actionType = "plugin"; // Use base keyword for handler lookup
                    Logger.Log($"ActionProcessor: New plugin format detected - plugin name: {pluginName}");
                }

                // Find corresponding handler
                IActionHandler handler = Handlers.FirstOrDefault(h => h.Keyword.ToLower() == actionType);

                // If no handler found, try parsing nested structure (e.g., talk(say(...)))
                if (handler == null && !string.IsNullOrEmpty(value))
                {
                    // Try to extract nested command
                    var nestedMatch = new Regex(@"^(\w+)\((.*)\)$", RegexOptions.Singleline).Match(value);
                    if (nestedMatch.Success)
                    {
                        var nestedCommand = nestedMatch.Groups[1].Value.ToLower();
                        var nestedValue = nestedMatch.Groups[2].Value;
                        
                        handler = Handlers.FirstOrDefault(h => h.Keyword.ToLower() == nestedCommand);
                        if (handler != null)
                        {
                            // Use nested value
                            value = nestedValue;
                            Logger.Log($"ActionProcessor: Found nested command: {nestedCommand}");
                        }
                    }
                }

                if (handler == null)
                {
                    Logger.Log($"ActionProcessor: No handler found for command: {actionType} (format: {command.Format})");
                    continue;
                }

                // For new plugin format, set the plugin name before execution
                if (!string.IsNullOrEmpty(pluginName) && handler is PluginHandler)
                {
                    PluginHandler.SetPluginName(pluginName);
                }

                bool isEnabled = IsHandlerEnabled(handler, settings);

                if (!isEnabled)
                {
                    Logger.Log($"ActionProcessor: Handler '{handler.Keyword}' is disabled.");
                    continue;
                }

                actions.Add(new HandlerAction(handler.ActionType, handler.Keyword, value, handler));
            }

            Logger.Log($"ActionProcessor: Processed {commands.Count} commands, {actions.Count} actions enabled");
            return actions;
        }

        /// <summary>
        /// 检查 handler 是否启用
        /// </summary>
        private bool IsHandlerEnabled(IActionHandler handler, Setting settings)
        {
            bool isEnabled = handler.ActionType switch
            {
                ActionType.State => settings.EnableState,
                ActionType.Body => (handler.Keyword.ToLower() == "move" && settings.EnableMove) || (handler.Keyword.ToLower() == "action" && settings.EnableActionExecution),
                ActionType.Talk => true,
                ActionType.Plugin => settings.EnablePlugin,
                ActionType.Tool => true, // Tool handlers (including record) are always enabled
                _ => false
            };
            if (handler.Keyword.ToLower() == "buy") isEnabled = settings.EnableBuy;
            
            // Special handling for record commands - check if Records system is enabled
            if (handler.Keyword.ToLower() == "record" || handler.Keyword.ToLower() == "record_modify")
            {
                isEnabled = settings.Records?.EnableRecords ?? true;
            }
            
            return isEnabled;
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