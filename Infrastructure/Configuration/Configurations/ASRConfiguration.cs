using VPetLLM.Configuration;

namespace VPetLLM.Infrastructure.Configuration.Configurations
{
    /// <summary>
    /// ASR配置
    /// </summary>
    public class ASRConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "ASR";

        /// <summary>
        /// 是否启用ASR
        /// </summary>
        public bool IsEnabled { get; set; } = false;

        /// <summary>
        /// ASR提供商
        /// </summary>
        public string Provider { get; set; } = "OpenAI";

        /// <summary>
        /// 热键修饰符
        /// </summary>
        public string HotkeyModifiers { get; set; } = "Win+Alt";

        /// <summary>
        /// 热键按键
        /// </summary>
        public string HotkeyKey { get; set; } = "V";

        /// <summary>
        /// 语言
        /// </summary>
        public string Language { get; set; } = "zh";

        /// <summary>
        /// 自动发送
        /// </summary>
        public bool AutoSend { get; set; } = true;

        /// <summary>
        /// 显示转录窗口
        /// </summary>
        public bool ShowTranscriptionWindow { get; set; } = true;

        /// <summary>
        /// 录音设备编号
        /// </summary>
        public int RecordingDeviceNumber { get; set; } = 0;

        /// <summary>
        /// 使用队列处理
        /// </summary>
        public bool UseQueueProcessing { get; set; } = false;

        /// <summary>
        /// OpenAI ASR设置
        /// </summary>
        public OpenAIASRConfiguration OpenAI { get; set; } = new();

        /// <summary>
        /// Soniox ASR设置
        /// </summary>
        public SonioxASRConfiguration Soniox { get; set; } = new();

        /// <summary>
        /// Free ASR设置
        /// </summary>
        public FreeASRConfiguration Free { get; set; } = new();

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(Provider))
            {
                result.AddError("ASR提供商不能为空");
            }

            if (string.IsNullOrWhiteSpace(Language))
            {
                result.AddError("语言设置不能为空");
            }

            if (RecordingDeviceNumber < 0)
            {
                result.AddError("录音设备编号不能为负数");
            }

            // 验证提供商配置
            switch (Provider)
            {
                case "OpenAI":
                    var openaiValidation = OpenAI?.Validate();
                    if (openaiValidation != null && !openaiValidation.IsValid)
                    {
                        result.Errors.AddRange(openaiValidation.Errors.Select(e => $"OpenAI ASR: {e}"));
                        result.IsValid = false;
                    }
                    break;

                case "Soniox":
                    var sonioxValidation = Soniox?.Validate();
                    if (sonioxValidation != null && !sonioxValidation.IsValid)
                    {
                        result.Errors.AddRange(sonioxValidation.Errors.Select(e => $"Soniox ASR: {e}"));
                        result.IsValid = false;
                    }
                    break;
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new ASRConfiguration
            {
                IsEnabled = IsEnabled,
                Provider = Provider,
                HotkeyModifiers = HotkeyModifiers,
                HotkeyKey = HotkeyKey,
                Language = Language,
                AutoSend = AutoSend,
                ShowTranscriptionWindow = ShowTranscriptionWindow,
                RecordingDeviceNumber = RecordingDeviceNumber,
                UseQueueProcessing = UseQueueProcessing,
                OpenAI = OpenAI?.Clone() as OpenAIASRConfiguration,
                Soniox = Soniox?.Clone() as SonioxASRConfiguration,
                Free = Free?.Clone() as FreeASRConfiguration,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is ASRConfiguration otherASR)
            {
                IsEnabled = otherASR.IsEnabled;
                Provider = otherASR.Provider;
                HotkeyModifiers = otherASR.HotkeyModifiers;
                HotkeyKey = otherASR.HotkeyKey;
                Language = otherASR.Language;
                AutoSend = otherASR.AutoSend;
                ShowTranscriptionWindow = otherASR.ShowTranscriptionWindow;
                RecordingDeviceNumber = otherASR.RecordingDeviceNumber;
                UseQueueProcessing = otherASR.UseQueueProcessing;

                OpenAI?.Merge(otherASR.OpenAI);
                Soniox?.Merge(otherASR.Soniox);
                Free?.Merge(otherASR.Free);

                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            IsEnabled = false;
            Provider = "OpenAI";
            HotkeyModifiers = "Win+Alt";
            HotkeyKey = "V";
            Language = "zh";
            AutoSend = true;
            ShowTranscriptionWindow = true;
            RecordingDeviceNumber = 0;
            UseQueueProcessing = false;

            OpenAI = new OpenAIASRConfiguration();
            Soniox = new SonioxASRConfiguration();
            Free = new FreeASRConfiguration();

            MarkAsModified();
        }
    }

    // ASR子配置类
    public class OpenAIASRConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "OpenAI ASR";

        public string ApiKey { get; set; } = "";
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";
        public string Model { get; set; } = "whisper-1";

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                result.AddError("API Key不能为空");
            }

            if (string.IsNullOrWhiteSpace(BaseUrl))
            {
                result.AddError("Base URL不能为空");
            }
            else if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
            {
                result.AddError("Base URL格式无效");
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new OpenAIASRConfiguration
            {
                ApiKey = ApiKey,
                BaseUrl = BaseUrl,
                Model = Model,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is OpenAIASRConfiguration otherOpenAI)
            {
                ApiKey = otherOpenAI.ApiKey;
                BaseUrl = otherOpenAI.BaseUrl;
                Model = otherOpenAI.Model;
                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            ApiKey = "";
            BaseUrl = "https://api.openai.com/v1";
            Model = "whisper-1";
            MarkAsModified();
        }
    }

    public class SonioxASRConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "Soniox ASR";

        public string ApiKey { get; set; } = "";
        public string BaseUrl { get; set; } = "https://api.soniox.com";
        public string Model { get; set; } = "stt-rt-v3";
        public bool EnablePunctuation { get; set; } = true;
        public bool EnableProfanityFilter { get; set; } = false;

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                result.AddError("API Key不能为空");
            }

            if (string.IsNullOrWhiteSpace(BaseUrl))
            {
                result.AddError("Base URL不能为空");
            }
            else if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
            {
                result.AddError("Base URL格式无效");
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new SonioxASRConfiguration
            {
                ApiKey = ApiKey,
                BaseUrl = BaseUrl,
                Model = Model,
                EnablePunctuation = EnablePunctuation,
                EnableProfanityFilter = EnableProfanityFilter,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is SonioxASRConfiguration otherSoniox)
            {
                ApiKey = otherSoniox.ApiKey;
                BaseUrl = otherSoniox.BaseUrl;
                Model = otherSoniox.Model;
                EnablePunctuation = otherSoniox.EnablePunctuation;
                EnableProfanityFilter = otherSoniox.EnableProfanityFilter;
                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            ApiKey = "";
            BaseUrl = "https://api.soniox.com";
            Model = "stt-rt-v3";
            EnablePunctuation = true;
            EnableProfanityFilter = false;
            MarkAsModified();
        }
    }

    public class FreeASRConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "Free ASR";

        public override SettingsValidationResult Validate()
        {
            return new SettingsValidationResult { IsValid = true };
        }

        public override IConfiguration Clone()
        {
            return new FreeASRConfiguration
            {
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            // Free ASR没有配置项需要合并
        }

        public override void ResetToDefaults()
        {
            // Free ASR没有配置项需要重置
        }
    }

    // 辅助类
    public class SonioxModelInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string TranscriptionMode { get; set; } = "";
        public List<SonioxLanguageInfo> Languages { get; set; } = new();
    }

    public class SonioxLanguageInfo
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
    }
}