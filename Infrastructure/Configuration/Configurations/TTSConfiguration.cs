namespace VPetLLM.Infrastructure.Configuration.Configurations
{
    /// <summary>
    /// TTS配置
    /// </summary>
    public class TTSConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "TTS";

        /// <summary>
        /// 是否启用TTS
        /// </summary>
        public bool IsEnabled { get; set; } = false;

        /// <summary>
        /// TTS提供商
        /// </summary>
        public string Provider { get; set; } = "URL";

        /// <summary>
        /// 仅播放AI回复
        /// </summary>
        public bool OnlyPlayAIResponse { get; set; } = true;

        /// <summary>
        /// 自动播放
        /// </summary>
        public bool AutoPlay { get; set; } = true;

        /// <summary>
        /// 基础音量百分比 (0-100)
        /// </summary>
        public double Volume { get; set; } = 100;

        /// <summary>
        /// 播放速度
        /// </summary>
        public double Speed { get; set; } = 1.0;

        /// <summary>
        /// 音量增益 (dB)
        /// </summary>
        public double VolumeGain { get; set; } = 0.0;

        /// <summary>
        /// 是否使用队列下载模式
        /// </summary>
        public bool UseQueueDownload { get; set; } = false;

        /// <summary>
        /// URL TTS设置
        /// </summary>
        public URLTTSConfiguration URL { get; set; } = new();

        /// <summary>
        /// OpenAI TTS设置
        /// </summary>
        public OpenAITTSConfiguration OpenAI { get; set; } = new();

        /// <summary>
        /// DIY TTS设置
        /// </summary>
        public DIYTTSConfiguration DIY { get; set; } = new();

        /// <summary>
        /// GPT-SoVITS TTS设置
        /// </summary>
        public GPTSoVITSTTSConfiguration GPTSoVITS { get; set; } = new();

        /// <summary>
        /// Free TTS设置
        /// </summary>
        public FreeTTSConfiguration Free { get; set; } = new();

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (Volume < 0 || Volume > 100)
            {
                result.AddError("音量必须在0-100之间");
            }

            if (Speed <= 0)
            {
                result.AddError("播放速度必须大于0");
            }

            // 验证提供商配置
            switch (Provider)
            {
                case "URL":
                    var urlValidation = URL?.Validate();
                    if (urlValidation is not null && !urlValidation.IsValid)
                    {
                        result.Errors.AddRange(urlValidation.Errors.Select(e => $"URL TTS: {e}"));
                        result.IsValid = false;
                    }
                    break;

                case "OpenAI":
                    var openaiValidation = OpenAI?.Validate();
                    if (openaiValidation is not null && !openaiValidation.IsValid)
                    {
                        result.Errors.AddRange(openaiValidation.Errors.Select(e => $"OpenAI TTS: {e}"));
                        result.IsValid = false;
                    }
                    break;

                case "DIY":
                    var diyValidation = DIY?.Validate();
                    if (diyValidation is not null && !diyValidation.IsValid)
                    {
                        result.Errors.AddRange(diyValidation.Errors.Select(e => $"DIY TTS: {e}"));
                        result.IsValid = false;
                    }
                    break;

                case "GPTSoVITS":
                    var gptSovitsValidation = GPTSoVITS?.Validate();
                    if (gptSovitsValidation is not null && !gptSovitsValidation.IsValid)
                    {
                        result.Errors.AddRange(gptSovitsValidation.Errors.Select(e => $"GPT-SoVITS: {e}"));
                        result.IsValid = false;
                    }
                    break;
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new TTSConfiguration
            {
                IsEnabled = IsEnabled,
                Provider = Provider,
                OnlyPlayAIResponse = OnlyPlayAIResponse,
                AutoPlay = AutoPlay,
                Volume = Volume,
                Speed = Speed,
                VolumeGain = VolumeGain,
                UseQueueDownload = UseQueueDownload,
                URL = URL?.Clone() as URLTTSConfiguration,
                OpenAI = OpenAI?.Clone() as OpenAITTSConfiguration,
                DIY = DIY?.Clone() as DIYTTSConfiguration,
                GPTSoVITS = GPTSoVITS?.Clone() as GPTSoVITSTTSConfiguration,
                Free = Free?.Clone() as FreeTTSConfiguration,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is TTSConfiguration otherTTS)
            {
                IsEnabled = otherTTS.IsEnabled;
                Provider = otherTTS.Provider;
                OnlyPlayAIResponse = otherTTS.OnlyPlayAIResponse;
                AutoPlay = otherTTS.AutoPlay;
                Volume = otherTTS.Volume;
                Speed = otherTTS.Speed;
                VolumeGain = otherTTS.VolumeGain;
                UseQueueDownload = otherTTS.UseQueueDownload;

                URL?.Merge(otherTTS.URL);
                OpenAI?.Merge(otherTTS.OpenAI);
                DIY?.Merge(otherTTS.DIY);
                GPTSoVITS?.Merge(otherTTS.GPTSoVITS);
                Free?.Merge(otherTTS.Free);

                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            IsEnabled = false;
            Provider = "URL";
            OnlyPlayAIResponse = true;
            AutoPlay = true;
            Volume = 100;
            Speed = 1.0;
            VolumeGain = 0.0;
            UseQueueDownload = false;

            URL = new URLTTSConfiguration();
            OpenAI = new OpenAITTSConfiguration();
            DIY = new DIYTTSConfiguration();
            GPTSoVITS = new GPTSoVITSTTSConfiguration();
            Free = new FreeTTSConfiguration();

            MarkAsModified();
        }
    }

    // TTS子配置类
    public class URLTTSConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "URL TTS";

        public string BaseUrl { get; set; } = "https://www.example.com";
        public string Voice { get; set; } = "36";
        public string Method { get; set; } = "GET";

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(BaseUrl))
            {
                result.AddError("Base URL不能为空");
            }
            else if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
            {
                result.AddError("Base URL格式无效");
            }

            if (Method != "GET" && Method != "POST")
            {
                result.AddError("HTTP方法必须是GET或POST");
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new URLTTSConfiguration
            {
                BaseUrl = BaseUrl,
                Voice = Voice,
                Method = Method,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is URLTTSConfiguration otherURL)
            {
                BaseUrl = otherURL.BaseUrl;
                Voice = otherURL.Voice;
                Method = otherURL.Method;
                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            BaseUrl = "https://www.example.com";
            Voice = "36";
            Method = "GET";
            MarkAsModified();
        }
    }

    public class OpenAITTSConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "OpenAI TTS";

        public string ApiKey { get; set; } = "";
        public string BaseUrl { get; set; } = "https://api.fish.audio/v1";
        public string Model { get; set; } = "tts-1";
        public string Voice { get; set; } = "alloy";
        public string Format { get; set; } = "mp3";

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
            return new OpenAITTSConfiguration
            {
                ApiKey = ApiKey,
                BaseUrl = BaseUrl,
                Model = Model,
                Voice = Voice,
                Format = Format,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is OpenAITTSConfiguration otherOpenAI)
            {
                ApiKey = otherOpenAI.ApiKey;
                BaseUrl = otherOpenAI.BaseUrl;
                Model = otherOpenAI.Model;
                Voice = otherOpenAI.Voice;
                Format = otherOpenAI.Format;
                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            ApiKey = "";
            BaseUrl = "https://api.fish.audio/v1";
            Model = "tts-1";
            Voice = "alloy";
            Format = "mp3";
            MarkAsModified();
        }
    }

    public class DIYTTSConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "DIY TTS";

        public string BaseUrl { get; set; } = "https://api.example.com/tts";
        public string Method { get; set; } = "POST";
        public string ContentType { get; set; } = "application/json";
        public string RequestBody { get; set; } = "{\n  \"text\": \"{text}\",\n  \"voice\": \"default\",\n  \"format\": \"mp3\"\n}";
        public List<CustomHeaderConfiguration> CustomHeaders { get; set; } = new();
        public string ResponseFormat { get; set; } = "mp3";

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(BaseUrl))
            {
                result.AddError("Base URL不能为空");
            }
            else if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
            {
                result.AddError("Base URL格式无效");
            }

            if (Method != "GET" && Method != "POST")
            {
                result.AddError("HTTP方法必须是GET或POST");
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new DIYTTSConfiguration
            {
                BaseUrl = BaseUrl,
                Method = Method,
                ContentType = ContentType,
                RequestBody = RequestBody,
                CustomHeaders = CustomHeaders?.Select(h => h.Clone() as CustomHeaderConfiguration).ToList() ?? new(),
                ResponseFormat = ResponseFormat,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is DIYTTSConfiguration otherDIY)
            {
                BaseUrl = otherDIY.BaseUrl;
                Method = otherDIY.Method;
                ContentType = otherDIY.ContentType;
                RequestBody = otherDIY.RequestBody;
                CustomHeaders = otherDIY.CustomHeaders?.Select(h => h.Clone() as CustomHeaderConfiguration).ToList() ?? new();
                ResponseFormat = otherDIY.ResponseFormat;
                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            BaseUrl = "https://api.example.com/tts";
            Method = "POST";
            ContentType = "application/json";
            RequestBody = "{\n  \"text\": \"{text}\",\n  \"voice\": \"default\",\n  \"format\": \"mp3\"\n}";
            CustomHeaders = new();
            ResponseFormat = "mp3";
            MarkAsModified();
        }
    }

    public class CustomHeaderConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "Custom Header";

        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
        public bool IsEnabled { get; set; } = true;

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (IsEnabled && string.IsNullOrWhiteSpace(Key))
            {
                result.AddError("Header Key不能为空");
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new CustomHeaderConfiguration
            {
                Key = Key,
                Value = Value,
                IsEnabled = IsEnabled,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is CustomHeaderConfiguration otherHeader)
            {
                Key = otherHeader.Key;
                Value = otherHeader.Value;
                IsEnabled = otherHeader.IsEnabled;
                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            Key = "";
            Value = "";
            IsEnabled = true;
            MarkAsModified();
        }
    }

    public enum GPTSoVITSApiMode
    {
        WebUI,
        ApiV2
    }

    public class GPTSoVITSTTSConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "GPT-SoVITS TTS";

        // 通用设置
        public string BaseUrl { get; set; } = "http://127.0.0.1:9880";
        public GPTSoVITSApiMode ApiMode { get; set; } = GPTSoVITSApiMode.WebUI;

        // WebUI模式设置
        public new string Version { get; set; } = "v4";
        public string ModelName { get; set; } = "";
        public string Emotion { get; set; } = "默认";
        public string ReferWavPath { get; set; } = "";
        public string PromptText { get; set; } = "";
        public string PromptLanguage { get; set; } = "中文";
        public string TextLanguage { get; set; } = "中文";
        public string TextSplitMethod { get; set; } = "按标点符号切";
        public string CutPunc { get; set; } = "";

        // 通用推理参数
        public int TopK { get; set; } = 15;
        public double TopP { get; set; } = 1.0;
        public double Temperature { get; set; } = 1.0;
        public double Speed { get; set; } = 1.0;

        // API v2模式设置
        public string RefAudioPath { get; set; } = "";
        public string PromptTextV2 { get; set; } = "";
        public string PromptLangV2 { get; set; } = "zh";
        public string TextLangV2 { get; set; } = "zh";
        public string TextSplitMethodV2 { get; set; } = "cut5";
        public int BatchSize { get; set; } = 1;
        public int StreamingMode { get; set; } = 0;
        public int SampleSteps { get; set; } = 32;
        public double RepetitionPenalty { get; set; } = 1.35;
        public bool SuperSampling { get; set; } = false;
        public string MediaType { get; set; } = "wav";

        // 模型权重路径
        public string GptWeightsPath { get; set; } = "";
        public string SovitsWeightsPath { get; set; } = "";

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(BaseUrl))
            {
                result.AddError("Base URL不能为空");
            }
            else if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
            {
                result.AddError("Base URL格式无效");
            }

            if (Temperature < 0 || Temperature > 2)
            {
                result.AddError("Temperature必须在0-2之间");
            }

            if (TopP < 0 || TopP > 1)
            {
                result.AddError("TopP必须在0-1之间");
            }

            if (Speed <= 0)
            {
                result.AddError("Speed必须大于0");
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new GPTSoVITSTTSConfiguration
            {
                BaseUrl = BaseUrl,
                ApiMode = ApiMode,
                Version = Version,
                ModelName = ModelName,
                Emotion = Emotion,
                ReferWavPath = ReferWavPath,
                PromptText = PromptText,
                PromptLanguage = PromptLanguage,
                TextLanguage = TextLanguage,
                TextSplitMethod = TextSplitMethod,
                CutPunc = CutPunc,
                TopK = TopK,
                TopP = TopP,
                Temperature = Temperature,
                Speed = Speed,
                RefAudioPath = RefAudioPath,
                PromptTextV2 = PromptTextV2,
                PromptLangV2 = PromptLangV2,
                TextLangV2 = TextLangV2,
                TextSplitMethodV2 = TextSplitMethodV2,
                BatchSize = BatchSize,
                StreamingMode = StreamingMode,
                SampleSteps = SampleSteps,
                RepetitionPenalty = RepetitionPenalty,
                SuperSampling = SuperSampling,
                MediaType = MediaType,
                GptWeightsPath = GptWeightsPath,
                SovitsWeightsPath = SovitsWeightsPath,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is GPTSoVITSTTSConfiguration otherGPT)
            {
                BaseUrl = otherGPT.BaseUrl;
                ApiMode = otherGPT.ApiMode;
                Version = otherGPT.Version;
                ModelName = otherGPT.ModelName;
                Emotion = otherGPT.Emotion;
                ReferWavPath = otherGPT.ReferWavPath;
                PromptText = otherGPT.PromptText;
                PromptLanguage = otherGPT.PromptLanguage;
                TextLanguage = otherGPT.TextLanguage;
                TextSplitMethod = otherGPT.TextSplitMethod;
                CutPunc = otherGPT.CutPunc;
                TopK = otherGPT.TopK;
                TopP = otherGPT.TopP;
                Temperature = otherGPT.Temperature;
                Speed = otherGPT.Speed;
                RefAudioPath = otherGPT.RefAudioPath;
                PromptTextV2 = otherGPT.PromptTextV2;
                PromptLangV2 = otherGPT.PromptLangV2;
                TextLangV2 = otherGPT.TextLangV2;
                TextSplitMethodV2 = otherGPT.TextSplitMethodV2;
                BatchSize = otherGPT.BatchSize;
                StreamingMode = otherGPT.StreamingMode;
                SampleSteps = otherGPT.SampleSteps;
                RepetitionPenalty = otherGPT.RepetitionPenalty;
                SuperSampling = otherGPT.SuperSampling;
                MediaType = otherGPT.MediaType;
                GptWeightsPath = otherGPT.GptWeightsPath;
                SovitsWeightsPath = otherGPT.SovitsWeightsPath;
                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            BaseUrl = "http://127.0.0.1:9880";
            ApiMode = GPTSoVITSApiMode.WebUI;
            Version = "v4";
            ModelName = "";
            Emotion = "默认";
            ReferWavPath = "";
            PromptText = "";
            PromptLanguage = "中文";
            TextLanguage = "中文";
            TextSplitMethod = "按标点符号切";
            CutPunc = "";
            TopK = 15;
            TopP = 1.0;
            Temperature = 1.0;
            Speed = 1.0;
            RefAudioPath = "";
            PromptTextV2 = "";
            PromptLangV2 = "zh";
            TextLangV2 = "zh";
            TextSplitMethodV2 = "cut5";
            BatchSize = 1;
            StreamingMode = 0;
            SampleSteps = 32;
            RepetitionPenalty = 1.35;
            SuperSampling = false;
            MediaType = "wav";
            GptWeightsPath = "";
            SovitsWeightsPath = "";
            MarkAsModified();
        }
    }

    public class FreeTTSConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "Free TTS";

        public override SettingsValidationResult Validate()
        {
            return new SettingsValidationResult { IsValid = true };
        }

        public override IConfiguration Clone()
        {
            return new FreeTTSConfiguration
            {
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            // Free TTS没有配置项需要合并
        }

        public override void ResetToDefaults()
        {
            // Free TTS没有配置项需要重置
        }
    }
}