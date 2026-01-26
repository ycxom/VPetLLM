namespace VPetLLM.Handlers.UI
{
    /// <summary>
    /// 气泡显示配置
    /// 用于控制气泡显示的各种参数
    /// </summary>
    public class BubbleDisplayConfig
    {
        /// <summary>
        /// 最小显示时间（毫秒）
        /// </summary>
        public int MinDisplayTime { get; set; } = 1000;

        /// <summary>
        /// 每字符显示时间（毫秒）
        /// </summary>
        public int TimePerCharacter { get; set; } = 50;

        /// <summary>
        /// 防抖间隔（毫秒）
        /// </summary>
        public int DebounceInterval { get; set; } = 50;

        /// <summary>
        /// 批量更新最大队列大小
        /// </summary>
        public int MaxBatchSize { get; set; } = 5;

        /// <summary>
        /// 思考动画更新间隔（毫秒）
        /// </summary>
        public int ThinkingAnimationInterval { get; set; } = 450;

        /// <summary>
        /// TTS 同步额外缓冲时间（毫秒）
        /// </summary>
        public int TTSSyncBuffer { get; set; } = 500;

        /// <summary>
        /// 流式传输刷新间隔（毫秒）
        /// </summary>
        public int StreamingFlushInterval { get; set; } = 100;

        /// <summary>
        /// 是否启用批量更新
        /// </summary>
        public bool EnableBatchUpdate { get; set; } = true;

        /// <summary>
        /// 是否启用防抖
        /// </summary>
        public bool EnableDebounce { get; set; } = true;

        /// <summary>
        /// 创建默认配置
        /// </summary>
        public static BubbleDisplayConfig Default => new BubbleDisplayConfig();

        /// <summary>
        /// 创建高性能配置（更短的间隔，更快的响应）
        /// </summary>
        public static BubbleDisplayConfig HighPerformance => new BubbleDisplayConfig
        {
            MinDisplayTime = 800,
            TimePerCharacter = 40,
            DebounceInterval = 30,
            MaxBatchSize = 3,
            ThinkingAnimationInterval = 350,
            TTSSyncBuffer = 300,
            StreamingFlushInterval = 50,
            EnableBatchUpdate = true,
            EnableDebounce = true
        };

        /// <summary>
        /// 创建低延迟配置（禁用批量和防抖）
        /// </summary>
        public static BubbleDisplayConfig LowLatency => new BubbleDisplayConfig
        {
            MinDisplayTime = 500,
            TimePerCharacter = 30,
            DebounceInterval = 0,
            MaxBatchSize = 1,
            ThinkingAnimationInterval = 300,
            TTSSyncBuffer = 200,
            StreamingFlushInterval = 30,
            EnableBatchUpdate = false,
            EnableDebounce = false
        };

        /// <summary>
        /// 根据文本长度计算显示时间
        /// </summary>
        public int CalculateDisplayTime(string text)
        {
            if (string.IsNullOrEmpty(text))
                return MinDisplayTime;

            return Math.Max(MinDisplayTime, text.Length * TimePerCharacter);
        }

        /// <summary>
        /// 基于 VPet 实际打印速度和显示时间计算总等待时间
        /// 
        /// VPet MessageBar 逻辑：
        /// 1. 打印阶段：ShowTimer.Interval = 150ms，每次打印 2-3 个字符（平均 2.5 个）
        ///    打印时间 = (字符数 / 2.5) * 150ms = 字符数 * 60ms
        /// 
        /// 2. 显示阶段：使用简化公式
        ///    显示时间 = 2000ms（固定 2 秒，足够阅读）
        /// 
        /// 3. 额外缓冲：打印时间 * 1（额外添加 1 倍打印时间作为缓冲）
        /// 
        /// 4. 总时间 = 打印时间 * 2 + 显示时间
        /// </summary>
        /// <param name="text">文本内容</param>
        /// <returns>预估显示时间（毫秒），最小 1000ms</returns>
        public static int CalculateActualDisplayTime(string text)
        {
            if (string.IsNullOrEmpty(text)) return 1000;

            // 1. 计算打印时间：字符数 * 60ms
            int printTime = text.Length * 60;

            // 2. 固定显示时间：2 秒
            int displayTime = 2000;

            // 3. 总时间 = 打印时间 * 2 + 显示时间（额外添加 1 倍打印时间）
            int totalTime = printTime * 2 + displayTime;

            // 最小 1000ms，最大 20000ms（避免过长）
            return Math.Max(1000, Math.Min(20000, totalTime));
        }

        /// <summary>
        /// 根据 TTS 音频时长调整显示时间
        /// </summary>
        public int AdjustForTTS(int baseDisplayTime, int audioDuration)
        {
            if (audioDuration <= 0)
                return baseDisplayTime;

            return Math.Max(baseDisplayTime, audioDuration + TTSSyncBuffer);
        }
    }
}
