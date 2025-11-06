using System.IO;
using System.Windows;
using System.Windows.Media;
using VPetLLM.Core;
using VPetLLM.Core.TTSCore;

namespace VPetLLM.Utils
{
    public class TTSService
    {
        private MediaPlayer? _mediaPlayer;
        private Setting.TTSSetting _ttsSettings;
        private Setting.ProxySetting? _proxySettings;
        private readonly object _playbackLock = new object();
        private bool _isPlaying = false;
        private TaskCompletionSource<bool>? _currentPlaybackTask;

        public TTSService(Setting.TTSSetting settings, Setting.ProxySetting? proxySettings = null)
        {
            _ttsSettings = settings;
            _proxySettings = proxySettings;
            // MediaPlayer将在需要时在UI线程上创建
            _mediaPlayer = null;
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
        public async Task<string> DownloadTTSAudioAsync(string text)
        {
            if (!_ttsSettings.IsEnabled || string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                // 使用 Core 层获取音频数据
                var ttsCore = GetTTSCore();
                var audioData = await ttsCore.GenerateAudioAsync(text);
                var fileExtension = ttsCore.GetAudioFormat();

                if (audioData == null || audioData.Length == 0)
                {
                    Logger.Log("TTS: 未获取到音频数据");
                    return null;
                }

                // 应用音量增益
                if (Math.Abs(_ttsSettings.VolumeGain) > 0.01)
                {
                    audioData = AudioProcessor.ApplyVolumeGain(audioData, _ttsSettings.VolumeGain);
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
                Logger.Log($"TTS下载错误: {ex.Message}");
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
                Logger.Log($"TTS: 音频文件不存在: {filePath}");
                return;
            }

            try
            {
                // Logger.Log($"TTS: 直接播放预下载音频: {filePath}");

                // 等待当前播放完成，确保不会被中断
                await WaitForCurrentPlaybackAsync();

                // 播放音频文件
                await PlayAudioFileAsync(filePath);

                // Logger.Log($"TTS: 预下载音频播放完成: {filePath}");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS直接播放错误: {ex.Message}");
                Logger.Log($"TTS直接播放异常堆栈: {ex.StackTrace}");
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
                if (_isPlaying && _currentPlaybackTask != null)
                {
                    currentTask = _currentPlaybackTask;
                }
            }

            if (currentTask != null)
            {
                Logger.Log("TTS: 等待当前播放完成...");
                await currentTask.Task;
                Logger.Log("TTS: 当前播放已完成");
            }
        }

        /// <summary>
        /// 开始播放文本转语音（等待当前播放完成后再播放新音频，但立即返回）
        /// </summary>
        /// <param name="text">要转换的文本</param>
        /// <returns></returns>
        public async Task<bool> StartPlayTextAsync(string text)
        {
            if (!_ttsSettings.IsEnabled || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            // 等待当前播放完成
            await WaitForCurrentPlaybackAsync();

            try
            {
                Logger.Log($"TTS: 开始转换文本: {text.Substring(0, Math.Min(text.Length, 50))}...");

                // 使用 Core 层获取音频数据
                var ttsCore = GetTTSCore();
                var audioData = await ttsCore.GenerateAudioAsync(text);
                var fileExtension = ttsCore.GetAudioFormat();

                if (audioData == null || audioData.Length == 0)
                {
                    Logger.Log("TTS: 未获取到音频数据");
                    return false;
                }

                Logger.Log($"TTS: 成功获取音频数据，大小: {audioData.Length} 字节");

                // 应用音量增益
                if (Math.Abs(_ttsSettings.VolumeGain) > 0.01)
                {
                    audioData = AudioProcessor.ApplyVolumeGain(audioData, _ttsSettings.VolumeGain);
                }

                // 保存到临时文件
                var tempDir = Path.GetTempPath();
                var tempFileName = $"VPetLLM_TTS_{Guid.NewGuid():N}.{fileExtension}";
                var tempFile = Path.Combine(tempDir, tempFileName);
                await File.WriteAllBytesAsync(tempFile, audioData);
                Logger.Log($"TTS: 音频文件保存到: {tempFile}");

                // 开始播放音频（不等待播放完成，但设置播放状态）
                _ = Task.Run(async () => await PlayAudioFileAsync(tempFile));
                Logger.Log($"TTS: 音频开始播放，立即返回");

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS播放错误: {ex.Message}");
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
            if (!_ttsSettings.IsEnabled || string.IsNullOrWhiteSpace(text))
            {
                return Task.CompletedTask;
            }

            // 等待当前播放完成
            await WaitForCurrentPlaybackAsync();

            try
            {
                Logger.Log($"TTS: 开始转换文本: {text.Substring(0, Math.Min(text.Length, 50))}...");

                // 使用 Core 层获取音频数据
                var ttsCore = GetTTSCore();
                var audioData = await ttsCore.GenerateAudioAsync(text);
                var fileExtension = ttsCore.GetAudioFormat();

                if (audioData == null || audioData.Length == 0)
                {
                    Logger.Log("TTS: 未获取到音频数据");
                    return Task.CompletedTask;
                }

                Logger.Log($"TTS: 成功获取音频数据，大小: {audioData.Length} 字节");

                // 应用音量增益
                if (Math.Abs(_ttsSettings.VolumeGain) > 0.01)
                {
                    audioData = AudioProcessor.ApplyVolumeGain(audioData, _ttsSettings.VolumeGain);
                }

                // 保存到临时文件
                var tempDir = Path.GetTempPath();
                var tempFileName = $"VPetLLM_TTS_{Guid.NewGuid():N}.{fileExtension}";
                var tempFile = Path.Combine(tempDir, tempFileName);
                await File.WriteAllBytesAsync(tempFile, audioData);
                Logger.Log($"TTS: 音频文件保存到: {tempFile}");

                // 返回播放任务
                var playTask = Task.Run(async () => await PlayAudioFileAsync(tempFile));
                Logger.Log($"TTS: 音频开始播放，返回播放任务");

                return playTask;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS播放错误: {ex.Message}");
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
            if (!_ttsSettings.IsEnabled || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            try
            {
                Logger.Log($"TTS: 开始转换文本: {text.Substring(0, Math.Min(text.Length, 50))}...");

                // 使用 Core 层获取音频数据
                var ttsCore = GetTTSCore();
                var audioData = await ttsCore.GenerateAudioAsync(text);
                var fileExtension = ttsCore.GetAudioFormat();

                if (audioData == null || audioData.Length == 0)
                {
                    Logger.Log("TTS: 未获取到音频数据");
                    return false;
                }

                Logger.Log($"TTS: 成功获取音频数据，大小: {audioData.Length} 字节");

                // 应用音量增益
                if (Math.Abs(_ttsSettings.VolumeGain) > 0.01)
                {
                    audioData = AudioProcessor.ApplyVolumeGain(audioData, _ttsSettings.VolumeGain);
                }

                // 保存到临时文件
                var tempDir = Path.GetTempPath();
                var tempFileName = $"VPetLLM_TTS_{Guid.NewGuid():N}.{fileExtension}";
                var tempFile = Path.Combine(tempDir, tempFileName);
                await File.WriteAllBytesAsync(tempFile, audioData);
                Logger.Log($"TTS: 音频文件保存到: {tempFile}");

                // 播放音频
                await PlayAudioFileAsync(tempFile);

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS播放错误: {ex.Message}");
                return false;
            }
        }



        /// <summary>
        /// 播放音频文件
        /// 优化：使用BeginInvoke异步调度UI操作，减少阻塞
        /// </summary>
        /// <param name="filePath">音频文件路径</param>
        /// <returns></returns>
        private async Task PlayAudioFileAsync(string filePath)
        {
            try
            {
                // Logger.Log($"TTS: 开始播放音频文件: {filePath}");

                TaskCompletionSource<bool> currentTask;
                lock (_playbackLock)
                {
                    _isPlaying = true;
                    _currentPlaybackTask = new TaskCompletionSource<bool>();
                    currentTask = _currentPlaybackTask;
                }

                var tcs = new TaskCompletionSource<bool>();

                // 优化：使用BeginInvoke异步调度，避免阻塞当前线程
                _ = Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // 确保MediaPlayer已初始化
                        if (_mediaPlayer == null)
                        {
                            _mediaPlayer = new MediaPlayer();
                            // Logger.Log("TTS: MediaPlayer已在UI线程上创建");
                        }

                        // 停止当前播放
                        _mediaPlayer.Stop();

                        // 设置音频文件
                        _mediaPlayer.Open(new Uri(filePath, UriKind.Absolute));

                        // 设置音量（基础音量 + 增益转换为线性系数）
                        // MediaPlayer.Volume 可以设置超过1.0，最大到100
                        double baseVolume = _ttsSettings.Volume;
                        double gainLinear = Math.Pow(10.0, _ttsSettings.VolumeGain / 20.0);
                        double finalVolume = Math.Min(100.0, Math.Max(0.0, baseVolume * gainLinear));
                        _mediaPlayer.Volume = finalVolume;

                        // 设置播放结束事件
                        EventHandler mediaEndedHandler = null;
                        EventHandler<ExceptionEventArgs> mediaFailedHandler = null;

                        mediaEndedHandler = (s, e) =>
                        {
                            try
                            {
                                _mediaPlayer.MediaEnded -= mediaEndedHandler;
                                _mediaPlayer.MediaFailed -= mediaFailedHandler;
                                // Logger.Log($"TTS: 音频播放完成: {filePath}");

                                lock (_playbackLock)
                                {
                                    _isPlaying = false;
                                    currentTask?.TrySetResult(true);
                                }

                                tcs.TrySetResult(true);
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"TTS: 播放完成事件处理异常: {ex.Message}");
                                tcs.TrySetResult(true); // 即使异常也标记为完成
                            }
                        };

                        mediaFailedHandler = (s, e) =>
                        {
                            try
                            {
                                _mediaPlayer.MediaEnded -= mediaEndedHandler;
                                _mediaPlayer.MediaFailed -= mediaFailedHandler;
                                Logger.Log($"TTS: 音频播放失败: {filePath}, 错误: {e.ErrorException?.Message}");

                                lock (_playbackLock)
                                {
                                    _isPlaying = false;
                                    currentTask?.TrySetResult(false);
                                }

                                tcs.TrySetResult(false);
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"TTS: 播放失败事件处理异常: {ex.Message}");
                                tcs.TrySetResult(false); // 即使异常也标记为失败
                            }
                        };

                        _mediaPlayer.MediaEnded += mediaEndedHandler;
                        _mediaPlayer.MediaFailed += mediaFailedHandler;

                        // 开始播放
                        _mediaPlayer.Play();
                        Logger.Log($"TTS: 开始播放音频: {filePath}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"TTS: UI线程播放设置失败: {ex.Message}");
                        Logger.Log($"TTS: UI线程异常堆栈: {ex.StackTrace}");

                        lock (_playbackLock)
                        {
                            _isPlaying = false;
                            currentTask?.TrySetResult(false);
                        }

                        tcs.TrySetResult(false);
                    }
                }), System.Windows.Threading.DispatcherPriority.Normal);

                // 等待播放完成（使用ConfigureAwait(false)避免UI线程阻塞）
                Logger.Log($"TTS: 等待音频播放完成: {filePath}");
                var playbackResult = await tcs.Task.ConfigureAwait(false);
                Logger.Log($"TTS: 音频播放结果: {playbackResult}, 文件: {filePath}");

                // 清理临时文件（延迟删除，优化：减少延迟到1秒）
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000).ConfigureAwait(false); // 优化：从2秒减少到1秒
                    try
                    {
                        if (File.Exists(filePath) && filePath.Contains("VPetLLM_TTS_"))
                        {
                            File.Delete(filePath);
                            Logger.Log($"TTS: 已删除临时文件: {filePath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"TTS: 删除临时文件失败: {ex.Message}");
                    }
                }).ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        Logger.Log($"TTS: 清理临时文件任务异常: {t.Exception.GetBaseException().Message}");
                    }
                }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS播放错误: {ex.Message}");
                Logger.Log($"TTS播放异常堆栈: {ex.StackTrace}");

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
                lock (_playbackLock)
                {
                    _mediaPlayer?.Stop();
                    _isPlaying = false;
                    _currentPlaybackTask?.TrySetResult(false);
                }
                Logger.Log("TTS: 已停止播放");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS停止播放错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查是否正在播放
        /// </summary>
        public bool IsPlaying
        {
            get
            {
                lock (_playbackLock)
                {
                    return _isPlaying;
                }
            }
        }

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
            if (_mediaPlayer != null)
            {
                try
                {
                    // 在UI线程上更新音量
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        if (_mediaPlayer != null)
                        {
                            // 计算最终音量（基础音量 + 增益转换为线性系数）
                            double baseVolume = _ttsSettings.Volume;
                            double gainLinear = Math.Pow(10.0, _ttsSettings.VolumeGain / 20.0);
                            double finalVolume = Math.Min(100.0, Math.Max(0.0, baseVolume * gainLinear));
                            
                            _mediaPlayer.Volume = finalVolume;
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.Log($"TTS: 更新播放器音量失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 公共方法：立即更新音量设置并应用到当前播放器
        /// </summary>
        /// <param name="volume">基础音量 (0-10)</param>
        /// <param name="volumeGain">音量增益 (dB, -20到+40)</param>
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
            Logger.Log("TTS: 开始测试TTS功能");
            return await PlayTextAsync(testText);
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            try
            {
                _mediaPlayer?.Stop();
                _mediaPlayer?.Close();
                _mediaPlayer = null;
                Logger.Log("TTS: 资源已释放");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS释放资源错误: {ex.Message}");
            }
        }
    }
}