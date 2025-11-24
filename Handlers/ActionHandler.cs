using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;
using static VPet_Simulator.Core.GraphInfo;

namespace VPetLLM.Handlers
{
    public class ActionHandler : IActionHandler
    {
        public string Keyword => "action";
        public ActionType ActionType => ActionType.Body;
        public ActionCategory Category => ActionCategory.StateBased;
        public string Description => PromptHelper.Get("Handler_Action_Description", VPetLLM.Instance.Settings.PromptLanguage);

        /// <summary>
        /// Set of actions that are interactive-only (animation without state change)
        /// </summary>
        private static readonly HashSet<string> InteractiveActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "touch_head", "touchhead",
            "touch_body", "touchbody",
            "pinch", "pinch_face", "touchpinch"
        };

        /// <summary>
        /// Determines the category of an action (state-based, interactive, or unknown)
        /// </summary>
        /// <param name="actionName">The name of the action</param>
        /// <returns>The action category</returns>
        private ActionCategory GetActionCategory(string actionName)
        {
            if (string.IsNullOrEmpty(actionName))
                return ActionCategory.Unknown;

            // Check if it's an interactive action
            if (InteractiveActions.Contains(actionName))
            {
                Logger.Log($"ActionHandler: Action '{actionName}' categorized as Interactive");
                return ActionCategory.Interactive;
            }

            // Check if it's a state-based action
            if (actionName.Equals("sleep", StringComparison.OrdinalIgnoreCase) ||
                actionName.Equals("wakeup", StringComparison.OrdinalIgnoreCase) ||
                actionName.Equals("normal", StringComparison.OrdinalIgnoreCase) ||
                actionName.Equals("nomal", StringComparison.OrdinalIgnoreCase) ||
                actionName.Equals("work", StringComparison.OrdinalIgnoreCase) ||
                actionName.Equals("study", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log($"ActionHandler: Action '{actionName}' categorized as StateBased");
                return ActionCategory.StateBased;
            }

            // Default to unknown (animation only, no state change)
            Logger.Log($"ActionHandler: Action '{actionName}' categorized as Unknown (default to animation-only)");
            return ActionCategory.Unknown;
        }

        public async Task Execute(string actionName, IMainWindow mainWindow)
        {
            // 检查是否为默认插件
            if (VPetLLM.Instance?.IsVPetLLMDefaultPlugin() != true)
            {
                Logger.Log("ActionHandler: VPetLLM不是默认插件，忽略动作请求");
                return;
            }

            Utils.Logger.Log($"ActionHandler: Executing action '{actionName}'");
            
            var action = string.IsNullOrEmpty(actionName) ? "idel" : actionName;

            // Handle stopaction command - force stop current animation/work
            if (action.Equals("stopaction", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log("ActionHandler: Executing stopaction - forcing stop");
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // Get current state first to determine proper stop method
                        string stateName = "Unknown";
                        object currentStateValue = null;
                        System.Reflection.FieldInfo stateField = null;
                        
                        // Get State field (VPet uses field, not property)
                        var mainType = mainWindow.Main.GetType();
                        stateField = mainType.GetField("State");
                        
                        if (stateField != null)
                        {
                            currentStateValue = stateField.GetValue(mainWindow.Main);
                            stateName = currentStateValue?.ToString() ?? "Unknown";
                            Logger.Log($"ActionHandler: Current state is {stateName}");
                        }
                        else
                        {
                            Logger.Log("ActionHandler: State field not found");
                        }

                        // Handle different states with appropriate animations
                        switch (stateName)
                        {
                            case "Work":
                                // For work state, use WorkTimer.Stop() for proper work end animation
                                Logger.Log("ActionHandler: Stopping work state");
                                try
                                {
                                    var workTimer = mainWindow.Main.WorkTimer;
                                    if (workTimer != null)
                                    {
                                        workTimer.Stop(reason: VPet_Simulator.Core.WorkTimer.FinishWorkInfo.StopReason.MenualStop);
                                        Logger.Log("ActionHandler: stopaction completed - work stopped with proper animation");
                                        return;
                                    }
                                }
                                catch (Exception workEx)
                                {
                                    Logger.Log($"ActionHandler: Direct WorkTimer access failed: {workEx.Message}, trying reflection");
                                    
                                    // Fallback to reflection
                                    var workTimerProperty = mainWindow.Main.GetType().GetProperty("WorkTimer");
                                    if (workTimerProperty != null)
                                    {
                                        var workTimer = workTimerProperty.GetValue(mainWindow.Main);
                                        if (workTimer != null)
                                        {
                                            var stopMethod = workTimer.GetType().GetMethod("Stop");
                                            if (stopMethod != null)
                                            {
                                                var stopReasonType = workTimer.GetType().GetNestedType("FinishWorkInfo")?.GetNestedType("StopReason");
                                                if (stopReasonType != null)
                                                {
                                                    var menuStopReason = Enum.Parse(stopReasonType, "MenualStop");
                                                    stopMethod.Invoke(workTimer, new object[] { null, menuStopReason });
                                                    Logger.Log("ActionHandler: stopaction completed - work stopped via reflection");
                                                    return;
                                                }
                                            }
                                        }
                                    }
                                }
                                break;

                            case "Sleep":
                                // For sleep state, wake up properly with sleep end animation
                                Logger.Log("ActionHandler: Stopping sleep state");
                                
                                // Set state to Nomal first
                                if (stateField != null)
                                {
                                    var workingStateType = stateField.FieldType;
                                    var nomalState = Enum.Parse(workingStateType, "Nomal");
                                    stateField.SetValue(mainWindow.Main, nomalState);
                                }
                                
                                // Play sleep end animation (C_End) then transition to normal
                                try
                                {
                                    // Get DisplayNomal property
                                    var displayNomalProp = mainWindow.Main.GetType().GetProperty("DisplayNomal");
                                    if (displayNomalProp != null)
                                    {
                                        var displayNomalAction = displayNomalProp.GetValue(mainWindow.Main) as Action;
                                        mainWindow.Main.Display(VPet_Simulator.Core.GraphInfo.GraphType.Sleep, 
                                                               VPet_Simulator.Core.GraphInfo.AnimatType.C_End, 
                                                               displayNomalAction);
                                    }
                                    else
                                    {
                                        // Fallback: just call DisplayToNomal
                                        mainWindow.Main.DisplayToNomal();
                                    }
                                }
                                catch (Exception animEx)
                                {
                                    Logger.Log($"ActionHandler: Sleep end animation failed: {animEx.Message}");
                                    mainWindow.Main.DisplayToNomal();
                                }
                                
                                Logger.Log("ActionHandler: stopaction completed");
                                return;

                            default:
                                // For other states, just return to normal
                                Logger.Log($"ActionHandler: Stopping {stateName} state - returning to normal");
                                mainWindow.Main.DisplayToNomal();
                                Logger.Log("ActionHandler: stopaction completed - returned to normal state");
                                return;
                        }

                        // If we reach here, fallback to DisplayToNomal
                        mainWindow.Main.DisplayToNomal();
                        Logger.Log("ActionHandler: stopaction completed - DisplayToNomal called as fallback");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"ActionHandler: stopaction failed: {ex.Message}");
                        Logger.Log($"ActionHandler: Exception type: {ex.GetType().Name}");
                        // Last resort fallback
                        try
                        {
                            mainWindow.Main.DisplayToNomal();
                            Logger.Log("ActionHandler: stopaction emergency fallback - DisplayToNomal called");
                        }
                        catch (Exception ex2)
                        {
                            Logger.Log($"ActionHandler: stopaction emergency fallback also failed: {ex2.Message}");
                        }
                    }
                });
                return;
            }
            
            // 检查VPet是否正在执行重要动画
            if (AnimationStateChecker.IsPlayingImportantAnimation(mainWindow))
            {
                Logger.Log($"ActionHandler: 当前VPet状态 ({AnimationStateChecker.GetCurrentAnimationDescription(mainWindow)}) 不允许执行动作，已跳过");
                return;
            }

            // Check if action contains work specification (format: work:工作名称 or study:学习名称 or play:玩耍名称)
            if (action.Contains(":"))
            {
                var parts = action.Split(new[] { ':' }, 2);
                if (parts.Length == 2)
                {
                    var actionType = parts[0].Trim().ToLower();
                    var workName = parts[1].Trim();

                    if (actionType == "work" || actionType == "study" || actionType == "play")
                    {
                        Logger.Log($"ActionHandler: Detected {actionType} request with name: {workName}");
                        await HandleWorkAction(mainWindow, workName, actionType);
                        return;
                    }
                }
            }

            // Determine action category
            var category = GetActionCategory(action);
            Logger.Log($"ActionHandler: Action '{action}' categorized as {category}");

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                bool actionTriggered = false;
                
                // Handle state-based actions
                if (category == ActionCategory.StateBased)
                {
                    Logger.Log($"ActionHandler: Processing state-based action '{action}'");
                    switch (action.ToLower())
                    {
                        case "sleep":
                            Logger.Log("ActionHandler: Executing state-based action 'sleep' - calling StateManager.TransitionToState");
                            StateManager.TransitionToState(mainWindow, "Sleep", action);
                            actionTriggered = true;
                            Logger.Log("ActionHandler: State-based action 'sleep' completed");
                            break;
                        case "wakeup":
                        case "normal":
                        case "nomal":
                            Logger.Log($"ActionHandler: Executing state-based action '{action}' (wakeup/normal) - calling StateManager.TransitionToState");
                            StateManager.TransitionToState(mainWindow, "Nomal", action);
                            actionTriggered = true;
                            Logger.Log($"ActionHandler: State-based action '{action}' (wakeup/normal) completed");
                            break;
                        case "work":
                            Logger.Log("ActionHandler: Executing state-based action 'work' - calling StateManager.TransitionToState");
                            StateManager.TransitionToState(mainWindow, "Work", action);
                            actionTriggered = true;
                            Logger.Log("ActionHandler: State-based action 'work' completed");
                            break;
                        case "study":
                            Logger.Log("ActionHandler: Executing state-based action 'study' - calling StateManager.TransitionToState");
                            StateManager.TransitionToState(mainWindow, "Study", action);
                            actionTriggered = true;
                            Logger.Log("ActionHandler: State-based action 'study' completed");
                            break;
                    }
                }
                // Handle interactive actions (animation only, no state change)
                else if (category == ActionCategory.Interactive)
                {
                    Logger.Log($"ActionHandler: Processing interactive action '{action}' (animation only, no state change)");
                    switch (action.ToLower())
                    {
                        case "touch_head":
                        case "touchhead":
                            Logger.Log("ActionHandler: Executing interactive action 'touch_head'");
                            mainWindow.Main.DisplayTouchHead();
                            actionTriggered = true;
                            Logger.Log("ActionHandler: Interactive action 'touch_head' completed");
                            break;
                        case "touch_body":
                        case "touchbody":
                            Logger.Log("ActionHandler: Executing interactive action 'touch_body'");
                            mainWindow.Main.DisplayTouchBody();
                            actionTriggered = true;
                            Logger.Log("ActionHandler: Interactive action 'touch_body' completed");
                            break;
                        case "pinch":
                        case "pinch_face":
                        case "touchpinch":
                            Logger.Log("ActionHandler: Executing interactive action 'pinch'");
                            // 调用VPet的DisplayPinch方法（如果可用）
                            try
                            {
                                var displayPinchMethod = mainWindow.GetType().GetMethod("DisplayPinch");
                                if (displayPinchMethod != null)
                                {
                                    displayPinchMethod.Invoke(mainWindow, null);
                                    actionTriggered = true;
                                    Logger.Log("ActionHandler: Interactive action 'pinch' completed");
                                }
                                else
                                {
                                    Logger.Log("ActionHandler: DisplayPinch method not found, pinch animation not available");
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Logger.Log($"ActionHandler: Failed to execute pinch action: {ex.GetType().Name}");
                                Logger.Log($"ActionHandler: Exception message: {ex.Message}");
                            }
                            break;
                    }
                }
                // Handle unknown actions (default to animation-only behavior)
                else
                {
                    Logger.Log($"ActionHandler: Processing unknown action '{action}' (default to animation-only behavior)");
                    switch (action.ToLower())
                    {
                        case "move":
                            Logger.Log("ActionHandler: Executing 'move' animation");
                            // 直接调用Display方法显示移动动画，绕过可能失效的委托属性
                            mainWindow.Main.Display(GraphType.Move, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                            actionTriggered = true;
                            Logger.Log("ActionHandler: 'move' animation completed");
                            break;
                        case "idel":
                            Logger.Log("ActionHandler: Executing 'idel' (DisplayToNomal)");
                            // 使用DisplayToNomal()作为待机状态的替代方法
                            mainWindow.Main.DisplayToNomal();
                            actionTriggered = true;
                            Logger.Log("ActionHandler: 'idel' completed");
                            break;
                        case "sideleft":
                            Logger.Log("ActionHandler: Attempting to set 'sideleft' state");
                            // 贴墙状态（左边）- VPet 11057+ 通过设置 State 实现
                            try
                            {
                                var stateProperty = mainWindow.Main.GetType().GetProperty("State");
                                if (stateProperty != null)
                                {
                                    var workingStateType = stateProperty.PropertyType;
                                    var sideLeftValue = System.Enum.Parse(workingStateType, "SideLeft");
                                    stateProperty.SetValue(mainWindow.Main, sideLeftValue);
                                    Logger.Log("ActionHandler: Successfully set state to SideLeft");
                                    actionTriggered = true;
                                }
                                else
                                {
                                    Logger.Log("ActionHandler: State property not found, falling back to idel");
                                    mainWindow.Main.DisplayToNomal();
                                    actionTriggered = true;
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Logger.Log($"ActionHandler: Failed to set SideLeft state: {ex.GetType().Name}");
                                Logger.Log($"ActionHandler: Exception message: {ex.Message}");
                                Logger.Log("ActionHandler: Falling back to idel");
                                mainWindow.Main.DisplayToNomal();
                                actionTriggered = true;
                            }
                            break;
                        case "sideright":
                            Logger.Log("ActionHandler: Attempting to set 'sideright' state");
                            // 贴墙状态（右边）- VPet 11057+ 通过设置 State 实现
                            try
                            {
                                var stateProperty = mainWindow.Main.GetType().GetProperty("State");
                                if (stateProperty != null)
                                {
                                    var workingStateType = stateProperty.PropertyType;
                                    var sideRightValue = System.Enum.Parse(workingStateType, "SideRight");
                                    stateProperty.SetValue(mainWindow.Main, sideRightValue);
                                    Logger.Log("ActionHandler: Successfully set state to SideRight");
                                    actionTriggered = true;
                                }
                                else
                                {
                                    Logger.Log("ActionHandler: State property not found, falling back to idel");
                                    mainWindow.Main.DisplayToNomal();
                                    actionTriggered = true;
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Logger.Log($"ActionHandler: Failed to set SideRight state: {ex.GetType().Name}");
                                Logger.Log($"ActionHandler: Exception message: {ex.Message}");
                                Logger.Log("ActionHandler: Falling back to idel");
                                mainWindow.Main.DisplayToNomal();
                                actionTriggered = true;
                            }
                            break;
                        default:
                            Logger.Log($"ActionHandler: Executing generic animation '{action}'");
                            mainWindow.Main.Display(action, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                            actionTriggered = true;
                            Logger.Log($"ActionHandler: Generic animation '{action}' completed");
                            break;
                    }
                }
                
                if (!actionTriggered)
                {
                    Logger.Log($"ActionHandler: WARNING - Action '{action}' (category: {category}) failed to trigger");
                }
                else
                {
                    Logger.Log($"ActionHandler: Action '{action}' (category: {category}) completed successfully");
                }
                
                await Task.Delay(1000);
            });
        }

        /// <summary>
        /// Handles work/study/play actions with specific work names
        /// </summary>
        private async Task HandleWorkAction(IMainWindow mainWindow, string workName, string workType)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                Logger.Log($"ActionHandler: Processing {workType} action with name: {workName}");

                // Refresh work lists
                WorkManager.RefreshWorkLists(mainWindow);

                // Find the work
                var work = WorkManager.FindWork(workName, workType);

                if (work == null)
                {
                    Logger.Log($"ActionHandler: Work '{workName}' not found in {workType} list");
                    // Fallback to generic work animation
                    Logger.Log($"ActionHandler: Falling back to generic {workType} animation");
                    StateManager.TransitionToState(mainWindow, workType == "study" ? "Study" : workType == "play" ? "Play" : "Work", workType);
                    return;
                }

                // Start the work using VPet's StartWork method
                bool success = WorkManager.StartWork(mainWindow, work);

                if (success)
                {
                    Logger.Log($"ActionHandler: Successfully started {workType}: {workName}");
                }
                else
                {
                    Logger.Log($"ActionHandler: Failed to start {workType}: {workName}, falling back to animation");
                    StateManager.TransitionToState(mainWindow, workType == "study" ? "Study" : workType == "play" ? "Play" : "Work", workType);
                }

                await Task.Delay(1000);
            });
        }

        public Task Execute(int value, IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }
        public Task Execute(IMainWindow mainWindow)
        {
            return Execute("idel", mainWindow);
        }
        public int GetAnimationDuration(string animationName) => 1000;
    }
}