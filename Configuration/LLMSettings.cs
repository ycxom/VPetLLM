namespace VPetLLM.Configuration
{
    /// <summary>
    /// LLM 相关配置模块
    /// </summary>
    public class LLMSettings : ISettings
    {
        /// <summary>
        /// LLM 提供商类型
        /// </summary>
        public Setting.LLMType Provider { get; set; } = Setting.LLMType.Ollama;

        /// <summary>
        /// Ollama 设置
        /// </summary>
        public Setting.OllamaSetting Ollama { get; set; } = new();

        /// <summary>
        /// OpenAI 设置
        /// </summary>
        public Setting.OpenAISetting OpenAI { get; set; } = new();

        /// <summary>
        /// Gemini 设置
        /// </summary>
        public Setting.GeminiSetting Gemini { get; set; } = new();

        /// <summary>
        /// Free 设置
        /// </summary>
        public Setting.FreeSetting Free { get; set; } = new();

        /// <summary>
        /// 是否保持上下文
        /// </summary>
        public bool KeepContext { get; set; } = true;

        /// <summary>
        /// 是否启用聊天历史
        /// </summary>
        public bool EnableChatHistory { get; set; } = true;

        /// <summary>
        /// 是否按提供商分离聊天记录
        /// </summary>
        public bool SeparateChatByProvider { get; set; } = false;

        /// <summary>
        /// 是否启用历史压缩
        /// </summary>
        public bool EnableHistoryCompression { get; set; } = false;

        /// <summary>
        /// 压缩触发模式
        /// </summary>
        public Setting.CompressionTriggerMode CompressionMode { get; set; } = Setting.CompressionTriggerMode.MessageCount;

        /// <summary>
        /// 历史压缩阈值（消息数）
        /// </summary>
        public int HistoryCompressionThreshold { get; set; } = 20;

        /// <summary>
        /// 历史压缩阈值（Token数）
        /// </summary>
        public int HistoryCompressionTokenThreshold { get; set; } = 4000;

        /// <inheritdoc/>
        public SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            // 验证当前提供商的配置
            switch (Provider)
            {
                case Setting.LLMType.Ollama:
                    if (string.IsNullOrWhiteSpace(Ollama?.Url))
                    {
                        result.AddError("Ollama URL is required");
                    }
                    break;

                case Setting.LLMType.OpenAI:
                    if (OpenAI?.OpenAINodes is null || OpenAI.OpenAINodes.Count == 0)
                    {
                        if (string.IsNullOrWhiteSpace(OpenAI?.ApiKey))
                        {
                            result.AddError("OpenAI API Key is required");
                        }
                    }
                    break;

                case Setting.LLMType.Gemini:
                    if (Gemini?.GeminiNodes is null || Gemini.GeminiNodes.Count == 0)
                    {
                        if (string.IsNullOrWhiteSpace(Gemini?.ApiKey))
                        {
                            result.AddError("Gemini API Key is required");
                        }
                    }
                    break;

                case Setting.LLMType.Free:
                    // Free 模式不需要额外验证
                    break;
            }

            // 验证压缩阈值
            if (EnableHistoryCompression)
            {
                if (HistoryCompressionThreshold < 5)
                {
                    result.AddWarning("History compression threshold is very low (< 5 messages)");
                }
                if (HistoryCompressionTokenThreshold < 1000)
                {
                    result.AddWarning("History compression token threshold is very low (< 1000 tokens)");
                }
            }

            return result;
        }
    }
}
