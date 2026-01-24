using VPetLLM.Interfaces;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils.Audio;
using VPetLLM.Core.TTS;

namespace VPetLLM.Core.Integration
{
    /// <summary>
    /// 使用策略模式的统一 TTS 处理
    /// 委托给 ITTSProvider 实现以处理提供者特定的逻辑
    /// </summary>
    public class TTSIntegrationLayer
    {
        private readonly TTSProviderFactory _providerFactory;
        private ITTSProvider? _currentProvider;
        private TTSStateInfo _currentState;
        private readonly object _stateLock = new object();

        public TTSIntegrationLayer(TTSProviderFactory providerFactory)
        {
            _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
            _currentState = new TTSStateInfo();
            _currentProvider = _providerFactory.GetActiveProvider();
            
            Logger.Log($"TTSIntegrationLayer: 使用提供者初始化: {_currentProvider?.ProviderName ?? "无"}");
        }

        /// <summary>
        /// Current TTS state
        /// </summary>
        public TTSStateInfo CurrentState
        {
            get
            {
                lock (_stateLock)
                {
                    return new TTSStateInfo
                    {
                        State = _currentState.State,
                        Text = _currentState.Text,
                        ProgressPercent = _currentState.ProgressPercent,
                        ErrorMessage = _currentState.ErrorMessage,
                        AudioFilePath = _currentState.AudioFilePath,
                        AudioDurationMs = _currentState.AudioDurationMs
                    };
                }
            }
        }

