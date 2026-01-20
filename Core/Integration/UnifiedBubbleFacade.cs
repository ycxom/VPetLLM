using VPetLLM.Interfaces;
using VPetLLM.Validation;

namespace VPetLLM.Core.Integration
{
    /// <summary>
    /// Unified bubble facade that replaces both BubbleManager and the existing BubbleFacade
    /// Provides a single, consistent interface for all bubble operations
    /// Integrates VPetAPIWrapper, AnimationCompatibilityCache, and TTSIntegrationLayer
    /// </summary>
    public class UnifiedBubbleFacade
    {
        private readonly IVPetAPI _vpetAPI;
        private readonly AnimationCompatibilityCache _animationCache;
        private readonly TTSIntegrationLayer _ttsLayer;
        private readonly object _operationLock = new object();
        private bool _disposed = false;

        // State tracking
        private string _currentText = string.Empty;
        private bool _isVisible = false;
        private bool _isThinking = false;
        private DateTime _lastUpdateTime = DateTime.MinValue;

        public UnifiedBubbleFacade(IVPetAPI vpetAPI, AnimationCompatibilityCache animationCache, TTSIntegrationLayer ttsLayer)
        {
            _vpetAPI = vpetAPI ?? throw new ArgumentNullException(nameof(vpetAPI));
            _animationCache = animationCache ?? throw new ArgumentNullException(nameof(animationCache));
            _ttsLayer = ttsLayer ?? throw new ArgumentNullException(nameof(ttsLayer));

            Logger.Log("UnifiedBubbleFacade: Initialized with VPet API, animation cache, and TTS integration");
        }

        /// <summary>
        /// Whether the bubble is currently visible
        /// </summary>
        public bool IsVisible
        {
            get
            {
                lock (_operationLock)
                {
                    return _isVisible;
                }
            }
        }

        /// <summary>
        /// Whether the bubble is in thinking state
        /// </summary>
        public bool IsThinking
        {
            get
            {
                lock (_operationLock)
                {
                    return _isThinking;
                }
            }
        }

        /// <summary>
        /// Current bubble text
        /// </summary>
        public string CurrentText
        {
            get
            {
                lock (_operationLock)
                {
                    return _currentText;
                }
            }
        }

