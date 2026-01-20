using VPet_Simulator.Windows.Interface;
using static VPet_Simulator.Core.GraphInfo;

namespace VPetLLM.Utils.UI
{
    /// <summary>
    /// 动画状态检查器 - 用于判断是否应该阻止VPetLLM执行动作
    /// 与StateManager集成，确保状态转换不会中断重要动画
    /// </summary>
    public static class AnimationStateChecker
    {
        /// <summary>
        /// 检查VPet是否正在执行重要动画，如果是则不应该被VPetLLM打断
        /// </summary>
        /// <param name="mainWindow">主窗口</param>
        /// <returns>true表示正在执行重要动画，应该阻止VPetLLM动作</returns>
        public static bool IsPlayingImportantAnimation(IMainWindow mainWindow)
        {
            if (mainWindow?.Main is null)
                return false;

            // 方法1：检查WorkingState（最准确的方式）
            var workingState = mainWindow.Main.State;
            switch (workingState)
            {
                case VPet_Simulator.Core.Main.WorkingState.Work:
                    // 工作状态 - 包括工作、学习、玩耍
                    // 进一步识别具体类型
                    var nowWork = mainWindow.Main.NowWork;
                    if (nowWork is not null)
                    {
                        switch (nowWork.Type)
                        {
                            case VPet_Simulator.Core.GraphHelper.Work.WorkType.Work:
                                Logger.Log($"AnimationStateChecker: VPet正在工作 (Work)，阻止VPetLLM动作执行");
                                break;
                            case VPet_Simulator.Core.GraphHelper.Work.WorkType.Study:
                                Logger.Log($"AnimationStateChecker: VPet正在学习 (Study)，阻止VPetLLM动作执行");
                                break;
                            case VPet_Simulator.Core.GraphHelper.Work.WorkType.Play:
                                Logger.Log($"AnimationStateChecker: VPet正在玩耍 (Play)，阻止VPetLLM动作执行");
                                break;
                        }
                    }
                    else
                    {
                        Logger.Log($"AnimationStateChecker: VPet正在工作 (WorkingState.Work)，阻止VPetLLM动作执行");
                    }
                    return true;

                case VPet_Simulator.Core.Main.WorkingState.Sleep:
                    // 睡觉状态 - 不应该被打断
                    Logger.Log($"AnimationStateChecker: VPet正在睡觉 (Sleep)，阻止VPetLLM动作执行");
                    return true;

                case VPet_Simulator.Core.Main.WorkingState.Travel:
                    // 旅游状态 - 不应该被打断
                    Logger.Log($"AnimationStateChecker: VPet正在旅游 (Travel)，阻止VPetLLM动作执行");
                    return true;
            }

            // 方法2：检查DisplayType（辅助检查特殊动画）
            var displayType = mainWindow.Main.DisplayType;
            if (displayType is not null)
            {
                // 首先检查是否是触摸类型的动画（Touch类型）
                if (displayType.Type == GraphType.Touch_Head ||
                    displayType.Type == GraphType.Touch_Body)
                {
                    Logger.Log($"AnimationStateChecker: VPet正在被触摸 ({displayType.Type})，阻止VPetLLM思考动画和气泡");
                    return true;
                }

                switch (displayType.Type)
                {
                    case GraphType.StartUP:
                        // 开机动画 - 不应该被打断
                        Logger.Log($"AnimationStateChecker: VPet正在开机 (StartUP)，阻止VPetLLM动作执行");
                        return true;

                    case GraphType.Shutdown:
                        // 关机动画 - 不应该被打断
                        Logger.Log($"AnimationStateChecker: VPet正在关机 (Shutdown)，阻止VPetLLM动作执行");
                        return true;

                    case GraphType.Raised_Dynamic:
                    case GraphType.Raised_Static:
                        // 被提起动画 - 不应该被打断
                        Logger.Log($"AnimationStateChecker: VPet正在被提起 ({displayType.Type})，阻止VPetLLM动作执行");
                        return true;

                    case GraphType.Switch_Up:
                    case GraphType.Switch_Down:
                    case GraphType.Switch_Thirsty:
                    case GraphType.Switch_Hunger:
                        // 状态切换动画 - 不应该被打断
                        Logger.Log($"AnimationStateChecker: VPet正在切换状态 ({displayType.Type})，阻止VPetLLM动作执行");
                        return true;
                }

                // 方法3：检查用户交互动画（捏脸、摸头、摸身体等）
                // 这些动画名称通常包含特定关键字
                if (displayType.Name is not null)
                {
                    var animName = displayType.Name.ToLower();

                    // 捏脸动画
                    if (animName.Contains("pinch"))
                    {
                        Logger.Log($"AnimationStateChecker: VPet正在被捏脸 (pinch)，阻止VPetLLM思考动画和气泡");
                        return true;
                    }

                    // 摸头动画 - 扩展检测关键字
                    if (animName.Contains("touch") && animName.Contains("head"))
                    {
                        Logger.Log($"AnimationStateChecker: VPet正在被摸头 ({animName})，阻止VPetLLM思考动画和气泡");
                        return true;
                    }

                    // 摸身体动画 - 扩展检测关键字
                    if (animName.Contains("touch") && animName.Contains("body"))
                    {
                        Logger.Log($"AnimationStateChecker: VPet正在被摸身体 ({animName})，阻止VPetLLM思考动画和气泡");
                        return true;
                    }

                    // 通用触摸检测 - 任何包含"touch"的动画都视为用户交互
                    if (animName.Contains("touch"))
                    {
                        Logger.Log($"AnimationStateChecker: VPet正在被触摸 ({animName})，阻止VPetLLM思考动画和气泡");
                        return true;
                    }
                }
            }

            // 其他状态可以被打断
            return false;
        }

