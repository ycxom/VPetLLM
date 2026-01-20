using System.Diagnostics;
using System.IO;
using VPetLLM.Core.TTSCore;
using VPetLLM.Models;
using VPetLLM.Utils.System;

namespace VPetLLM.Core.UnifiedTTS
{
    /// <summary>
    /// 外部 TTS 适配器
    /// 封装现有的 TTSCore 实现，提供统一接口
    /// </summary>
    public class ExternalTTSAdapter : IExternalTTSAdapter
    {
        /// <summary>
        /// 适配器类型标识
        /// </summary>
        public string AdapterType => "External";

        private TTSCoreBase _ttsCore;
        private readonly object _coreLock = new object();
        private bool _isInitialized = false;
        private DateTime _lastHealthCheck = DateTime.MinValue;
        private AdapterHealthStatus _lastHealthStatus;

        public ExternalTTSAdapter()
        {
            _lastHealthStatus = new AdapterHealthStatus
            {
                IsHealthy = false,
                Message = "Not initialized"
            };
        }

        /// <summary>
        /// 处理 TTS 请求
        /// </summary>
        /// <param name="request">TTS 请求对象</param>
        /// <returns>TTS 响应结果</returns>
        public async Task<TTSResponse> ProcessAsync(TTSRequest request)
        {
            if (request == null)
            {
                return TTSResponse.CreateError(null, TTSErrorCodes.InvalidRequest, "Request cannot be null");
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                Logger.Log($"ExternalTTSAdapter: Processing request {request.RequestId} with text length {request.Text?.Length}");

                // 验证请求
                var validationResult = request.Validate();
                if (!validationResult.IsValid)
                {
                    return TTSResponse.CreateError(request.RequestId, TTSErrorCodes.ValidationFailed,
                        validationResult.ErrorMessage, string.Join("; ", validationResult.Errors));
                }

                // 检查适配器是否可用
                if (!await IsAvailableAsync())
                {
                    return TTSResponse.CreateError(request.RequestId, TTSErrorCodes.AdapterUnavailable,
                        "External TTS adapter is not available");
                }

                // 获取 TTS Core 实例
                TTSCoreBase ttsCore;
                lock (_coreLock)
                {
                    ttsCore = _ttsCore;
                }

                if (ttsCore == null)
                {
                    return TTSResponse.CreateError(request.RequestId, TTSErrorCodes.InitializationFailed,
                        "TTS Core is not initialized");
                }

                // 调用 TTS Core 生成音频
                var audioData = await ttsCore.GenerateAudioAsync(request.Text);

                stopwatch.Stop();

                if (audioData == null || audioData.Length == 0)
                {
                    return TTSResponse.CreateError(request.RequestId, TTSErrorCodes.ProcessingError,
                        "TTS Core returned empty audio data");
                }

                // 创建成功响应
                var response = TTSResponse.CreateSuccess(request.RequestId, audioData, stopwatch.Elapsed, AdapterType);
                response.AudioFormat = ttsCore.GetAudioFormat();
                response.AudioDurationMs = EstimateAudioDuration(request.Text, request.Settings?.Speed ?? 1.0f);

                Logger.Log($"ExternalTTSAdapter: Successfully processed request {request.RequestId} in {stopwatch.ElapsedMilliseconds}ms");
                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Logger.Log($"ExternalTTSAdapter: Error processing request {request.RequestId}: {ex.Message}");

                var error = TTSError.FromException(ex, request.RequestId, AdapterType);
                return TTSResponse.CreateError(request.RequestId, error.Code, error.Message, error.Details);
            }
        }

        /// <summary>
        /// 检查适配器是否可用
        /// </summary>
        /// <returns>是否可用</returns>
        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                if (!_isInitialized)
                {
                    Logger.Log("ExternalTTSAdapter: Adapter not initialized");
                    return false;
                }

                lock (_coreLock)
                {
                    if (_ttsCore == null)
                    {
                        Logger.Log("ExternalTTSAdapter: TTS Core is null");
                        return false;
                    }
                }

