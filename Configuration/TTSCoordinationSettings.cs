namespace VPetLLM.Configuration
{
    /// <summary>
    /// TTS 协调配置
    /// </summary>
    public class TTSCoordinationSettings
    {
        private static TTSCoordinationSettings? _instance;
        private static readonly object _lock = new object();

        public static TTSCoordinationSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new TTSCoordinationSettings();
                        }
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// 是否启用协调功能
        /// </summary>
        public bool EnableCoordination { get; set; } = true;

        /// <summary>
        /// 检测超时时间（毫秒）
        /// </summary>
        public int DetectionTimeoutMs { get; set; } = 1000;

        /// <summary>
        /// 是否启用独占会话模式
        /// </summary>
        public bool EnableExclusiveMode { get; set; } = true;

        /// <summary>
        /// 会话超时时间（毫秒），默认 60 秒
        /// </summary>
        public int ExclusiveModeTimeoutMs { get; set; } = 60000;

        /// <summary>
        /// 是否自动清理超时会话
        /// </summary>
        public bool AutoExitOnTimeout { get; set; } = true;

        /// <summary>
        /// 是否启用请求 ID 验证
        /// </summary>
        public bool EnableRequestIdValidation { get; set; } = true;

        /// <summary>
        /// 是否启用预加载功能
        /// </summary>
        public bool EnablePreload { get; set; } = true;

        /// <summary>
        /// 预加载超时时间（毫秒）
        /// </summary>
        public int PreloadTimeoutMs { get; set; } = 30000;

        /// <summary>
        /// 状态监控轮询间隔（毫秒）
        /// </summary>
        public int PollingIntervalMs { get; set; } = 200;

        /// <summary>
        /// 请求完成检查间隔（毫秒）
        /// </summary>
        public int RequestCompleteCheckIntervalMs { get; set; } = 200;

        /// <summary>
        /// 请求完成等待超时（毫秒）
        /// </summary>
        public int RequestCompleteTimeoutMs { get; set; } = 60000;

        /// <summary>
        /// 是否让气泡等语音真正起播后再显示。
        ///
        /// 关掉就退回旧时序（提交 TTS 后立刻出气泡），字会早于声音出现；
        /// 仅在排查问题或对方插件行为异常时才需要关。
        /// </summary>
        public bool EnablePlaybackStartSync { get; set; } = true;

        /// <summary>
        /// 等待语音起播的最长时间（毫秒）。
        ///
        /// 超过这个时间还没出声，就认为这句合成失败或被跳过了，直接把气泡放出来 ——
        /// 宁可字先出来，也不能让桌宠既不出声又不出字地干等着。
        /// 取值要盖住一次冷合成的网络往返（预加载未命中时通常 1-4 秒）。
        /// </summary>
        public int PlaybackStartTimeoutMs { get; set; } = 8000;

        /// <summary>
        /// 是否启用调试日志
        /// </summary>
        public bool EnableDebugLogging { get; set; } = false;

        /// <summary>
        /// 调试日志级别
        /// </summary>
        public int DebugLogLevel { get; set; } = 2; // 默认为 Info (LogLevel.Info = 2)

        /// <summary>
        /// 是否启用状态监控器
        /// </summary>
        public bool EnableStateMonitor { get; set; } = true;

        /// <summary>
        /// 等待超时时间（毫秒）
        /// </summary>
        public int WaitTimeoutMs { get; set; } = 60000;

        /// <summary>
        /// 最大记录保留时间（小时）
        /// </summary>
        public int MaxRecordRetentionHours { get; set; } = 24;
    }
}
