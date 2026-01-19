using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using VPetLLM.Infrastructure.Configuration.Configurations;
using VPetLLM.Infrastructure.Logging;

namespace VPetLLM.Infrastructure.Configuration
{
    /// <summary>
    /// 配置迁移器 - 负责将旧版本的配置迁移到新的模块化配置结构
    /// </summary>
    public class ConfigurationMigrator
    {
        private readonly string _legacyConfigPath;
        private readonly string _newConfigBasePath;
        private readonly IStructuredLogger _logger;
        private readonly Dictionary<string, Func<JObject, IConfiguration>> _migrationHandlers;

        public ConfigurationMigrator(string legacyConfigPath, string newConfigBasePath, IStructuredLogger logger = null)
        {
            _legacyConfigPath = legacyConfigPath ?? throw new ArgumentNullException(nameof(legacyConfigPath));
            _newConfigBasePath = newConfigBasePath ?? throw new ArgumentNullException(nameof(newConfigBasePath));
            _logger = logger;

            // 初始化迁移处理器
            _migrationHandlers = new Dictionary<string, Func<JObject, IConfiguration>>
            {
                { "LLM", MigrateLLMConfiguration },
                { "TTS", MigrateTTSConfiguration },
                { "ASR", MigrateASRConfiguration },
                { "Proxy", MigrateProxyConfiguration },
                { "Application", MigrateApplicationConfiguration }
            };
        }

        /// <summary>
        /// 检查是否需要迁移
        /// </summary>
        public bool NeedsMigration()
        {
            // 检查旧配置文件是否存在
            if (!File.Exists(_legacyConfigPath))
            {
                return false;
            }

            // 检查新配置目录是否已存在配置文件
            if (!Directory.Exists(_newConfigBasePath))
            {
                return true;
            }

            var configFiles = Directory.GetFiles(_newConfigBasePath, "*.json");
            return configFiles.Length == 0;
        }

        /// <summary>
        /// 执行配置迁移
        /// </summary>
        public async Task<MigrationResult> MigrateAsync()
        {
            var result = new MigrationResult();

            try
            {
                _logger?.LogInformation("Starting configuration migration", new
                {
                    LegacyPath = _legacyConfigPath,
                    NewBasePath = _newConfigBasePath
                });

                // 读取旧配置文件
                var legacyJson = await File.ReadAllTextAsync(_legacyConfigPath);
                var legacyConfig = JObject.Parse(legacyJson);

                // 确保新配置目录存在
                Directory.CreateDirectory(_newConfigBasePath);

                // 执行各个模块的迁移
                foreach (var handler in _migrationHandlers)
                {
                    try
                    {
                        var configuration = handler.Value(legacyConfig);
                        var fileName = $"{handler.Key}.json";
                        var filePath = Path.Combine(_newConfigBasePath, fileName);

                        // 序列化并保存新配置
                        var json = JsonConvert.SerializeObject(configuration, Formatting.Indented);
                        await File.WriteAllTextAsync(filePath, json);

                        result.MigratedConfigurations.Add(handler.Key);
                        _logger?.LogInformation("Configuration migrated successfully", new
                        {
                            ConfigurationType = handler.Key,
                            FilePath = filePath
                        });
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"Failed to migrate {handler.Key}: {ex.Message}");
                        _logger?.LogError(ex, "Failed to migrate configuration", new
                        {
                            ConfigurationType = handler.Key
                        });
                    }
                }

                // 创建备份
                await CreateBackupAsync();
                result.BackupCreated = true;

                result.Success = result.Errors.Count == 0;
                result.Message = result.Success
                    ? $"Successfully migrated {result.MigratedConfigurations.Count} configurations"
                    : $"Migration completed with {result.Errors.Count} errors";

                _logger?.LogInformation("Configuration migration completed", new
                {
                    Success = result.Success,
                    MigratedCount = result.MigratedConfigurations.Count,
                    ErrorCount = result.Errors.Count
                });

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Migration failed: {ex.Message}";
                result.Errors.Add(ex.Message);

                _logger?.LogError(ex, "Configuration migration failed");
                return result;
            }
        }