        /// <summary>
        /// 使用当前提供者异步处理 TTS 请求
        /// </summary>
        public async Task ProcessTTSAsync(string text, TTSOptions options)
        {
            if (string.IsNullOrEmpty(text))
            {
                Logger.Log("TTSIntegrationLayer: Text is null or empty, cannot process TTS");
                throw new ArgumentException("Text cannot be null or empty", nameof(text));
            }

            if (options is null)
            {
                Logger.Log("TTSIntegrationLayer: TTS options are null, using defaults");
                options = new TTSOptions();
            }

            try
            {
                Logger.Log($"TTSIntegrationLayer: 使用提供者处理 TTS: {_currentProvider?.ProviderName}");
                UpdateState(TTSState.Processing, text);

                // 确保我们有提供者
                _currentProvider ??= _providerFactory.GetActiveProvider();
                
                if (_currentProvider is null || !_currentProvider.IsAvailable())
                {
                    Logger.Log("TTSIntegrationLayer: No TTS provider available");
                    UpdateState(TTSState.Error, text, "No TTS provider available");
                    throw new InvalidOperationException("没有可用的 TTS 提供者");
                }

                // 使用提供者生成音频
                UpdateProgress(25);
                var audioResult = await _currentProvider.GenerateAudioAsync(
                    text, 
                    options, 
                    CancellationToken.None);

                if (!audioResult.Success)
                {
                    throw new InvalidOperationException($"音频生成失败: {audioResult.ErrorMessage}");
                }

                UpdateAudioInfo(audioResult.AudioFilePath, audioResult.DurationMs);
                UpdateProgress(50);

                // 使用提供者等待播放
                await _currentProvider.WaitForPlaybackAsync(audioResult, CancellationToken.None);

                UpdateProgress(100);
                UpdateState(TTSState.Completed, text);
                Logger.Log("TTSIntegrationLayer: TTS processing completed successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSIntegrationLayer: TTS processing failed: {ex.Message}");
                UpdateState(TTSState.Error, text, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// 切换到不同的 TTS 提供者
        /// </summary>
        public void SwitchProvider(string providerName)
        {
            var newProvider = _providerFactory.GetProviderByName(providerName);
            if (newProvider is not null && newProvider.IsAvailable())
            {
                _currentProvider = newProvider;
                Logger.Log($"TTSIntegrationLayer: 切换到提供者: {providerName}");
            }
            else
            {
                Logger.Log($"TTSIntegrationLayer: 提供者 {providerName} 不可用");
            }
        }

        /// <summary>
        /// 获取当前提供者名称
        /// </summary>
        public string GetCurrentProviderName()
        {
            return _currentProvider?.ProviderName ?? "无";
        }

        /// <summary>
        /// Synchronize bubble display with TTS processing
        /// Ensures proper coordination between text display and speech output
        /// </summary>
        /// <param name="text">Text being displayed</param>
        public void SynchronizeWithBubbleDisplay(string text)
        {
            try
            {
                Logger.Log($"TTSIntegrationLayer: Synchronizing with bubble display for text length: {text?.Length}");

                lock (_stateLock)
                {
                    if (_currentState.State == TTSState.Processing || _currentState.State == TTSState.Streaming)
                    {
                        // TTS is active, coordinate timing
                        if (_currentProvider is not null)
                        {
                            var estimatedDuration = _currentProvider.EstimateDuration(text, 1.0f);
                            Logger.Log($"TTSIntegrationLayer: Estimated audio duration: {estimatedDuration}ms");

                            // Update audio duration for coordination
                            _currentState.AudioDurationMs = estimatedDuration;
                        }
                    }
                    else
                    {
                        Logger.Log("TTSIntegrationLayer: No active TTS to synchronize with");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSIntegrationLayer: Error synchronizing with bubble display: {ex.Message}");
            }
        }

        /// <summary>
        /// Cancel current TTS processing
        /// </summary>
        public void CancelTTS()
        {
            try
            {
                Logger.Log("TTSIntegrationLayer: Cancelling TTS processing");
                UpdateState(TTSState.Cancelled, _currentState.Text);
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSIntegrationLayer: Error cancelling TTS: {ex.Message}");
            }
        }

        /// <summary>
        /// Reset TTS state to idle
        /// </summary>
        public void Reset()
        {
            try
            {
                Logger.Log("TTSIntegrationLayer: Resetting TTS state");
                UpdateState(TTSState.Idle, null);
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSIntegrationLayer: Error resetting TTS state: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if TTS is currently active
        /// </summary>
        /// <returns>True if TTS is processing or streaming</returns>
        public bool IsActive()
        {
            lock (_stateLock)
            {
                return _currentState.State == TTSState.Processing ||
                       _currentState.State == TTSState.Streaming;
            }
        }

        /// <summary>
        /// Get estimated completion time for current TTS operation
        /// </summary>
        /// <returns>Estimated completion time in milliseconds, or 0 if not active</returns>
        public int GetEstimatedCompletionTimeMs()
        {
            lock (_stateLock)
            {
                if (!IsActive())
                {
                    return 0;
                }

                var remainingProgress = 100 - _currentState.ProgressPercent;
                var estimatedRemaining = (int)((_currentState.AudioDurationMs * remainingProgress) / 100.0);

                return Math.Max(0, estimatedRemaining);
            }
        }

        /// <summary>
        /// Update TTS state
        /// </summary>
        private void UpdateState(TTSState newState, string text, string errorMessage = null)
        {
            lock (_stateLock)
            {
                _currentState.State = newState;
                _currentState.Text = text;
                _currentState.ErrorMessage = errorMessage;

                if (newState == TTSState.Idle || newState == TTSState.Completed || newState == TTSState.Cancelled)
                {
                    _currentState.ProgressPercent = newState == TTSState.Completed ? 100 : 0;
                }

                Logger.Log($"TTSIntegrationLayer: State updated to {newState}");
            }
        }

        /// <summary>
        /// Update progress percentage
        /// </summary>
        private void UpdateProgress(int progressPercent)
        {
            lock (_stateLock)
            {
                _currentState.ProgressPercent = Math.Max(0, Math.Min(100, progressPercent));
                Logger.Log($"TTSIntegrationLayer: Progress updated to {_currentState.ProgressPercent}%");
            }
        }

        /// <summary>
        /// Update audio information
        /// </summary>
        private void UpdateAudioInfo(string audioFilePath, int durationMs)
        {
            lock (_stateLock)
            {
                _currentState.AudioFilePath = audioFilePath;
                _currentState.AudioDurationMs = durationMs;
                Logger.Log($"TTSIntegrationLayer: Audio info updated - Duration: {durationMs}ms");
            }
        }
    }
}