        /// <summary>
        /// Display text bubble asynchronously
        /// </summary>
        /// <param name="text">Text to display</param>
        /// <param name="animationName">Animation name (optional)</param>
        /// <param name="force">Whether to force display</param>
        /// <returns>Task representing the async operation</returns>
        public async Task DisplayTextAsync(string text, string animationName = null, bool force = false)
        {
            if (_disposed)
            {
                Logger.Log("UnifiedBubbleFacade: Cannot display text, facade is disposed");
                throw new ObjectDisposedException(nameof(UnifiedBubbleFacade));
            }

            try
            {
                Logger.Log($"UnifiedBubbleFacade: DisplayTextAsync called - text length: {text?.Length}, animation: '{animationName}', force: {force}");

                // Validate parameters with graceful fallback
                string safeText, safeAnimationName;
                bool safeForce;

                try
                {
                    ValidateParameters(text, animationName, force);
                    (safeText, safeAnimationName, safeForce) = ParameterValidator.GetSafeDefaults(text, animationName, force);
                }
                catch (Exception validationEx)
                {
                    Logger.Log($"UnifiedBubbleFacade: Parameter validation failed, using safe defaults: {validationEx.Message}");
                    // Fallback to safe defaults even if validation fails
                    safeText = string.IsNullOrEmpty(text) ? "..." : text;
                    safeAnimationName = "say"; // Safe default animation
                    safeForce = false;
                }

                // Update state with error handling
                try
                {
                    lock (_operationLock)
                    {
                        if (_disposed) return;

                        _currentText = safeText;
                        _isVisible = true;
                        _isThinking = false;
                        _lastUpdateTime = DateTime.Now;
                    }
                }
                catch (Exception stateEx)
                {
                    Logger.Log($"UnifiedBubbleFacade: Error updating state: {stateEx.Message}");
                    // Continue with display even if state update fails
                }

                // Check animation support with fallback
                if (!string.IsNullOrEmpty(safeAnimationName))
                {
                    try
                    {
                        if (!IsAnimationSupported(safeAnimationName))
                        {
                            Logger.Log($"UnifiedBubbleFacade: Animation '{safeAnimationName}' not supported, using default");
                            safeAnimationName = "say"; // Default fallback
                        }
                    }
                    catch (Exception animEx)
                    {
                        Logger.Log($"UnifiedBubbleFacade: Error checking animation support: {animEx.Message}");
                        safeAnimationName = "say"; // Safe fallback
                    }
                }

                // Display using VPet API with comprehensive error handling
                try
                {
                    await Task.Run(() =>
                    {
                        _vpetAPI.Say(safeText, safeAnimationName, safeForce);
                    });

                    Logger.Log("UnifiedBubbleFacade: Text displayed successfully");
                }
                catch (Exception apiEx)
                {
                    Logger.Log($"UnifiedBubbleFacade: VPet API call failed: {apiEx.Message}");

                    // Fallback: Try with minimal parameters
                    try
                    {
                        Logger.Log("UnifiedBubbleFacade: Attempting fallback with minimal parameters");
                        await Task.Run(() =>
                        {
                            _vpetAPI.Say(safeText, null, false); // No animation, no force
                        });
                        Logger.Log("UnifiedBubbleFacade: Fallback display successful");
                    }
                    catch (Exception fallbackEx)
                    {
                        Logger.Log($"UnifiedBubbleFacade: Fallback display also failed: {fallbackEx.Message}");

                        // Final fallback: Try with just text
                        try
                        {
                            Logger.Log("UnifiedBubbleFacade: Attempting final fallback with text only");
                            await Task.Run(() =>
                            {
                                _vpetAPI.Say(safeText);
                            });
                            Logger.Log("UnifiedBubbleFacade: Final fallback successful");
                        }
                        catch (Exception finalEx)
                        {
                            Logger.Log($"UnifiedBubbleFacade: All display attempts failed: {finalEx.Message}");
                            throw new InvalidOperationException($"Failed to display text after all fallback attempts: {finalEx.Message}", finalEx);
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Re-throw disposal exceptions
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"UnifiedBubbleFacade: Unexpected error in DisplayTextAsync: {ex.Message}");
                Logger.Log($"UnifiedBubbleFacade: Stack trace: {ex.StackTrace}");
                throw new InvalidOperationException($"Failed to display text: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Display text bubble with TTS integration
        /// </summary>
        /// <param name="text">Text to display</param>
        /// <param name="ttsOptions">TTS options</param>
        /// <returns>Task representing the async operation</returns>
        public async Task DisplayWithTTSAsync(string text, TTSOptions ttsOptions)
        {
            if (_disposed)
            {
                Logger.Log("UnifiedBubbleFacade: Cannot display with TTS, facade is disposed");
                throw new ObjectDisposedException(nameof(UnifiedBubbleFacade));
            }

            try
            {
                Logger.Log($"UnifiedBubbleFacade: DisplayWithTTSAsync called - text length: {text?.Length}, TTS enabled: {ttsOptions?.Enabled}");

                // Validate parameters with graceful fallback
                string safeText;
                try
                {
                    ValidateParameters(text, null, false);
                    (safeText, _, _) = ParameterValidator.GetSafeDefaults(text, null, false);
                }
                catch (Exception validationEx)
                {
                    Logger.Log($"UnifiedBubbleFacade: Parameter validation failed, using safe defaults: {validationEx.Message}");
                    safeText = string.IsNullOrEmpty(text) ? "..." : text;
                }

                // Handle null TTS options gracefully
                if (ttsOptions is null)
                {
                    Logger.Log("UnifiedBubbleFacade: TTS options are null, falling back to text-only display");
                    await DisplayTextAsync(safeText);
                    return;
                }

                // Start TTS processing if enabled
                if (ttsOptions.Enabled)
                {
                    Logger.Log("UnifiedBubbleFacade: Starting TTS processing");

                    Task ttsTask = null;
                    bool ttsStarted = false;

                    try
                    {
                        // Start TTS processing (don't await - run in parallel with bubble display)
                        ttsTask = _ttsLayer.ProcessTTSAsync(safeText, ttsOptions);
                        ttsStarted = true;
                        Logger.Log("UnifiedBubbleFacade: TTS task started successfully");
                    }
                    catch (Exception ttsStartEx)
                    {
                        Logger.Log($"UnifiedBubbleFacade: Failed to start TTS processing: {ttsStartEx.Message}");
                        // Continue with text-only display
                    }

                    // Display bubble immediately (always attempt this)
                    try
                    {
                        await DisplayTextAsync(safeText);
                        Logger.Log("UnifiedBubbleFacade: Bubble displayed successfully");
                    }
                    catch (Exception bubbleEx)
                    {
                        Logger.Log($"UnifiedBubbleFacade: Failed to display bubble: {bubbleEx.Message}");
                        // If bubble display fails, we still try to complete TTS if it was started
                    }

                    // Synchronize with TTS if it was started
                    if (ttsStarted && ttsTask is not null)
                    {
                        try
                        {
                            _ttsLayer.SynchronizeWithBubbleDisplay(safeText);
                            Logger.Log("UnifiedBubbleFacade: TTS synchronization initiated");
                        }
                        catch (Exception syncEx)
                        {
                            Logger.Log($"UnifiedBubbleFacade: TTS synchronization failed: {syncEx.Message}");
                            // Continue without synchronization
                        }

                        // Wait for TTS to complete with timeout
                        try
                        {
                            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5)); // 5-minute timeout
                            var completedTask = await Task.WhenAny(ttsTask, timeoutTask);

                            if (completedTask == ttsTask)
                            {
                                await ttsTask; // Re-await to get any exceptions
                                Logger.Log("UnifiedBubbleFacade: TTS processing completed successfully");
                            }
                            else
                            {
                                Logger.Log("UnifiedBubbleFacade: TTS processing timed out after 5 minutes");
                                // Cancel TTS if possible
                                try
                                {
                                    _ttsLayer.CancelTTS();
                                }
                                catch (Exception cancelEx)
                                {
                                    Logger.Log($"UnifiedBubbleFacade: Failed to cancel TTS: {cancelEx.Message}");
                                }
                            }
                        }
                        catch (Exception ttsWaitEx)
                        {
                            Logger.Log($"UnifiedBubbleFacade: TTS processing failed: {ttsWaitEx.Message}");
                            // TTS failed, but bubble was already displayed, so this is acceptable
                        }
                    }
                }
                else
                {
                    Logger.Log("UnifiedBubbleFacade: TTS disabled, displaying text only");
                    await DisplayTextAsync(safeText);
                }

                Logger.Log("UnifiedBubbleFacade: DisplayWithTTSAsync completed");
            }
            catch (ObjectDisposedException)
            {
                // Re-throw disposal exceptions
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"UnifiedBubbleFacade: Unexpected error in DisplayWithTTSAsync: {ex.Message}");
                Logger.Log($"UnifiedBubbleFacade: Stack trace: {ex.StackTrace}");

                // Final fallback: Try to display text only
                try
                {
                    Logger.Log("UnifiedBubbleFacade: Attempting final fallback to text-only display");
                    var safeText = string.IsNullOrEmpty(text) ? "..." : text;
                    await DisplayTextAsync(safeText);
                    Logger.Log("UnifiedBubbleFacade: Final fallback successful");
                }
                catch (Exception fallbackEx)
                {
                    Logger.Log($"UnifiedBubbleFacade: Final fallback also failed: {fallbackEx.Message}");
                    throw new InvalidOperationException($"Failed to display with TTS after all fallback attempts: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Display thinking bubble
        /// </summary>
        /// <param name="thinkingText">Thinking text to display</param>
        /// <returns>Task representing the async operation</returns>
        public async Task ShowThinkingBubbleAsync(string thinkingText)
        {
            if (_disposed)
            {
                Logger.Log("UnifiedBubbleFacade: Cannot show thinking bubble, facade is disposed");
                throw new ObjectDisposedException(nameof(UnifiedBubbleFacade));
            }

            try
            {
                Logger.Log($"UnifiedBubbleFacade: ShowThinkingBubbleAsync called - text: '{thinkingText}'");

                // Provide safe default for thinking text
                var safeText = string.IsNullOrEmpty(thinkingText) ? "思考中..." : thinkingText;

                // Update state with error handling
                try
                {
                    lock (_operationLock)
                    {
                        if (_disposed) return;

                        _currentText = safeText;
                        _isVisible = true;
                        _isThinking = true;
                        _lastUpdateTime = DateTime.Now;
                    }
                }
                catch (Exception stateEx)
                {
                    Logger.Log($"UnifiedBubbleFacade: Error updating thinking state: {stateEx.Message}");
                    // Continue with display even if state update fails
                }

                // Display thinking bubble with comprehensive error handling
                try
                {
                    await Task.Run(() =>
                    {
                        _vpetAPI.Say(safeText, null, false); // No animation for thinking state
                    });

                    Logger.Log("UnifiedBubbleFacade: Thinking bubble displayed successfully");
                }
                catch (Exception apiEx)
                {
                    Logger.Log($"UnifiedBubbleFacade: VPet API call failed for thinking bubble: {apiEx.Message}");

                    // Fallback: Try with minimal parameters
                    try
                    {
                        Logger.Log("UnifiedBubbleFacade: Attempting fallback for thinking bubble");
                        await Task.Run(() =>
                        {
                            _vpetAPI.Say(safeText);
                        });
                        Logger.Log("UnifiedBubbleFacade: Thinking bubble fallback successful");
                    }
                    catch (Exception fallbackEx)
                    {
                        Logger.Log($"UnifiedBubbleFacade: Thinking bubble fallback also failed: {fallbackEx.Message}");

                        // Final fallback: Try with default thinking text
                        try
                        {
                            Logger.Log("UnifiedBubbleFacade: Attempting final fallback with default thinking text");
                            await Task.Run(() =>
                            {
                                _vpetAPI.Say("...");
                            });
                            Logger.Log("UnifiedBubbleFacade: Final thinking bubble fallback successful");
                        }
                        catch (Exception finalEx)
                        {
                            Logger.Log($"UnifiedBubbleFacade: All thinking bubble attempts failed: {finalEx.Message}");
                            throw new InvalidOperationException($"Failed to show thinking bubble after all fallback attempts: {finalEx.Message}", finalEx);
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Re-throw disposal exceptions
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"UnifiedBubbleFacade: Unexpected error in ShowThinkingBubbleAsync: {ex.Message}");
                Logger.Log($"UnifiedBubbleFacade: Stack trace: {ex.StackTrace}");
                throw new InvalidOperationException($"Failed to show thinking bubble: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Transition from thinking state to response
        /// </summary>
        /// <param name="responseText">Response text</param>
        /// <param name="animationName">Animation name (optional)</param>
        /// <returns>Task representing the async operation</returns>
        public async Task TransitionToResponseAsync(string responseText, string animationName = null)
        {
            if (_disposed)
            {
                Logger.Log("UnifiedBubbleFacade: Cannot transition to response, facade is disposed");
                throw new ObjectDisposedException(nameof(UnifiedBubbleFacade));
            }

            try
            {
                Logger.Log($"UnifiedBubbleFacade: TransitionToResponseAsync called - response length: {responseText?.Length}");

                // Validate parameters
                ValidateParameters(responseText, animationName, false);

                // Small delay for smooth transition
                await Task.Delay(100);

                // Display response (this will automatically clear thinking state)
                await DisplayTextAsync(responseText, animationName, false);

                Logger.Log("UnifiedBubbleFacade: Transitioned to response successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"UnifiedBubbleFacade: Error transitioning to response: {ex.Message}");
                throw new InvalidOperationException($"Failed to transition to response: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Hide the bubble
        /// </summary>
        public void HideBubble()
        {
            if (_disposed)
            {
                Logger.Log("UnifiedBubbleFacade: Cannot hide bubble, facade is disposed");
                return;
            }

            try
            {
                Logger.Log("UnifiedBubbleFacade: HideBubble called");

                try
                {
                    lock (_operationLock)
                    {
                        if (_disposed) return;

                        _currentText = string.Empty;
                        _isVisible = false;
                        _isThinking = false;
                        _lastUpdateTime = DateTime.Now;
                    }
                }
                catch (Exception stateEx)
                {
                    Logger.Log($"UnifiedBubbleFacade: Error updating state during hide: {stateEx.Message}");
                    // Continue with hide operation even if state update fails
                }

                // Note: VPet doesn't have a direct "hide" method
                // The bubble will naturally disappear after its display time
                Logger.Log("UnifiedBubbleFacade: Bubble hidden (state updated)");
            }
            catch (Exception ex)
            {
                Logger.Log($"UnifiedBubbleFacade: Error hiding bubble: {ex.Message}");
                // Don't throw exceptions from HideBubble as it's often called in cleanup scenarios
            }
        }

        /// <summary>
        /// Clear all state and reset
        /// </summary>
        public void Clear()
        {
            if (_disposed)
            {
                Logger.Log("UnifiedBubbleFacade: Cannot clear, facade is disposed");
                return;
            }

            try
            {
                Logger.Log("UnifiedBubbleFacade: Clear called");

                try
                {
                    lock (_operationLock)
                    {
                        if (_disposed) return;

                        _currentText = string.Empty;
                        _isVisible = false;
                        _isThinking = false;
                        _lastUpdateTime = DateTime.MinValue;
                    }
                }
                catch (Exception stateEx)
                {
                    Logger.Log($"UnifiedBubbleFacade: Error clearing state: {stateEx.Message}");
                    // Continue with TTS reset even if state clear fails
                }

                // Reset TTS state
                try
                {
                    _ttsLayer.Reset();
                    Logger.Log("UnifiedBubbleFacade: TTS layer reset successfully");
                }
                catch (Exception ttsEx)
                {
                    Logger.Log($"UnifiedBubbleFacade: Error resetting TTS layer: {ttsEx.Message}");
                    // Continue even if TTS reset fails
                }

                Logger.Log("UnifiedBubbleFacade: State cleared");
            }
            catch (Exception ex)
            {
                Logger.Log($"UnifiedBubbleFacade: Error clearing state: {ex.Message}");
                // Don't throw exceptions from Clear as it's often called in cleanup scenarios
            }
        }

        /// <summary>
        /// Validate parameters for VPet API calls
        /// </summary>
        /// <param name="text">Text parameter</param>
        /// <param name="animationName">Animation name parameter</param>
        /// <param name="force">Force parameter</param>
        public void ValidateParameters(string text, string animationName, bool force)
        {
            try
            {
                var result = ParameterValidator.ValidateAll(text, animationName, force);

                if (!result.IsValid)
                {
                    Logger.Log($"UnifiedBubbleFacade: Parameter validation failed: {result.ErrorMessage}");
                    throw new ArgumentException($"Parameter validation failed: {result.ErrorMessage}");
                }

                if (result.Warnings?.Length > 0)
                {
                    foreach (var warning in result.Warnings)
                    {
                        Logger.Log($"UnifiedBubbleFacade: Parameter warning: {warning}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"UnifiedBubbleFacade: Error validating parameters: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Check if an animation is supported
        /// </summary>
        /// <param name="animationName">Animation name to check</param>
        /// <returns>True if supported, false otherwise</returns>
        public bool IsAnimationSupported(string animationName)
        {
            try
            {
                if (string.IsNullOrEmpty(animationName))
                {
                    Logger.Log("UnifiedBubbleFacade: Animation name is null or empty, returning false");
                    return false;
                }

                return _animationCache.IsAnimationSupported(animationName);
            }
            catch (Exception ex)
            {
                Logger.Log($"UnifiedBubbleFacade: Error checking animation support for '{animationName}': {ex.Message}");
                return false; // Default to unsupported on error
            }
        }

        /// <summary>
        /// Get supported animations
        /// </summary>
        /// <returns>Array of supported animation names</returns>
        public string[] GetSupportedAnimations()
        {
            try
            {
                return _animationCache.GetSupportedAnimations();
            }
            catch (Exception ex)
            {
                Logger.Log($"UnifiedBubbleFacade: Error getting supported animations: {ex.Message}");
                return new string[0]; // Return empty array on error
            }
        }

        /// <summary>
        /// Get current TTS state
        /// </summary>
        /// <returns>Current TTS state information</returns>
        public TTSStateInfo GetTTSState()
        {
            try
            {
                return _ttsLayer.CurrentState;
            }
            catch (Exception ex)
            {
                Logger.Log($"UnifiedBubbleFacade: Error getting TTS state: {ex.Message}");
                return new TTSStateInfo(); // Return default state on error
            }
        }

        /// <summary>
        /// Cancel current TTS operation
        /// </summary>
        public void CancelTTS()
        {
            try
            {
                Logger.Log("UnifiedBubbleFacade: Cancelling TTS");
                _ttsLayer.CancelTTS();
                Logger.Log("UnifiedBubbleFacade: TTS cancelled successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"UnifiedBubbleFacade: Error cancelling TTS: {ex.Message}");
                // Don't throw exceptions from CancelTTS as it's often called in cleanup scenarios
            }
        }

        /// <summary>
        /// Show bubble with text (compatibility method for SmartMessageProcessor)
        /// </summary>
        /// <param name="text">Text to display</param>
        /// <param name="options">Bubble options (optional)</param>
        /// <returns>Task representing the async operation</returns>
        public async Task ShowBubbleAsync(string text, object options = null)
        {
            try
            {
                Logger.Log($"UnifiedBubbleFacade: ShowBubbleAsync (compatibility) called - text length: {text?.Length}");

                // Extract animation from options if provided
                string animationName = null;
                if (options is not null)
                {
                    try
                    {
                        var animationProperty = options.GetType().GetProperty("Animation");
                        if (animationProperty is not null)
                        {
                            animationName = animationProperty.GetValue(options) as string;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"UnifiedBubbleFacade: Error extracting animation from options: {ex.Message}");
                        // Continue without animation
                    }
                }

                await DisplayTextAsync(text, animationName, false);
                Logger.Log("UnifiedBubbleFacade: ShowBubbleAsync (compatibility) completed successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"UnifiedBubbleFacade: Error in ShowBubbleAsync (compatibility): {ex.Message}");

                // Final fallback: Try with just the text and no options
                try
                {
                    Logger.Log("UnifiedBubbleFacade: Attempting fallback for ShowBubbleAsync");
                    await DisplayTextAsync(text, null, false);
                    Logger.Log("UnifiedBubbleFacade: ShowBubbleAsync fallback successful");
                }
                catch (Exception fallbackEx)
                {
                    Logger.Log($"UnifiedBubbleFacade: ShowBubbleAsync fallback failed: {fallbackEx.Message}");
                    throw new InvalidOperationException($"Failed to show bubble: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Show bubble with TTS coordination (compatibility method for SmartMessageProcessor)
        /// </summary>
        /// <param name="text">Text to display and speak</param>
        /// <param name="bubbleOptions">Bubble display options (optional)</param>
        /// <param name="ttsOptions">TTS coordination options (optional)</param>
        /// <returns>Task representing the async operation</returns>
        public async Task ShowBubbleWithTTSAsync(string text, object bubbleOptions = null, object ttsOptions = null)
        {
            try
            {
                Logger.Log($"UnifiedBubbleFacade: ShowBubbleWithTTSAsync (compatibility) called - text length: {text?.Length}");

                // Extract animation from bubble options if provided
                string animationName = null;
                if (bubbleOptions is not null)
                {
                    try
                    {
                        var animationProperty = bubbleOptions.GetType().GetProperty("Animation");
                        if (animationProperty is not null)
                        {
                            animationName = animationProperty.GetValue(bubbleOptions) as string;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"UnifiedBubbleFacade: Error extracting animation from bubble options: {ex.Message}");
                        // Continue without animation
                    }
                }

                // Extract TTS settings from tts options if provided
                bool ttsEnabled = true;
                string audioFilePath = null;
                if (ttsOptions is not null)
                {
                    try
                    {
                        var audioFileProperty = ttsOptions.GetType().GetProperty("AudioFilePath");
                        if (audioFileProperty is not null)
                        {
                            audioFilePath = audioFileProperty.GetValue(ttsOptions) as string;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"UnifiedBubbleFacade: Error extracting audio file from TTS options: {ex.Message}");
                        // Continue without audio file
                    }
                }

                // Create TTSOptions for the TTS layer
                TTSOptions ttsOptionsInternal = null;
                try
                {
                    ttsOptionsInternal = new TTSOptions
                    {
                        Enabled = ttsEnabled,
                        AudioFilePath = audioFilePath
                    };
                }
                catch (Exception ex)
                {
                    Logger.Log($"UnifiedBubbleFacade: Error creating TTS options: {ex.Message}");
                    // Fallback to text-only display
                    ttsOptionsInternal = new TTSOptions { Enabled = false };
                }

                await DisplayWithTTSAsync(text, ttsOptionsInternal);
                Logger.Log("UnifiedBubbleFacade: ShowBubbleWithTTSAsync (compatibility) completed successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"UnifiedBubbleFacade: Error in ShowBubbleWithTTSAsync (compatibility): {ex.Message}");

                // Fallback to text-only display
                try
                {
                    Logger.Log("UnifiedBubbleFacade: Attempting fallback to text-only display");
                    await ShowBubbleAsync(text, bubbleOptions);
                    Logger.Log("UnifiedBubbleFacade: ShowBubbleWithTTSAsync fallback successful");
                }
                catch (Exception fallbackEx)
                {
                    Logger.Log($"UnifiedBubbleFacade: ShowBubbleWithTTSAsync fallback failed: {fallbackEx.Message}");
                    throw new InvalidOperationException($"Failed to show bubble with TTS: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Get facade statistics
        /// </summary>
        /// <returns>Facade statistics</returns>
        public BubbleFacadeStatistics GetStatistics()
        {
            try
            {
                lock (_operationLock)
                {
                    return new BubbleFacadeStatistics
                    {
                        IsVisible = _isVisible,
                        IsThinking = _isThinking,
                        CurrentTextLength = _currentText?.Length ?? 0,
                        LastUpdateTime = _lastUpdateTime,
                        TTSState = _ttsLayer.CurrentState.State,
                        AnimationCacheSize = _animationCache.CacheSize,
                        IsDisposed = _disposed
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"UnifiedBubbleFacade: Error getting statistics: {ex.Message}");
                return new BubbleFacadeStatistics();
            }
        }

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                Logger.Log("UnifiedBubbleFacade: Disposing");

                lock (_operationLock)
                {
                    _disposed = true;
                    _currentText = string.Empty;
                    _isVisible = false;
                    _isThinking = false;
                }

                // Reset TTS
                _ttsLayer.Reset();

                Logger.Log("UnifiedBubbleFacade: Disposed");
            }
            catch (Exception ex)
            {
                Logger.Log($"UnifiedBubbleFacade: Error during disposal: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Statistics for the bubble facade
    /// </summary>
    public class BubbleFacadeStatistics
    {
        public bool IsVisible { get; set; }
        public bool IsThinking { get; set; }
        public int CurrentTextLength { get; set; }
        public DateTime LastUpdateTime { get; set; }
        public TTSState TTSState { get; set; }
        public int AnimationCacheSize { get; set; }
        public bool IsDisposed { get; set; }
    }
}