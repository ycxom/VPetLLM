namespace VPetLLM.Models
{
    /// <summary>
    /// TTS 配置对象
    /// 包含 TTS 类型选择和相关配置
    /// </summary>
    public class TTSConfiguration
    {
        /// <summary>
        /// TTS 类型
        /// </summary>
        public TTSType Type { get; set; }

        /// <summary>
        /// 外部 TTS 设置
        /// </summary>
        public ExternalTTSSettings ExternalSettings { get; set; }

        /// <summary>
        /// 内置 TTS 设置
        /// </summary>
        public BuiltInTTSSettings BuiltInSettings { get; set; }

        /// <summary>
        /// 超时时间（秒）
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// 是否启用日志记录
        /// </summary>
        public bool EnableLogging { get; set; } = true;

        /// <summary>
        /// 最大重试次数
        /// </summary>
        public int MaxRetryCount { get; set; } = 3;

        /// <summary>
        /// 重试延迟（毫秒）
        /// </summary>
        public int RetryDelayMs { get; set; } = 1000;

        /// <summary>
        /// 是否启用缓存
        /// </summary>
        public bool EnableCaching { get; set; } = false;

        /// <summary>
        /// 缓存过期时间（分钟）
        /// </summary>
        public int CacheExpirationMinutes { get; set; } = 60;

        /// <summary>
        /// 配置版本
        /// </summary>
        public string Version { get; set; } = "1.0";

        /// <summary>
        /// 配置创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 配置最后更新时间
        /// </summary>
        public DateTime UpdatedAt { get; set; }

        public TTSConfiguration()
        {
            Type = TTSType.External;
            ExternalSettings = new ExternalTTSSettings();
            BuiltInSettings = new BuiltInTTSSettings();
            CreatedAt = DateTime.UtcNow;
            UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// 验证配置
        /// </summary>
        /// <returns>验证结果</returns>
        public ValidationResult Validate()
        {
            var result = new ValidationResult { IsValid = true };

            if (TimeoutSeconds <= 0)
            {
                result.AddError("Timeout seconds must be positive");
            }

            if (MaxRetryCount < 0)
            {
                result.AddError("Max retry count cannot be negative");
            }

            if (RetryDelayMs < 0)
            {
                result.AddError("Retry delay cannot be negative");
            }

            if (EnableCaching && CacheExpirationMinutes <= 0)
            {
                result.AddError("Cache expiration minutes must be positive when caching is enabled");
            }

            // 根据类型验证相应的设置
            switch (Type)
            {
                case TTSType.External:
                    if (ExternalSettings is null)
                    {
                        result.AddError("External TTS settings are required when type is External");
                    }
                    else
                    {
                        var externalValidation = ExternalSettings.Validate();
                        if (!externalValidation.IsValid)
                        {
                            result.Errors.AddRange(externalValidation.Errors);
                            if (externalValidation.Warnings is not null)
                            {
                                var warningsList = result.Warnings?.ToList() ?? new List<string>();
                                warningsList.AddRange(externalValidation.Warnings);
                                result.Warnings = warningsList.ToArray();
                            }
                        }
                    }
                    break;

                case TTSType.BuiltIn:
                    if (BuiltInSettings is null)
                    {
                        result.AddError("Built-in TTS settings are required when type is BuiltIn");
                    }
                    else
                    {
                        var builtInValidation = BuiltInSettings.Validate();
                        if (!builtInValidation.IsValid)
                        {
                            result.Errors.AddRange(builtInValidation.Errors);
                            if (builtInValidation.Warnings is not null)
                            {
                                var warningsList = result.Warnings?.ToList() ?? new List<string>();
                                warningsList.AddRange(builtInValidation.Warnings);
                                result.Warnings = warningsList.ToArray();
                            }
                        }
                    }
                    break;
            }

            return result;
        }

        /// <summary>
        /// 创建配置的副本
        /// </summary>
        /// <returns>配置副本</returns>
        public TTSConfiguration Clone()
        {
            return new TTSConfiguration
            {
                Type = Type,
                ExternalSettings = ExternalSettings?.Clone(),
                BuiltInSettings = BuiltInSettings?.Clone(),
                TimeoutSeconds = TimeoutSeconds,
                EnableLogging = EnableLogging,
                MaxRetryCount = MaxRetryCount,
                RetryDelayMs = RetryDelayMs,
                EnableCaching = EnableCaching,
                CacheExpirationMinutes = CacheExpirationMinutes,
                Version = Version,
                CreatedAt = CreatedAt,
                UpdatedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// 获取当前活跃的设置
        /// </summary>
        /// <returns>当前活跃的设置对象</returns>
        public object GetActiveSettings()
        {
            return Type switch
            {
                TTSType.External => ExternalSettings,
                TTSType.BuiltIn => BuiltInSettings,
                _ => null
            };
        }
    }

    /// <summary>
    /// TTS 类型枚举
    /// </summary>
    public enum TTSType
    {
        /// <summary>
        /// 外部 TTS（TTSCore 实现）
        /// </summary>
        External,

        /// <summary>
        /// 内置 TTS（VPetTTS 插件）
        /// </summary>
        BuiltIn
    }

    /// <summary>
    /// 外部 TTS 设置
    /// </summary>
    public class ExternalTTSSettings
    {
        /// <summary>
        /// TTS 提供商名称
        /// </summary>
        public string Provider { get; set; } = "OpenAI";

        /// <summary>
        /// TTSCore 类型
        /// </summary>
        public string TTSCoreType { get; set; } = "URL";

        /// <summary>
        /// 参数字典
        /// </summary>
        public Dictionary<string, object> Parameters { get; set; }

        /// <summary>
        /// 提供商特定配置
        /// </summary>
        public Dictionary<string, string> ProviderConfig { get; set; }

        public ExternalTTSSettings()
        {
            Parameters = new Dictionary<string, object>();
            ProviderConfig = new Dictionary<string, string>();
        }

        /// <summary>
        /// 验证外部 TTS 设置
        /// </summary>
        /// <returns>验证结果</returns>
        public ValidationResult Validate()
        {
            var result = new ValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(TTSCoreType))
            {
                result.AddError("TTS Core type cannot be empty");
            }

            // 根据不同的 TTSCore 类型进行特定验证
            switch (TTSCoreType?.ToUpper())
            {
                case "URL":
                    if (!Parameters.ContainsKey("BaseUrl") || string.IsNullOrWhiteSpace(Parameters["BaseUrl"]?.ToString()))
                    {
                        result.AddError("BaseUrl is required for URL TTS");
                    }
                    break;

                case "OPENAI":
                    if (!Parameters.ContainsKey("ApiKey") || string.IsNullOrWhiteSpace(Parameters["ApiKey"]?.ToString()))
                    {
                        result.AddError("ApiKey is required for OpenAI TTS");
                    }
                    break;

                case "DIY":
                    if (!Parameters.ContainsKey("BaseUrl") || string.IsNullOrWhiteSpace(Parameters["BaseUrl"]?.ToString()))
                    {
                        result.AddError("BaseUrl is required for DIY TTS");
                    }
                    break;
            }

            return result;
        }

        /// <summary>
        /// 创建设置的副本
        /// </summary>
        /// <returns>设置副本</returns>
        public ExternalTTSSettings Clone()
        {
            return new ExternalTTSSettings
            {
                Provider = Provider,
                TTSCoreType = TTSCoreType,
                Parameters = new Dictionary<string, object>(Parameters),
                ProviderConfig = new Dictionary<string, string>(ProviderConfig)
            };
        }
    }

    /// <summary>
    /// 内置 TTS 设置
    /// </summary>
    public class BuiltInTTSSettings
    {
        /// <summary>
        /// 语音配置文件
        /// </summary>
        public string VoiceProfile { get; set; } = "default";

        /// <summary>
        /// 语速
        /// </summary>
        public float Speed { get; set; } = 1.0f;

        /// <summary>
        /// 音调
        /// </summary>
        public float Pitch { get; set; } = 1.0f;

        /// <summary>
        /// 音量
        /// </summary>
        public float Volume { get; set; } = 1.0f;

        /// <summary>
        /// 是否使用流式处理
        /// </summary>
        public bool UseStreaming { get; set; } = false;

        /// <summary>
        /// VPetTTS 插件特定配置
        /// </summary>
        public Dictionary<string, object> PluginConfig { get; set; }

        public BuiltInTTSSettings()
        {
            PluginConfig = new Dictionary<string, object>();
        }

        /// <summary>
        /// 验证内置 TTS 设置
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

            return result;
        }

        /// <summary>
        /// 创建设置的副本
        /// </summary>
        /// <returns>设置副本</returns>
        public BuiltInTTSSettings Clone()
        {
            return new BuiltInTTSSettings
            {
                VoiceProfile = VoiceProfile,
                Speed = Speed,
                Pitch = Pitch,
                Volume = Volume,
                UseStreaming = UseStreaming,
                PluginConfig = new Dictionary<string, object>(PluginConfig)
            };
        }
    }
}