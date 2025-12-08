using System;
using System.Threading.Tasks;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;
using static VPet_Simulator.Core.GraphInfo;

namespace VPetLLM.Handlers.Animation
{
    /// <summary>
    /// 动画辅助类
    /// 提供简化的 API 供现有处理器使用
    /// </summary>
    public static class AnimationHelper
    {
        private static bool _initialized = false;

        /// <summary>
        /// 初始化动画协调器
        /// </summary>
        public static void Initialize(IMainWindow mainWindow)
        {
            if (_initialized) return;

            try
            {
                AnimationCoordinator.Instance.Initialize(mainWindow);
                _initialized = true;
                Logger.Log("AnimationHelper: Initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"AnimationHelper: Initialization failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查是否已初始化
        /// </summary>
        public static bool IsInitialized => _initialized;

        /// <summary>
        /// 请求显示动画
        /// </summary>
        public static async Task<bool> RequestDisplayAsync(
            string source,
            string animationName,
            AnimatType animatType = AnimatType.Single,
            Action endAction = null,
            AnimationPriority priority = AnimationPriority.Normal)
        {
            if (!_initialized)
            {
                Logger.Log("AnimationHelper: Not initialized, falling back to direct call");
                return false;
            }

            var request = AnimationRequest.CreateDisplayRequest(
                source, animationName, animatType, endAction, priority);

            return await AnimationCoordinator.Instance.RequestAnimationAsync(request);
        }

        /// <summary>
        /// 请求显示动画 (按类型)
        /// </summary>
        public static async Task<bool> RequestDisplayAsync(
            string source,
            GraphType graphType,
            AnimatType animatType = AnimatType.Single,
            Action endAction = null,
            AnimationPriority priority = AnimationPriority.Normal)
        {
            if (!_initialized)
            {
                Logger.Log("AnimationHelper: Not initialized, falling back to direct call");
                return false;
            }

            var request = new AnimationRequest
            {
                Source = source,
                Type = AnimationRequestType.Display,
                TargetGraphType = graphType,
                AnimatType = animatType,
                EndAction = endAction,
                Priority = priority
            };

            return await AnimationCoordinator.Instance.RequestAnimationAsync(request);
        }

        /// <summary>
        /// 请求状态变更
        /// </summary>
        public static async Task<bool> RequestStateChangeAsync(
            string source,
            object targetState,
            AnimationPriority priority = AnimationPriority.High)
        {
            if (!_initialized)
            {
                Logger.Log("AnimationHelper: Not initialized, falling back to direct call");
                return false;
            }

            var request = AnimationRequest.CreateStateChangeRequest(source, targetState, priority);
            return await AnimationCoordinator.Instance.RequestAnimationAsync(request);
        }

        /// <summary>
        /// 请求停止当前动画
        /// </summary>
        public static async Task<bool> RequestStopAsync(string source)
        {
            if (!_initialized)
            {
                Logger.Log("AnimationHelper: Not initialized, falling back to direct call");
                return false;
            }

            var request = AnimationRequest.CreateStopRequest(source);
            return await AnimationCoordinator.Instance.RequestAnimationAsync(request);
        }

        /// <summary>
        /// 设置用户交互状态
        /// </summary>
        public static void SetUserInteracting(bool isInteracting)
        {
            if (!_initialized) return;
            AnimationCoordinator.Instance.SetUserInteracting(isInteracting);
        }

        /// <summary>
        /// 取消指定来源的待处理请求
        /// </summary>
        public static void CancelPendingRequests(string source)
        {
            if (!_initialized) return;
            AnimationCoordinator.Instance.CancelPendingRequests(source);
        }

        /// <summary>
        /// 检查是否可以执行动画
        /// </summary>
        public static bool CanExecuteAnimation(IMainWindow mainWindow)
        {
            if (!_initialized) return true;
            return !AnimationCoordinator.Instance.GetState().CurrentAnimation?.IsInImportantState() ?? true;
        }

        /// <summary>
        /// 获取当前状态
        /// </summary>
        public static AnimationCoordinatorState GetState()
        {
            if (!_initialized) return new AnimationCoordinatorState { IsInitialized = false };
            return AnimationCoordinator.Instance.GetState();
        }

        /// <summary>
        /// 检查是否存在闪烁风险
        /// </summary>
        public static bool IsFlickerRisk()
        {
            if (!_initialized) return false;
            return AnimationCoordinator.Instance.GetState().FlickerRiskLevel > 50;
        }

        /// <summary>
        /// 获取推荐延迟
        /// </summary>
        public static int GetRecommendedDelay()
        {
            if (!_initialized) return 0;
            var state = AnimationCoordinator.Instance.GetState();
            return state.FlickerRiskLevel > 0 ? Math.Max(50, state.FlickerRiskLevel) : 0;
        }
    }
}
