using VPetLLM.Interfaces;

namespace VPetLLM.Core.Integration
{
    /// <summary>
    /// Unified TTS processing with proper state management
    /// Handles both synchronous and asynchronous TTS operations
    /// Now integrated with UnifiedTTS system
    /// </summary>
    public class TTSIntegrationLayer
    {
        private readonly IVPetAPI _vpetAPI;
        private TTSStateInfo _currentState;
        private readonly object _stateLock = new object();

        // 统一TTS系统集成 - 通过依赖注入
        private readonly ITTSDispatcher? _unifiedTTSDispatcher;
        private readonly bool _useUnifiedSystem;

        public TTSIntegrationLayer(IVPetAPI vpetAPI, ITTSDispatcher? unifiedTTSDispatcher = null)
        {
            _vpetAPI = vpetAPI ?? throw new ArgumentNullException(nameof(vpetAPI));
            _unifiedTTSDispatcher = unifiedTTSDispatcher;
            _useUnifiedSystem = _unifiedTTSDispatcher is not null;
            _currentState = new TTSStateInfo();

            Logger.Log($"TTSIntegrationLayer: 初始化完成，使用统一系统: {_useUnifiedSystem}");
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
        /// Process TTS request asynchronously
        /// Uses UnifiedTTS system when available, falls back to VPetAPI
        /// </summary>
        /// <param name="text">Text to process</param>
        /// <param name="options">TTS options</param>
        /// <returns>Task representing the async operation</returns>
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
                Logger.Log($"TTSIntegrationLayer: Processing TTS for text length {text.Length}, streaming: {options.UseStreaming}");

                // Update state to processing
                UpdateState(TTSState.Processing, text);

                if (_useUnifiedSystem && _unifiedTTSDispatcher is not null)
                {
                    // 使用统一TTS系统
                    await ProcessWithUnifiedSystemAsync(text, options);
                }
                else if (_vpetAPI.IsAvailable)
                {
                    // 使用传统VPetAPI
                    await ProcessWithVPetAPIAsync(text, options);
                }
                else
                {
                    Logger.Log("TTSIntegrationLayer: No TTS system available");
                    UpdateState(TTSState.Error, text, "No TTS system available");
                    throw new InvalidOperationException("No TTS system available");
                }

                Logger.Log("TTSIntegrationLayer: TTS processing completed successfully");
                UpdateState(TTSState.Completed, text);
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSIntegrationLayer: TTS processing failed: {ex.Message}");
                UpdateState(TTSState.Error, text, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// 使用统一TTS系统处理请求
        /// </summary>
        private async Task ProcessWithUnifiedSystemAsync(string text, TTSOptions options)
        {
            try
            {
                Logger.Log("TTSIntegrationLayer: 使用统一TTS系统处理请求");

                var request = new ModelsTTSRequest
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Text = text,
                    Settings = new TTSRequestSettings
                    {
                        Voice = options.Voice ?? "default",
                        Speed = options.Speed,
                        Volume = options.Volume,
                        UseStreaming = options.UseStreaming,
                        TimeoutMs = options.TimeoutMs
                    }
                };

                if (options.UseStreaming)
                {
                    UpdateState(TTSState.Streaming, text);
                }

                var response = await _unifiedTTSDispatcher.ProcessRequestAsync(request);

                if (!response.Success)
                {
                    throw new InvalidOperationException($"Unified TTS processing failed: {response.ErrorMessage}");
                }

                // 更新音频信息
                if (response.AudioData is not null)
                {
                    var estimatedDuration = response.AudioDurationMs > 0 ? response.AudioDurationMs :
                                           CalculateAudioDuration(text, options.Speed);
                    UpdateAudioInfo(null, estimatedDuration);
                }

                UpdateProgress(100);
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSIntegrationLayer: 统一TTS系统处理失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 使用传统VPetAPI处理请求
        /// </summary>
        private async Task ProcessWithVPetAPIAsync(string text, TTSOptions options)
        {
            try
            {
                Logger.Log("TTSIntegrationLayer: 使用传统VPetAPI处理请求");

                // Choose processing method based on options
                if (options.UseStreaming)
                {
                    await ProcessStreamingTTSAsync(text, options);
                }
                else
                {
                    await ProcessSynchronousTTSAsync(text, options);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSIntegrationLayer: VPetAPI处理失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Process streaming TTS request
        /// Uses SayInfoWithStream for asynchronous streaming
        /// </summary>
        /// <param name="text">Text to process</param>
        /// <param name="options">TTS options</param>
        /// <returns>Task representing the async operation</returns>
        public async Task ProcessStreamingTTSAsync(string text, TTSOptions options)
        {
            if (string.IsNullOrEmpty(text))
            {
                throw new ArgumentException("Text cannot be null or empty", nameof(text));
            }

            options ??= new TTSOptions { UseStreaming = true };

            try
            {
                Logger.Log($"TTSIntegrationLayer: Processing streaming TTS for text: {text.Substring(0, Math.Min(50, text.Length))}...");

                // Update state to streaming
                UpdateState(TTSState.Streaming, text);

                // Use VPet API for streaming TTS
                await _vpetAPI.SayInfoWithStreamAsync(text, options.Voice);

                // Simulate progress updates for streaming
                for (int progress = 0; progress <= 100; progress += 20)
                {
                    UpdateProgress(progress);
                    await Task.Delay(100); // Small delay to simulate streaming progress
                }

                Logger.Log("TTSIntegrationLayer: Streaming TTS completed");
                UpdateState(TTSState.Completed, text);
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSIntegrationLayer: Streaming TTS failed: {ex.Message}");
                UpdateState(TTSState.Error, text, ex.Message);
                throw new InvalidOperationException($"Streaming TTS processing failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Process synchronous TTS request
        /// Uses SayInfoWithOutStream for synchronous operation
        /// </summary>
        /// <param name="text">Text to process</param>
        /// <param name="options">TTS options</param>
        /// <returns>Task representing the async operation</returns>
        private async Task ProcessSynchronousTTSAsync(string text, TTSOptions options)
        {
            try
            {
                Logger.Log($"TTSIntegrationLayer: Processing synchronous TTS for text: {text.Substring(0, Math.Min(50, text.Length))}...");

                // Update progress
                UpdateProgress(25);

                // Use VPet API for synchronous TTS
                await _vpetAPI.SayInfoWithOutStreamAsync(text, options.Voice);

                // Update progress
                UpdateProgress(75);

                // Simulate audio duration calculation
                var estimatedDuration = CalculateAudioDuration(text, options.Speed);
                UpdateAudioInfo(null, estimatedDuration);

                UpdateProgress(100);
                Logger.Log("TTSIntegrationLayer: Synchronous TTS completed");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSIntegrationLayer: Synchronous TTS failed: {ex.Message}");
                throw new InvalidOperationException($"Synchronous TTS processing failed: {ex.Message}", ex);
            }
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
                        var estimatedDuration = CalculateAudioDuration(text, 1.0f);
                        Logger.Log($"TTSIntegrationLayer: Estimated audio duration: {estimatedDuration}ms");

                        // Update audio duration for coordination
                        _currentState.AudioDurationMs = estimatedDuration;
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

        /// <summary>
        /// Calculate estimated audio duration based on text length and speed
        /// </summary>
        private int CalculateAudioDuration(string text, float speed)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            try
            {
                // Rough estimation: ~150 words per minute at normal speed
                // Average 5 characters per word
                var wordsPerMinute = 150 * speed;
                var charactersPerMinute = wordsPerMinute * 5;
                var charactersPerSecond = charactersPerMinute / 60.0;

                var estimatedSeconds = text.Length / charactersPerSecond;
                var estimatedMs = (int)(estimatedSeconds * 1000);

                // Add some buffer time
                estimatedMs = (int)(estimatedMs * 1.2);

                // Minimum duration
                estimatedMs = Math.Max(1000, estimatedMs);

                Logger.Log($"TTSIntegrationLayer: Calculated audio duration: {estimatedMs}ms for {text.Length} characters");
                return estimatedMs;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSIntegrationLayer: Error calculating audio duration: {ex.Message}");
                return 3000; // Default 3 seconds
            }
        }

        /// <summary>
        /// Validate TTS options
        /// </summary>
        private void ValidateTTSOptions(TTSOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (options.Speed <= 0 || options.Speed > 10)
            {
                throw new ArgumentException($"Invalid TTS speed: {options.Speed}. Must be between 0.1 and 10.0");
            }

            if (options.TimeoutMs <= 0)
            {
                throw new ArgumentException($"Invalid TTS timeout: {options.TimeoutMs}. Must be positive");
            }
        }

        /// <summary>
        /// Handle TTS timeout
        /// </summary>
        private void HandleTimeout(string text)
        {
            Logger.Log($"TTSIntegrationLayer: TTS operation timed out for text: {text?.Substring(0, Math.Min(50, text?.Length ?? 0))}...");
            UpdateState(TTSState.TimedOut, text, "TTS operation timed out");
        }
    }
}