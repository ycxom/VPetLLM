using VPetLLM.Configuration;

namespace VPetLLM.Infrastructure.Configuration.Configurations
{
    /// <summary>
    /// LLM配置
    /// </summary>
    public class LLMConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "LLM";

        /// <summary>
        /// 当前LLM提供商
        /// </summary>
        public LLMProviderType Provider { get; set; } = LLMProviderType.Ollama;

        /// <summary>
        /// AI名称
        /// </summary>
        public string AiName { get; set; } = "虚拟宠物";

        /// <summary>
        /// 用户名称
        /// </summary>
        public string UserName { get; set; } = "主人";

        /// <summary>
        /// 角色设定
        /// </summary>
        public string Role { get; set; } = "你是一个可爱的虚拟宠物助手，请用友好、可爱的语气回应我。";

        /// <summary>
        /// 是否跟随VPet名称
        /// </summary>
        public bool FollowVPetName { get; set; } = true;

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
        /// 是否减少输入Token使用
        /// </summary>
        public bool ReduceInputTokenUsage { get; set; } = false;

        /// <summary>
        /// 是否启用历史压缩
        /// </summary>
        public bool EnableHistoryCompression { get; set; } = false;

        /// <summary>
        /// 压缩触发模式
        /// </summary>
        public CompressionTriggerMode CompressionMode { get; set; } = CompressionTriggerMode.MessageCount;

        /// <summary>
        /// 历史压缩阈值
        /// </summary>
        public int HistoryCompressionThreshold { get; set; } = 20;

        /// <summary>
        /// 历史压缩Token阈值
        /// </summary>
        public int HistoryCompressionTokenThreshold { get; set; } = 4000;

        /// <summary>
        /// Ollama配置
        /// </summary>
        public OllamaConfiguration Ollama { get; set; } = new();

        /// <summary>
        /// OpenAI配置
        /// </summary>
        public OpenAIConfiguration OpenAI { get; set; } = new();

        /// <summary>
        /// Gemini配置
        /// </summary>
        public GeminiConfiguration Gemini { get; set; } = new();

        /// <summary>
        /// Free配置
        /// </summary>
        public FreeConfiguration Free { get; set; } = new();

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            // 验证基本设置
            if (string.IsNullOrWhiteSpace(AiName))
            {
                result.AddError("AI名称不能为空");
            }

            if (string.IsNullOrWhiteSpace(UserName))
            {
                result.AddError("用户名称不能为空");
            }

            if (string.IsNullOrWhiteSpace(Role))
            {
                result.AddError("角色设定不能为空");
            }

            // 验证阈值设置
            if (HistoryCompressionThreshold <= 0)
            {
                result.AddError("历史压缩阈值必须大于0");
            }

            if (HistoryCompressionTokenThreshold <= 0)
            {
                result.AddError("历史压缩Token阈值必须大于0");
            }