        /// <summary>
        /// 迁移所有配置（同步版本）
        /// </summary>
        public void MigrateAllConfigurations()
        {
            try
            {
                if (NeedsMigration())
                {
                    var result = MigrateAsync().GetAwaiter().GetResult();
                    if (!result.Success)
                    {
                        _logger?.LogWarning("Configuration migration completed with errors", new { Errors = result.Errors });
                    }
                }
                else
                {
                    _logger?.LogInformation("Configuration migration not needed");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to migrate configurations");
            }
        }

        /// <summary>
        /// 迁移LLM配置
        /// </summary>
        private IConfiguration MigrateLLMConfiguration(JObject legacyConfig)
        {
            var config = new LLMConfiguration();

            // 迁移基本设置
            config.Provider = GetEnumValue<LLMProviderType>(legacyConfig, "Provider", LLMProviderType.Ollama);
            config.AiName = GetStringValue(legacyConfig, "AiName", "虚拟宠物");
            config.UserName = GetStringValue(legacyConfig, "UserName", "主人");
            config.Role = GetStringValue(legacyConfig, "Role", "你是一个可爱的虚拟宠物助手，请用友好、可爱的语气回应我。");
            config.FollowVPetName = GetBoolValue(legacyConfig, "FollowVPetName", true);
            config.KeepContext = GetBoolValue(legacyConfig, "KeepContext", true);
            config.EnableChatHistory = GetBoolValue(legacyConfig, "EnableChatHistory", true);
            config.SeparateChatByProvider = GetBoolValue(legacyConfig, "SeparateChatByProvider", false);
            config.ReduceInputTokenUsage = GetBoolValue(legacyConfig, "ReduceInputTokenUsage", false);

            // 迁移历史压缩设置
            config.EnableHistoryCompression = GetBoolValue(legacyConfig, "EnableHistoryCompression", false);
            config.CompressionMode = GetEnumValue<CompressionTriggerMode>(legacyConfig, "CompressionMode", CompressionTriggerMode.MessageCount);
            config.HistoryCompressionThreshold = GetIntValue(legacyConfig, "HistoryCompressionThreshold", 20);
            config.HistoryCompressionTokenThreshold = GetIntValue(legacyConfig, "HistoryCompressionTokenThreshold", 4000);

            // 迁移提供商设置
            MigrateProviderSettings(legacyConfig, config);

            return config;
        }

        /// <summary>
        /// 迁移提供商设置
        /// </summary>
        private void MigrateProviderSettings(JObject legacyConfig, LLMConfiguration config)
        {
            // 迁移Ollama设置
            var ollamaToken = legacyConfig["Ollama"];
            if (ollamaToken != null)
            {
                config.Ollama.Url = GetStringValue(ollamaToken, "Url", "http://localhost:11434");
                config.Ollama.Model = GetStringValue(ollamaToken, "Model", "");
                config.Ollama.Temperature = GetDoubleValue(ollamaToken, "Temperature", 0.7);
                config.Ollama.MaxTokens = GetIntValue(ollamaToken, "MaxTokens", 2048);
                config.Ollama.EnableAdvanced = GetBoolValue(ollamaToken, "EnableAdvanced", false);
                config.Ollama.EnableStreaming = GetBoolValue(ollamaToken, "EnableStreaming", false);
                config.Ollama.EnableVision = GetBoolValue(ollamaToken, "EnableVision", false);
            }

            // 迁移OpenAI设置 - 简化为单节点配置
            var openAIToken = legacyConfig["OpenAI"];
            if (openAIToken != null)
            {
                config.OpenAI.ApiKey = GetStringValue(openAIToken, "ApiKey", "");
                config.OpenAI.Model = GetStringValue(openAIToken, "Model", "");
                config.OpenAI.Url = GetStringValue(openAIToken, "Url", "https://api.openai.com/v1");
                config.OpenAI.Temperature = GetDoubleValue(openAIToken, "Temperature", 0.7);
                config.OpenAI.MaxTokens = GetIntValue(openAIToken, "MaxTokens", 2048);
                config.OpenAI.EnableAdvanced = GetBoolValue(openAIToken, "EnableAdvanced", false);
                config.OpenAI.EnableStreaming = GetBoolValue(openAIToken, "EnableStreaming", false);
                config.OpenAI.EnableVision = GetBoolValue(openAIToken, "EnableVision", false);
            }

            // 迁移Gemini设置 - 简化为单节点配置
            var geminiToken = legacyConfig["Gemini"];
            if (geminiToken != null)
            {
                config.Gemini.ApiKey = GetStringValue(geminiToken, "ApiKey", "");
                config.Gemini.Model = GetStringValue(geminiToken, "Model", "gemini-pro");
                config.Gemini.Url = GetStringValue(geminiToken, "Url", "https://generativelanguage.googleapis.com/v1beta");
                config.Gemini.Temperature = GetDoubleValue(geminiToken, "Temperature", 0.7);
                config.Gemini.MaxTokens = GetIntValue(geminiToken, "MaxTokens", 2048);
                config.Gemini.EnableAdvanced = GetBoolValue(geminiToken, "EnableAdvanced", false);
                config.Gemini.EnableStreaming = GetBoolValue(geminiToken, "EnableStreaming", false);
                config.Gemini.EnableVision = GetBoolValue(geminiToken, "EnableVision", false);
            }

            // 迁移Free设置
            var freeToken = legacyConfig["Free"];
            if (freeToken != null)
            {
                config.Free.Model = GetStringValue(freeToken, "Model", "");
                config.Free.Temperature = GetDoubleValue(freeToken, "Temperature", 0.7);
                config.Free.MaxTokens = GetIntValue(freeToken, "MaxTokens", 2048);
                config.Free.EnableAdvanced = GetBoolValue(freeToken, "EnableAdvanced", false);
                config.Free.EnableStreaming = GetBoolValue(freeToken, "EnableStreaming", false);
                config.Free.EnableVision = GetBoolValue(freeToken, "EnableVision", false);
            }
        }

        /// <summary>
        /// 迁移TTS配置
        /// </summary>
        private IConfiguration MigrateTTSConfiguration(JObject legacyConfig)
        {
            var config = new TTSConfiguration();
            var ttsToken = legacyConfig["TTS"];

            if (ttsToken != null)
            {
                config.IsEnabled = GetBoolValue(ttsToken, "IsEnabled", false);
                config.Provider = GetStringValue(ttsToken, "Provider", "URL");
                config.OnlyPlayAIResponse = GetBoolValue(ttsToken, "OnlyPlayAIResponse", true);
                config.AutoPlay = GetBoolValue(ttsToken, "AutoPlay", true);
                config.Volume = GetDoubleValue(ttsToken, "Volume", 100);
                config.Speed = GetDoubleValue(ttsToken, "Speed", 1.0);
                config.VolumeGain = GetDoubleValue(ttsToken, "VolumeGain", 0.0);
                config.UseQueueDownload = GetBoolValue(ttsToken, "UseQueueDownload", false);

                // 迁移提供商特定设置
                MigrateTTSProviderSettings(ttsToken, config);
            }

            return config;
        }

        /// <summary>
        /// 迁移TTS提供商设置
        /// </summary>
        private void MigrateTTSProviderSettings(JToken ttsToken, TTSConfiguration config)
        {
            // 迁移URL TTS设置
            var urlToken = ttsToken["URL"];
            if (urlToken != null)
            {
                config.URL.BaseUrl = GetStringValue(urlToken, "BaseUrl", "https://www.example.com");
                config.URL.Voice = GetStringValue(urlToken, "Voice", "36");
                config.URL.Method = GetStringValue(urlToken, "Method", "GET");
            }

            // 迁移OpenAI TTS设置
            var openAIToken = ttsToken["OpenAI"];
            if (openAIToken != null)
            {
                config.OpenAI.ApiKey = GetStringValue(openAIToken, "ApiKey", "");
                config.OpenAI.BaseUrl = GetStringValue(openAIToken, "BaseUrl", "https://api.fish.audio/v1");
                config.OpenAI.Model = GetStringValue(openAIToken, "Model", "tts-1");
                config.OpenAI.Voice = GetStringValue(openAIToken, "Voice", "alloy");
                config.OpenAI.Format = GetStringValue(openAIToken, "Format", "mp3");
            }

            // 迁移DIY TTS设置
            var diyToken = ttsToken["DIY"];
            if (diyToken != null)
            {
                config.DIY.BaseUrl = GetStringValue(diyToken, "BaseUrl", "https://api.example.com/tts");
                config.DIY.Method = GetStringValue(diyToken, "Method", "POST");
                config.DIY.ContentType = GetStringValue(diyToken, "ContentType", "application/json");
                config.DIY.RequestBody = GetStringValue(diyToken, "RequestBody", "{\n  \"text\": \"{text}\",\n  \"voice\": \"default\",\n  \"format\": \"mp3\"\n}");
                config.DIY.ResponseFormat = GetStringValue(diyToken, "ResponseFormat", "mp3");

                var headersToken = diyToken["CustomHeaders"];
                if (headersToken != null && headersToken.Type == JTokenType.Array)
                {
                    config.DIY.CustomHeaders.Clear();
                    foreach (var headerToken in headersToken)
                    {
                        config.DIY.CustomHeaders.Add(new CustomHeaderConfiguration
                        {
                            Key = GetStringValue(headerToken, "Key", ""),
                            Value = GetStringValue(headerToken, "Value", ""),
                            IsEnabled = GetBoolValue(headerToken, "IsEnabled", true)
                        });
                    }
                }
            }

            // 迁移GPT-SoVITS设置
            var gptSoVITSToken = ttsToken["GPTSoVITS"];
            if (gptSoVITSToken != null)
            {
                config.GPTSoVITS.BaseUrl = GetStringValue(gptSoVITSToken, "BaseUrl", "http://127.0.0.1:9880");
                config.GPTSoVITS.ApiMode = GetEnumValue<GPTSoVITSApiMode>(gptSoVITSToken, "ApiMode", GPTSoVITSApiMode.WebUI);
                config.GPTSoVITS.Version = GetStringValue(gptSoVITSToken, "Version", "v4");
                config.GPTSoVITS.ModelName = GetStringValue(gptSoVITSToken, "ModelName", "");
                config.GPTSoVITS.Emotion = GetStringValue(gptSoVITSToken, "Emotion", "默认");
                config.GPTSoVITS.ReferWavPath = GetStringValue(gptSoVITSToken, "ReferWavPath", "");
                config.GPTSoVITS.PromptText = GetStringValue(gptSoVITSToken, "PromptText", "");
                config.GPTSoVITS.TextLanguage = GetStringValue(gptSoVITSToken, "TextLanguage", "中文");
                config.GPTSoVITS.TextSplitMethod = GetStringValue(gptSoVITSToken, "TextSplitMethod", "按标点符号切");
                config.GPTSoVITS.TopK = GetIntValue(gptSoVITSToken, "TopK", 15);
                config.GPTSoVITS.TopP = GetDoubleValue(gptSoVITSToken, "TopP", 1.0);
                config.GPTSoVITS.Temperature = GetDoubleValue(gptSoVITSToken, "Temperature", 1.0);
                config.GPTSoVITS.Speed = GetDoubleValue(gptSoVITSToken, "Speed", 1.0);
            }
        }

        /// <summary>
        /// 迁移ASR配置
        /// </summary>
        private IConfiguration MigrateASRConfiguration(JObject legacyConfig)
        {
            var config = new ASRConfiguration();
            var asrToken = legacyConfig["ASR"];

            if (asrToken != null)
            {
                config.IsEnabled = GetBoolValue(asrToken, "IsEnabled", false);
                config.Provider = GetStringValue(asrToken, "Provider", "OpenAI");
                config.Language = GetStringValue(asrToken, "Language", "zh");
                config.AutoSend = GetBoolValue(asrToken, "AutoSend", true);
                config.ShowTranscriptionWindow = GetBoolValue(asrToken, "ShowTranscriptionWindow", true);
                config.RecordingDeviceNumber = GetIntValue(asrToken, "RecordingDeviceNumber", 0);

                // 迁移热键设置
                config.HotkeyModifiers = GetStringValue(asrToken, "HotkeyModifiers", "Win+Alt");
                config.HotkeyKey = GetStringValue(asrToken, "HotkeyKey", "V");

                // 迁移提供商设置
                MigrateASRProviderSettings(asrToken, config);
            }

            return config;
        }

        /// <summary>
        /// 迁移ASR提供商设置
        /// </summary>
        private void MigrateASRProviderSettings(JToken asrToken, ASRConfiguration config)
        {
            // 迁移OpenAI设置
            var openAIToken = asrToken["OpenAI"];
            if (openAIToken != null)
            {
                config.OpenAI.ApiKey = GetStringValue(openAIToken, "ApiKey", "");
                config.OpenAI.BaseUrl = GetStringValue(openAIToken, "BaseUrl", "https://api.openai.com/v1");
                config.OpenAI.Model = GetStringValue(openAIToken, "Model", "whisper-1");
            }

            // 迁移Soniox设置
            var sonioxToken = asrToken["Soniox"];
            if (sonioxToken != null)
            {
                config.Soniox.ApiKey = GetStringValue(sonioxToken, "ApiKey", "");
                config.Soniox.BaseUrl = GetStringValue(sonioxToken, "BaseUrl", "https://api.soniox.com");
                config.Soniox.Model = GetStringValue(sonioxToken, "Model", "stt-rt-v3");
                config.Soniox.EnablePunctuation = GetBoolValue(sonioxToken, "EnablePunctuation", true);
                config.Soniox.EnableProfanityFilter = GetBoolValue(sonioxToken, "EnableProfanityFilter", false);
            }
        }

        /// <summary>
        /// 迁移代理配置
        /// </summary>
        private IConfiguration MigrateProxyConfiguration(JObject legacyConfig)
        {
            var config = new ProxyConfiguration();
            var proxyToken = legacyConfig["Proxy"];

            if (proxyToken != null)
            {
                config.IsEnabled = GetBoolValue(proxyToken, "IsEnabled", false);
                config.FollowSystemProxy = GetBoolValue(proxyToken, "FollowSystemProxy", false);
                config.Protocol = GetStringValue(proxyToken, "Protocol", "http");
                config.Address = GetStringValue(proxyToken, "Address", "127.0.0.1:8080");
                config.ForAllAPI = GetBoolValue(proxyToken, "ForAllAPI", false);
                config.ForOllama = GetBoolValue(proxyToken, "ForOllama", false);
                config.ForOpenAI = GetBoolValue(proxyToken, "ForOpenAI", false);
                config.ForGemini = GetBoolValue(proxyToken, "ForGemini", false);
                config.ForFree = GetBoolValue(proxyToken, "ForFree", false);
                config.ForTTS = GetBoolValue(proxyToken, "ForTTS", false);
                config.ForASR = GetBoolValue(proxyToken, "ForASR", false);
                config.ForMcp = GetBoolValue(proxyToken, "ForMcp", false);
                config.ForPlugin = GetBoolValue(proxyToken, "ForPlugin", false);
            }

            return config;
        }

        /// <summary>
        /// 迁移应用程序配置
        /// </summary>
        private IConfiguration MigrateApplicationConfiguration(JObject legacyConfig)
        {
            var config = new ApplicationConfiguration();

            // 迁移基本应用设置
            config.Language = GetStringValue(legacyConfig, "Language", "zh-hans");
            config.PromptLanguage = GetStringValue(legacyConfig, "PromptLanguage", "zh");
            config.LogAutoScroll = GetBoolValue(legacyConfig, "LogAutoScroll", true);
            config.MaxLogCount = GetIntValue(legacyConfig, "MaxLogCount", 1000);
            config.EnableAction = GetBoolValue(legacyConfig, "EnableAction", true);
            config.EnableBuy = GetBoolValue(legacyConfig, "EnableBuy", true);
            config.EnableState = GetBoolValue(legacyConfig, "EnableState", true);
            config.EnableExtendedState = GetBoolValue(legacyConfig, "EnableExtendedState", false);
            config.EnableActionExecution = GetBoolValue(legacyConfig, "EnableActionExecution", true);
            config.SayTimeMultiplier = GetIntValue(legacyConfig, "SayTimeMultiplier", 200);
            config.SayTimeMin = GetIntValue(legacyConfig, "SayTimeMin", 2000);
            config.EnableMove = GetBoolValue(legacyConfig, "EnableMove", true);
            config.EnableTime = GetBoolValue(legacyConfig, "EnableTime", true);
            config.EnablePlugin = GetBoolValue(legacyConfig, "EnablePlugin", true);
            config.ShowUninstallWarning = GetBoolValue(legacyConfig, "ShowUninstallWarning", true);
            config.EnableBuyFeedback = GetBoolValue(legacyConfig, "EnableBuyFeedback", true);
            config.EnableLiveMode = GetBoolValue(legacyConfig, "EnableLiveMode", false);
            config.LimitStateChanges = GetBoolValue(legacyConfig, "LimitStateChanges", true);
            config.EnableVPetSettingsControl = GetBoolValue(legacyConfig, "EnableVPetSettingsControl", false);
            config.EnableStreamingBatch = GetBoolValue(legacyConfig, "EnableStreamingBatch", true);
            config.StreamingBatchWindowMs = GetIntValue(legacyConfig, "StreamingBatchWindowMs", 100);
            config.EnableMediaPlayback = GetBoolValue(legacyConfig, "EnableMediaPlayback", true);

            // 迁移工具设置
            var toolsToken = legacyConfig["Tools"];
            if (toolsToken != null && toolsToken.Type == JTokenType.Array)
            {
                foreach (var toolToken in toolsToken)
                {
                    config.Tools.Add(new ToolConfiguration
                    {
                        Name = GetStringValue(toolToken, "Name", ""),
                        Url = GetStringValue(toolToken, "Url", ""),
                        ApiKey = GetStringValue(toolToken, "ApiKey", ""),
                        Description = GetStringValue(toolToken, "Description", ""),
                        IsEnabled = GetBoolValue(toolToken, "IsEnabled", true)
                    });
                }
            }

            // 迁移速率限制设置
            var rateLimiterToken = legacyConfig["RateLimiter"];
            if (rateLimiterToken != null)
            {
                config.RateLimiter.EnableToolRateLimit = GetBoolValue(rateLimiterToken, "EnableToolRateLimit", true);
                config.RateLimiter.ToolMaxCount = GetIntValue(rateLimiterToken, "ToolMaxCount", 5);
                config.RateLimiter.ToolWindowMinutes = GetIntValue(rateLimiterToken, "ToolWindowMinutes", 2);
                config.RateLimiter.EnablePluginRateLimit = GetBoolValue(rateLimiterToken, "EnablePluginRateLimit", true);
                config.RateLimiter.PluginMaxCount = GetIntValue(rateLimiterToken, "PluginMaxCount", 5);
                config.RateLimiter.PluginWindowMinutes = GetIntValue(rateLimiterToken, "PluginWindowMinutes", 2);
                config.RateLimiter.LogRateLimitEvents = GetBoolValue(rateLimiterToken, "LogRateLimitEvents", true);
            }

            // 迁移记录设置
            var recordsToken = legacyConfig["Records"];
            if (recordsToken != null)
            {
                config.Records.EnableRecords = GetBoolValue(recordsToken, "EnableRecords", true);
                config.Records.MaxRecordsInContext = GetIntValue(recordsToken, "MaxRecordsInContext", 20);
                config.Records.AutoDecrementWeights = GetBoolValue(recordsToken, "AutoDecrementWeights", true);
                config.Records.MaxRecordContentLength = GetIntValue(recordsToken, "MaxRecordContentLength", 500);
                config.Records.InjectIntoSummary = GetBoolValue(recordsToken, "InjectIntoSummary", false);
                config.Records.WeightDecayTurns = GetIntValue(recordsToken, "WeightDecayTurns", 1);
                config.Records.MaxRecordsLimit = GetIntValue(recordsToken, "MaxRecordsLimit", 10);
            }

            // 迁移媒体播放设置
            var mediaPlaybackToken = legacyConfig["MediaPlayback"];
            if (mediaPlaybackToken != null)
            {
                config.MediaPlayback.DefaultVolume = GetIntValue(mediaPlaybackToken, "DefaultVolume", 100);
                config.MediaPlayback.MonitorWindowVisibility = GetBoolValue(mediaPlaybackToken, "MonitorWindowVisibility", true);
                config.MediaPlayback.WindowCheckIntervalMs = GetIntValue(mediaPlaybackToken, "WindowCheckIntervalMs", 1000);
                config.MediaPlayback.MpvPath = GetStringValue(mediaPlaybackToken, "MpvPath", "");
            }

            // 迁移插件商店设置
            var pluginStoreToken = legacyConfig["PluginStore"];
            if (pluginStoreToken != null)
            {
                config.PluginStore.UseProxy = GetBoolValue(pluginStoreToken, "UseProxy", true);
                config.PluginStore.ProxyUrl = GetStringValue(pluginStoreToken, "ProxyUrl", "https://ghfast.top");
            }

            return config;
        }

        /// <summary>
        /// 创建旧配置文件的备份
        /// </summary>
        private async Task CreateBackupAsync()
        {
            var backupPath = $"{_legacyConfigPath}.backup.{DateTime.Now:yyyyMMdd_HHmmss}";
            var content = await File.ReadAllTextAsync(_legacyConfigPath);
            await File.WriteAllTextAsync(backupPath, content);

            _logger?.LogInformation("Legacy configuration backup created", new
            {
                OriginalPath = _legacyConfigPath,
                BackupPath = backupPath
            });
        }

        #region Helper Methods

        private string GetStringValue(JToken token, string propertyName, string defaultValue)
        {
            return token?[propertyName]?.Value<string>() ?? defaultValue;
        }

        private bool GetBoolValue(JToken token, string propertyName, bool defaultValue)
        {
            return token?[propertyName]?.Value<bool>() ?? defaultValue;
        }

        private int GetIntValue(JToken token, string propertyName, int defaultValue)
        {
            return token?[propertyName]?.Value<int>() ?? defaultValue;
        }

        private double GetDoubleValue(JToken token, string propertyName, double defaultValue)
        {
            return token?[propertyName]?.Value<double>() ?? defaultValue;
        }

        private T GetEnumValue<T>(JToken token, string propertyName, T defaultValue) where T : struct, Enum
        {
            var stringValue = token?[propertyName]?.Value<string>();
            if (string.IsNullOrEmpty(stringValue))
                return defaultValue;

            return Enum.TryParse<T>(stringValue, true, out var result) ? result : defaultValue;
        }

        #endregion
    }

    /// <summary>
    /// 迁移结果
    /// </summary>
    public class MigrationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public List<string> MigratedConfigurations { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public bool BackupCreated { get; set; }
    }
}