using System.Text.RegularExpressions;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers.Infrastructure;
using VPetLLM.Handlers.Legacy;

namespace VPetLLM.Handlers.Core
{
    public class ActionProcessor
    {
        /// <summary>
        /// 获取所有已注册的 Handler（通过 HandlerRegistry）
        /// </summary>
        public IEnumerable<IActionHandler> Handlers => _handlerRegistry.GetAllHandlers();

        private readonly IMainWindow _mainWindow;
        private readonly IHandlerRegistry _handlerRegistry;
        private RecordManager _recordManager;
        private Setting _settings;
        private IMediaPlaybackService _mediaPlaybackService;

        public ActionProcessor(IMainWindow mainWindow) : this(mainWindow, HandlerRegistry.Instance)
        {
        }

        public ActionProcessor(IMainWindow mainWindow, IHandlerRegistry handlerRegistry)
        {
            _mainWindow = mainWindow;
            _handlerRegistry = handlerRegistry ?? throw new ArgumentNullException(nameof(handlerRegistry));
            RegisterHandlers();
        }

        public void SetSettings(Setting settings)
        {
            _settings = settings;
            // Re-register handlers to include VPetSettingsHandler
            RegisterHandlers();
        }

        public void SetRecordManager(RecordManager recordManager)
        {
            _recordManager = recordManager;
            // Re-register handlers to include RecordCommandHandler
            RegisterHandlers();
        }

        public void SetMediaPlaybackService(IMediaPlaybackService mediaPlaybackService)
        {
            _mediaPlaybackService = mediaPlaybackService;
            // Re-register handlers to include PlayHandler
            RegisterHandlers();
        }

        public void RegisterHandlers()
        {
            _handlerRegistry.Clear();

            // 注册核心 Handler
            _handlerRegistry.Register("happy", new HappyHandler());
            _handlerRegistry.Register("health", new HealthHandler());
            _handlerRegistry.Register("exp", new ExpHandler());
            _handlerRegistry.Register("buy", new BuyHandler());
            _handlerRegistry.Register("use_item", new UseItemHandler());  // 新增：物品使用处理器
            _handlerRegistry.Register("action", new ActionHandler());
            _handlerRegistry.Register("move", new MoveHandler());
            _handlerRegistry.Register("say", new SayHandler());
            _handlerRegistry.Register("plugin", new PluginHandler());

            // Add RecordCommandHandler if RecordManager is available
            if (_recordManager is not null)
            {
                _handlerRegistry.Register("record", new RecordCommandHandler(_recordManager));
                _handlerRegistry.Register("record_modify", new RecordModifyCommandHandler(_recordManager));
            }

            // Add VPetSettingsHandler if Settings is available
            if (_settings is not null)
            {
                _handlerRegistry.Register("vpet_settings", new VPetSettingsHandler(_settings));
            }

            // Add PlayHandler if MediaPlaybackService is available
            if (_mediaPlaybackService is not null)
            {
                _handlerRegistry.Register("play", new PlayHandler(_mediaPlaybackService));
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

                // Find corresponding handler using HandlerRegistry
                IActionHandler handler = _handlerRegistry.GetHandler(actionType);

                // If no handler found, try parsing nested structure (e.g., talk(say(...)))
                if (handler is null && !string.IsNullOrEmpty(value))
                {
                    // Try to extract nested command
                    var nestedMatch = new Regex(@"^(\w+)\((.*)\)$", RegexOptions.Singleline).Match(value);
                    if (nestedMatch.Success)
                    {
                        var nestedCommand = nestedMatch.Groups[1].Value.ToLower();
                        var nestedValue = nestedMatch.Groups[2].Value;

                        handler = _handlerRegistry.GetHandler(nestedCommand);
                        if (handler is not null)
                        {
                            // Use nested value
                            value = nestedValue;
                            Logger.Log($"ActionProcessor: Found nested command: {nestedCommand}");
                        }
                    }
                }

                if (handler is null)
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

            // Special handling for play command - check if MediaPlayback is enabled
            if (handler.Keyword.ToLower() == "play")
            {
                isEnabled = settings.EnableMediaPlayback;
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