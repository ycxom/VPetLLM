namespace VPetLLM.Configuration
{
    /// <summary>
    /// 截图处理模式
    /// </summary>
    public enum ScreenshotProcessingMode
    {
        /// <summary>
        /// 使用原生多模态模型
        /// </summary>
        NativeMultimodal,
        
        /// <summary>
        /// 使用 OCR API 提取文字
        /// </summary>
        OCRApi
    }

    /// <summary>
    /// OCR 设置
    /// </summary>
    public class OCRSettings
    {
        /// <summary>
        /// OCR 提供商 (OpenAI, Free)
        /// </summary>
        public string Provider { get; set; } = "OpenAI";

        /// <summary>
        /// API Key
        /// </summary>
        public string ApiKey { get; set; } = "";

        /// <summary>
        /// API Base URL
        /// </summary>
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";

        /// <summary>
        /// 识别语言
        /// </summary>
        public string Language { get; set; } = "auto";
    }

    /// <summary>
    /// 截图功能配置模块
    /// </summary>
    public class ScreenshotSettings : ISettings
    {
        /// <summary>
        /// 是否启用截图功能
        /// </summary>
        public bool IsEnabled { get; set; } = false;

        /// <summary>
        /// 快捷键修饰符
        /// </summary>
        public string HotkeyModifiers { get; set; } = "Win+Alt";

        /// <summary>
        /// 快捷键
        /// </summary>
        public string HotkeyKey { get; set; } = "S";

        /// <summary>
        /// 处理模式
        /// </summary>
        public ScreenshotProcessingMode ProcessingMode { get; set; } = ScreenshotProcessingMode.NativeMultimodal;

        /// <summary>
        /// 是否自动发送
        /// </summary>
        public bool AutoSend { get; set; } = false;

        /// <summary>
        /// 是否显示截图窗口
        /// </summary>
        public bool ShowCaptureWindow { get; set; } = true;

        /// <summary>
        /// OCR 设置
        /// </summary>
        public OCRSettings OCR { get; set; } = new();

        /// <inheritdoc/>
        public SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (!IsEnabled)
            {
                return result; // 截图未启用，无需验证
            }

            // 验证快捷键配置
            if (string.IsNullOrWhiteSpace(HotkeyKey))
            {
                result.AddError("Screenshot hotkey is required when screenshot is enabled");
            }

            // 根据处理模式验证特定配置
            if (ProcessingMode == ScreenshotProcessingMode.OCRApi)
            {
                if (OCR == null)
                {
                    result.AddError("OCR settings are required when using OCR API mode");
                }
                else
                {
                    switch (OCR.Provider)
                    {
                        case "OpenAI":
                            if (string.IsNullOrWhiteSpace(OCR.ApiKey))
                            {
                                result.AddError("OpenAI OCR requires an API key");
                            }
                            if (string.IsNullOrWhiteSpace(OCR.BaseUrl))
                            {
                                result.AddError("OpenAI OCR requires a base URL");
                            }
                            break;

                        case "Free":
                            // Free OCR 不需要额外验证
                            break;

                        default:
                            result.AddWarning($"Unknown OCR provider: {OCR.Provider}");
                            break;
                    }
                }
            }

            return result;
        }
    }
}
