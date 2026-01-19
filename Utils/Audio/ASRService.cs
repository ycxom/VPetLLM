using NAudio.Wave;
using System.IO;
using System.Net;
using System.Net.Http;
using VPetLLM.Core;
using VPetLLM.Core.ASRCore;
using VPetLLM.Services;
using VPetLLM.Utils.System;

namespace VPetLLM.Utils.Audio
{
    public class ASRService : IASRService
    {
        private Setting _settings;
        private Setting.ASRSetting _asrSettings;
        private Setting.ProxySetting _proxySettings;
        private HttpClient _httpClient;
        private WaveInEvent? _waveIn;
        private MemoryStream? _recordingStream;
        private WaveFileWriter? _waveWriter;
        private bool _isRecording;
        private ASRCoreBase? _asrCore;

        public event EventHandler<string>? TranscriptionCompleted;
        public event EventHandler<Exception>? TranscriptionError;
        public event EventHandler? RecordingStarted;
        public event EventHandler? RecordingStopped;

        public bool IsRecording => _isRecording;

        /// <summary>
        /// 服务提供商名称
        /// </summary>
        public string ProviderName => _asrSettings?.Provider ?? "Unknown";

        /// <summary>
        /// 服务是否启用
        /// </summary>
        public bool IsEnabled => _asrSettings?.IsEnabled ?? false;

        public ASRService(Setting settings)
        {
            _settings = settings;
            _asrSettings = settings.ASR;
            _proxySettings = settings.Proxy;
            _httpClient = CreateHttpClient();
            InitializeASRCore();
        }

        private void InitializeASRCore()
        {
            switch (_asrSettings.Provider)
            {
                case "OpenAI":
                    _asrCore = new OpenAIASRCore(_settings);
                    break;
                case "Soniox":
                    _asrCore = new SonioxASRCore(_settings);
                    break;
                case "Free":
                    _asrCore = new FreeASRCore(_settings);
                    break;
                default:
                    Logger.Log($"ASR: 未知的提供商: {_asrSettings.Provider}");
                    break;
            }
        }

