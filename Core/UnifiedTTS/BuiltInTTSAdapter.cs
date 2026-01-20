using System.Diagnostics;
using VPetLLM.Interfaces;
using VPetLLM.Models;
using VPetLLM.Utils.System;

namespace VPetLLM.Core.UnifiedTTS
{
    /// <summary>
    /// 内置 TTS 适配器
    /// 封装 VPetTTS 插件，提供统一接口
    /// </summary>
    public class BuiltInTTSAdapter : IBuiltInTTSAdapter
    {
        /// <summary>
        /// 适配器类型标识
        /// </summary>
        public string AdapterType => "BuiltIn";

        private readonly IVPetAPI _vpetAPI;
        private BuiltInTTSSettings _settings;
        private bool _isInitialized = false;
        private readonly object _settingsLock = new object();
        private AdapterHealthStatus _lastHealthStatus;

        public BuiltInTTSAdapter(IVPetAPI vpetAPI = null)
        {
            _vpetAPI = vpetAPI;
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
                Logger.Log($"BuiltInTTSAdapter: Processing request {request.RequestId} with text length {request.Text?.Length}");

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
                        "Built-in TTS adapter is not available");
                }

                // 获取设置
                BuiltInTTSSettings currentSettings;
                lock (_settingsLock)
                {
                    currentSettings = _settings;
                }

                if (currentSettings == null)
                {
                    return TTSResponse.CreateError(request.RequestId, TTSErrorCodes.ConfigurationError,
                        "Built-in TTS settings not configured");
                }

                // 处理 TTS 请求
                await ProcessTTSWithVPetAPIAsync(request.Text, currentSettings);

                stopwatch.Stop();

                // 创建成功响应（内置 TTS 通常不返回音频数据，而是直接播放）
                var response = TTSResponse.CreateSuccess(request.RequestId, null, stopwatch.Elapsed, AdapterType);
                response.AudioDurationMs = EstimateAudioDuration(request.Text, currentSettings.Speed);
                response.AudioFormat = "internal"; // 内置 TTS 不返回具体格式

                Logger.Log($"BuiltInTTSAdapter: Successfully processed request {request.RequestId} in {stopwatch.ElapsedMilliseconds}ms");
                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Logger.Log($"BuiltInTTSAdapter: Error processing request {request.RequestId}: {ex.Message}");

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
                    Logger.Log("BuiltInTTSAdapter: Adapter not initialized");
                    return false;
                }

                if (_vpetAPI == null)
                {
                    Logger.Log("BuiltInTTSAdapter: VPet API is null");
                    return false;
                }

                if (!_vpetAPI.IsAvailable)
                {
                    Logger.Log("BuiltInTTSAdapter: VPet API is not available");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"BuiltInTTSAdapter: Error checking availability: {ex.Message}");
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
                Logger.Log("BuiltInTTSAdapter: Initializing adapter");

                if (configuration is not BuiltInTTSSettings builtInSettings)
                {
                    Logger.Log("BuiltInTTSAdapter: Invalid configuration type");
                    return false;
                }

                // 验证配置
                var validationResult = builtInSettings.Validate();
                if (!validationResult.IsValid)
                {
                    Logger.Log($"BuiltInTTSAdapter: Configuration validation failed: {validationResult.ErrorMessage}");
                    return false;
                }

                // 检查 VPet API 是否可用
                if (_vpetAPI == null)
                {
                    Logger.Log("BuiltInTTSAdapter: VPet API is not provided");
                    return false;
                }

                if (!_vpetAPI.IsAvailable)
                {
                    Logger.Log("BuiltInTTSAdapter: VPet API is not available");
                    return false;
                }

                lock (_settingsLock)
                {
                    _settings = builtInSettings;
                }

                _isInitialized = true;
                _lastHealthStatus = new AdapterHealthStatus
                {
                    IsHealthy = true,
                    Message = "Initialized with VPetTTS plugin"
                };

                Logger.Log("BuiltInTTSAdapter: Successfully initialized with VPetTTS plugin");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"BuiltInTTSAdapter: Error during initialization: {ex.Message}");
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
                Logger.Log("BuiltInTTSAdapter: Cleaning up resources");

                lock (_settingsLock)
                {
                    _settings = null;
                }

                _isInitialized = false;
                _lastHealthStatus = new AdapterHealthStatus
                {
                    IsHealthy = false,
                    Message = "Cleaned up"
                };

                Logger.Log("BuiltInTTSAdapter: Cleanup completed");
            }
            catch (Exception ex)
            {
                Logger.Log($"BuiltInTTSAdapter: Error during cleanup: {ex.Message}");
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

                if (_vpetAPI == null)
                {
                    status.IsHealthy = false;
                    status.Message = "VPet API is null";
                    return status;
                }

                if (!_vpetAPI.IsAvailable)
                {
                    status.IsHealthy = false;
                    status.Message = "VPet API is not available";
                    return status;
                }

                status.IsHealthy = true;
                status.Message = "Healthy - VPetTTS plugin available";

                stopwatch.Stop();
                status.ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds;

                _lastHealthStatus = status;
                return status;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Logger.Log($"BuiltInTTSAdapter: Error getting health status: {ex.Message}");

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
        /// 使用 VPet API 处理 TTS
        /// </summary>
        /// <param name="text">要转换的文本</param>
        /// <param name="settings">内置 TTS 设置</param>
        /// <returns>处理任务</returns>
        private async Task ProcessTTSWithVPetAPIAsync(string text, BuiltInTTSSettings settings)
        {
            try
            {
                Logger.Log($"BuiltInTTSAdapter: Processing TTS with VPet API, streaming: {settings.UseStreaming}");

                // 根据设置选择处理方式
                if (settings.UseStreaming)
                {
                    await _vpetAPI.SayInfoWithStreamAsync(text, settings.VoiceProfile);
                }
                else
                {
                    await _vpetAPI.SayInfoWithOutStreamAsync(text, settings.VoiceProfile);
                }

                Logger.Log("BuiltInTTSAdapter: VPet API TTS processing completed");
            }
            catch (Exception ex)
            {
                Logger.Log($"BuiltInTTSAdapter: Error processing TTS with VPet API: {ex.Message}");
                throw new InvalidOperationException($"VPet API TTS processing failed: {ex.Message}", ex);
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

        /// <summary>
        /// 设置 VPet API 实例（用于依赖注入）
        /// </summary>
        /// <param name="vpetAPI">VPet API 实例</param>
        public void SetVPetAPI(IVPetAPI vpetAPI)
        {
            if (_vpetAPI == null && vpetAPI != null)
            {
                Logger.Log("BuiltInTTSAdapter: VPet API instance set");
            }
        }
    }
}