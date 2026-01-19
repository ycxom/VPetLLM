using VPetLLM.Configuration;

namespace VPetLLM.Infrastructure.Configuration.Configurations
{
    /// <summary>
    /// 应用程序配置
    /// </summary>
    public class ApplicationConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "Application";

        /// <summary>
        /// 语言设置
        /// </summary>
        public string Language { get; set; } = "zh-hans";

        /// <summary>
        /// 提示语言
        /// </summary>
        public string PromptLanguage { get; set; } = "zh";

        /// <summary>
        /// 日志自动滚动
        /// </summary>
        public bool LogAutoScroll { get; set; } = true;

        /// <summary>
        /// 最大日志数量
        /// </summary>
        public int MaxLogCount { get; set; } = 1000;

        /// <summary>
        /// 启用动作
        /// </summary>
        public bool EnableAction { get; set; } = true;

        /// <summary>
        /// 启用购买
        /// </summary>
        public bool EnableBuy { get; set; } = true;

        /// <summary>
        /// 启用状态
        /// </summary>
        public bool EnableState { get; set; } = true;

        /// <summary>
        /// 启用扩展状态
        /// </summary>
        public bool EnableExtendedState { get; set; } = false;

        /// <summary>
        /// 启用动作执行
        /// </summary>
        public bool EnableActionExecution { get; set; } = true;

        /// <summary>
        /// 说话时间倍数
        /// </summary>
        public int SayTimeMultiplier { get; set; } = 200;

        /// <summary>
        /// 最小说话时间
        /// </summary>
        public int SayTimeMin { get; set; } = 2000;

        /// <summary>
        /// 启用移动
        /// </summary>
        public bool EnableMove { get; set; } = true;

        /// <summary>
        /// 启用时间
        /// </summary>
        public bool EnableTime { get; set; } = true;

        /// <summary>
        /// 启用插件
        /// </summary>
        public bool EnablePlugin { get; set; } = true;

        /// <summary>
        /// 工具设置列表
        /// </summary>
        public List<ToolConfiguration> Tools { get; set; } = new();

        /// <summary>
        /// 显示卸载警告
        /// </summary>
        public bool ShowUninstallWarning { get; set; } = true;

        /// <summary>
        /// 启用购买反馈
        /// </summary>
        public bool EnableBuyFeedback { get; set; } = true;

        /// <summary>
        /// 启用实时模式
        /// </summary>
        public bool EnableLiveMode { get; set; } = false;

        /// <summary>
        /// 限制状态变更
        /// </summary>
        public bool LimitStateChanges { get; set; } = true;

        /// <summary>
        /// 启用VPet设置控制
        /// </summary>
        public bool EnableVPetSettingsControl { get; set; } = false;

        /// <summary>
        /// 启用流式传输批处理
        /// </summary>
        public bool EnableStreamingBatch { get; set; } = true;

        /// <summary>
        /// 流式传输批处理窗口时间(毫秒)
        /// </summary>
        public int StreamingBatchWindowMs { get; set; } = 100;

        /// <summary>
        /// 启用媒体播放
        /// </summary>
        public bool EnableMediaPlayback { get; set; } = true;

        /// <summary>
        /// 速率限制设置
        /// </summary>
        public RateLimiterConfiguration RateLimiter { get; set; } = new();

        /// <summary>
        /// 记录设置
        /// </summary>
        public RecordConfiguration Records { get; set; } = new();

        /// <summary>
        /// 媒体播放设置
        /// </summary>
        public MediaPlaybackConfiguration MediaPlayback { get; set; } = new();