            // 验证提供商配置
            switch (Provider)
            {
                case LLMProviderType.Ollama:
                    var ollamaValidation = Ollama?.Validate();
                    if (ollamaValidation != null && !ollamaValidation.IsValid)
                    {
                        result.Errors.AddRange(ollamaValidation.Errors.Select(e => $"Ollama: {e}"));
                        result.IsValid = false;
                    }
                    break;

                case LLMProviderType.OpenAI:
                    var openaiValidation = OpenAI?.Validate();
                    if (openaiValidation != null && !openaiValidation.IsValid)
                    {
                        result.Errors.AddRange(openaiValidation.Errors.Select(e => $"OpenAI: {e}"));
                        result.IsValid = false;
                    }
                    break;

                case LLMProviderType.Gemini:
                    var geminiValidation = Gemini?.Validate();
                    if (geminiValidation != null && !geminiValidation.IsValid)
                    {
                        result.Errors.AddRange(geminiValidation.Errors.Select(e => $"Gemini: {e}"));
                        result.IsValid = false;
                    }
                    break;

                case LLMProviderType.Free:
                    var freeValidation = Free?.Validate();
                    if (freeValidation != null && !freeValidation.IsValid)
                    {
                        result.Errors.AddRange(freeValidation.Errors.Select(e => $"Free: {e}"));
                        result.IsValid = false;
                    }
                    break;
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new LLMConfiguration
            {
                Provider = Provider,
                AiName = AiName,
                UserName = UserName,
                Role = Role,
                FollowVPetName = FollowVPetName,
                KeepContext = KeepContext,
                EnableChatHistory = EnableChatHistory,
                SeparateChatByProvider = SeparateChatByProvider,
                ReduceInputTokenUsage = ReduceInputTokenUsage,
                EnableHistoryCompression = EnableHistoryCompression,
                CompressionMode = CompressionMode,
                HistoryCompressionThreshold = HistoryCompressionThreshold,
                HistoryCompressionTokenThreshold = HistoryCompressionTokenThreshold,
                Ollama = Ollama?.Clone() as OllamaConfiguration,
                OpenAI = OpenAI?.Clone() as OpenAIConfiguration,
                Gemini = Gemini?.Clone() as GeminiConfiguration,
                Free = Free?.Clone() as FreeConfiguration,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is LLMConfiguration otherLLM)
            {
                Provider = otherLLM.Provider;
                AiName = otherLLM.AiName;
                UserName = otherLLM.UserName;
                Role = otherLLM.Role;
                FollowVPetName = otherLLM.FollowVPetName;
                KeepContext = otherLLM.KeepContext;
                EnableChatHistory = otherLLM.EnableChatHistory;
                SeparateChatByProvider = otherLLM.SeparateChatByProvider;
                ReduceInputTokenUsage = otherLLM.ReduceInputTokenUsage;
                EnableHistoryCompression = otherLLM.EnableHistoryCompression;
                CompressionMode = otherLLM.CompressionMode;
                HistoryCompressionThreshold = otherLLM.HistoryCompressionThreshold;
                HistoryCompressionTokenThreshold = otherLLM.HistoryCompressionTokenThreshold;

                Ollama?.Merge(otherLLM.Ollama);
                OpenAI?.Merge(otherLLM.OpenAI);
                Gemini?.Merge(otherLLM.Gemini);
                Free?.Merge(otherLLM.Free);

                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            Provider = LLMProviderType.Ollama;
            AiName = "虚拟宠物";
            UserName = "主人";
            Role = "你是一个可爱的虚拟宠物助手，请用友好、可爱的语气回应我。";
            FollowVPetName = true;
            KeepContext = true;
            EnableChatHistory = true;
            SeparateChatByProvider = false;
            ReduceInputTokenUsage = false;
            EnableHistoryCompression = false;
            CompressionMode = CompressionTriggerMode.MessageCount;
            HistoryCompressionThreshold = 20;
            HistoryCompressionTokenThreshold = 4000;

            Ollama = new OllamaConfiguration();
            OpenAI = new OpenAIConfiguration();
            Gemini = new GeminiConfiguration();
            Free = new FreeConfiguration();

            MarkAsModified();
        }
    }

    /// <summary>
    /// LLM提供商类型
    /// </summary>
    public enum LLMProviderType
    {
        Ollama,
        OpenAI,
        Gemini,
        Free
    }

    /// <summary>
    /// 压缩触发模式
    /// </summary>
    public enum CompressionTriggerMode
    {
        MessageCount,  // 按消息数量触发
        TokenCount,    // 按Token数量触发
        Both           // 两者任一达到阈值即触发
    }

    // 子配置类
    public class OllamaConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "Ollama";