        /// <summary>
        /// 获取当前动画状态的描述（用于日志）
        /// </summary>
        public static string GetCurrentAnimationDescription(IMainWindow mainWindow)
        {
            if (mainWindow?.Main?.DisplayType is null)
                return "Unknown";

            var displayType = mainWindow.Main.DisplayType;
            return $"{displayType.Type} ({displayType.Animat}) - {displayType.Name}";
        }

        /// <summary>
        /// Say动作只是显示气泡，不影响动画，所以在所有情况下都允许执行
        /// 此方法保留用于兼容性，始终返回true
        /// </summary>
        public static bool CanExecuteSayAction(IMainWindow mainWindow)
        {
            // Say动作（显示气泡）不影响动画，始终允许
            return true;
        }

        /// <summary>
        /// 检查是否可以执行状态转换
        /// 确保重要动画不会被状态转换中断
        /// </summary>
        /// <param name="mainWindow">主窗口</param>
        /// <param name="targetState">目标状态</param>
        /// <param name="actionName">触发状态转换的动作名称</param>
        /// <returns>true表示可以执行状态转换，false表示应该延迟或取消</returns>
        public static bool CanExecuteStateTransition(IMainWindow mainWindow, object targetState, string actionName)
        {
            if (mainWindow?.Main is null)
            {
                Logger.Log("AnimationStateChecker: mainWindow is null, allowing state transition");
                return true;
            }

            // 检查是否正在执行重要动画
            if (IsPlayingImportantAnimation(mainWindow))
            {
                Logger.Log($"AnimationStateChecker: Important animation is playing, state transition to {targetState} (action: {actionName}) should be queued or delayed");
                // 注意：我们返回true因为StateManager的队列机制会处理延迟
                // 如果返回false，调用者需要自己处理重试逻辑
                return true;
            }

            // 获取当前状态
            try
            {
                var stateProperty = mainWindow.Main.GetType().GetProperty("State");
                if (stateProperty is not null)
                {
                    var currentState = stateProperty.GetValue(mainWindow.Main);
                    string currentStateName = currentState?.ToString() ?? "Unknown";
                    string targetStateName = targetState?.ToString() ?? "Unknown";

                    // 检查是否是相同状态（幂等性检查）
                    if (currentStateName.Equals(targetStateName, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Log($"AnimationStateChecker: Already in state {currentStateName}, state transition is idempotent");
                        return true;
                    }

                    Logger.Log($"AnimationStateChecker: State transition from {currentStateName} to {targetStateName} is allowed");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"AnimationStateChecker: Error checking current state: {ex.Message}");
                // 出错时允许状态转换继续
                return true;
            }

            return true;
        }

        /// <summary>
        /// 检查当前状态是否允许被中断
        /// 某些状态（如Sleep、Work）不应该被随意中断
        /// </summary>
        /// <param name="mainWindow">主窗口</param>
        /// <returns>true表示当前状态可以被中断，false表示不应该中断</returns>
        public static bool CanInterruptCurrentState(IMainWindow mainWindow)
        {
            if (mainWindow?.Main is null)
                return true;

            try
            {
                var stateProperty = mainWindow.Main.GetType().GetProperty("State");
                if (stateProperty is null)
                    return true;

                var currentState = stateProperty.GetValue(mainWindow.Main);
                string currentStateName = currentState?.ToString() ?? "Unknown";

                // 检查是否是不应该被中断的状态
                switch (currentStateName)
                {
                    case "Sleep":
                        Logger.Log("AnimationStateChecker: Sleep state should not be interrupted casually");
                        return false;

                    case "Work":
                        Logger.Log("AnimationStateChecker: Work state should not be interrupted casually");
                        return false;

                    case "Travel":
                        Logger.Log("AnimationStateChecker: Travel state should not be interrupted casually");
                        return false;

                    case "Nomal":
                    default:
                        // Normal state and unknown states can be interrupted
                        return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"AnimationStateChecker: Error checking if state can be interrupted: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// 获取当前状态的描述（用于日志和调试）
        /// </summary>
        /// <param name="mainWindow">主窗口</param>
        /// <returns>当前状态的描述字符串</returns>
        public static string GetCurrentStateDescription(IMainWindow mainWindow)
        {
            if (mainWindow?.Main is null)
                return "Unknown (mainWindow is null)";

            try
            {
                var stateProperty = mainWindow.Main.GetType().GetProperty("State");
                if (stateProperty is null)
                    return "Unknown (State property not found)";

                var currentState = stateProperty.GetValue(mainWindow.Main);
                string stateName = currentState?.ToString() ?? "null";

                var animationDesc = GetCurrentAnimationDescription(mainWindow);
                return $"State: {stateName}, Animation: {animationDesc}";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }
}
