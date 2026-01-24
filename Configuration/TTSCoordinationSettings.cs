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