        /// <summary>
        /// 插件商店设置
        /// </summary>
        public PluginStoreConfiguration PluginStore { get; set; } = new();

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(Language))
            {
                result.AddError("语言设置不能为空");
            }

            if (string.IsNullOrWhiteSpace(PromptLanguage))
            {
                result.AddError("提示语言不能为空");
            }

            if (MaxLogCount <= 0)
            {
                result.AddError("最大日志数量必须大于0");
            }

            if (SayTimeMultiplier <= 0)
            {
                result.AddError("说话时间倍数必须大于0");
            }

            if (SayTimeMin < 0)
            {
                result.AddError("最小说话时间不能为负数");
            }

            if (StreamingBatchWindowMs <= 0)
            {
                result.AddError("流式传输批处理窗口时间必须大于0");
            }

            // 验证子配置
            var rateLimiterValidation = RateLimiter?.Validate();
            if (rateLimiterValidation != null && !rateLimiterValidation.IsValid)
            {
                result.Errors.AddRange(rateLimiterValidation.Errors);
                result.IsValid = false;
            }

            var recordsValidation = Records?.Validate();
            if (recordsValidation != null && !recordsValidation.IsValid)
            {
                result.Errors.AddRange(recordsValidation.Errors);
                result.IsValid = false;
            }

            var mediaPlaybackValidation = MediaPlayback?.Validate();
            if (mediaPlaybackValidation != null && !mediaPlaybackValidation.IsValid)
            {
                result.Errors.AddRange(mediaPlaybackValidation.Errors);
                result.IsValid = false;
            }

            var pluginStoreValidation = PluginStore?.Validate();
            if (pluginStoreValidation != null && !pluginStoreValidation.IsValid)
            {
                result.Errors.AddRange(pluginStoreValidation.Errors);
                result.IsValid = false;
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new ApplicationConfiguration
            {
                Language = Language,
                PromptLanguage = PromptLanguage,
                LogAutoScroll = LogAutoScroll,
                MaxLogCount = MaxLogCount,
                EnableAction = EnableAction,
                EnableBuy = EnableBuy,
                EnableState = EnableState,
                EnableExtendedState = EnableExtendedState,
                EnableActionExecution = EnableActionExecution,
                SayTimeMultiplier = SayTimeMultiplier,
                SayTimeMin = SayTimeMin,
                EnableMove = EnableMove,
                EnableTime = EnableTime,
                EnablePlugin = EnablePlugin,
                Tools = new List<ToolConfiguration>(Tools),
                ShowUninstallWarning = ShowUninstallWarning,
                EnableBuyFeedback = EnableBuyFeedback,
                EnableLiveMode = EnableLiveMode,
                LimitStateChanges = LimitStateChanges,
                EnableVPetSettingsControl = EnableVPetSettingsControl,
                EnableStreamingBatch = EnableStreamingBatch,
                StreamingBatchWindowMs = StreamingBatchWindowMs,
                EnableMediaPlayback = EnableMediaPlayback,
                RateLimiter = RateLimiter?.Clone() as RateLimiterConfiguration,
                Records = Records?.Clone() as RecordConfiguration,
                MediaPlayback = MediaPlayback?.Clone() as MediaPlaybackConfiguration,
                PluginStore = PluginStore?.Clone() as PluginStoreConfiguration,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is ApplicationConfiguration otherApp)
            {
                Language = otherApp.Language;
                PromptLanguage = otherApp.PromptLanguage;
                LogAutoScroll = otherApp.LogAutoScroll;
                MaxLogCount = otherApp.MaxLogCount;
                EnableAction = otherApp.EnableAction;
                EnableBuy = otherApp.EnableBuy;
                EnableState = otherApp.EnableState;
                EnableExtendedState = otherApp.EnableExtendedState;
                EnableActionExecution = otherApp.EnableActionExecution;
                SayTimeMultiplier = otherApp.SayTimeMultiplier;
                SayTimeMin = otherApp.SayTimeMin;
                EnableMove = otherApp.EnableMove;
                EnableTime = otherApp.EnableTime;
                EnablePlugin = otherApp.EnablePlugin;
                Tools = new List<ToolConfiguration>(otherApp.Tools);
                ShowUninstallWarning = otherApp.ShowUninstallWarning;
                EnableBuyFeedback = otherApp.EnableBuyFeedback;
                EnableLiveMode = otherApp.EnableLiveMode;
                LimitStateChanges = otherApp.LimitStateChanges;
                EnableVPetSettingsControl = otherApp.EnableVPetSettingsControl;
                EnableStreamingBatch = otherApp.EnableStreamingBatch;
                StreamingBatchWindowMs = otherApp.StreamingBatchWindowMs;
                EnableMediaPlayback = otherApp.EnableMediaPlayback;

                RateLimiter?.Merge(otherApp.RateLimiter);
                Records?.Merge(otherApp.Records);
                MediaPlayback?.Merge(otherApp.MediaPlayback);
                PluginStore?.Merge(otherApp.PluginStore);

                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            Language = "zh-hans";
            PromptLanguage = "zh";
            LogAutoScroll = true;
            MaxLogCount = 1000;
            EnableAction = true;
            EnableBuy = true;
            EnableState = true;
            EnableExtendedState = false;
            EnableActionExecution = true;
            SayTimeMultiplier = 200;
            SayTimeMin = 2000;
            EnableMove = true;
            EnableTime = true;
            EnablePlugin = true;
            Tools = new List<ToolConfiguration>();
            ShowUninstallWarning = true;
            EnableBuyFeedback = true;
            EnableLiveMode = false;
            LimitStateChanges = true;
            EnableVPetSettingsControl = false;
            EnableStreamingBatch = true;
            StreamingBatchWindowMs = 100;
            EnableMediaPlayback = true;

            RateLimiter = new RateLimiterConfiguration();
            Records = new RecordConfiguration();
            MediaPlayback = new MediaPlaybackConfiguration();
            PluginStore = new PluginStoreConfiguration();

            MarkAsModified();
        }
    }

    // 子配置类
    public class ToolConfiguration
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsEnabled { get; set; } = true;
    }

    public class RateLimiterConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "Rate Limiter";

        public bool EnableToolRateLimit { get; set; } = true;
        public int ToolMaxCount { get; set; } = 5;
        public int ToolWindowMinutes { get; set; } = 2;
        public bool EnablePluginRateLimit { get; set; } = true;
        public int PluginMaxCount { get; set; } = 5;
        public int PluginWindowMinutes { get; set; } = 2;
        public bool LogRateLimitEvents { get; set; } = true;

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (ToolMaxCount <= 0)
            {
                result.AddError("工具最大调用次数必须大于0");
            }

            if (ToolWindowMinutes <= 0)
            {
                result.AddError("工具时间窗口必须大于0分钟");
            }

            if (PluginMaxCount <= 0)
            {
                result.AddError("插件最大调用次数必须大于0");
            }

            if (PluginWindowMinutes <= 0)
            {
                result.AddError("插件时间窗口必须大于0分钟");
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new RateLimiterConfiguration
            {
                EnableToolRateLimit = EnableToolRateLimit,
                ToolMaxCount = ToolMaxCount,
                ToolWindowMinutes = ToolWindowMinutes,
                EnablePluginRateLimit = EnablePluginRateLimit,
                PluginMaxCount = PluginMaxCount,
                PluginWindowMinutes = PluginWindowMinutes,
                LogRateLimitEvents = LogRateLimitEvents,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is RateLimiterConfiguration otherRateLimit)
            {
                EnableToolRateLimit = otherRateLimit.EnableToolRateLimit;
                ToolMaxCount = otherRateLimit.ToolMaxCount;
                ToolWindowMinutes = otherRateLimit.ToolWindowMinutes;
                EnablePluginRateLimit = otherRateLimit.EnablePluginRateLimit;
                PluginMaxCount = otherRateLimit.PluginMaxCount;
                PluginWindowMinutes = otherRateLimit.PluginWindowMinutes;
                LogRateLimitEvents = otherRateLimit.LogRateLimitEvents;
                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            EnableToolRateLimit = true;
            ToolMaxCount = 5;
            ToolWindowMinutes = 2;
            EnablePluginRateLimit = true;
            PluginMaxCount = 5;
            PluginWindowMinutes = 2;
            LogRateLimitEvents = true;
            MarkAsModified();
        }
    }

    public class RecordConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "Records";

        public bool EnableRecords { get; set; } = true;
        public int MaxRecordsInContext { get; set; } = 20;
        public bool AutoDecrementWeights { get; set; } = true;
        public int MaxRecordContentLength { get; set; } = 500;
        public bool InjectIntoSummary { get; set; } = false;
        public int WeightDecayTurns { get; set; } = 1;
        public int MaxRecordsLimit { get; set; } = 10;

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (MaxRecordsInContext <= 0)
            {
                result.AddError("上下文中最大记录数必须大于0");
            }

            if (MaxRecordContentLength <= 0)
            {
                result.AddError("单条记录最大内容长度必须大于0");
            }

            if (WeightDecayTurns <= 0)
            {
                result.AddError("权重衰减轮次必须大于0");
            }

            if (MaxRecordsLimit <= 0 || MaxRecordsLimit > 100)
            {
                result.AddError("最大记录限制必须在1-100之间");
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new RecordConfiguration
            {
                EnableRecords = EnableRecords,
                MaxRecordsInContext = MaxRecordsInContext,
                AutoDecrementWeights = AutoDecrementWeights,
                MaxRecordContentLength = MaxRecordContentLength,
                InjectIntoSummary = InjectIntoSummary,
                WeightDecayTurns = WeightDecayTurns,
                MaxRecordsLimit = MaxRecordsLimit,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is RecordConfiguration otherRecord)
            {
                EnableRecords = otherRecord.EnableRecords;
                MaxRecordsInContext = otherRecord.MaxRecordsInContext;
                AutoDecrementWeights = otherRecord.AutoDecrementWeights;
                MaxRecordContentLength = otherRecord.MaxRecordContentLength;
                InjectIntoSummary = otherRecord.InjectIntoSummary;
                WeightDecayTurns = otherRecord.WeightDecayTurns;
                MaxRecordsLimit = otherRecord.MaxRecordsLimit;
                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            EnableRecords = true;
            MaxRecordsInContext = 20;
            AutoDecrementWeights = true;
            MaxRecordContentLength = 500;
            InjectIntoSummary = false;
            WeightDecayTurns = 1;
            MaxRecordsLimit = 10;
            MarkAsModified();
        }
    }

    public class MediaPlaybackConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "Media Playback";

        public int DefaultVolume { get; set; } = 100;
        public bool MonitorWindowVisibility { get; set; } = true;
        public int WindowCheckIntervalMs { get; set; } = 1000;
        public string MpvPath { get; set; } = "";

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (DefaultVolume < 0 || DefaultVolume > 100)
            {
                result.AddError("默认音量必须在0-100之间");
            }

            if (WindowCheckIntervalMs <= 0)
            {
                result.AddError("窗口检查间隔必须大于0毫秒");
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new MediaPlaybackConfiguration
            {
                DefaultVolume = DefaultVolume,
                MonitorWindowVisibility = MonitorWindowVisibility,
                WindowCheckIntervalMs = WindowCheckIntervalMs,
                MpvPath = MpvPath,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is MediaPlaybackConfiguration otherMedia)
            {
                DefaultVolume = otherMedia.DefaultVolume;
                MonitorWindowVisibility = otherMedia.MonitorWindowVisibility;
                WindowCheckIntervalMs = otherMedia.WindowCheckIntervalMs;
                MpvPath = otherMedia.MpvPath;
                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            DefaultVolume = 100;
            MonitorWindowVisibility = true;
            WindowCheckIntervalMs = 1000;
            MpvPath = "";
            MarkAsModified();
        }
    }

    public class PluginStoreConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "Plugin Store";

        public bool UseProxy { get; set; } = true;
        public string ProxyUrl { get; set; } = "https://ghfast.top";

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (UseProxy && string.IsNullOrWhiteSpace(ProxyUrl))
            {
                result.AddError("启用代理时，代理URL不能为空");
            }

            if (!string.IsNullOrWhiteSpace(ProxyUrl) && !Uri.TryCreate(ProxyUrl, UriKind.Absolute, out _))
            {
                result.AddError("代理URL格式无效");
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new PluginStoreConfiguration
            {
                UseProxy = UseProxy,
                ProxyUrl = ProxyUrl,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is PluginStoreConfiguration otherStore)
            {
                UseProxy = otherStore.UseProxy;
                ProxyUrl = otherStore.ProxyUrl;
                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            UseProxy = true;
            ProxyUrl = "https://ghfast.top";
            MarkAsModified();
        }
    }
}