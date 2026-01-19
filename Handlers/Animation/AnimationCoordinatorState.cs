namespace VPetLLM.Handlers.Animation
{
    /// <summary>
    /// 动画协调器状态数据结构
    /// 用于监控和诊断协调器的运行状态
    /// </summary>
    public class AnimationCoordinatorState
    {
        /// <summary>当前队列深度</summary>
        public int QueueDepth { get; set; }

        /// <summary>是否正在处理请求</summary>
        public bool IsProcessing { get; set; }

        /// <summary>当前动画状态</summary>
        public AnimationState CurrentAnimation { get; set; }

        /// <summary>闪烁风险等级 (0-100)</summary>
        public int FlickerRiskLevel { get; set; }

        /// <summary>待处理请求的来源列表</summary>
        public List<string> PendingRequestSources { get; set; } = new List<string>();

        /// <summary>最近处理的请求数量 (过去1秒)</summary>
        public int RecentRequestCount { get; set; }

        /// <summary>最近被阻塞的请求数量</summary>
        public int RecentBlockedCount { get; set; }

        /// <summary>最近被合并的请求数量</summary>
        public int RecentCoalescedCount { get; set; }

        /// <summary>协调器是否已初始化</summary>
        public bool IsInitialized { get; set; }

        /// <summary>
        /// 获取状态摘要
        /// </summary>
        public string GetSummary()
        {
            return $"Queue: {QueueDepth}, Processing: {IsProcessing}, FlickerRisk: {FlickerRiskLevel}%, " +
                   $"Recent: {RecentRequestCount} requests, {RecentBlockedCount} blocked, {RecentCoalescedCount} coalesced";
        }

        /// <summary>
        /// 检查是否处于健康状态
        /// </summary>
        public bool IsHealthy()
        {
            return QueueDepth < 5 && FlickerRiskLevel < 50 && IsInitialized;
        }

        public override string ToString()
        {
            return GetSummary();
        }
    }
}
