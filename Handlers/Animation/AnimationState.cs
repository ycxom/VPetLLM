using System;
using VPet_Simulator.Core;

namespace VPetLLM.Handlers.Animation
{
    /// <summary>
    /// 动画状态数据结构
    /// 跟踪当前动画的状态信息
    /// </summary>
    public class AnimationState
    {
        /// <summary>当前显示的动画信息</summary>
        public GraphInfo CurrentDisplay { get; set; }

        /// <summary>VPet 工作状态</summary>
        public Main.WorkingState WorkingState { get; set; }

        /// <summary>是否正在播放动画</summary>
        public bool IsAnimating { get; set; }

        /// <summary>上次动画切换时间</summary>
        public DateTime LastSwitchTime { get; set; }

        /// <summary>上次动画请求来源</summary>
        public string LastAnimationSource { get; set; }

        /// <summary>用户是否正在交互 (触摸、拖拽等)</summary>
        public bool IsUserInteracting { get; set; }

        /// <summary>当前动画是否为循环动画</summary>
        public bool IsLooping { get; set; }

        /// <summary>当前动画开始时间</summary>
        public DateTime AnimationStartTime { get; set; }

        /// <summary>
        /// 检查是否处于重要状态 (不应被中断)
        /// </summary>
        public bool IsInImportantState()
        {
            return WorkingState == Main.WorkingState.Work ||
                   WorkingState == Main.WorkingState.Sleep ||
                   WorkingState == Main.WorkingState.Travel ||
                   IsUserInteracting;
        }

        /// <summary>
        /// 检查是否处于过渡动画中
        /// </summary>
        public bool IsInTransition()
        {
            if (CurrentDisplay == null) return false;

            var type = CurrentDisplay.Type;
            return type == GraphInfo.GraphType.Switch_Up ||
                   type == GraphInfo.GraphType.Switch_Down ||
                   type == GraphInfo.GraphType.Switch_Thirsty ||
                   type == GraphInfo.GraphType.Switch_Hunger;
        }

        /// <summary>
        /// 检查是否处于触摸动画中
        /// </summary>
        public bool IsInTouchAnimation()
        {
            if (CurrentDisplay == null) return false;

            var type = CurrentDisplay.Type;
            return type == GraphInfo.GraphType.Touch_Head ||
                   type == GraphInfo.GraphType.Touch_Body;
        }

        /// <summary>
        /// 获取自上次切换以来的毫秒数
        /// </summary>
        public double GetMillisecondsSinceLastSwitch()
        {
            return (DateTime.Now - LastSwitchTime).TotalMilliseconds;
        }

        /// <summary>
        /// 更新状态
        /// </summary>
        public void Update(GraphInfo display, Main.WorkingState workingState, string source)
        {
            CurrentDisplay = display;
            WorkingState = workingState;
            LastSwitchTime = DateTime.Now;
            LastAnimationSource = source;
            IsAnimating = true;
            AnimationStartTime = DateTime.Now;
        }

        /// <summary>
        /// 标记动画完成
        /// </summary>
        public void MarkCompleted()
        {
            IsAnimating = false;
        }

        public override string ToString()
        {
            var displayInfo = CurrentDisplay != null 
                ? $"{CurrentDisplay.Type} ({CurrentDisplay.Animat})" 
                : "None";
            return $"State: {WorkingState}, Display: {displayInfo}, Animating: {IsAnimating}, UserInteracting: {IsUserInteracting}";
        }
    }
}
