using System;
using System.Collections.Generic;
using System.Linq;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers;

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

            var basePrompt = $"你的名字是{_settings.AiName}，我的名字是{_settings.UserName}。";
            var parts = new List<string> { basePrompt };

            if (_settings.EnableAction)
            {
                parts.Add(_settings.Role);

                if (_settings.EnableState)
                {
                    var core = _mainWindow.Core;
                    var status = $"当前状态: 等级({core.Save.Level}), 金钱({core.Save.Money:F2}), 体力({core.Save.Strength:F0}/{core.Save.StrengthMax:F0}), 健康({core.Save.Health:F0}), 心情({core.Save.Feeling:F0}/{core.Save.FeelingMax:F0}), 好感度({core.Save.Likability:F0}/{core.Save.LikabilityMax:F0}), 饱食度({core.Save.StrengthFood:F0}/{core.Save.StrengthMax:F0}), 口渴度({core.Save.StrengthDrink:F0}/{core.Save.StrengthMax:F0})";
                    if (_settings.EnableTime)
                    {
                        status += $", 当前时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
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
                    var rule = "你必须严格遵循以下规则:\n" +
                               "1. 你的回复可以包含一个或多个指令，用于按顺序控制我的行为和情绪。\n" +
                               "2. 严禁在括号或星号中描述动作，例如 '(高兴地摇尾巴)' 是错误的。你必须使用指令来替代。\n" +
                               "3. 优先级规则: `move`指令拥有最高优先级。如果回复中包含`move`指令，则只会执行`move`，并忽略所有其他指令。\n" +
                               "4. `say`指令用于说话，格式为 `[:talk(say(\"文本\",情绪))]`。文本必须用英文双引号包裹。所有要说的文本都必须在指令内部。\n" +
                               "5. 你可以像编写脚本一样，将多个非`move`指令组合在一起，它们会按顺序执行。例如: `[:talk(say(\"你好！\",happy))][:body(action(touchhead))][:state(happy(10))]`\n" +
                               "6. 聊天需要拼接[:state(happy(10))]控制心情，以便调整VPet_Simulator参数";
                    parts.Add(rule);
                    parts.Add("可用指令列表(包括可用情绪: happy, nomal, poorcondition, ill):\n" + string.Join("\n", instructions));
                }

                if (_settings.EnableBuy)
                {
                    var items = string.Join(",", _mainWindow.Foods.Select(f => f.Name));
                    parts.Add($"可购买物品列表:{items}。");
                }
            }

           if (_settings.EnablePlugin && VPetLLM.Instance.Plugins.Any(p => p.Enabled))
           {
               var pluginDescriptions = VPetLLM.Instance.Plugins.Where(p => p.Enabled).Select(p => $"{p.Name}: {p.Description}");
               parts.Add("可用插件列表:\n" + string.Join("\n", pluginDescriptions));
           }

            return string.Join("\n", parts);
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