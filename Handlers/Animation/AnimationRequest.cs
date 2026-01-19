using static VPet_Simulator.Core.GraphInfo;

namespace VPetLLM.Handlers.Animation
{
    /// <summary>
    /// 动画请求类型
    /// </summary>
    public enum AnimationRequestType
    {
        /// <summary>显示动画</summary>
        Display,
        /// <summary>状态变更</summary>
        StateChange,
        /// <summary>过渡动画</summary>
        Transition,
        /// <summary>停止当前动画</summary>
        Stop
    }

    /// <summary>
    /// 动画请求优先级
    /// </summary>
    public enum AnimationPriority
    {
        /// <summary>可被丢弃</summary>
        Low = 0,
        /// <summary>正常优先级</summary>
        Normal = 1,
        /// <summary>高优先级</summary>
        High = 2,
        /// <summary>不可丢弃</summary>
        Critical = 3
    }

    /// <summary>
    /// 动画请求数据结构
    /// 封装所有动画请求的元数据，用于队列管理和日志记录
    /// </summary>
    public class AnimationRequest
    {
        /// <summary>唯一请求ID</summary>
        public string Id { get; } = Guid.NewGuid().ToString();

        /// <summary>请求时间戳</summary>
        public DateTime Timestamp { get; } = DateTime.Now;

        /// <summary>请求来源 (ActionHandler, SayHandler, StateManager 等)</summary>
        public string Source { get; set; }

        /// <summary>请求类型</summary>
        public AnimationRequestType Type { get; set; }

        /// <summary>请求优先级</summary>
        public AnimationPriority Priority { get; set; } = AnimationPriority.Normal;

        /// <summary>目标图形类型 (可选)</summary>
        public GraphType? TargetGraphType { get; set; }

        /// <summary>动画名称</summary>
        public string AnimationName { get; set; }

        /// <summary>动画动作类型 (A_Start, B_Loop, C_End, Single)</summary>
        public AnimatType AnimatType { get; set; } = AnimatType.Single;

        /// <summary>动画结束后的回调</summary>
        public Action EndAction { get; set; }

        /// <summary>是否允许与其他请求合并</summary>
        public bool AllowCoalesce { get; set; } = true;

        /// <summary>请求超时时间 (毫秒)</summary>
        public int TimeoutMs { get; set; } = 5000;

        /// <summary>目标状态 (用于状态变更请求)</summary>
        public object TargetState { get; set; }

        /// <summary>是否强制执行 (忽略状态检查)</summary>
        public bool Force { get; set; } = false;

        /// <summary>
        /// 创建显示动画请求
        /// </summary>
        public static AnimationRequest CreateDisplayRequest(
            string source,
            string animationName,
            AnimatType animatType = AnimatType.Single,
            Action endAction = null,
            AnimationPriority priority = AnimationPriority.Normal)
        {
            return new AnimationRequest
            {
                Source = source,
                Type = AnimationRequestType.Display,
                AnimationName = animationName,
                AnimatType = animatType,
                EndAction = endAction,
                Priority = priority
            };
        }

        /// <summary>
        /// 创建状态变更请求
        /// </summary>
        public static AnimationRequest CreateStateChangeRequest(
            string source,
            object targetState,
            AnimationPriority priority = AnimationPriority.High)
        {
            return new AnimationRequest
            {
                Source = source,
                Type = AnimationRequestType.StateChange,
                TargetState = targetState,
                Priority = priority,
                AllowCoalesce = false
            };
        }

        /// <summary>
        /// 创建停止请求
        /// </summary>
        public static AnimationRequest CreateStopRequest(string source)
        {
            return new AnimationRequest
            {
                Source = source,
                Type = AnimationRequestType.Stop,
                Priority = AnimationPriority.Critical,
                AllowCoalesce = false
            };
        }

        public override string ToString()
        {
            return $"[{Id.Substring(0, 8)}] {Type} from {Source} @ {Timestamp:HH:mm:ss.fff} (Priority: {Priority})";
        }
    }
}
