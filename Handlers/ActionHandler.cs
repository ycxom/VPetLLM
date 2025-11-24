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
            
            // 检查VPet是否正在执行重要动画
            if (AnimationStateChecker.IsPlayingImportantAnimation(mainWindow))
            {
                Logger.Log($"ActionHandler: 当前VPet状态 ({AnimationStateChecker.GetCurrentAnimationDescription(mainWindow)}) 不允许执行动作，已跳过");
                return;
            }
            
            var action = string.IsNullOrEmpty(actionName) ? "idel" : actionName;

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