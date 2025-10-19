using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers;
using VPetLLM.Utils;

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

            var lang = _settings.PromptLanguage;
            var parts = new List<string>
           {
               PromptHelper.Get("Role", lang)
                           .Replace("{AiName}", _settings.AiName)
                           .Replace("{UserName}", _settings.UserName)
           };

            // 只有在EnableAction开启时才添加自定义Role
            if (_settings.EnableAction)
            {
                parts.Add(_settings.Role);
            }

            if (_settings.EnableAction)
            {
                parts.Add(PromptHelper.Get("Character_Setting", lang));

                // 只有在EnableState开启时才添加状态信息
                if (_settings.EnableState)
                {
                    var core = _mainWindow.Core;
                    var status = PromptHelper.Get("Status_Prefix", lang)
                        .Replace("{Level}", core.Save.Level.ToString())
                        .Replace("{Money:F2}", core.Save.Money.ToString("F2"))
                        .Replace("{Strength:F0}", $"{(core.Save.Strength / core.Save.StrengthMax * 100):F0}%")
                        .Replace("{StrengthMax:F0}", "100%")
                        .Replace("{Health:F0}", $"{(core.Save.Health / 100.0 * 100):F0}%")
                        .Replace("{Feeling:F0}", $"{(core.Save.Feeling / core.Save.FeelingMax * 100):F0}%")
                        .Replace("{FeelingMax:F0}", "100%")
                        .Replace("{Likability:F0}", $"{(core.Save.Likability / core.Save.LikabilityMax * 100):F0}%")
                        .Replace("{LikabilityMax:F0}", "100%")
                        .Replace("{StrengthFood:F0}", $"{(core.Save.StrengthFood / core.Save.StrengthMax * 100):F0}%")
                        .Replace("{StrengthDrink:F0}", $"{(core.Save.StrengthDrink / core.Save.StrengthMax * 100):F0}%");

                    parts.Add(status);
                }
                
                // 只有在EnableTime开启时才添加时间信息（独立于EnableState）
                if (_settings.EnableTime)
                {
                    var timeInfo = PromptHelper.Get("Time_Prefix", lang)
                                .Replace("{CurrentTime:yyyy-MM-dd HH:mm:ss}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    parts.Add(timeInfo);
                }

                // 只有在EnablePlugin开启时才添加插件动态信息
                if (_settings.EnablePlugin)
                {
                    var dynamicPluginInfos = VPetLLM.Instance.Plugins
                        .OfType<IDynamicInfoPlugin>()
                        .Where(p => p.Enabled)
                        .Select(p => p.GetDynamicInfo())
                        .Where(info => !string.IsNullOrEmpty(info));

                    if (dynamicPluginInfos.Any())
                    {
                        parts.AddRange(dynamicPluginInfos);
                    }
                }

                var instructions = new List<string>();
                foreach (var handler in _actionProcessor.Handlers)
                {
                    bool isEnabled = handler.ActionType switch
                    {
                        ActionType.State => _settings.EnableState,
                        ActionType.Body => (handler.Keyword.ToLower() == "action" && _settings.EnableActionExecution) || 
                                          (handler.Keyword.ToLower() == "move" && _settings.EnableMove),
                        ActionType.Talk => true,
                        ActionType.Plugin => _settings.EnablePlugin,
                        _ => false
                    };
                    
                    // 特殊处理buy指令
                    if (handler.Keyword.ToLower() == "buy")
                    {
                        isEnabled = _settings.EnableBuy;
                    }

                    if (isEnabled)
                    {
                        instructions.Add(handler.Description);
                    }
                }

                if (instructions.Any())
                {
                    parts.Add(PromptHelper.Get("Available_Commands_Prefix", lang)
                                .Replace("{CommandList}", string.Join("\n", instructions)));
                }
                
                // 只有在EnableActionExecution开启时才添加动画列表
                if (_settings.EnableActionExecution)
                {
                    parts.Add(PromptHelper.Get("Available_Animations_Prefix", lang)
                                .Replace("{AnimationList}", string.Join(", ", VPetLLM.Instance.GetAvailableAnimations())));
                    parts.Add(PromptHelper.Get("Available_Say_Animations_Prefix", lang)
                                .Replace("{SayAnimationList}", string.Join(", ", VPetLLM.Instance.GetAvailableSayAnimations())));
                }

                // 只有在EnableBuy开启时才添加可购买物品列表
                if (_settings.EnableBuy)
                {
                    var items = string.Join(",", _mainWindow.Foods.Select(f => f.Name));
                    parts.Add(PromptHelper.Get("Available_Items_Prefix", lang)
                                .Replace("{ItemList}", items));
                }
            }

            // 只有在EnablePlugin开启时才添加插件说明（独立于EnableAction）
            if (_settings.EnablePlugin && VPetLLM.Instance.Plugins.Any(p => p.Enabled))
            {
                var pluginDescriptions = VPetLLM.Instance.Plugins.Where(p => p.Enabled).Select(p => $"{p.Name}: {p.Description} {p.Examples}");
                parts.Add("Available Plugins (all plugins must be called in the format `[:plugin(plugin_name(parameters))]`):\n" + string.Join("\n", pluginDescriptions));
                parts.Add("Plugin return results will be formatted as `[Plugin Result: {pluginName}] {result}`. When you see this format, it means a plugin has executed successfully and returned a result. You should acknowledge and respond to the plugin result appropriately.");
            }

            var systemMessage = string.Join("\n", parts);
            //    Logger.Log("System Message Generated:\n" + systemMessage); // System Role 查看用的
            return systemMessage;
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