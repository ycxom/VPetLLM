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
        private Utils.FoodSearchService _foodSearchService;

        public SystemMessageProvider(Setting settings, IMainWindow mainWindow, ActionProcessor actionProcessor)
        {
            _settings = settings;
            _mainWindow = mainWindow;
            _actionProcessor = actionProcessor;
        }
        
        /// <summary>
        /// 获取或创建食物搜索服务
        /// </summary>
        private Utils.FoodSearchService GetFoodSearchService()
        {
            if (_foodSearchService == null)
            {
                _foodSearchService = new Utils.FoodSearchService(_mainWindow);
            }
            return _foodSearchService;
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
                
                // 只有在EnableVPetSettingsControl启用时才添加VPet设置控制命令说明
                if (_settings.EnableVPetSettingsControl)
                {
                    var vpetSettingsInstructions = PromptHelper.Get("VPetSettings_Commands_Description", lang);
                    if (!string.IsNullOrEmpty(vpetSettingsInstructions))
                    {
                        parts.Add(vpetSettingsInstructions);
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

                // 只有在EnableBuy开启时才添加可购买物品列表（使用简化版本减少token）
                if (_settings.EnableBuy)
                {
                    var searchService = GetFoodSearchService();
                    var simplifiedList = searchService.GetSimplifiedFoodListPrompt(lang);
                    var totalCount = searchService.GetTotalFoodCount();
                    
                    // 添加提示：AI可以使用任何物品名称，系统会自动进行模糊匹配
                    var fuzzySearchHint = lang == "zh" 
                        ? $"（共{totalCount}个物品，支持模糊搜索，你可以使用任何相近的名称）" 
                        : $"({totalCount} items total, fuzzy search supported, you can use similar names)";
                    
                    parts.Add(PromptHelper.Get("Available_Items_Prefix", lang)
                                .Replace("{ItemList}", simplifiedList + fuzzySearchHint));
                }
            }

            // 只有在EnablePlugin开启时才添加插件说明（独立于EnableAction）
            if (_settings.EnablePlugin && VPetLLM.Instance.Plugins.Any(p => p.Enabled))
            {
                var pluginDescriptions = VPetLLM.Instance.Plugins.Where(p => p.Enabled).Select(p => $"{p.Name}: {p.Description} {p.Examples}");
                parts.Add(PromptHelper.Get("Available_Plugins_Prefix", lang)
                            .Replace("{PluginList}", string.Join("\n", pluginDescriptions)));
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
            
            // 构建简洁的状态字符串 - 明确标识为宠物状态
            var statusPrefix = lang switch 
            { 
                "zh" => "[桌宠状态]", 
                _ => "[Pet Status]" 
            };
            
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
            
            // 如果启用了拓展状态获取，添加工作状态信息
            if (_settings.EnableExtendedState)
            {
                var activityState = GetActivityState();
                if (!string.IsNullOrEmpty(activityState))
                {
                    statusParts.Add(activityState);
                }
            }
            
            return $"{statusPrefix} {string.Join(";", statusParts)}";
        }
        
        /// <summary>
        /// 获取宠物当前活动状态（睡觉、工作、学习、玩耍等）
        /// </summary>
        private string GetActivityState()
        {
            try
            {
                var lang = _settings.PromptLanguage;
                var main = _mainWindow.Main;
                
                // 获取工作状态
                var workingState = main.State;
                
                switch (workingState)
                {
                    case VPet_Simulator.Core.Main.WorkingState.Sleep:
                        return lang switch 
                        { 
                            "zh" => "活动:睡觉中", 
                            _ => "Activity:Sleeping" 
                        };
                        
                    case VPet_Simulator.Core.Main.WorkingState.Work:
                        // 进一步判断是工作还是学习
                        if (main.NowWork != null)
                        {
                            var workType = main.NowWork.Type;
                            if (workType == VPet_Simulator.Core.GraphHelper.Work.WorkType.Work)
                            {
                                return lang switch 
                                { 
                                    "zh" => $"活动:工作中({main.NowWork.NameTrans})", 
                                    _ => $"Activity:Working({main.NowWork.NameTrans})" 
                                };
                            }
                            else if (workType == VPet_Simulator.Core.GraphHelper.Work.WorkType.Study)
                            {
                                return lang switch 
                                { 
                                    "zh" => $"活动:学习中({main.NowWork.NameTrans})", 
                                    _ => $"Activity:Studying({main.NowWork.NameTrans})" 
                                };
                            }
                            else
                            {
                                return lang switch 
                                { 
                                    "zh" => $"活动:忙碌中({main.NowWork.NameTrans})", 
                                    _ => $"Activity:Busy({main.NowWork.NameTrans})" 
                                };
                            }
                        }
                        return lang switch 
                        { 
                            "zh" => "活动:工作中", 
                            _ => "Activity:Working" 
                        };
                        
                    case VPet_Simulator.Core.Main.WorkingState.Travel:
                        return lang switch 
                        { 
                            "zh" => "活动:旅游中", 
                            _ => "Activity:Traveling" 
                        };
                        
                    case VPet_Simulator.Core.Main.WorkingState.Nomal:
                        // 正常状态，检查是否在播放音乐或其他特殊动画
                        if (main.DisplayType.Name == "music")
                        {
                            return lang switch 
                            { 
                                "zh" => "活动:听音乐", 
                                _ => "Activity:Listening to music" 
                            };
                        }
                        return lang switch 
                        { 
                            "zh" => "活动:空闲", 
                            _ => "Activity:Idle" 
                        };
                        
                    default:
                        return "";
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"获取活动状态失败: {ex.Message}");
                return "";
            }
        }
    }
}