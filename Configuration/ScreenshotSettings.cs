using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace VPetLLM.Configuration
{
    /// <summary>
    /// 截图处理模式
    /// </summary>
    public enum ScreenshotProcessingMode
    {
        /// <summary>
        /// 使用原生多模态模型（直接发送图片给支持视觉的 LLM）
        /// </summary>
        NativeMultimodal,
        
        /// <summary>
        /// 使用前置多模态处理（先分析图片生成描述，再发送给主 LLM）
        /// </summary>
        PreprocessingMultimodal,
        
        /// <summary>
        /// 使用 OCR API 提取文字
        /// </summary>
        OCRApi
    }

    /// <summary>
    /// 多模态提供商类型
    /// </summary>
    public enum MultimodalProviderType
    {
        /// <summary>
        /// 使用 Free 渠道
        /// </summary>
        Free,
        
        /// <summary>
        /// 使用启用视觉的渠道
        /// </summary>
        VisionChannels
    }

    /// <summary>
    /// 视觉节点标识符
    /// </summary>
    public class VisionNodeIdentifier
    {
        /// <summary>
        /// 提供商类型 ("OpenAI", "Gemini", "Ollama")
        /// </summary>
        public string ProviderType { get; set; } = "";

        /// <summary>
        /// 节点名称
        /// </summary>
        public string NodeName { get; set; } = "";

        /// <summary>
        /// 模型名称
        /// </summary>
        public string Model { get; set; } = "";

        /// <summary>
        /// 唯一标识符
        /// </summary>
        public string UniqueId => $"{ProviderType}:{NodeName}";

        /// <summary>
        /// 显示名称
        /// </summary>
        public string DisplayName => $"[{ProviderType}] {NodeName} ({Model})";

        public override bool Equals(object? obj)
        {
            if (obj is VisionNodeIdentifier other)
            {
                return UniqueId == other.UniqueId;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return UniqueId.GetHashCode();
        }
    }

    /// <summary>
    /// 可选择的视觉节点包装类，用于UI数据绑定
    /// </summary>
    public class SelectableVisionNode : INotifyPropertyChanged
    {
        /// <summary>
        /// 包装的视觉节点标识符
        /// </summary>
        public VisionNodeIdentifier Node { get; set; } = new();

        /// <summary>
        /// 显示名称
        /// </summary>
        public string DisplayName => Node.DisplayName;

        private bool _isSelected;
        /// <summary>
        /// 是否被选中
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 属性变更事件
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 触发属性变更通知
        /// </summary>
        /// <param name="propertyName">属性名称</param>
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 多模态提供商配置
    /// </summary>
    public class MultimodalProviderConfig
    {
        /// <summary>
        /// 提供商类型
        /// </summary>
        public MultimodalProviderType ProviderType { get; set; } = MultimodalProviderType.Free;

        /// <summary>
        /// 选中的视觉节点列表（当 ProviderType 为 VisionChannels 时使用）
        /// </summary>
        public List<VisionNodeIdentifier> SelectedNodes { get; set; } = new();

        /// <summary>
        /// 默认图片描述提示词（备用）
        /// </summary>
        public const string DefaultImageDescriptionPrompt = 
            "请详细描述这张图片的内容，包括：主要元素、文字内容、布局结构、颜色和风格等。如果图片中有文字，请完整提取。";

        /// <summary>
        /// 获取有效的提示词（从 Prompt.json 读取）
        /// </summary>
        /// <param name="lang">语言代码</param>
        public string GetEffectivePrompt(string lang = "zh")
        {
            var prompt = Utils.PromptHelper.Get("Screenshot_ImageDescription_Prompt", lang);
            // 如果从 Prompt.json 读取失败，使用默认值
            if (prompt.StartsWith("[Prompt"))
            {
                return DefaultImageDescriptionPrompt;
            }
            return prompt;
        }
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

        /// <summary>
        /// 多模态提供商配置
        /// </summary>
        public MultimodalProviderConfig MultimodalProvider { get; set; } = new();

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

            // 验证前置多模态提供商配置
            if (ProcessingMode == ScreenshotProcessingMode.PreprocessingMultimodal)
            {
                if (MultimodalProvider == null)
                {
                    MultimodalProvider = new MultimodalProviderConfig();
                }

                if (MultimodalProvider.ProviderType == MultimodalProviderType.VisionChannels)
                {
                    if (MultimodalProvider.SelectedNodes == null || !MultimodalProvider.SelectedNodes.Any())
                    {
                        result.AddWarning("No vision-enabled nodes selected for preprocessing multimodal");
                    }
                }
            }

            return result;
        }
    }
}
