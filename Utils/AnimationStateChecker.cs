using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using static VPet_Simulator.Core.GraphInfo;

namespace VPetLLM.Utils
{
    /// <summary>
    /// 动画状态检查器 - 用于判断是否应该阻止VPetLLM执行动作
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
            if (mainWindow?.Main == null)
                return false;

            // 方法1：检查WorkingState（最准确的方式）
            var workingState = mainWindow.Main.State;
            switch (workingState)
            {
                case VPet_Simulator.Core.Main.WorkingState.Work:
                    // 工作状态 - 包括工作、学习、玩耍
                    // 进一步识别具体类型
                    var nowWork = mainWindow.Main.NowWork;
                    if (nowWork != null)
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
            if (displayType != null)
            {
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
            }

            // 其他状态可以被打断
            return false;
        }

        /// <summary>
        /// 获取当前动画状态的描述（用于日志）
        /// </summary>
        public static string GetCurrentAnimationDescription(IMainWindow mainWindow)
        {
            if (mainWindow?.Main?.DisplayType == null)
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
    }
}
