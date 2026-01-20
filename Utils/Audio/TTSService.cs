using System.Reflection;
using VPetLLM.Configuration;
using VPetLLM.Models;
using VPetLLMUtils = VPetLLM.Utils.System;

namespace VPetLLM.Utils.Audio
{
    public class TTSService : ITTSService
    {
        private IMediaPlayer? _mediaPlayer;
        private Setting.TTSSetting _ttsSettings;
        private Setting.ProxySetting? _proxySettings;
        private readonly object _playbackLock = new object();
        private bool _isPlaying = false;
        private TaskCompletionSource<bool>? _currentPlaybackTask;

        // 统一TTS系统集成 - 通过依赖注入
        private readonly ITTSDispatcher? _unifiedTTSDispatcher;
        private readonly bool _useUnifiedSystem;

        /// <summary>
        /// 实时检测 VPet TTS 插件状态的委托
        /// </summary>
        private Func<bool>? _checkVPetTTSPluginEnabled;

        public TTSService(Setting.TTSSetting settings, Setting.ProxySetting? proxySettings = null)
        {
            _ttsSettings = settings;
            _proxySettings = proxySettings;
            _useUnifiedSystem = false;

            // 初始化传统播放器
            InitializePlayer();
        }

        /// <summary>
        /// 构造函数 - 支持统一TTS系统注入
        /// </summary>
        /// <param name="settings">TTS设置</param>
        /// <param name="proxySettings">代理设置</param>
        /// <param name="unifiedTTSDispatcher">统一TTS调度器（可选）</param>
        public TTSService(Setting.TTSSetting settings, Setting.ProxySetting? proxySettings, ITTSDispatcher? unifiedTTSDispatcher)
        {
            _ttsSettings = settings;
            _proxySettings = proxySettings;
            _unifiedTTSDispatcher = unifiedTTSDispatcher;
            _useUnifiedSystem = _unifiedTTSDispatcher is not null;

            VPetLLMUtils.Logger.Log($"TTSService: 初始化完成，使用统一系统: {_useUnifiedSystem}");

            // 只有在不使用统一系统时才初始化传统播放器
            if (!_useUnifiedSystem)
            {
                InitializePlayer();
            }
        }

        /// <summary>
        /// 设置实时检测 VPet TTS 插件状态的委托
        /// 由 VPetLLM 主类在初始化时设置
        /// </summary>
        public void SetVPetTTSPluginChecker(Func<bool> checker)
        {
            _checkVPetTTSPluginEnabled = checker;
            VPetLLMUtils.Logger.Log("TTS: 已设置 VPet TTS 插件实时检测委托");
        }

