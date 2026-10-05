using System.Text.RegularExpressions;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core.Services;
using VPetLLM.Handlers.Animation;
using static VPet_Simulator.Core.GraphInfo;

namespace VPetLLM.Handlers.Actions
{
    public class SayHandler : IActionHandler
    {
        public string Keyword => "say";
        public ActionType ActionType => ActionType.Talk;
        public ActionCategory Category => ActionCategory.Interactive;
        public string Description => PromptHelper.Get("Handler_Say_Description", VPetLLM.Instance.Settings.PromptLanguage);

        public async Task Execute(string value, IMainWindow mainWindow)
        {
            // 检查是否为默认插件
            if (VPetLLM.Instance?.IsVPetLLMDefaultPlugin() != true)
            {
                Logger.Log("SayHandler: VPetLLM不是默认插件，忽略Say请求");
                return;
            }

            Logger.Log($"SayHandler executed with value: {value}");

            // Say动作只是显示气泡，不影响动画，所以在所有情况下都允许执行
            // 不需要检查动画状态

            try
            {
                string text;
                string animation = null;
                string bodyAnimation = null;

                // Singleline：让 . 也匹配换行。回复本来就可能是多行的（列表、分段），
                // 不加的话多行 say 整条匹配不上，会退回 text = value —— 连同外层引号
                // 一起显示到气泡里。而 TTS 那侧走的是 [^"]* 字符类、本来就吃换行，
                // 于是出现"念的是对的、显示的多了一对引号"这种别扭的不一致。
                var match = new Regex("\"(.*?)\"(?:,\\s*([^,]*))?(?:,\\s*(.*))?", RegexOptions.Singleline).Match(value);
                if (match.Success)
                {
                    text = match.Groups[1].Value;
                    animation = match.Groups[2].Success && !string.IsNullOrEmpty(match.Groups[2].Value) ? match.Groups[2].Value.Trim() : null;
                    bodyAnimation = match.Groups[3].Success && !string.IsNullOrEmpty(match.Groups[3].Value) ? match.Groups[3].Value.Trim() : null;
                }
                else
                {
                    text = value;
                }

                // 使用动画协调器检查闪烁风险
                if (AnimationHelper.IsInitialized && AnimationHelper.IsFlickerRisk())
                {
                    var delay = AnimationHelper.GetRecommendedDelay();
                    Logger.Log($"SayHandler: 检测到闪烁风险，延迟 {delay}ms");
                    await Task.Delay(delay);
                }

                if (!string.IsNullOrEmpty(bodyAnimation))
                {
                    // Play body animation AND show the speech bubble without a conflicting talk animation.
                    var action = bodyAnimation.ToLower();
                    Logger.Log($"SayHandler performing body animation: {action} while talking.");

                    // 使用动画协调器检查是否可以执行动画
                    bool shouldBlockAnimation = AnimationStateChecker.IsPlayingImportantAnimation(mainWindow);
                    if (!shouldBlockAnimation)
                    {
                        Logger.Log($"SayHandler: 准备播放Body动画");
                        // 不需要停止当前动画，直接播放Body动画会自动处理过渡
                    }
                    else
                    {
                        Logger.Log($"SayHandler: VPet正在执行重要动画，跳过Body动画");
                        // 只显示气泡，不执行Body动画
                        await ShowBubbleOnlyAsync(mainWindow, text);

                        // SayHandler 不再负责等待，由 SmartMessageProcessor 统一处理
                        Logger.Log($"SayHandler: 仅气泡已启动（重要动画阻止），等待由调用方处理");
                        return;
                    }

                    // 1. Start the body animation. It will manage its own lifecycle.
                    bool actionTriggered = false;
                    switch (action)
                    {
                        case "touch_head":
                        case "touchhead":
                            mainWindow.Main.DisplayTouchHead?.Invoke();
                            actionTriggered = true;
                            break;
                        case "touch_body":
                        case "touchbody":
                            mainWindow.Main.DisplayTouchBody?.Invoke();
                            actionTriggered = true;
                            break;
                        case "pinch":
                        case "pinch_face":
                        case "touchpinch":
                            // 调用VPet的DisplayPinch方法（如果可用）
                            try
                            {
                                if (VPetHostAdapter.TryDisplayPinch(mainWindow))
                                {
                                    actionTriggered = true;
                                }
                                else
                                {
                                    Logger.Log("SayHandler: DisplayPinch method not found, pinch animation not available");
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Logger.Log($"SayHandler: Failed to execute pinch action: {ex.Message}");
                            }
                            break;
                        case "move":
                            // A Move graph alone does not initialize VPet's move timer.
                            // Always use the native move entry point and honor both settings.
                            if (VPetLLM.Instance?.Settings?.EnableMove == true
                                && mainWindow.Set?.AllowMove == true
                                && VPetHostAdapter.TryDisplayToMove(mainWindow))
                            {
                                actionTriggered = true;
                            }
                            else
                            {
                                Logger.Log("SayHandler: Native movement is disabled or no eligible move is available");
                            }
                            break;
                        case "sleep":
                            mainWindow.Main.DisplaySleep();
                            actionTriggered = true;
                            break;
                        case "idel":
                            // 使用DisplayToNomal()作为待机状态的替代方法
                            mainWindow.Main.DisplayToNomal();
                            actionTriggered = true;
                            break;
                        case "sideleft":
                            // 贴墙状态（左边）- VPet 11057+ 通过设置 State 实现
                            // 经适配层：旧实现 GetProperty("State") 因 State 是字段而恒为 null，贴墙从未生效
                            if (VPetHostAdapter.TrySetStateByName(mainWindow, "SideLeft"))
                            {
                                Logger.Log("SayHandler: Set state to SideLeft");
                            }
                            else
                            {
                                Logger.Log("SayHandler: SideLeft state unavailable, falling back to idel");
                                mainWindow.Main.DisplayToNomal();
                            }
                            actionTriggered = true;
                            break;
                        case "sideright":
                            // 贴墙状态（右边）- VPet 11057+ 通过设置 State 实现
                            if (VPetHostAdapter.TrySetStateByName(mainWindow, "SideRight"))
                            {
                                Logger.Log("SayHandler: Set state to SideRight");
                            }
                            else
                            {
                                Logger.Log("SayHandler: SideRight state unavailable, falling back to idel");
                                mainWindow.Main.DisplayToNomal();
                            }
                            actionTriggered = true;
                            break;
                        default:
                            mainWindow.Main.Display(action, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                            actionTriggered = true;
                            break;
                    }

                    if (!actionTriggered)
                    {
                        Logger.Log($"SayHandler: Body animation '{action}' failed to trigger, falling back to default");
                    }

                    // 2. Show the speech bubble ONLY by passing a null animation name.
                    // 显示气泡
                    await ShowBubbleOnlyAsync(mainWindow, text);

                    // SayHandler 不再负责等待，由 SmartMessageProcessor 统一处理
                    Logger.Log($"SayHandler: Body动画+气泡已启动，等待由调用方处理");
                }
                else
                {
                    // No body animation, so just perform the talk animation.
                    var sayAnimation = animation;

                    // 检查VPet是否正在执行重要动画
                    // 如果是，则屏蔽Say动画，只显示气泡
                    bool shouldBlockAnimation = AnimationStateChecker.IsPlayingImportantAnimation(mainWindow);

                    if (shouldBlockAnimation)
                    {
                        Logger.Log($"SayHandler: VPet正在执行重要动画，屏蔽Say动画，仅显示气泡");

                        // 只显示气泡，不执行Say动画
                        await ShowBubbleOnlyAsync(mainWindow, text);
                        Logger.Log($"SayHandler called Say with text: \"{text}\", animation: none (blocked)");

                        // SayHandler 不再负责等待，由 SmartMessageProcessor 统一处理
                        Logger.Log($"SayHandler: 仅气泡已启动（无动画模式），等待由调用方处理");
                    }
                    else
                    {
                        // VPet不在重要状态，直接播放Say动画
                        // 注意：不要使用 AnimationHelper.RequestStopAsync()，因为它会调用 DisplayToNomal() 覆盖 Say 动画
                        Logger.Log($"SayHandler: 准备播放Say动画");

                        // 解析动画参数（支持"状态_动画"格式）
                        var (animName, requestedMode) = ParseAnimationParameter(sayAnimation);
                        var main = mainWindow.Main;
                        var graphCore = main.Core.Graph;
                        var currentMode = main.Core.Save.Mode;

                        // 关键：动画名字不一定叫"say"！VPet通过路径解析动画名，
                        // 例如 happy/say/a/shy_500.png 的Name是"shy"而非"say"
                        // 必须使用 FindName(GraphType.Say) 获取实际注册名，与VPet内部SayRndFunction一致
                        var registeredSayName = graphCore.FindName(GraphType.Say);
                        if (animName == "say" && !string.IsNullOrEmpty(registeredSayName))
                        {
                            animName = registeredSayName;
                        }

                        // 心情：没指定就跟随宠物当前心情；指定了但和实际冲突（涉及生病）以实际为准
                        var targetMode = SayAnimationPlanner.ResolveTargetMode(requestedMode, currentMode);
                        if (requestedMode.HasValue && targetMode != requestedMode.Value)
                        {
                            Logger.Log($"SayHandler: 指定心情 {requestedMode.Value} 与当前心情 {currentMode} 冲突，按当前心情播放");
                        }

                        var graphs = new SayGraphLookup(graphCore, animName);
                        var plan = SayAnimationPlanner.Plan(graphs.Has, targetMode, currentMode);

                        if (plan.Kind == SayPlanKind.BubbleOnly && !string.IsNullOrEmpty(registeredSayName) && registeredSayName != animName)
                        {
                            // 指定的动画不存在，回退到默认说话动画
                            Logger.Log($"SayHandler: Animation '{animName}' not found, falling back to '{registeredSayName}'");
                            animName = registeredSayName;
                            graphs = new SayGraphLookup(graphCore, animName);
                            plan = SayAnimationPlanner.Plan(graphs.Has, targetMode, currentMode);
                        }

                        // 自己串的循环段必须是说话类动画：气泡结束时宿主只给说话类动画收尾，
                        // 其他类型会一直循环下去。不满足就退回交给宿主（按当前心情）。
                        if (plan.Kind == SayPlanKind.OwnLoop && !graphs.AllSayType(AnimatType.B_Loop, plan.Mode))
                        {
                            Logger.Log($"SayHandler: '{animName}' 不是说话类动画，无法自行循环，交给宿主按当前心情播放");
                            plan = new SayPlan(SayPlanKind.HostSay, currentMode);
                        }

                        Logger.Log($"SayHandler: 说话动画 '{animName}'，当前心情 {currentMode}，目标心情 {targetMode} -> {plan.Kind}({plan.Mode})");

                        // 添加UI操作延迟，减少瞬时性能压力
                        Utils.UI.BubbleDelayController.ApplyUIDelay();

                        switch (plan.Kind)
                        {
                            case SayPlanKind.HostSay:
                                // 宿主按当前心情选出的开始/循环/结束段就是这一套（A_Start→B_Loop→C_End 完整流程）
                                main.SayGuarded(text, animName, true);
                                break;

                            case SayPlanKind.OwnLoop:
                                // 先显示气泡（照常触发 SayProcess：TTS、表情包），再用同一心情的开始段接循环段。
                                // 气泡结束时宿主会对说话类动画执行 DisplayCEndtoNomal 收尾。
                                await ShowBubbleOnlyAsync(mainWindow, text);
                                PlayOwnLoop(main, animName, graphs.Get(AnimatType.A_Start, plan.Mode), graphs.Get(AnimatType.B_Loop, plan.Mode));
                                break;

                            case SayPlanKind.Single:
                                // Main.Say 硬编码 A_Start，只有 Single 的动画不能交给它：先显示气泡，再直接播放
                                await ShowBubbleOnlyAsync(mainWindow, text);
                                main.Display(Pick(graphs.Get(AnimatType.Single, plan.Mode)), main.DisplayToNomal);
                                break;

                            default:
                                // 当前心情下没有可用的说话动画（例如生病但没有生病的说话动画），仅显示气泡
                                await ShowBubbleOnlyAsync(mainWindow, text);
                                break;
                        }

                        // SayHandler 不再负责等待，由 SmartMessageProcessor 统一处理
                        // 这样可以避免重复等待的问题
                        Logger.Log($"SayHandler: Say动画已启动，等待由调用方处理");
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Log($"Error in SayHandler: {e.Message}");
            }
        }

        public Task Execute(int value, IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }
        public Task Execute(IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }
        public int GetAnimationDuration(string animationName) => 0;

        /// <summary>
        /// 仅显示气泡（不触发动画）
        /// 使用DirectBubbleManager实现直接覆盖
        /// 修复：确保在所有情况下都能正确显示气泡内容
        /// </summary>
        private async Task ShowBubbleOnlyAsync(IMainWindow mainWindow, string text)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var plugin = VPetLLM.Instance;
                    if (plugin is not null)
                    {
                        Logger.Log($"SayHandler: 显示气泡（仅气泡模式）- 文本: \"{text.Substring(0, Math.Min(30, text.Length))}...\"");

                        // 添加UI操作延迟，减少瞬时性能压力
                        Utils.UI.BubbleDelayController.ApplyUIDelay();
                        
                        // 直接使用VPet原生API
                        mainWindow.Main.SayGuarded(text, null, false);

                        Logger.Log($"SayHandler: 气泡显示完成（使用VPet原生API）");
                    }
                    else
                    {
                        Logger.Log("SayHandler: VPetLLM实例不可用，回退到API调用");
                        // 回退到标准API调用
                        mainWindow.Main.SayGuarded(text, null, false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"SayHandler: 显示气泡失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 自己串的循环段最长持续多久。正常情况下气泡结束时宿主会收尾（DisplayCEndtoNomal），
        /// 这里只防"气泡被别的途径关掉、宿主没收尾"时无限循环。
        /// </summary>
        private static readonly TimeSpan OwnLoopMaxDuration = TimeSpan.FromMinutes(2);

        /// <summary>
        /// 用同一心情的开始段接循环段，全程不碰 Core.Save.Mode。
        /// 新动画接手时宿主会 Stop(true) 丢弃本动画的结束回调，循环自然停止。
        /// </summary>
        private static void PlayOwnLoop(VPet_Simulator.Core.Main main, string animName,
            List<VPet_Simulator.Core.IGraph> starts, List<VPet_Simulator.Core.IGraph> loops)
        {
            var deadline = DateTime.UtcNow + OwnLoopMaxDuration;

            void Loop()
            {
                // 动画没加载好时宿主会同步回调"播完了"，不拦着就会在 UI 线程上无限递归
                var ready = loops.Where(g => g.IsReady).ToList();
                if (ready.Count == 0 || DateTime.UtcNow > deadline)
                {
                    main.DisplayCEndtoNomal(animName);
                    return;
                }
                main.Display(Pick(ready), Loop);
            }

            main.Display(Pick(starts), Loop);
        }

        private static VPet_Simulator.Core.IGraph Pick(List<VPet_Simulator.Core.IGraph> graphs) =>
            graphs.Count == 1 ? graphs[0] : graphs[VPet_Simulator.Core.Function.Rnd.Next(graphs.Count)];

        /// <summary>
        /// 按"段 + 心情"精确查动画：只要该心情本身的、已加载好的，不要宿主的跨心情回退结果。
        /// 结果在本次调用内缓存。
        /// </summary>
        private sealed class SayGraphLookup
        {
            private readonly VPet_Simulator.Core.GraphCore _core;
            private readonly string _name;
            private readonly Dictionary<(AnimatType, VPet_Simulator.Core.IGameSave.ModeType), List<VPet_Simulator.Core.IGraph>> _cache = new();

            public SayGraphLookup(VPet_Simulator.Core.GraphCore core, string name)
            {
                _core = core;
                _name = name;
            }

            public List<VPet_Simulator.Core.IGraph> Get(AnimatType animat, VPet_Simulator.Core.IGameSave.ModeType mode)
            {
                if (!_cache.TryGetValue((animat, mode), out var list))
                {
                    list = (_core.FindGraphs(_name, animat, mode) ?? new List<VPet_Simulator.Core.IGraph>())
                        .Where(g => g.GraphInfo.ModeType == mode && g.IsReady)
                        .ToList();
                    _cache[(animat, mode)] = list;
                }
                return list;
            }

            public bool Has(AnimatType animat, VPet_Simulator.Core.IGameSave.ModeType mode) => Get(animat, mode).Count > 0;

            public bool AllSayType(AnimatType animat, VPet_Simulator.Core.IGameSave.ModeType mode) =>
                Get(animat, mode).All(g => g.GraphInfo.Type == GraphType.Say);
        }

        /// <summary>
        /// 心情名 → 宿主心情。除宿主拼写外，也认模型常写的 normal / poor。
        /// </summary>
        private static VPet_Simulator.Core.IGameSave.ModeType? ParseMoodName(string name) => name switch
        {
            "happy" => VPet_Simulator.Core.IGameSave.ModeType.Happy,
            "nomal" or "normal" => VPet_Simulator.Core.IGameSave.ModeType.Nomal,
            "poorcondition" or "poor" => VPet_Simulator.Core.IGameSave.ModeType.PoorCondition,
            "ill" => VPet_Simulator.Core.IGameSave.ModeType.Ill,
            _ => null
        };

        /// <summary>
        /// 解析动画参数，支持"状态_动画"格式
        /// 例如：happy_shy -> 在happy状态下播放shy动画
        ///       shy -> 在当前状态下播放shy动画
        ///       happy -> 在happy状态下播放默认say动画
        /// </summary>
        private (string animationName, VPet_Simulator.Core.IGameSave.ModeType? modeType) ParseAnimationParameter(string animation)
        {
            if (string.IsNullOrEmpty(animation))
                return ("say", null);

            var animLower = animation.ToLower().Trim();

            // 检查是否为"状态_动画"格式
            var parts = animLower.Split('_');
            if (parts.Length >= 2)
            {
                var potentialMode = parts[0];
                var animName = string.Join("_", parts.Skip(1));

                // 检查第一部分是否为有效的状态模式
                VPet_Simulator.Core.IGameSave.ModeType? mode = ParseMoodName(potentialMode);

                if (mode.HasValue)
                {
                    Logger.Log($"SayHandler: 解析为状态模式动画 - 状态: {potentialMode}, 动画: {animName}");
                    return (animName, mode.Value);
                }
            }

            // 检查是否为纯状态名（如happy, nomal等），映射到该状态的默认say动画
            var stateOnlyMode = ParseMoodName(animLower);

            if (stateOnlyMode.HasValue)
            {
                Logger.Log($"SayHandler: 解析为纯状态名 '{animLower}'，使用该状态的say动画");
                return ("say", stateOnlyMode.Value);
            }

            // 不是状态_动画格式，直接返回动画名（使用当前状态）
            Logger.Log($"SayHandler: 使用动画名称 '{animLower}'（当前状态）");
            return (animLower, null);
        }
    }
}
