namespace VPetLLM.Models
{
    /// <summary>
    /// TTS 请求对象
    /// 包含文本和配置信息
    /// </summary>
    public class TTSRequest
    {
        /// <summary>
        /// 要转换的文本
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// TTS 设置
        /// </summary>
        public TTSRequestSettings Settings { get; set; }

        /// <summary>
        /// 请求唯一标识符
        /// </summary>
        public string RequestId { get; set; }

        /// <summary>
        /// 请求时间戳
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// 重试次数
        /// </summary>
        public int RetryCount { get; set; }

        /// <summary>
        /// 请求优先级
        /// </summary>
        public TTSPriority Priority { get; set; }

        /// <summary>
        /// 超时时间（毫秒）
        /// </summary>
        public int TimeoutMs { get; set; }

        /// <summary>
        /// 附加参数
        /// </summary>
        public Dictionary<string, object> AdditionalParameters { get; set; }

        public TTSRequest()
        {
            RequestId = Guid.NewGuid().ToString();
            Timestamp = DateTime.UtcNow;
            RetryCount = 0;
            Priority = TTSPriority.Normal;
            TimeoutMs = 30000; // 默认 30 秒超时
            AdditionalParameters = new Dictionary<string, object>();
        }

        public TTSRequest(string text, TTSRequestSettings settings = null) : this()
        {
            Text = text;
            Settings = settings ?? new TTSRequestSettings();
        }

        /// <summary>
        /// 验证请求是否有效
        /// </summary>
        /// <returns>验证结果</returns>
        public ValidationResult Validate()
        {
            var result = new ValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(Text))
            {
                result.AddError("Text cannot be null or empty");
            }

            if (Text?.Length > 10000) // 限制文本长度
            {
                result.AddWarning("Text length exceeds recommended limit (10000 characters)");
            }

            if (TimeoutMs <= 0)
            {
                result.AddError("Timeout must be positive");
            }

            if (Settings is not null)
            {
                var settingsValidation = Settings.Validate();
                if (!settingsValidation.IsValid)
                {
                    result.Errors.AddRange(settingsValidation.Errors);
                    if (settingsValidation.Warnings is not null)
                    {
                        var warningsList = result.Warnings?.ToList() ?? new List<string>();
                        warningsList.AddRange(settingsValidation.Warnings);
                        result.Warnings = warningsList.ToArray();
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 创建请求的副本
        /// </summary>
        /// <returns>请求副本</returns>
        public TTSRequest Clone()
        {
            return new TTSRequest
            {
                Text = Text,
                Settings = Settings?.Clone(),
                RequestId = RequestId,
                Timestamp = Timestamp,
                RetryCount = RetryCount,
                Priority = Priority,
                TimeoutMs = TimeoutMs,
                AdditionalParameters = new Dictionary<string, object>(AdditionalParameters)
            };
        }
    }

    /// <summary>
    /// TTS 请求优先级
    /// </summary>
    public enum TTSPriority
    {
        /// <summary>
        /// 低优先级
        /// </summary>
        Low = 0,

        /// <summary>
        /// 普通优先级
        /// </summary>
        Normal = 1,

        /// <summary>
        /// 高优先级
        /// </summary>
        High = 2,

        /// <summary>
        /// 紧急优先级
        /// </summary>
        Urgent = 3
    }

    /// <summary>
    /// TTS 请求设置
    /// </summary>
    public class TTSRequestSettings
    {
        /// <summary>
        /// 语音标识符
        /// </summary>
        public string Voice { get; set; }

        /// <summary>
        /// 语速（0.1 - 5.0）
        /// </summary>
        public float Speed { get; set; } = 1.0f;

        /// <summary>
        /// 音调（0.1 - 2.0）
        /// </summary>
        public float Pitch { get; set; } = 1.0f;

        /// <summary>
        /// 音量（0.0 - 1.0）
        /// </summary>
        public float Volume { get; set; } = 1.0f;

        /// <summary>
        /// 音频格式
        /// </summary>
        public string AudioFormat { get; set; } = "mp3";

        /// <summary>
        /// 是否使用流式处理
        /// </summary>
        public bool UseStreaming { get; set; } = false;

        /// <summary>
        /// 语言代码
        /// </summary>
        public string Language { get; set; } = "zh-CN";

        /// <summary>
        /// 超时时间（毫秒）
        /// </summary>
        public int TimeoutMs { get; set; } = 30000;

        /// <summary>
        /// 验证设置
        /// </summary>
        /// <returns>验证结果</returns>
        public ValidationResult Validate()
        {
            var result = new ValidationResult { IsValid = true };

            if (Speed < 0.1f || Speed > 5.0f)
            {
                result.AddError($"Speed must be between 0.1 and 5.0, got {Speed}");
            }

            if (Pitch < 0.1f || Pitch > 2.0f)
            {
                result.AddError($"Pitch must be between 0.1 and 2.0, got {Pitch}");
            }

            if (Volume < 0.0f || Volume > 1.0f)
            {
                result.AddError($"Volume must be between 0.0 and 1.0, got {Volume}");
            }

            if (TimeoutMs <= 0)
            {
                result.AddError($"TimeoutMs must be positive, got {TimeoutMs}");
            }

            return result;
        }

        /// <summary>
        /// 创建设置的副本
        /// </summary>
        /// <returns>设置副本</returns>
        public TTSRequestSettings Clone()
        {
            return new TTSRequestSettings
            {
                Voice = Voice,
                Speed = Speed,
                Pitch = Pitch,
                Volume = Volume,
                AudioFormat = AudioFormat,
                UseStreaming = UseStreaming,
                Language = Language,
                TimeoutMs = TimeoutMs
            };
        }
    }
}