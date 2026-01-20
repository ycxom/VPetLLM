using Newtonsoft.Json;

namespace VPetLLM.Handlers.State
{
    /// <summary>
    /// 触摸反馈设置
    /// </summary>
    public class TouchFeedbackSettings
    {
        /// <summary>
        /// 是否启用触摸反馈
        /// </summary>
        [JsonProperty("enableTouchFeedback")]
        public bool EnableTouchFeedback { get; set; } = true;

        /// <summary>
        /// 触摸反馈冷却时间（毫秒）
        /// </summary>
        [JsonProperty("touchCooldown")]
        public int TouchCooldown { get; set; } = 3000;
    }
}