        public string Url { get; set; } = "http://localhost:11434";
        public string Model { get; set; }
        public double Temperature { get; set; } = 0.7;
        public int MaxTokens { get; set; } = 2048;
        public bool EnableAdvanced { get; set; } = false;
        public bool EnableStreaming { get; set; } = false;
        public bool EnableVision { get; set; } = false;

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(Url))
            {
                result.AddError("Ollama URL不能为空");
            }
            else if (!Uri.TryCreate(Url, UriKind.Absolute, out _))
            {
                result.AddError("Ollama URL格式无效");
            }

            if (Temperature < 0 || Temperature > 2)
            {
                result.AddError("Temperature必须在0-2之间");
            }

            if (MaxTokens <= 0)
            {
                result.AddError("MaxTokens必须大于0");
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new OllamaConfiguration
            {
                Url = Url,
                Model = Model,
                Temperature = Temperature,
                MaxTokens = MaxTokens,
                EnableAdvanced = EnableAdvanced,
                EnableStreaming = EnableStreaming,
                EnableVision = EnableVision,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is OllamaConfiguration otherOllama)
            {
                Url = otherOllama.Url;
                Model = otherOllama.Model;
                Temperature = otherOllama.Temperature;
                MaxTokens = otherOllama.MaxTokens;
                EnableAdvanced = otherOllama.EnableAdvanced;
                EnableStreaming = otherOllama.EnableStreaming;
                EnableVision = otherOllama.EnableVision;
                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            Url = "http://localhost:11434";
            Model = null;
            Temperature = 0.7;
            MaxTokens = 2048;
            EnableAdvanced = false;
            EnableStreaming = false;
            EnableVision = false;
            MarkAsModified();
        }
    }

    public class OpenAIConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "OpenAI";

        public string ApiKey { get; set; }
        public string Model { get; set; }
        public string Url { get; set; } = "https://api.openai.com/v1";
        public double Temperature { get; set; } = 0.7;
        public int MaxTokens { get; set; } = 2048;
        public bool EnableAdvanced { get; set; } = false;
        public bool EnableStreaming { get; set; } = false;
        public bool EnableVision { get; set; } = false;

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                result.AddError("OpenAI API Key不能为空");
            }

            if (string.IsNullOrWhiteSpace(Url))
            {
                result.AddError("OpenAI URL不能为空");
            }
            else if (!Uri.TryCreate(Url, UriKind.Absolute, out _))
            {
                result.AddError("OpenAI URL格式无效");
            }

            if (Temperature < 0 || Temperature > 2)
            {
                result.AddError("Temperature必须在0-2之间");
            }

            if (MaxTokens <= 0)
            {
                result.AddError("MaxTokens必须大于0");
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new OpenAIConfiguration
            {
                ApiKey = ApiKey,
                Model = Model,
                Url = Url,
                Temperature = Temperature,
                MaxTokens = MaxTokens,
                EnableAdvanced = EnableAdvanced,
                EnableStreaming = EnableStreaming,
                EnableVision = EnableVision,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is OpenAIConfiguration otherOpenAI)
            {
                ApiKey = otherOpenAI.ApiKey;
                Model = otherOpenAI.Model;
                Url = otherOpenAI.Url;
                Temperature = otherOpenAI.Temperature;
                MaxTokens = otherOpenAI.MaxTokens;
                EnableAdvanced = otherOpenAI.EnableAdvanced;
                EnableStreaming = otherOpenAI.EnableStreaming;
                EnableVision = otherOpenAI.EnableVision;
                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            ApiKey = null;
            Model = null;
            Url = "https://api.openai.com/v1";
            Temperature = 0.7;
            MaxTokens = 2048;
            EnableAdvanced = false;
            EnableStreaming = false;
            EnableVision = false;
            MarkAsModified();
        }
    }

    public class GeminiConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "Gemini";

        public string ApiKey { get; set; }
        public string Model { get; set; } = "gemini-pro";
        public string Url { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
        public double Temperature { get; set; } = 0.7;
        public int MaxTokens { get; set; } = 2048;
        public bool EnableAdvanced { get; set; } = false;
        public bool EnableStreaming { get; set; } = false;
        public bool EnableVision { get; set; } = false;

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                result.AddError("Gemini API Key不能为空");
            }

            if (string.IsNullOrWhiteSpace(Url))
            {
                result.AddError("Gemini URL不能为空");
            }
            else if (!Uri.TryCreate(Url, UriKind.Absolute, out _))
            {
                result.AddError("Gemini URL格式无效");
            }

            if (Temperature < 0 || Temperature > 2)
            {
                result.AddError("Temperature必须在0-2之间");
            }

            if (MaxTokens <= 0)
            {
                result.AddError("MaxTokens必须大于0");
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new GeminiConfiguration
            {
                ApiKey = ApiKey,
                Model = Model,
                Url = Url,
                Temperature = Temperature,
                MaxTokens = MaxTokens,
                EnableAdvanced = EnableAdvanced,
                EnableStreaming = EnableStreaming,
                EnableVision = EnableVision,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is GeminiConfiguration otherGemini)
            {
                ApiKey = otherGemini.ApiKey;
                Model = otherGemini.Model;
                Url = otherGemini.Url;
                Temperature = otherGemini.Temperature;
                MaxTokens = otherGemini.MaxTokens;
                EnableAdvanced = otherGemini.EnableAdvanced;
                EnableStreaming = otherGemini.EnableStreaming;
                EnableVision = otherGemini.EnableVision;
                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            ApiKey = null;
            Model = "gemini-pro";
            Url = "https://generativelanguage.googleapis.com/v1beta";
            Temperature = 0.7;
            MaxTokens = 2048;
            EnableAdvanced = false;
            EnableStreaming = false;
            EnableVision = false;
            MarkAsModified();
        }
    }

    public class FreeConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "Free";

        public string Model { get; set; }
        public double Temperature { get; set; } = 0.7;
        public int MaxTokens { get; set; } = 2048;
        public bool EnableAdvanced { get; set; } = false;
        public bool EnableStreaming { get; set; } = false;
        public bool EnableVision { get; set; } = false;

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (Temperature < 0 || Temperature > 2)
            {
                result.AddError("Temperature必须在0-2之间");
            }

            if (MaxTokens <= 0)
            {
                result.AddError("MaxTokens必须大于0");
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new FreeConfiguration
            {
                Model = Model,
                Temperature = Temperature,
                MaxTokens = MaxTokens,
                EnableAdvanced = EnableAdvanced,
                EnableStreaming = EnableStreaming,
                EnableVision = EnableVision,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is FreeConfiguration otherFree)
            {
                Model = otherFree.Model;
                Temperature = otherFree.Temperature;
                MaxTokens = otherFree.MaxTokens;
                EnableAdvanced = otherFree.EnableAdvanced;
                EnableStreaming = otherFree.EnableStreaming;
                EnableVision = otherFree.EnableVision;
                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            Model = null;
            Temperature = 0.7;
            MaxTokens = 2048;
            EnableAdvanced = false;
            EnableStreaming = false;
            EnableVision = false;
            MarkAsModified();
        }
    }
}