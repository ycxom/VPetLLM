namespace VPetLLM.Configuration
{
    /// <summary>
    /// ASR (Automatic Speech Recognition) 相关配置模块
    /// </summary>
    public class ASRSettings : ISettings
    {
        /// <summary>
        /// 是否启用 ASR
        /// </summary>
        public bool IsEnabled { get; set; } = false;

        /// <summary>
        /// ASR 提供商
        /// </summary>
        public string Provider { get; set; } = "OpenAI";

        /// <summary>
        /// 快捷键修饰符
        /// </summary>
        public string HotkeyModifiers { get; set; } = "Win+Alt";

        /// <summary>
        /// 快捷键
        /// </summary>
        public string HotkeyKey { get; set; } = "V";

        /// <summary>
        /// 语言
        /// </summary>
        public string Language { get; set; } = "zh";

        /// <summary>
        /// 是否自动发送
        /// </summary>
        public bool AutoSend { get; set; } = true;

        /// <summary>
        /// 是否显示转录窗口
        /// </summary>
        public bool ShowTranscriptionWindow { get; set; } = true;

        /// <summary>
        /// 录音设备编号
        /// </summary>
        public int RecordingDeviceNumber { get; set; } = 0;

        /// <summary>
        /// OpenAI Whisper 设置
        /// </summary>
        public Setting.OpenAIASRSetting OpenAI { get; set; } = new();

        /// <summary>
        /// Soniox 设置
        /// </summary>
        public Setting.SonioxASRSetting Soniox { get; set; } = new();

        /// <summary>
        /// Free 设置
        /// </summary>
        public Setting.FreeASRSetting Free { get; set; } = new();

        /// <inheritdoc/>
        public SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (!IsEnabled)
            {
                return result; // ASR 未启用，无需验证
            }

            // 验证快捷键配置
            if (string.IsNullOrWhiteSpace(HotkeyKey))
            {
                result.AddError("ASR hotkey is required when ASR is enabled");
            }

            // 验证录音设备编号
            if (RecordingDeviceNumber < 0)
            {
                result.AddError("Recording device number cannot be negative");
            }

            // 根据提供商验证特定配置
            switch (Provider)
            {
                case "OpenAI":
                    if (string.IsNullOrWhiteSpace(OpenAI?.ApiKey))
                    {
                        result.AddError("OpenAI ASR requires an API key");
                    }
                    if (string.IsNullOrWhiteSpace(OpenAI?.BaseUrl))
                    {
                        result.AddError("OpenAI ASR requires a base URL");
                    }
                    break;

                case "Soniox":
                    if (string.IsNullOrWhiteSpace(Soniox?.ApiKey))
                    {
                        result.AddError("Soniox ASR requires an API key");
                    }
                    break;

                case "Free":
                    // Free ASR 不需要额外验证
                    break;

                default:
                    result.AddWarning($"Unknown ASR provider: {Provider}");
                    break;
            }

            return result;
        }
    }
}
