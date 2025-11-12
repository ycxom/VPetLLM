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

            // 使用手动解析来处理复杂的嵌套括号（与 SmartMessageProcessor 一致）
            int index = 0;
            int matchCount = 0;

            while (index < response.Length)
            {
                // 查找下一个动作指令的开始
                int startIndex = response.IndexOf("[:", index);
                if (startIndex == -1)
                    break;

                // 提取动作类型
                int typeStart = startIndex + 2;
                int typeEnd = response.IndexOf('(', typeStart);
                
                // 如果没有括号，尝试查找直接的 ]
                if (typeEnd == -1)
                {
                    typeEnd = response.IndexOf(']', typeStart);
                    if (typeEnd != -1)
                    {
                        // 无参数的动作，如 [:happy]
                        string command = response.Substring(typeStart, typeEnd - typeStart).ToLower();
                        IActionHandler noParamHandler = Handlers.FirstOrDefault(h => h.Keyword.ToLower() == command);
                        
                        if (noParamHandler != null)
                        {
                            matchCount++;
                            bool noParamEnabled = IsHandlerEnabled(noParamHandler, settings);
                            if (noParamEnabled)
                            {
                                actions.Add(new HandlerAction(noParamHandler.ActionType, noParamHandler.Keyword, "", noParamHandler));
                            }
                        }
                    }
                    index = startIndex + 2;
                    continue;
                }

                string actionType = response.Substring(typeStart, typeEnd - typeStart).ToLower();

                // 使用括号计数来找到匹配的结束位置（处理字符串内的括号）
                int parenCount = 1;
                int contentStart = typeEnd + 1;
                int contentEnd = contentStart;
                bool inString = false;
                char stringDelimiter = '\0';

                while (contentEnd < response.Length && parenCount > 0)
                {
                    char c = response[contentEnd];
                    
                    // 处理字符串边界
                    if ((c == '"' || c == '\'') && (contentEnd == contentStart || response[contentEnd - 1] != '\\'))
                    {
                        if (!inString)
                        {
                            inString = true;
                            stringDelimiter = c;
                        }
                        else if (c == stringDelimiter)
                        {
                            inString = false;
                        }
                    }
                    
                    // 只在字符串外计数括号
                    if (!inString)
                    {
                        if (c == '(')
                            parenCount++;
                        else if (c == ')')
                            parenCount--;
                    }

                    if (parenCount > 0)
                        contentEnd++;
                }

                if (parenCount != 0)
                {
                    // 括号不匹配，跳过
                    index = startIndex + 2;
                    continue;
                }

                // 查找闭合的 ]（容忍空格）
                int closeBracketIndex = contentEnd + 1;
                while (closeBracketIndex < response.Length && char.IsWhiteSpace(response[closeBracketIndex]))
                    closeBracketIndex++;

                if (closeBracketIndex >= response.Length || response[closeBracketIndex] != ']')
                {
                    index = startIndex + 2;
                    continue;
                }

                // 提取动作值
                string value = response.Substring(contentStart, contentEnd - contentStart);
                matchCount++;

                // 查找对应的 handler
                IActionHandler handler = Handlers.FirstOrDefault(h => h.Keyword.ToLower() == actionType);

                // 如果没找到 handler，尝试解析嵌套结构（如 talk(say(...))）
                if (handler == null)
                {
                    // 尝试提取嵌套的命令
                    var nestedMatch = new Regex(@"^(\w+)\((.*)\)$", RegexOptions.Singleline).Match(value);
                    if (nestedMatch.Success)
                    {
                        var nestedCommand = nestedMatch.Groups[1].Value.ToLower();
                        var nestedValue = nestedMatch.Groups[2].Value;
                        
                        handler = Handlers.FirstOrDefault(h => h.Keyword.ToLower() == nestedCommand);
                        if (handler != null)
                        {
                            // 使用嵌套的值
                            value = nestedValue;
                            Logger.Log($"ActionProcessor: Found nested command: {nestedCommand}");
                        }
                    }
                }

                if (handler == null)
                {
                    Logger.Log($"ActionProcessor: No handler found for command: {actionType}");
                    index = contentEnd + 2;
                    continue;
                }

                bool isEnabled = IsHandlerEnabled(handler, settings);

                if (!isEnabled)
                {
                    Logger.Log($"ActionProcessor: Handler '{handler.Keyword}' is disabled.");
                    index = contentEnd + 2;
                    continue;
                }

                actions.Add(new HandlerAction(handler.ActionType, handler.Keyword, value, handler));

                // 移动到下一个位置
                index = closeBracketIndex + 1;
            }

            Logger.Log($"ActionProcessor: Found {matchCount} matches, {actions.Count} actions enabled");
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