        /// <summary>
        /// 实时检测 VPet TTS 插件是否启用
        /// </summary>
        private bool IsVPetTTSPluginEnabled()
        {
            if (_checkVPetTTSPluginEnabled is null)
            {
                return false;
            }

            try
            {
                return _checkVPetTTSPluginEnabled();
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"TTS: 检测 VPet TTS 插件状态时发生错误: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取 VPet TTS 插件是否被检测到（实时检测）
        /// </summary>
        public bool IsVPetTTSPluginDetected => IsVPetTTSPluginEnabled();

        private void InitializePlayer()
        {
            // 使用与 Language.json 相同的方式获取插件目录
            var dllPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            // VPetLLMUtils.Logger.Log($"TTS: DLL 目录: {dllPath}");

            var mpvDir = Path.Combine(dllPath, "mpv");

            if (Directory.Exists(mpvDir))
            {
                var files = Directory.GetFiles(mpvDir);
            }

            var mpvExePath = Path.Combine(mpvDir, "mpv.exe");

            if (!File.Exists(mpvExePath))
            {
                var errorMsg = $"mpv.exe 未找到！\n请将 mpv 文件夹放到插件目录: {dllPath}\n下载: https://mpv.io/installation/";
                VPetLLMUtils.Logger.Log($"TTS: {errorMsg}");
                throw new FileNotFoundException(errorMsg);
            }

            try
            {
                // 使用 mpv 播放器，指定 exe 路径
                _mediaPlayer = new MpvPlayer(mpvExePath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法初始化 mpv 播放器: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 获取当前 TTS Core 实例
        /// </summary>
        private TTSCoreBase GetTTSCore()
        {
            // 创建临时 Setting 对象用于传递给 Core
            var tempSettings = new Setting(Path.GetTempPath())
            {
                TTS = _ttsSettings,
                Proxy = _proxySettings
            };

            return _ttsSettings.Provider switch
            {
                "OpenAI" => new OpenAITTSCore(tempSettings),
                "DIY" => new DIYTTSCore(tempSettings),
                "GPT-SoVITS" => new GPTSoVITSTTSCore(tempSettings),
                "Free" => new FreeTTSCore(tempSettings),
                "URL" => new URLTTSCore(tempSettings),
                _ => new URLTTSCore(tempSettings)
            };
        }

        /// <summary>
        /// 仅下载TTS音频文件（不播放）
        /// </summary>
        /// <param name="text">要转换的文本</param>
        /// <returns>音频文件路径，失败时返回null</returns>
        public async Task<string?> DownloadTTSAudioAsync(string text)
        {
            // 实时检测 VPet TTS 插件状态
            if (IsVPetTTSPluginEnabled())
            {
                VPetLLMUtils.Logger.Log("TTS: VPet TTS 插件已启用，跳过内置TTS下载");
                return null;
            }

            if (!_ttsSettings.IsEnabled || string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                byte[] audioData;
                string fileExtension;

                if (_useUnifiedSystem && _unifiedTTSDispatcher is not null)
                {
                    // 使用统一TTS系统
                    var request = new ModelsTTSRequest
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Text = text,
                        Settings = new TTSRequestSettings
                        {
                            Voice = "default",
                            Speed = 1.0f,
                            Volume = 1.0f
                        }
                    };

                    var response = await _unifiedTTSDispatcher.ProcessRequestAsync(request);
                    if (!response.Success || response.AudioData is null)
                    {
                        VPetLLMUtils.Logger.Log($"TTS: 统一系统处理失败: {response.ErrorMessage}");
                        return null;
                    }

                    audioData = response.AudioData;
                    fileExtension = response.AudioFormat ?? "mp3";
                }
                else
                {
                    // 使用传统TTS Core
                    var ttsCore = GetTTSCore();
                    audioData = await ttsCore.GenerateAudioAsync(text);
                    fileExtension = ttsCore.GetAudioFormat();
                }

                if (audioData is null || audioData.Length == 0)
                {
                    VPetLLMUtils.Logger.Log("TTS: 未获取到音频数据");
                    return null;
                }

                // 保存到临时文件
                var tempDir = Path.GetTempPath();
                var tempFileName = $"VPetLLM_TTS_{Guid.NewGuid():N}.{fileExtension}";
                var tempFile = Path.Combine(tempDir, tempFileName);
                await File.WriteAllBytesAsync(tempFile, audioData);

                return tempFile;
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"TTS下载错误: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 直接播放音频文件
        /// </summary>
        /// <param name="filePath">音频文件路径</param>
        /// <returns></returns>
        public async Task PlayAudioFileDirectAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                VPetLLMUtils.Logger.Log($"TTS: 音频文件不存在: {filePath}");
                return;
            }

            try
            {
                // VPetLLMUtils.Logger.Log($"TTS: 直接播放预下载音频: {filePath}");

                // 等待当前播放完成，确保不会被中断
                await WaitForCurrentPlaybackAsync();

                // 播放音频文件
                await PlayAudioFileAsync(filePath);

                // VPetLLMUtils.Logger.Log($"TTS: 预下载音频播放完成: {filePath}");
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"TTS直接播放错误: {ex.Message}");
                VPetLLMUtils.Logger.Log($"TTS直接播放异常堆栈: {ex.StackTrace}");
            }
        }



        /// <summary>
        /// 等待当前播放完成
        /// </summary>
        private async Task WaitForCurrentPlaybackAsync()
        {
            TaskCompletionSource<bool>? currentTask = null;

            lock (_playbackLock)
            {
                if (_isPlaying && _currentPlaybackTask is not null)
                {
                    currentTask = _currentPlaybackTask;
                }
            }

            if (currentTask is not null)
            {
                VPetLLMUtils.Logger.Log("TTS: 等待当前播放完成...");
                await currentTask.Task;
                VPetLLMUtils.Logger.Log("TTS: 当前播放已完成");
            }
        }

        /// <summary>
        /// 开始播放文本转语音（等待当前播放完成后再播放新音频，但立即返回）
        /// </summary>
        /// <param name="text">要转换的文本</param>
        /// <returns></returns>
        public async Task<bool> StartPlayTextAsync(string text)
        {
            // 实时检测 VPet TTS 插件状态
            if (IsVPetTTSPluginEnabled())
            {
                VPetLLMUtils.Logger.Log("TTS: VPet TTS 插件已启用，跳过内置TTS播放");
                return false;
            }

            if (!_ttsSettings.IsEnabled || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            // 等待当前播放完成
            await WaitForCurrentPlaybackAsync();

            try
            {
                VPetLLMUtils.Logger.Log($"TTS: 开始转换文本: {text.Substring(0, Math.Min(text.Length, 50))}...");

                byte[] audioData;
                string fileExtension;

                if (_useUnifiedSystem && _unifiedTTSDispatcher is not null)
                {
                    // 使用统一TTS系统
                    var request = new ModelsTTSRequest
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Text = text,
                        Settings = new TTSRequestSettings
                        {
                            Voice = "default",
                            Speed = 1.0f,
                            Volume = 1.0f
                        }
                    };

                    var response = await _unifiedTTSDispatcher.ProcessRequestAsync(request);
                    if (!response.Success || response.AudioData is null)
                    {
                        VPetLLMUtils.Logger.Log($"TTS: 统一系统处理失败: {response.ErrorMessage}");
                        return false;
                    }

                    audioData = response.AudioData;
                    fileExtension = response.AudioFormat ?? "mp3";
                }
                else
                {
                    // 使用传统TTS Core
                    var ttsCore = GetTTSCore();
                    audioData = await ttsCore.GenerateAudioAsync(text);
                    fileExtension = ttsCore.GetAudioFormat();
                }

                if (audioData is null || audioData.Length == 0)
                {
                    VPetLLMUtils.Logger.Log("TTS: 未获取到音频数据");
                    return false;
                }

                VPetLLMUtils.Logger.Log($"TTS: 成功获取音频数据，大小: {audioData.Length} 字节");

                // 保存到临时文件
                var tempDir = Path.GetTempPath();
                var tempFileName = $"VPetLLM_TTS_{Guid.NewGuid():N}.{fileExtension}";
                var tempFile = Path.Combine(tempDir, tempFileName);
                await File.WriteAllBytesAsync(tempFile, audioData);
                VPetLLMUtils.Logger.Log($"TTS: 音频文件保存到: {tempFile}");

                // 开始播放音频（不等待播放完成，但设置播放状态）
                _ = Task.Run(async () => await PlayAudioFileAsync(tempFile));
                VPetLLMUtils.Logger.Log($"TTS: 音频开始播放，立即返回");

                return true;
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"TTS播放错误: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 开始播放文本转语音并返回可等待的任务
        /// </summary>
        /// <param name="text">要转换的文本</param>
        /// <returns>可等待的播放任务</returns>
        public async Task<Task> StartPlayTextAsyncWithTask(string text)
        {
            // 实时检测 VPet TTS 插件状态
            if (IsVPetTTSPluginEnabled())
            {
                VPetLLMUtils.Logger.Log("TTS: VPet TTS 插件已启用，跳过内置TTS播放");
                return Task.CompletedTask;
            }

            if (!_ttsSettings.IsEnabled || string.IsNullOrWhiteSpace(text))
            {
                return Task.CompletedTask;
            }

            // 等待当前播放完成
            await WaitForCurrentPlaybackAsync();

            try
            {
                VPetLLMUtils.Logger.Log($"TTS: 开始转换文本: {text.Substring(0, Math.Min(text.Length, 50))}...");

                byte[] audioData;
                string fileExtension;

                if (_useUnifiedSystem && _unifiedTTSDispatcher is not null)
                {
                    // 使用统一TTS系统
                    var request = new ModelsTTSRequest
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Text = text,
                        Settings = new TTSRequestSettings
                        {
                            Voice = "default",
                            Speed = 1.0f,
                            Volume = 1.0f
                        }
                    };

                    var response = await _unifiedTTSDispatcher.ProcessRequestAsync(request);
                    if (!response.Success || response.AudioData is null)
                    {
                        VPetLLMUtils.Logger.Log($"TTS: 统一系统处理失败: {response.ErrorMessage}");
                        return Task.CompletedTask;
                    }

                    audioData = response.AudioData;
                    fileExtension = response.AudioFormat ?? "mp3";
                }
                else
                {
                    // 使用传统TTS Core
                    var ttsCore = GetTTSCore();
                    audioData = await ttsCore.GenerateAudioAsync(text);
                    fileExtension = ttsCore.GetAudioFormat();
                }

                if (audioData is null || audioData.Length == 0)
                {
                    VPetLLMUtils.Logger.Log("TTS: 未获取到音频数据");
                    return Task.CompletedTask;
                }

                VPetLLMUtils.Logger.Log($"TTS: 成功获取音频数据，大小: {audioData.Length} 字节");

                // 保存到临时文件（音量增益由 mpv 播放器处理）
                var tempDir = Path.GetTempPath();
                var tempFileName = $"VPetLLM_TTS_{Guid.NewGuid():N}.{fileExtension}";
                var tempFile = Path.Combine(tempDir, tempFileName);
                await File.WriteAllBytesAsync(tempFile, audioData);
                VPetLLMUtils.Logger.Log($"TTS: 音频文件保存到: {tempFile}");

                // 返回播放任务
                var playTask = Task.Run(async () => await PlayAudioFileAsync(tempFile));
                VPetLLMUtils.Logger.Log($"TTS: 音频开始播放，返回播放任务");

                return playTask;
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"TTS播放错误: {ex.Message}");
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// 播放文本转语音
        /// </summary>
        /// <param name="text">要转换的文本</param>
        /// <returns></returns>
        public async Task<bool> PlayTextAsync(string text)
        {
            // 实时检测 VPet TTS 插件状态
            if (IsVPetTTSPluginEnabled())
            {
                VPetLLMUtils.Logger.Log("TTS: VPet TTS 插件已启用，跳过内置TTS播放");
                return false;
            }

            if (!_ttsSettings.IsEnabled || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            try
            {
                VPetLLMUtils.Logger.Log($"TTS: 开始转换文本: {text.Substring(0, Math.Min(text.Length, 50))}...");

                byte[] audioData;
                string fileExtension;

                if (_useUnifiedSystem && _unifiedTTSDispatcher is not null)
                {
                    // 使用统一TTS系统
                    var request = new ModelsTTSRequest
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Text = text,
                        Settings = new TTSRequestSettings
                        {
                            Voice = "default",
                            Speed = 1.0f,
                            Volume = 1.0f
                        }
                    };

                    var response = await _unifiedTTSDispatcher.ProcessRequestAsync(request);
                    if (!response.Success || response.AudioData is null)
                    {
                        VPetLLMUtils.Logger.Log($"TTS: 统一系统处理失败: {response.ErrorMessage}");
                        return false;
                    }

                    audioData = response.AudioData;
                    fileExtension = response.AudioFormat ?? "mp3";
                }
                else
                {
                    // 使用传统TTS Core
                    var ttsCore = GetTTSCore();
                    audioData = await ttsCore.GenerateAudioAsync(text);
                    fileExtension = ttsCore.GetAudioFormat();
                }

                if (audioData is null || audioData.Length == 0)
                {
                    VPetLLMUtils.Logger.Log("TTS: 未获取到音频数据");
                    return false;
                }

                VPetLLMUtils.Logger.Log($"TTS: 成功获取音频数据，大小: {audioData.Length} 字节");

                // 保存到临时文件（音量增益由 mpv 播放器处理）
                var tempDir = Path.GetTempPath();
                var tempFileName = $"VPetLLM_TTS_{Guid.NewGuid():N}.{fileExtension}";
                var tempFile = Path.Combine(tempDir, tempFileName);
                await File.WriteAllBytesAsync(tempFile, audioData);
                VPetLLMUtils.Logger.Log($"TTS: 音频文件保存到: {tempFile}");

                // 播放音频
                await PlayAudioFileAsync(tempFile);

                return true;
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"TTS播放错误: {ex.Message}");
                return false;
            }
        }



        /// <summary>
        /// 播放音频文件
        /// </summary>
        /// <param name="filePath">音频文件路径</param>
        /// <returns></returns>
        private async Task PlayAudioFileAsync(string filePath)
        {
            try
            {
                TaskCompletionSource<bool> currentTask;
                lock (_playbackLock)
                {
                    _isPlaying = true;
                    _currentPlaybackTask = new TaskCompletionSource<bool>();
                    currentTask = _currentPlaybackTask;
                }

                // 设置音量和增益（直接传递给 mpv）
                _mediaPlayer?.SetVolume(_ttsSettings.Volume);
                _mediaPlayer?.SetGain(_ttsSettings.VolumeGain);

                VPetLLMUtils.Logger.Log($"TTS: 开始播放音频: {filePath}");

                // 使用播放器播放
                await _mediaPlayer.PlayAsync(filePath);

                lock (_playbackLock)
                {
                    _isPlaying = false;
                    currentTask?.TrySetResult(true);
                }

                VPetLLMUtils.Logger.Log($"TTS: 音频播放完成: {filePath}");

                // 清理临时文件
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    try
                    {
                        if (File.Exists(filePath) && filePath.Contains("VPetLLM_TTS_"))
                        {
                            File.Delete(filePath);
                            VPetLLMUtils.Logger.Log($"TTS: 已删除临时文件: {filePath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        VPetLLMUtils.Logger.Log($"TTS: 删除临时文件失败: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"TTS播放错误: {ex.Message}");

                lock (_playbackLock)
                {
                    _isPlaying = false;
                    _currentPlaybackTask?.TrySetResult(false);
                }
            }
        }

        /// <summary>
        /// 停止当前播放
        /// </summary>
        public void Stop()
        {
            try
            {
                _mediaPlayer?.Stop();

                lock (_playbackLock)
                {
                    _isPlaying = false;
                    _currentPlaybackTask?.TrySetResult(false);
                }

                VPetLLMUtils.Logger.Log("TTS: 已停止播放");
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"TTS停止播放错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查是否正在播放
        /// </summary>
        public bool IsPlaying
        {
            get
            {
                return _mediaPlayer?.IsPlaying ?? false;
            }
        }

        /// <summary>
        /// 服务提供商名称
        /// </summary>
        public string ProviderName => _ttsSettings?.Provider ?? "Unknown";

        /// <summary>
        /// 服务是否启用
        /// </summary>
        public bool IsEnabled => _ttsSettings?.IsEnabled ?? false;

        /// <summary>
        /// 更新TTS设置
        /// </summary>
        /// <param name="settings">新的设置</param>
        /// <param name="proxySettings">代理设置</param>
        public void UpdateSettings(Setting.TTSSetting settings, Setting.ProxySetting? proxySettings = null)
        {
            var oldVolume = _ttsSettings?.Volume ?? 1.0;
            var oldVolumeGain = _ttsSettings?.VolumeGain ?? 0.0;

            _ttsSettings = settings;
            _proxySettings = proxySettings;

            // 如果音量设置发生变化，立即应用到当前播放器
            if (Math.Abs(oldVolume - settings.Volume) > 0.01 || Math.Abs(oldVolumeGain - settings.VolumeGain) > 0.01)
            {
                UpdateCurrentPlayerVolume();
            }
        }

        /// <summary>
        /// 立即更新当前播放器的音量
        /// </summary>
        private void UpdateCurrentPlayerVolume()
        {
            if (_mediaPlayer is not null)
            {
                try
                {
                    // 直接设置音量和增益（由 mpv 处理）
                    _mediaPlayer.SetVolume(_ttsSettings.Volume);
                    _mediaPlayer.SetGain(_ttsSettings.VolumeGain);
                }
                catch (Exception ex)
                {
                    VPetLLMUtils.Logger.Log($"TTS: 更新播放器音量失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 公共方法：立即更新音量设置并应用到当前播放器
        /// </summary>
        /// <param name="volume">基础音量百分比 (0-100)</param>
        /// <param name="volumeGain">音量增益 (dB)</param>
        public void UpdateVolumeSettings(double volume, double volumeGain)
        {
            _ttsSettings.Volume = volume;
            _ttsSettings.VolumeGain = volumeGain;
            UpdateCurrentPlayerVolume();
        }

        /// <summary>
        /// 测试TTS功能
        /// </summary>
        /// <returns></returns>
        public async Task<bool> TestTTSAsync()
        {
            var testText = "这是一个TTS测试，Hello World!";
            VPetLLMUtils.Logger.Log("TTS: 开始测试TTS功能");
            return await PlayTextAsync(testText);
        }

        /// <summary>
        /// 实现 ITTSService.SynthesizeAsync - 将文本合成为音频数据
        /// </summary>
        public async Task<byte[]?> SynthesizeAsync(string text)
        {
            // 实时检测 VPet TTS 插件状态
            if (IsVPetTTSPluginEnabled())
            {
                VPetLLMUtils.Logger.Log("TTS: VPet TTS 插件已启用，跳过内置TTS合成");
                return null;
            }

            if (!_ttsSettings.IsEnabled || string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                if (_useUnifiedSystem && _unifiedTTSDispatcher is not null)
                {
                    // 使用统一TTS系统
                    var request = new ModelsTTSRequest
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Text = text,
                        Settings = new TTSRequestSettings
                        {
                            Voice = "default",
                            Speed = 1.0f,
                            Volume = 1.0f
                        }
                    };

                    var response = await _unifiedTTSDispatcher.ProcessRequestAsync(request);
                    if (!response.Success || response.AudioData is null)
                    {
                        VPetLLMUtils.Logger.Log($"TTS: 统一系统合成失败: {response.ErrorMessage}");
                        return null;
                    }

                    return response.AudioData;
                }
                else
                {
                    // 使用传统TTS Core
                    var ttsCore = GetTTSCore();
                    var audioData = await ttsCore.GenerateAudioAsync(text);

                    if (audioData is null || audioData.Length == 0)
                    {
                        return null;
                    }

                    // 音量增益由 mpv 播放器处理，这里直接返回原始音频数据
                    return audioData;
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"TTS合成错误: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 实现 ITTSService.PlayAsync - 将文本合成并播放
        /// </summary>
        async Task ITTSService.PlayAsync(string text)
        {
            await PlayTextAsync(text);
        }

        /// <summary>
        /// 实现 ITTSService.SetVolume
        /// </summary>
        public void SetVolume(double volume)
        {
            _ttsSettings.Volume = volume;
            UpdateCurrentPlayerVolume();
        }

        /// <summary>
        /// 实现 ITTSService.SetSpeed
        /// </summary>
        public void SetSpeed(double speed)
        {
            _ttsSettings.Speed = speed;
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            try
            {
                _mediaPlayer?.Dispose();
                _mediaPlayer = null;
                VPetLLMUtils.Logger.Log("TTS: 资源已释放");
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"TTS释放资源错误: {ex.Message}");
            }
        }
    }
}