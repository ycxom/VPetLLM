using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using NAudio.Wave;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VPetLLM.Core;
using VPetLLM.Core.ASRCore;

namespace VPetLLM.Utils
{
    public class ASRService : IDisposable
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
        public event EventHandler<string>? TranscriptionError;
        public event EventHandler? RecordingStarted;
        public event EventHandler? RecordingStopped;

        public bool IsRecording => _isRecording;

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
                        handler.Proxy = new System.Net.WebProxy(proxyUri);
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
                TranscriptionError?.Invoke(this, $"启动录音失败: {ex.Message}");
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
                TranscriptionError?.Invoke(this, $"停止录音失败: {ex.Message}");
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
                    TranscriptionError?.Invoke(this, "没有录制到音频数据");
                    CleanupRecording();
                    return;
                }

                Logger.Log($"ASR: Audio data size: {_recordingStream.Length} bytes");

                // 检查音频大小是否合理（不超过 25MB，这是 OpenAI 的限制）
                if (_recordingStream.Length > 25 * 1024 * 1024)
                {
                    Logger.Log("ASR: Audio file too large");
                    TranscriptionError?.Invoke(this, "录音文件过大，请录制更短的音频");
                    CleanupRecording();
                    return;
                }

                // 在 Dispose 之前先读取数据到字节数组
                _recordingStream.Position = 0;
                byte[] audioData = _recordingStream.ToArray();
                
                Logger.Log($"ASR: Audio data prepared, size: {audioData.Length} bytes");
                
                // 调试：保存音频文件到本地
                SaveAudioForDebug(audioData);
                
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
                    TranscriptionError?.Invoke(this, "未识别到语音内容");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"ASR: Error processing recording: {ex.Message}");
                TranscriptionError?.Invoke(this, $"处理录音失败: {ex.Message}");
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



        private void LogAvailableRecordingDevices()
        {
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
            }
            catch (Exception ex)
            {
                Logger.Log($"ASR: Error listing recording devices: {ex.Message}");
            }
        }

        private void SaveAudioForDebug(byte[] audioData)
        {
            try
            {
                // 创建调试目录
                var debugDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VPetLLM", "ASR_Debug");
                Directory.CreateDirectory(debugDir);

                // 生成文件名（带时间戳）
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var filename = $"recording_{timestamp}.wav";
                var filepath = Path.Combine(debugDir, filename);

                // 保存音频文件
                File.WriteAllBytes(filepath, audioData);
                Logger.Log($"ASR: Debug audio saved to: {filepath}");
                Logger.Log($"ASR: Audio file size: {audioData.Length} bytes ({audioData.Length / 1024.0:F2} KB)");
            }
            catch (Exception ex)
            {
                Logger.Log($"ASR: Failed to save debug audio: {ex.Message}");
                // 不抛出异常，调试功能失败不影响主流程
            }
        }

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
                    var modelIds = await sonioxCore.GetModelsAsync();
                    var models = new List<Setting.SonioxModelInfo>();
                    
                    foreach (var modelId in modelIds)
                    {
                        models.Add(new Setting.SonioxModelInfo
                        {
                            Id = modelId,
                            Name = modelId,
                            TranscriptionMode = "",
                            Languages = new List<Setting.SonioxLanguageInfo>()
                        });
                    }
                    
                    Logger.Log($"ASR: Fetched {models.Count} Soniox models");
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

        public void Dispose()
        {
            CleanupRecording();
            _httpClient?.Dispose();
        }
    }
}