        private HttpClient CreateHttpClient()
        {
            HttpClientHandler handler = new HttpClientHandler();

            // 检查是否应该使用代理
            bool useProxy = false;

            if (_proxySettings != null && _proxySettings.IsEnabled)
            {
                // 如果ForAllAPI为true，则对所有API使用代理
                if (_proxySettings.ForAllAPI)
                {
                    useProxy = true;
                    Logger.Log($"ASR: 配置代理设置 (ForAllAPI: {_proxySettings.ForAllAPI})");
                }
                else
                {
                    // 如果ForAllAPI为false，则根据ForASR设置决定
                    useProxy = _proxySettings.ForASR;
                    Logger.Log($"ASR: 配置代理设置 (ForASR: {_proxySettings.ForASR})");
                }
            }

            if (useProxy)
            {
                if (_proxySettings.FollowSystemProxy)
                {
                    handler.UseProxy = true;
                    handler.UseDefaultCredentials = true;
                    Logger.Log($"ASR: 使用系统代理");
                }
                else if (!string.IsNullOrWhiteSpace(_proxySettings.Address))
                {
                    try
                    {
                        // 确保协议格式正确
                        var protocol = _proxySettings.Protocol?.ToLower() ?? "http";
                        if (protocol != "http" && protocol != "https" &&
                            protocol != "socks4" && protocol != "socks4a" && protocol != "socks5")
                        {
                            protocol = "http"; // 默认使用http
                        }

                        var proxyUri = new Uri($"{protocol}://{_proxySettings.Address}");
                        handler.Proxy = new WebProxy(proxyUri);
                        handler.UseProxy = true;
                        Logger.Log($"ASR: 使用自定义代理: {proxyUri} (协议: {protocol})");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"ASR: 代理配置错误: {ex.Message}，将不使用代理");
                        handler.UseProxy = false;
                    }
                }
            }
            else
            {
                Logger.Log($"ASR: 不使用代理 (代理未启用或ASR代理被禁用)");
                // 明确禁用代理，防止使用系统默认代理
                handler.UseProxy = false;
                handler.Proxy = null;
            }

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60) // 增加到 60 秒以支持较大的音频文件
            };

            return client;
        }

        public void UpdateSettings(Setting settings)
        {
            _settings = settings;
            _asrSettings = settings.ASR;
            _proxySettings = settings.Proxy;

            // 重新创建 HttpClient 以应用新的代理设置
            _httpClient?.Dispose();
            _httpClient = CreateHttpClient();

            // 重新初始化 ASR Core
            InitializeASRCore();
        }

        public void StartRecording()
        {
            if (_isRecording)
            {
                Logger.Log("ASR: Already recording");
                return;
            }

            try
            {
                Logger.Log("ASR: Starting recording...");

                // 列出所有可用的录音设备
                LogAvailableRecordingDevices();

                _recordingStream = new MemoryStream();
                _waveIn = new WaveInEvent
                {
                    DeviceNumber = _asrSettings.RecordingDeviceNumber,
                    WaveFormat = new WaveFormat(16000, 16, 1), // 16kHz, 16-bit, mono - 提高采样率以改善质量
                    BufferMilliseconds = 50 // 缓冲区大小
                };

                var deviceName = "Unknown";
                try
                {
                    var capabilities = WaveInEvent.GetCapabilities(_waveIn.DeviceNumber);
                    deviceName = capabilities.ProductName;
                }
                catch { }

                Logger.Log($"ASR: Using device #{_waveIn.DeviceNumber}: {deviceName}");
                Logger.Log($"ASR: Wave format: {_waveIn.WaveFormat.SampleRate}Hz, {_waveIn.WaveFormat.BitsPerSample}-bit, {_waveIn.WaveFormat.Channels} channel(s)");

                _waveWriter = new WaveFileWriter(_recordingStream, _waveIn.WaveFormat);

                _waveIn.DataAvailable += (s, e) =>
                {
                    if (e.BytesRecorded > 0)
                    {
                        _waveWriter?.Write(e.Buffer, 0, e.BytesRecorded);
                        // Logger.Log($"ASR: Recorded {e.BytesRecorded} bytes");
                    }
                };

                _waveIn.RecordingStopped += async (s, e) =>
                {
                    Logger.Log("ASR: Recording stopped event triggered");
                    await ProcessRecording();
                };

                _waveIn.StartRecording();
                _isRecording = true;
                RecordingStarted?.Invoke(this, EventArgs.Empty);
                Logger.Log("ASR: Recording started successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"ASR: Error starting recording: {ex.Message}");
                TranscriptionError?.Invoke(this, new InvalidOperationException($"启动录音失败: {ex.Message}", ex));
                CleanupRecording();
            }
        }

        public void StopRecording()
        {
            if (!_isRecording)
            {
                Logger.Log("ASR: Not recording");
                return;
            }

            try
            {
                Logger.Log("ASR: Stopping recording...");
                _isRecording = false;
                _waveIn?.StopRecording();
                RecordingStopped?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Logger.Log($"ASR: Error stopping recording: {ex.Message}");
                TranscriptionError?.Invoke(this, new InvalidOperationException($"停止录音失败: {ex.Message}", ex));
                CleanupRecording();
            }
        }

        private async Task ProcessRecording()
        {
            try
            {
                Logger.Log("ASR: Processing recording...");

                // 先刷新数据
                _waveWriter?.Flush();

                // 检查是否有数据
                if (_recordingStream == null || _recordingStream.Length == 0)
                {
                    Logger.Log("ASR: No audio data recorded");
                    TranscriptionError?.Invoke(this, new InvalidOperationException("没有录制到音频数据"));
                    CleanupRecording();
                    return;
                }

                Logger.Log($"ASR: Audio data size: {_recordingStream.Length} bytes");

                // 检查音频大小是否合理（不超过 25MB，这是 OpenAI 的限制）
                if (_recordingStream.Length > 25 * 1024 * 1024)
                {
                    Logger.Log("ASR: Audio file too large");
                    TranscriptionError?.Invoke(this, new InvalidOperationException("录音文件过大，请录制更短的音频"));
                    CleanupRecording();
                    return;
                }

                // 在 Dispose 之前先读取数据到字节数组
                _recordingStream.Position = 0;
                byte[] audioData = _recordingStream.ToArray();

                Logger.Log($"ASR: Audio data prepared, size: {audioData.Length} bytes ({audioData.Length / 1024.0:F2} KB)");

                // 现在可以安全地 Dispose
                _waveWriter?.Dispose();
                _waveWriter = null;

                // 发送到 ASR 服务
                string transcription = await TranscribeAudio(audioData);

                if (!string.IsNullOrWhiteSpace(transcription))
                {
                    Logger.Log($"ASR: Transcription result: {transcription}");
                    TranscriptionCompleted?.Invoke(this, transcription);
                }
                else
                {
                    Logger.Log("ASR: Empty transcription result");
                    TranscriptionError?.Invoke(this, new InvalidOperationException("未识别到语音内容"));
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"ASR: Error processing recording: {ex.Message}");
                TranscriptionError?.Invoke(this, new InvalidOperationException($"处理录音失败: {ex.Message}", ex));
            }
            finally
            {
                CleanupRecording();
            }
        }

        private async Task<string> TranscribeAudio(byte[] audioData)
        {
            try
            {
                Logger.Log($"ASR: Sending audio to {_asrSettings.Provider} service...");

                if (_asrCore == null)
                {
                    throw new InvalidOperationException($"ASR Core 未初始化");
                }

                return await _asrCore.TranscribeAsync(audioData);
            }
            catch (Exception ex)
            {
                Logger.Log($"ASR: Transcription error: {ex.Message}");
                throw;
            }
        }



        private static bool _devicesLogged = false;

        private void LogAvailableRecordingDevices()
        {
            // 只在程序启动时或用户刷新时记录设备列表
            if (_devicesLogged)
            {
                return;
            }

            try
            {
                var deviceCount = WaveInEvent.DeviceCount;
                Logger.Log($"ASR: Found {deviceCount} recording device(s)");

                for (int i = 0; i < deviceCount; i++)
                {
                    var capabilities = WaveInEvent.GetCapabilities(i);
                    Logger.Log($"ASR: Device #{i}: {capabilities.ProductName} (Channels: {capabilities.Channels})");
                }

                if (deviceCount == 0)
                {
                    Logger.Log("ASR: WARNING - No recording devices found!");
                }

                _devicesLogged = true;
            }
            catch (Exception ex)
            {
                Logger.Log($"ASR: Error listing recording devices: {ex.Message}");
            }
        }

        /// <summary>
        /// 刷新设备列表（用户手动触发）
        /// </summary>
        public static void RefreshDeviceList()
        {
            _devicesLogged = false;
        }

        // Debug 音频保存功能已移除，以减少不必要的文件生成

        private void CleanupRecording()
        {
            try
            {
                _waveWriter?.Dispose();
                _waveWriter = null;

                _recordingStream?.Dispose();
                _recordingStream = null;

                _waveIn?.Dispose();
                _waveIn = null;

                _isRecording = false;
                Logger.Log("ASR: Recording cleanup completed");
            }
            catch (Exception ex)
            {
                Logger.Log($"ASR: Error during cleanup: {ex.Message}");
            }
        }

        public async Task<List<Setting.SonioxModelInfo>> FetchSonioxModels()
        {
            try
            {
                if (_asrCore is SonioxASRCore sonioxCore)
                {
                    // 使用新的详细模型获取方法
                    var models = await sonioxCore.GetModelsWithDetailsAsync();
                    Logger.Log($"ASR: Fetched {models.Count} Soniox models with details");
                    return models;
                }
                else
                {
                    Logger.Log($"ASR: Current provider is not Soniox");
                    return new List<Setting.SonioxModelInfo>();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"ASR: Error fetching Soniox models: {ex.Message}");
                return new List<Setting.SonioxModelInfo>();
            }
        }

        /// <summary>
        /// 实现 IASRService.TranscribeAsync - 将音频数据转录为文本
        /// </summary>
        public async Task<string?> TranscribeAsync(byte[] audioData)
        {
            if (!IsEnabled || audioData == null || audioData.Length == 0)
            {
                return null;
            }

            try
            {
                return await TranscribeAudio(audioData);
            }
            catch (Exception ex)
            {
                Logger.Log($"ASR: TranscribeAsync error: {ex.Message}");
                TranscriptionError?.Invoke(this, ex);
                return null;
            }
        }

        /// <summary>
        /// 实现 IASRService.TranscribeAsync - 将音频流转录为文本
        /// </summary>
        public async Task<string?> TranscribeAsync(Stream audioStream)
        {
            if (!IsEnabled || audioStream == null)
            {
                return null;
            }

            try
            {
                using var memoryStream = new MemoryStream();
                await audioStream.CopyToAsync(memoryStream);
                return await TranscribeAsync(memoryStream.ToArray());
            }
            catch (Exception ex)
            {
                Logger.Log($"ASR: TranscribeAsync (stream) error: {ex.Message}");
                TranscriptionError?.Invoke(this, ex);
                return null;
            }
        }

        /// <summary>
        /// 实现 IASRService.StopRecordingAsync - 停止录音并返回转录结果
        /// </summary>
        public async Task<string?> StopRecordingAsync()
        {
            if (!_isRecording)
            {
                Logger.Log("ASR: Not recording");
                return null;
            }

            var tcs = new TaskCompletionSource<string?>();

            void OnTranscriptionCompleted(object? sender, string result)
            {
                TranscriptionCompleted -= OnTranscriptionCompleted;
                TranscriptionError -= OnTranscriptionError;
                tcs.TrySetResult(result);
            }

            void OnTranscriptionError(object? sender, Exception error)
            {
                TranscriptionCompleted -= OnTranscriptionCompleted;
                TranscriptionError -= OnTranscriptionError;
                tcs.TrySetResult(null);
            }

            TranscriptionCompleted += OnTranscriptionCompleted;
            TranscriptionError += OnTranscriptionError;

            StopRecording();

            // 设置超时
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60));
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                TranscriptionCompleted -= OnTranscriptionCompleted;
                TranscriptionError -= OnTranscriptionError;
                Logger.Log("ASR: StopRecordingAsync timed out");
                return null;
            }

            return await tcs.Task;
        }

        /// <summary>
        /// 实现 IASRService.CancelRecording - 取消录音
        /// </summary>
        public void CancelRecording()
        {
            if (!_isRecording)
            {
                return;
            }

            try
            {
                Logger.Log("ASR: Cancelling recording...");
                _isRecording = false;

                // 直接清理，不触发处理
                _waveIn?.StopRecording();
                CleanupRecording();

                RecordingStopped?.Invoke(this, EventArgs.Empty);
                Logger.Log("ASR: Recording cancelled");
            }
            catch (Exception ex)
            {
                Logger.Log($"ASR: Error cancelling recording: {ex.Message}");
                CleanupRecording();
            }
        }

        public void Dispose()
        {
            CleanupRecording();
            _httpClient?.Dispose();
        }
    }
}
