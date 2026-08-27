using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers.Infrastructure;
using VPetLLM.Core.Data.Managers;

namespace VPetLLM.Core.Services
{
    public class SystemMessageProvider
    {
        private readonly Setting _settings;
        private readonly IMainWindow _mainWindow;
        private readonly ActionProcessor _actionProcessor;
        private FoodSearchService _foodSearchService;

        /// <summary>
        /// Optional SkillManager for injecting available skills into the system prompt.
        /// Set by ChatCoreBase after construction.
        /// </summary>
        public SkillManager? SkillManager { get; set; }

        public SystemMessageProvider(Setting settings, IMainWindow mainWindow, ActionProcessor actionProcessor)
        {
            _settings = settings;
            _mainWindow = mainWindow;
            _actionProcessor = actionProcessor;
        }

        /// <summary>
        /// 获取或创建食物搜索服务
        /// </summary>
        private FoodSearchService GetFoodSearchService()
        {
            if (_foodSearchService is null)
            {
                _foodSearchService = new FoodSearchService(_mainWindow);
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

        /// <summary>
        /// 汇总当前真实可用的「自身能力」，只列开关打开的项。
        /// 每条形如「- 能力：一句话说明（调用方式）」，让模型能直接回答用户的能力问询。
        /// </summary>
        private List<string> BuildCapabilityList(string lang)
        {
            var items = new List<string>();

            void Add(string key)
            {
                var text = PromptHelper.Get(key, lang);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    items.Add(text);
                }
            }

            try
            {
                if (_settings.Screenshot?.IsEnabled == true)
                {
                    Add("Capability_SeeScreen");
                }

                if (_settings.ASR?.IsEnabled == true)
                {
                    Add("Capability_Listen");
                }

                if (_settings.TTS?.IsEnabled == true || VPetLLM.Instance?.IsVPetTTSPluginDetected == true)
                {
                    Add("Capability_Speak");
                }

                if (_settings.EnableMediaPlayback)
                {
                    Add("Capability_Play");
                }

                if (_settings.Records?.EnableRecords ?? true)
                {
                    Add("Capability_Memory");
                }

                if (_settings.EnableBuy)
                {
                    Add("Capability_Buy");
                }

                if (_settings.EnableActionExecution)
                {
                    Add("Capability_Action");
                }

                if (_settings.EnablePlugin && (VPetLLM.Instance?.Plugins.Any(p => p.Enabled) ?? false))
                {
                    Add("Capability_Plugin");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SystemMessageProvider: 构建能力清单失败: {ex.Message}");
            }

            return items;
        }

        /// <param name="nodeToolsEnabled">
        /// 本轮请求要发往的节点是否开启了原生工具调用。
        ///
        /// 必须按**节点**判断而不是按 provider：节点混搭是有意的兼容性配置
        /// （新模型开工具、老模型走标记，负载均衡在它们之间轮询）。若沿用
        /// "任一节点开了就写提示"的粗判，轮到没开工具的节点时，提示词会告诉模型
        /// "本节点已开启原生工具调用，优先直接调用"，而请求里根本没有 tools ——
        /// 模型只能把工具调用写进正文，实测就是 DeepSeek-R1 / Qwen2.5-7B 那种
        /// <c>&lt;tool_call&gt;</c> 泄漏到回复里的样子。
        ///
        /// null 表示调用方不知道（提示词预览等场景），此时回退到粗略判断。
        /// </param>
        public string GetSystemMessage(bool? nodeToolsEnabled = null)
        {
            if (_settings is null || _mainWindow is null || _actionProcessor is null) return "";

            var lang = _settings.PromptLanguage;
            var parts = new List<string>
           {
               PromptHelper.Get("Role", lang)
                           .Replace("{AiName}", _settings.AiName)
                           .Replace("{UserName}", _settings.UserName)
           };

            // 样貌设定：模型默认不知道自己长什么样，用户问起时会瞎编，这里显式告诉它。
            // 描述对应的是默认皮肤，换皮肤后 AppearancePolicy 会把开关默认关掉。
            try
            {
                if (AppearancePolicy.SyncWithPetGraph(_settings, _mainWindow))
                {
                    _settings.Save();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SystemMessageProvider: 同步样貌开关失败: {ex.Message}");
            }

            if (_settings.EnableAppearance)
            {
                var appearance = PromptHelper.Get("Appearance", lang);
                if (!string.IsNullOrWhiteSpace(appearance) && !appearance.StartsWith("[Prompt "))
                {
                    parts.Add(appearance);
                }
            }

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

                    // 特殊处理play指令 - 检查MediaPlayback是否启用
                    if (handler.Keyword.ToLower() == "play")
                    {
                        isEnabled = _settings.EnableMediaPlayback;
                    }

                    // 描述为空表示该 handler 自认不可用（如未配置的能力），不要塞空行进提示词
                    if (isEnabled && !string.IsNullOrWhiteSpace(handler.Description))
                    {
                        instructions.Add(handler.Description);
                    }
                }

                if (instructions.Any())
                {
                    parts.Add(PromptHelper.Get("Available_Commands_Prefix", lang)
                                .Replace("{CommandList}", string.Join("\n", instructions)));
                }

                // 能力清单：命令说明只告诉 AI「怎么调」，这里额外告诉它「你确实拥有这些能力」，
                // 避免用户问「你能看屏幕吗」时它凭直觉否认
                var capabilities = BuildCapabilityList(lang);
                if (capabilities.Any())
                {
                    parts.Add(PromptHelper.Get("Self_Capabilities_Prefix", lang)
                                .Replace("{CapabilityList}", string.Join("\n", capabilities)));
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

                // Add skills system instructions if SkillManager is available
                if (SkillManager is not null)
                {
                    var skillInstructions = PromptHelper.Get("Skill_System_Instructions", lang);
                    if (!string.IsNullOrEmpty(skillInstructions))
                    {
                        parts.Add(skillInstructions);
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
                    // 宿主窗口还没建好时动画表是空的（启动初期的插件回灌会撞上这个窗口）。
                    // 这时**整段都不要写** —— 空列表在提示词里是句谎话（"可用动画：" 后面什么都没有），
                    // 模型会据此认为一个动画都不能用；省略掉它反而只是少一条信息。
                    var animationList = string.Join(", ", VPetLLM.Instance.GetAvailableAnimations());
                    if (!string.IsNullOrEmpty(animationList))
                    {
                        parts.Add(PromptHelper.Get("Available_Animations_Prefix", lang)
                                    .Replace("{AnimationList}", animationList));
                    }

                    var sayAnimationList = string.Join(", ", VPetLLM.Instance.GetAvailableSayAnimations());
                    if (!string.IsNullOrEmpty(sayAnimationList))
                    {
                        parts.Add(PromptHelper.Get("Available_Say_Animations_Prefix", lang)
                                    .Replace("{SayAnimationList}", sayAnimationList));
                    }
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

                    // 添加物品栏信息（桌宠已拥有的物品）
                    var inventorySummary = searchService.GetInventorySummary(lang);
                    if (!string.IsNullOrEmpty(inventorySummary) && inventorySummary != (lang == "zh" ? "物品栏为空" : "Inventory is empty"))
                    {
                        parts.Add(PromptHelper.Get("Available_Inventory_Prefix", lang)
                                    .Replace("{InventoryList}", inventorySummary));
                    }
                }
            }

            // 添加可用的工作/学习/玩耍列表（仅在EnableAction开启时）
            if (_settings.EnableAction)
            {
                try
                {
                    var workList = WorkManager.GetWorkListForPrompt(_mainWindow);
                    if (!string.IsNullOrWhiteSpace(workList))
                    {
                        parts.Add(PromptHelper.Get("Available_Works_Prefix", lang)
                                    .Replace("{WorkList}", workList));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"SystemMessageProvider: Error getting work list: {ex.Message}");
                }
            }

            // 只有在EnablePlugin开启时才添加插件说明（独立于EnableAction）
            if (_settings.EnablePlugin && VPetLLM.Instance.Plugins.Any(p => p.Enabled))
            {
                // 实现了 IToolSchemaPlugin 的插件渲染成 TypeScript 风格签名，
                // 其余仍走 "Name: Description Examples" 的老格式（见 ToolSchemaRenderer）
                var pluginList = ToolSchemaRenderer.RenderAll(VPetLLM.Instance.Plugins.Where(p => p.Enabled));
                parts.Add(PromptHelper.Get("Available_Plugins_Prefix", lang)
                            .Replace("{PluginList}", pluginList));

                // 开了原生工具调用就补一句优先级说明。
                // 刻意**不**移除上面的标记说明：节点混搭时（有的节点开了工具、有的没开）
                // 拿掉标记说明会让没开的那些节点彻底调不动插件，而多留着最多只是冗余。
                //
                // 这句提示词说的是"**本节点**已开启原生工具调用"，所以判断依据必须是
                // 本轮真正要发往的那个节点。调用方给了 nodeToolsEnabled 就用它；
                // 给不了（比如提示词预览）才回退到"任一启用节点开了工具"的粗略判断。
                var toolsForThisRequest = nodeToolsEnabled
                                          ?? Core.Tools.NativeToolSession.IsLikelyActive(_settings);
                if (toolsForThisRequest)
                {
                    parts.Add(Core.Tools.NativeToolSession.BuildPromptNotice(lang));
                }

                // 有插件调用还挂在后台时告诉模型，避免它以为没执行成功又发一遍
                var running = BackgroundPluginTasks.DescribeRunning(lang);
                if (!string.IsNullOrEmpty(running))
                {
                    parts.Add(running);
                }
            }

            // 草稿纸上有东西时列出键名，否则模型不知道有什么可 load
            var storedKeys = SessionStore.Describe(lang);
            if (!string.IsNullOrEmpty(storedKeys))
            {
                parts.Add(storedKeys);
            }

            // 上一轮的格式纠正放在**最后**：越靠近输出位置的指令模型越容易照做，
            // 而这条恰恰是要它立刻改的。取出即清，不重复唠叨。
            // 注意先记下种类再取 —— TakeReminder 会清空，取完再读 Pending 永远是 None
            var pendingKind = FormatComplianceTracker.Pending;
            var formatReminder = FormatComplianceTracker.TakeReminder(lang);
            if (!string.IsNullOrEmpty(formatReminder))
            {
                Logger.Log($"SystemMessageProvider: 注入格式纠正提醒（{pendingKind}）");
                parts.Add(formatReminder);
            }

            var systemMessage = string.Join("\n", parts);
            return systemMessage;
        }

        /// <summary>
        /// 获取有效的强调文本。
        /// null = 用户停用，返回 ""；"" = 跟随 Prompt.json 默认；非空 = 用户自定义。
        /// </summary>
        public string GetEmphasis()
        {
            if (_settings?.Emphasis is null)
                return "";

            if (_settings.Emphasis.Length > 0)
                return _settings.Emphasis;

            var lang = _settings.PromptLanguage ?? "zh";
            return PromptHelper.Get("Emphasis", lang);
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
            if (_settings is null || _mainWindow is null || !_settings.EnableState || !_settings.ReduceInputTokenUsage)
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

            // 构建简洁的状态字符串（不再添加前缀，由 DisplayContent 的 JSON 格式标识）
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

            return string.Join(";", statusParts);
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
                        if (main.NowWork is not null)
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