                // 简单的可用性检查 - 可以扩展为更复杂的健康检查
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"ExternalTTSAdapter: Error checking availability: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 初始化适配器
        /// </summary>
        /// <param name="configuration">配置信息</param>
        /// <returns>是否初始化成功</returns>
        public async Task<bool> InitializeAsync(object configuration)
        {
            try
            {
                Logger.Log("ExternalTTSAdapter: Initializing adapter");

                if (configuration is not ExternalTTSSettings externalSettings)
                {
                    Logger.Log("ExternalTTSAdapter: Invalid configuration type");
                    return false;
                }

                // 验证配置
                var validationResult = externalSettings.Validate();
                if (!validationResult.IsValid)
                {
                    Logger.Log($"ExternalTTSAdapter: Configuration validation failed: {validationResult.ErrorMessage}");
                    return false;
                }

                // 创建相应的 TTS Core 实例
                var ttsCore = CreateTTSCore(externalSettings);
                if (ttsCore == null)
                {
                    Logger.Log($"ExternalTTSAdapter: Failed to create TTS Core for type: {externalSettings.TTSCoreType}");
                    return false;
                }

                lock (_coreLock)
                {
                    _ttsCore = ttsCore;
                }

                _isInitialized = true;
                _lastHealthStatus = new AdapterHealthStatus
                {
                    IsHealthy = true,
                    Message = $"Initialized with {externalSettings.TTSCoreType} TTS Core"
                };

                Logger.Log($"ExternalTTSAdapter: Successfully initialized with {externalSettings.TTSCoreType} TTS Core");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"ExternalTTSAdapter: Error during initialization: {ex.Message}");
                _lastHealthStatus = new AdapterHealthStatus
                {
                    IsHealthy = false,
                    Message = $"Initialization failed: {ex.Message}"
                };
                return false;
            }
        }

