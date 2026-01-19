using VPetLLM.Utils.System;

namespace VPetLLM.Configuration
{
    /// <summary>
    /// TTS协调配置设置，管理VPetLLM与VPetTTS协作的各项参数
    /// 提供配置验证和默认值管理功能
    /// </summary>
    public class TTSCoordinationSettings
    {
        private static TTSCoordinationSettings _instance = null;
        private static readonly object _lockObject = new object();

        /// <summary>
        /// 单例实例
        /// </summary>
        public static TTSCoordinationSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lockObject)
                    {
                        if (_instance == null)
                        {
                            _instance = new TTSCoordinationSettings();
                            _instance.LoadDefaults();
                        }
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// TTS等待超时时间（毫秒），默认15秒，范围5-60秒
        /// </summary>
        public int WaitTimeoutMs { get; set; } = 15000;

        /// <summary>
        /// 状态检测轮询间隔（毫秒），默认200ms，范围50-1000ms
        /// </summary>
        public int PollingIntervalMs { get; set; } = 200;

        /// <summary>
        /// 是否启用严格序列化模式，确保严格的播放顺序
        /// </summary>
        public bool EnableStrictSequencing { get; set; } = true;

        /// <summary>
        /// 是否启用TTS协调调试模式
        /// </summary>
        public bool EnableDebugMode { get; set; } = false;

        /// <summary>
        /// 请求频率监控阈值（毫秒），低于此值认为请求过于频繁
        /// </summary>
        public int FrequencyThresholdMs { get; set; } = 1500;

        /// <summary>
        /// 播放器优雅停止超时时间（毫秒）
        /// </summary>
        public int GracefulStopTimeoutMs { get; set; } = 3000;

        /// <summary>
        /// 是否启用状态监控器
        /// </summary>
        public bool EnableStateMonitor { get; set; } = true;

        /// <summary>
        /// 是否启用操作跟踪器
        /// </summary>
        public bool EnableOperationTracker { get; set; } = true;

        /// <summary>
        /// 操作记录最大保留时间（小时）
        /// </summary>
        public int MaxRecordRetentionHours { get; set; } = 24;

        /// <summary>
        /// 是否启用性能报告
        /// </summary>
        public bool EnablePerformanceReporting { get; set; } = true;

        /// <summary>
        /// 性能报告生成间隔（分钟）
        /// </summary>
        public int PerformanceReportIntervalMinutes { get; set; } = 30;

        /// <summary>
        /// 启用调试日志记录
        /// </summary>
        public bool EnableDebugLogging { get; set; } = false;

        /// <summary>
        /// 调试日志级别 (0=Trace, 1=Debug, 2=Info, 3=Warning, 4=Error, 5=Critical)
        /// </summary>
        public int DebugLogLevel { get; set; } = 2;

        /// <summary>
        /// 加载默认配置
        /// </summary>
        private void LoadDefaults()
        {
            Logger.Log("TTSCoordinationSettings: 加载默认配置");

            // 所有默认值已在属性声明中设置
            Validate();
        }

        /// <summary>
        /// 验证配置参数的有效性
        /// </summary>
        public void Validate()
        {
            var originalWaitTimeout = WaitTimeoutMs;
            var originalPollingInterval = PollingIntervalMs;
            var originalFrequencyThreshold = FrequencyThresholdMs;
            var originalGracefulStopTimeout = GracefulStopTimeoutMs;
            var originalMaxRetention = MaxRecordRetentionHours;
            var originalReportInterval = PerformanceReportIntervalMinutes;

            // 验证并修正超时时间（5秒到60秒）
            WaitTimeoutMs = Math.Max(5000, Math.Min(60000, WaitTimeoutMs));

            // 验证并修正轮询间隔
            PollingIntervalMs = Math.Max(50, Math.Min(1000, PollingIntervalMs));

            // 验证并修正频率阈值
            FrequencyThresholdMs = Math.Max(500, FrequencyThresholdMs);

            // 验证并修正优雅停止超时
            GracefulStopTimeoutMs = Math.Max(1000, Math.Min(10000, GracefulStopTimeoutMs));

            // 验证并修正记录保留时间
            MaxRecordRetentionHours = Math.Max(1, Math.Min(168, MaxRecordRetentionHours)); // 1小时到1周

            // 验证并修正报告间隔
            PerformanceReportIntervalMinutes = Math.Max(5, Math.Min(1440, PerformanceReportIntervalMinutes)); // 5分钟到1天

            // 记录修正的配置项
            if (WaitTimeoutMs != originalWaitTimeout)
            {
                Logger.Log($"TTSCoordinationSettings: 等待超时时间已修正: {originalWaitTimeout} -> {WaitTimeoutMs}ms");
            }

            if (PollingIntervalMs != originalPollingInterval)
            {
                Logger.Log($"TTSCoordinationSettings: 轮询间隔已修正: {originalPollingInterval} -> {PollingIntervalMs}ms");
            }

            if (FrequencyThresholdMs != originalFrequencyThreshold)
            {
                Logger.Log($"TTSCoordinationSettings: 频率阈值已修正: {originalFrequencyThreshold} -> {FrequencyThresholdMs}ms");
            }

            if (GracefulStopTimeoutMs != originalGracefulStopTimeout)
            {
                Logger.Log($"TTSCoordinationSettings: 优雅停止超时已修正: {originalGracefulStopTimeout} -> {GracefulStopTimeoutMs}ms");
            }

            if (MaxRecordRetentionHours != originalMaxRetention)
            {
                Logger.Log($"TTSCoordinationSettings: 记录保留时间已修正: {originalMaxRetention} -> {MaxRecordRetentionHours}小时");
            }

            if (PerformanceReportIntervalMinutes != originalReportInterval)
            {
                Logger.Log($"TTSCoordinationSettings: 报告间隔已修正: {originalReportInterval} -> {PerformanceReportIntervalMinutes}分钟");
            }
        }

        /// <summary>
        /// 更新配置
        /// </summary>
        /// <param name="action">配置更新操作</param>
        public void UpdateSettings(Action<TTSCoordinationSettings> action)
        {
            lock (_lockObject)
            {
                Logger.Log("TTSCoordinationSettings: 开始更新配置");

                try
                {
                    action?.Invoke(this);
                    Validate();

                    Logger.Log("TTSCoordinationSettings: 配置更新完成");

                    // 触发配置变更事件
                    OnSettingsChanged();
                }
                catch (Exception ex)
                {
                    Logger.Log($"TTSCoordinationSettings: 配置更新失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 重置为默认配置
        /// </summary>
        public void ResetToDefaults()
        {
            lock (_lockObject)
            {
                Logger.Log("TTSCoordinationSettings: 重置为默认配置");

                WaitTimeoutMs = 15000;
                PollingIntervalMs = 200;
                EnableStrictSequencing = true;
                EnableDebugMode = false;
                FrequencyThresholdMs = 1500;
                GracefulStopTimeoutMs = 3000;
                EnableStateMonitor = true;
                EnableOperationTracker = true;
                MaxRecordRetentionHours = 24;
                EnablePerformanceReporting = true;
                PerformanceReportIntervalMinutes = 30;
                EnableDebugLogging = false;
                DebugLogLevel = 2;

                Validate();
                OnSettingsChanged();
            }
        }

        /// <summary>
        /// 获取配置摘要
        /// </summary>
        /// <returns>配置摘要字符串</returns>
        public string GetSummary()
        {
            return $"TTS协调配置 - 等待超时: {WaitTimeoutMs}ms, 轮询间隔: {PollingIntervalMs}ms, " +
                   $"严格序列化: {EnableStrictSequencing}, 调试模式: {EnableDebugMode}, " +
                   $"频率阈值: {FrequencyThresholdMs}ms, 状态监控: {EnableStateMonitor}";
        }

        /// <summary>
        /// 配置变更事件
        /// </summary>
        public event EventHandler SettingsChanged;

        /// <summary>
        /// 触发配置变更事件
        /// </summary>
        protected virtual void OnSettingsChanged()
        {
            try
            {
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSCoordinationSettings: 配置变更事件处理异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查配置是否有效
        /// </summary>
        /// <returns>配置有效性检查结果</returns>
        public ValidationResult ValidateConfiguration()
        {
            var result = new ValidationResult { IsValid = true };

            // 检查关键配置项
            if (WaitTimeoutMs < 5000 || WaitTimeoutMs > 60000)
            {
                result.IsValid = false;
                result.Errors.Add($"等待超时时间无效: {WaitTimeoutMs}ms (应在5000-60000ms范围内)");
            }

            if (PollingIntervalMs < 50 || PollingIntervalMs > 1000)
            {
                result.IsValid = false;
                result.Errors.Add($"轮询间隔无效: {PollingIntervalMs}ms (应在50-1000ms范围内)");
            }

            if (FrequencyThresholdMs < 500)
            {
                result.IsValid = false;
                result.Errors.Add($"频率阈值过小: {FrequencyThresholdMs}ms (应不小于500ms)");
            }

            if (GracefulStopTimeoutMs < 1000 || GracefulStopTimeoutMs > 10000)
            {
                result.IsValid = false;
                result.Errors.Add($"优雅停止超时无效: {GracefulStopTimeoutMs}ms (应在1000-10000ms范围内)");
            }

            return result;
        }

        /// <summary>
        /// 配置验证结果
        /// </summary>
        public class ValidationResult
        {
            public bool IsValid { get; set; }
            public System.Collections.Generic.List<string> Errors { get; set; } = new System.Collections.Generic.List<string>();

            public override string ToString()
            {
                if (IsValid)
                {
                    return "配置验证通过";
                }
                else
                {
                    return $"配置验证失败: {string.Join("; ", Errors)}";
                }
            }
        }
    }
}