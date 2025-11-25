namespace VPetLLM.Configuration
{
    /// <summary>
    /// TTS (Text-to-Speech) 相关配置模块
    /// </summary>
    public class TTSSettings : ISettings
    {
        /// <summary>
        /// 是否启用 TTS
        /// </summary>
        public bool IsEnabled { get; set; } = false;

        /// <summary>
        /// TTS 提供商
        /// </summary>
        public string Provider { get; set; } = "URL";

        /// <summary>
        /// 是否只播放 AI 回复
        /// </summary>
        public bool OnlyPlayAIResponse { get; set; } = true;

        /// <summary>
        /// 是否自动播放
        /// </summary>
        public bool AutoPlay { get; set; } = true;

        /// <summary>
        /// 基础音量 (0-10)
        /// </summary>
        public double Volume { get; set; } = 1.0;

        /// <summary>
        /// 语速
        /// </summary>
        public double Speed { get; set; } = 1.0;

        /// <summary>
        /// 音量增益 (dB, -20 到 +40)
        /// </summary>
        public double VolumeGain { get; set; } = 0.0;

        /// <summary>
        /// 是否使用队列下载模式
        /// </summary>
        public bool UseQueueDownload { get; set; } = false;

        /// <summary>
        /// URL TTS 设置
        /// </summary>
        public Setting.URLTTSSetting URL { get; set; } = new();

        /// <summary>
        /// OpenAI TTS 设置
        /// </summary>
        public Setting.OpenAITTSSetting OpenAI { get; set; } = new();

        /// <summary>
        /// DIY TTS 设置
        /// </summary>
        public Setting.DIYTTSSetting DIY { get; set; } = new();

        /// <summary>
        /// GPT-SoVITS TTS 设置
        /// </summary>
        public Setting.GPTSoVITSTTSSetting GPTSoVITS { get; set; } = new();

        /// <summary>
        /// Free TTS 设置
        /// </summary>
        public Setting.FreeTTSSetting Free { get; set; } = new();

        /// <inheritdoc/>
        public SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (!IsEnabled)
            {
                return result; // TTS 未启用，无需验证
            }

            // 验证音量范围
            if (Volume < 0 || Volume > 10)
            {
                result.AddError("Volume must be between 0 and 10");
            }

            // 验证音量增益范围
            if (VolumeGain < -20 || VolumeGain > 40)
            {
                result.AddError("Volume gain must be between -20 and +40 dB");
            }

            // 验证语速范围
            if (Speed < 0.1 || Speed > 5.0)
            {
                result.AddWarning("Speed value is outside typical range (0.1 - 5.0)");
            }

            // 根据提供商验证特定配置
            switch (Provider)
            {
                case "URL":
                    if (string.IsNullOrWhiteSpace(URL?.BaseUrl))
                    {
                        result.AddError("URL TTS requires a base URL");
                    }
                    break;

                case "OpenAI":
                    if (string.IsNullOrWhiteSpace(OpenAI?.ApiKey))
                    {
                        result.AddError("OpenAI TTS requires an API key");
                    }
                    break;

                case "DIY":
                    if (string.IsNullOrWhiteSpace(DIY?.BaseUrl))
                    {
                        result.AddError("DIY TTS requires a base URL");
                    }
                    break;

                case "GPT-SoVITS":
                    if (string.IsNullOrWhiteSpace(GPTSoVITS?.BaseUrl))
                    {
                        result.AddError("GPT-SoVITS TTS requires a base URL");
                    }
                    break;

                case "Free":
                    // Free TTS 不需要额外验证
                    break;

                default:
                    result.AddWarning($"Unknown TTS provider: {Provider}");
                    break;
            }

            return result;
        }
    }
}