        /// <summary>
        /// 清理资源
        /// </summary>
        /// <returns>清理任务</returns>
        public async Task CleanupAsync()
        {
            try
            {
                Logger.Log("ExternalTTSAdapter: Cleaning up resources");

                lock (_coreLock)
                {
                    _ttsCore = null;
                }

                _isInitialized = false;
                _lastHealthStatus = new AdapterHealthStatus
                {
                    IsHealthy = false,
                    Message = "Cleaned up"
                };

                Logger.Log("ExternalTTSAdapter: Cleanup completed");
            }
            catch (Exception ex)
            {
                Logger.Log($"ExternalTTSAdapter: Error during cleanup: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取适配器健康状态
        /// </summary>
        /// <returns>健康状态信息</returns>
        public async Task<AdapterHealthStatus> GetHealthStatusAsync()
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var status = new AdapterHealthStatus();

                if (!_isInitialized)
                {
                    status.IsHealthy = false;
                    status.Message = "Adapter not initialized";
                    return status;
                }

                lock (_coreLock)
                {
                    if (_ttsCore == null)
                    {
                        status.IsHealthy = false;
                        status.Message = "TTS Core is null";
                        return status;
                    }

                    status.IsHealthy = true;
                    status.Message = $"Healthy - Using {_ttsCore.Name} TTS Core";
                }

                stopwatch.Stop();
                status.ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds;

                _lastHealthCheck = DateTime.UtcNow;
                _lastHealthStatus = status;

                return status;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Logger.Log($"ExternalTTSAdapter: Error getting health status: {ex.Message}");

                var errorStatus = new AdapterHealthStatus
                {
                    IsHealthy = false,
                    Message = $"Health check failed: {ex.Message}",
                    ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds
                };

                _lastHealthStatus = errorStatus;
                return errorStatus;
            }
        }

        /// <summary>
        /// 创建 TTS Core 实例
        /// </summary>
        /// <param name="settings">外部 TTS 设置</param>
        /// <returns>TTS Core 实例</returns>
        private TTSCoreBase CreateTTSCore(ExternalTTSSettings settings)
        {
            try
            {
                // 创建 Setting 对象（需要适配现有的 Setting 结构）
                var setting = CreateSettingFromExternalSettings(settings);

                return settings.TTSCoreType?.ToUpper() switch
                {
                    "URL" => new URLTTSCore(setting),
                    "OPENAI" => new OpenAITTSCore(setting),
                    "DIY" => new DIYTTSCore(setting),
                    "GPTSOVITS" => new GPTSoVITSTTSCore(setting),
                    "FREE" => new FreeTTSCore(setting),
                    _ => null
                };
            }
            catch (Exception ex)
            {
                Logger.Log($"ExternalTTSAdapter: Error creating TTS Core: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从外部设置创建 Setting 对象
        /// </summary>
        /// <param name="externalSettings">外部设置</param>
        /// <returns>Setting 对象</returns>
        private Setting CreateSettingFromExternalSettings(ExternalTTSSettings externalSettings)
        {
            try
            {
                // 创建一个临时的配置文件路径（不会实际使用）
                var tempPath = Path.GetTempPath();
                var setting = new Setting(tempPath);

                // 根据 TTS Core 类型设置相应的配置
                switch (externalSettings.TTSCoreType?.ToUpper())
                {
                    case "URL":
                        setting.TTS.URL = new Setting.URLTTSSetting
                        {
                            BaseUrl = externalSettings.Parameters.GetValueOrDefault("BaseUrl", "")?.ToString() ?? "",
                            Voice = externalSettings.Parameters.GetValueOrDefault("Voice", "36")?.ToString() ?? "36",
                            Method = externalSettings.Parameters.GetValueOrDefault("Method", "GET")?.ToString() ?? "GET"
                        };
                        break;

                    case "OPENAI":
                        setting.TTS.OpenAI = new Setting.OpenAITTSSetting
                        {
                            ApiKey = externalSettings.Parameters.GetValueOrDefault("ApiKey", "")?.ToString() ?? "",
                            BaseUrl = externalSettings.Parameters.GetValueOrDefault("BaseUrl", "https://api.fish.audio/v1")?.ToString() ?? "https://api.fish.audio/v1",
                            Model = externalSettings.Parameters.GetValueOrDefault("Model", "tts-1")?.ToString() ?? "tts-1",
                            Voice = externalSettings.Parameters.GetValueOrDefault("Voice", "alloy")?.ToString() ?? "alloy",
                            Format = externalSettings.Parameters.GetValueOrDefault("Format", "mp3")?.ToString() ?? "mp3"
                        };
                        break;

                    case "DIY":
                        setting.TTS.DIY = new Setting.DIYTTSSetting
                        {
                            BaseUrl = externalSettings.Parameters.GetValueOrDefault("BaseUrl", "")?.ToString() ?? "",
                            Method = externalSettings.Parameters.GetValueOrDefault("Method", "POST")?.ToString() ?? "POST",
                            ContentType = externalSettings.Parameters.GetValueOrDefault("ContentType", "application/json")?.ToString() ?? "application/json",
                            RequestBody = externalSettings.Parameters.GetValueOrDefault("RequestBody", "{\"text\": \"{text}\", \"voice\": \"default\", \"format\": \"mp3\"}")?.ToString() ?? "{\"text\": \"{text}\", \"voice\": \"default\", \"format\": \"mp3\"}",
                            ResponseFormat = externalSettings.Parameters.GetValueOrDefault("ResponseFormat", "mp3")?.ToString() ?? "mp3"
                        };
                        break;

                    case "GPTSOVITS":
                        setting.TTS.GPTSoVITS = new Setting.GPTSoVITSTTSSetting
                        {
                            BaseUrl = externalSettings.Parameters.GetValueOrDefault("BaseUrl", "")?.ToString() ?? "",
                            // 其他 GPTSoVITS 特定参数可以在这里设置
                        };
                        break;

                    case "FREE":
                        setting.TTS.Free = new Setting.FreeTTSSetting();
                        break;
                }

                // 设置通用 TTS 配置
                setting.TTS.Provider = externalSettings.TTSCoreType;
                setting.TTS.IsEnabled = true;

                return setting;
            }
            catch (Exception ex)
            {
                Logger.Log($"ExternalTTSAdapter: Error creating Setting from external settings: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 估算音频时长
        /// </summary>
        /// <param name="text">文本内容</param>
        /// <param name="speed">语速</param>
        /// <returns>估算的音频时长（毫秒）</returns>
        private int EstimateAudioDuration(string text, float speed)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            try
            {
                // 简单的时长估算：假设平均每分钟150个单词，每个单词5个字符
                var wordsPerMinute = 150 * speed;
                var charactersPerMinute = wordsPerMinute * 5;
                var charactersPerSecond = charactersPerMinute / 60.0;

                var estimatedSeconds = text.Length / charactersPerSecond;
                var estimatedMs = (int)(estimatedSeconds * 1000);

                // 添加缓冲时间并设置最小时长
                estimatedMs = (int)(estimatedMs * 1.2);
                return Math.Max(1000, estimatedMs);
            }
            catch
            {
                return 3000; // 默认3秒
            }
        }
    }
}