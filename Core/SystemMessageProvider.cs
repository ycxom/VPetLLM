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

        /// <summary>
        /// 获取状态描述文本
        /// </summary>
        private string GetStatusDescription(double percentage, string type)
        {
            var lang = _settings.PromptLanguage;
            
            if (type == "hunger" || type == "thirst")
            {
                // 对于饥饿度和口渴度，100%表示不饿/不渴
                if (percentage >= 80)
                    return lang == "zh" ? (type == "hunger" ? "不饿" : "不渴") : (type == "hunger" ? "not hungry" : "not thirsty");
                else if (percentage >= 50)
                    return lang == "zh" ? (type == "hunger" ? "略微饥饿" : "略微口渴") : (type == "hunger" ? "slightly hungry" : "slightly thirsty");
                else if (percentage >= 20)
                    return lang == "zh" ? (type == "hunger" ? "饥饿" : "口渴") : (type == "hunger" ? "hungry" : "thirsty");
                else
                    return lang == "zh" ? (type == "hunger" ? "非常饥饿" : "非常口渴") : (type == "hunger" ? "very hungry" : "very thirsty");
            }
            else if (type == "health")
            {
                if (percentage >= 80)
                    return lang == "zh" ? "健康" : "healthy";
                else if (percentage >= 50)
                    return lang == "zh" ? "略微不适" : "slightly unwell";
                else if (percentage >= 20)
                    return lang == "zh" ? "不健康" : "unhealthy";
                else
                    return lang == "zh" ? "非常虚弱" : "very weak";
            }
            else if (type == "mood")
            {
                if (percentage >= 80)
                    return lang == "zh" ? "心情很好" : "very happy";
                else if (percentage >= 50)
                    return lang == "zh" ? "心情一般" : "neutral";
                else if (percentage >= 20)
                    return lang == "zh" ? "心情不好" : "unhappy";
                else
                    return lang == "zh" ? "心情很差" : "very unhappy";
            }
            else if (type == "likability")
            {
                if (percentage >= 80)
                    return lang == "zh" ? "非常喜欢" : "very fond";
                else if (percentage >= 50)
                    return lang == "zh" ? "喜欢" : "fond";
                else if (percentage >= 20)
                    return lang == "zh" ? "一般" : "neutral";
                else
                    return lang == "zh" ? "不喜欢" : "dislike";
            }
            else if (type == "stamina")
            {
                if (percentage >= 80)
                    return lang == "zh" ? "精力充沛" : "energetic";
                else if (percentage >= 50)
                    return lang == "zh" ? "精力一般" : "moderate energy";
                else if (percentage >= 20)
                    return lang == "zh" ? "有些疲惫" : "tired";
                else
                    return lang == "zh" ? "非常疲惫" : "exhausted";
            }
            
            return "";
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
                
                // 只有在Records系统启用时才添加记忆系统规则
                if (_settings.Records?.EnableRecords ?? true)
                {
                    var memoryRule = PromptHelper.Get("Character_Setting_Memory", lang);
                    if (!string.IsNullOrEmpty(memoryRule))
                    {
                        parts.Add(memoryRule);
                    }
                }

                // 只有在EnableState开启且未启用减少输入token消耗时才添加状态信息到system role
                if (_settings.EnableState && !_settings.ReduceInputTokenUsage)
                {
                    var core = _mainWindow.Core;
                    
                    // 计算各项百分比
                    var staminaPercent = core.Save.Strength / core.Save.StrengthMax * 100;
                    var healthPercent = core.Save.Health;
                    var moodPercent = core.Save.Feeling / core.Save.FeelingMax * 100;
                    var likabilityPercent = core.Save.Likability / core.Save.LikabilityMax * 100;
                    var hungerPercent = core.Save.StrengthFood / core.Save.StrengthMax * 100;
                    var thirstPercent = core.Save.StrengthDrink / core.Save.StrengthMax * 100;
                    
                    // 获取状态描述
                    var staminaDesc = GetStatusDescription(staminaPercent, "stamina");
                    var healthDesc = GetStatusDescription(healthPercent, "health");
                    var moodDesc = GetStatusDescription(moodPercent, "mood");
                    var likabilityDesc = GetStatusDescription(likabilityPercent, "likability");
                    var hungerDesc = GetStatusDescription(hungerPercent, "hunger");
                    var thirstDesc = GetStatusDescription(thirstPercent, "thirst");
                    
                    var status = PromptHelper.Get("Status_Prefix", lang)
                        .Replace("{Level}", core.Save.Level.ToString())
                        .Replace("{Money:F2}", core.Save.Money.ToString("F2"))
                        .Replace("{Strength:F0}", $"{staminaPercent:F0}%")
                        .Replace("{StaminaDesc}", staminaDesc)
                        .Replace("{Health:F0}", $"{healthPercent:F0}%")
                        .Replace("{HealthDesc}", healthDesc)
                        .Replace("{Feeling:F0}", $"{moodPercent:F0}%")
                        .Replace("{MoodDesc}", moodDesc)
                        .Replace("{Likability:F0}", $"{likabilityPercent:F0}%")
                        .Replace("{LikabilityDesc}", likabilityDesc)
                        .Replace("{StrengthFood:F0}", $"{hungerPercent:F0}%")
                        .Replace("{HungerDesc}", hungerDesc)
                        .Replace("{StrengthDrink:F0}", $"{thirstPercent:F0}%")
                        .Replace("{ThirstDesc}", thirstDesc);

                    parts.Add(status);
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
                        ActionType.Tool => true, // Tool handlers are enabled by default
                        _ => false
                    };
                    
                    // 特殊处理buy指令
                    if (handler.Keyword.ToLower() == "buy")
                    {
                        isEnabled = _settings.EnableBuy;
                    }
                    
                    // 特殊处理record指令 - 检查Records系统是否启用
                    if (handler.Keyword.ToLower() == "record" || handler.Keyword.ToLower() == "record_modify")
                    {
                        isEnabled = _settings.Records?.EnableRecords ?? true;
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
                
                // 只有在Records系统启用时才添加记录系统说明
                if (_settings.Records?.EnableRecords ?? true)
                {
                    var recordInstructions = PromptHelper.Get("Record_System_Instructions", lang);
                    if (!string.IsNullOrEmpty(recordInstructions))
                    {
                        parts.Add(recordInstructions);
                    }
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

        /// <summary>
        /// 获取状态信息字符串（用于添加到用户消息中）
        /// </summary>
        public string GetStatusString()
        {
            if (_settings == null || _mainWindow == null || !_settings.EnableState || !_settings.ReduceInputTokenUsage)
                return "";

            var lang = _settings.PromptLanguage;
            var core = _mainWindow.Core;
            
            // 计算各项百分比
            var staminaPercent = core.Save.Strength / core.Save.StrengthMax * 100;
            var healthPercent = core.Save.Health;
            var moodPercent = core.Save.Feeling / core.Save.FeelingMax * 100;
            var likabilityPercent = core.Save.Likability / core.Save.LikabilityMax * 100;
            var hungerPercent = core.Save.StrengthFood / core.Save.StrengthMax * 100;
            var thirstPercent = core.Save.StrengthDrink / core.Save.StrengthMax * 100;
            
            // 获取状态描述
            var staminaDesc = GetStatusDescription(staminaPercent, "stamina");
            var healthDesc = GetStatusDescription(healthPercent, "health");
            var moodDesc = GetStatusDescription(moodPercent, "mood");
            var likabilityDesc = GetStatusDescription(likabilityPercent, "likability");
            var hungerDesc = GetStatusDescription(hungerPercent, "hunger");
            var thirstDesc = GetStatusDescription(thirstPercent, "thirst");
            
            // 构建简洁的状态字符串
            var statusParts = new List<string>
            {
                $"Lv{core.Save.Level}",
                $"${core.Save.Money:F2}",
                $"{lang switch { "zh" => "体力", _ => "Stamina" }}:{staminaPercent:F0}%({staminaDesc})",
                $"{lang switch { "zh" => "健康", _ => "Health" }}:{healthPercent:F0}%({healthDesc})",
                $"{lang switch { "zh" => "心情", _ => "Mood" }}:{moodPercent:F0}%({moodDesc})",
                $"{lang switch { "zh" => "好感", _ => "Likability" }}:{likabilityPercent:F0}%({likabilityDesc})",
                $"{lang switch { "zh" => "饱食", _ => "Hunger" }}:{hungerPercent:F0}%({hungerDesc})",
                $"{lang switch { "zh" => "口渴", _ => "Thirst" }}:{thirstPercent:F0}%({thirstDesc})"
            };
            
            return string.Join(";", statusParts);
        }
    }
}