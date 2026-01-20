using System.Collections.Concurrent;
using VPet_Simulator.Core;
using VPetLLM.Interfaces;

namespace VPetLLM.Core.Cache
{
    /// <summary>
    /// Efficient caching system for animation compatibility results
    /// Performs compatibility checks once during initialization and caches results for reuse
    /// </summary>
    public class AnimationCompatibilityCache
    {
        private readonly ConcurrentDictionary<string, AnimationCompatibilityResult> _compatibilityResults;
        private readonly IVPetAPI _vpetAPI;
        private readonly object _initializationLock = new object();
        private bool _isInitialized = false;
        private DateTime _lastInitialization = DateTime.MinValue;
        private IGameSave.ModeType _lastMode = IGameSave.ModeType.Nomal;

        // Common animations to check during initialization
        private static readonly string[] CommonAnimations = new[]
        {
            "say", "happy", "sad", "angry", "surprised", "thinking",
            "sleep", "eat", "play", "work", "idle", "move", "nomal",
            "laugh", "cry", "dance", "run", "walk", "sit", "stand",
            "wave", "nod", "shake", "bow", "clap", "point"
        };

        public AnimationCompatibilityCache(IVPetAPI vpetAPI)
        {
            _vpetAPI = vpetAPI ?? throw new ArgumentNullException(nameof(vpetAPI));
            _compatibilityResults = new ConcurrentDictionary<string, AnimationCompatibilityResult>();
        }

        /// <summary>
        /// Whether the cache has been initialized
        /// </summary>
        public bool IsInitialized
        {
            get
            {
                lock (_initializationLock)
                {
                    return _isInitialized;
                }
            }
        }

        /// <summary>
        /// Number of cached animation results
        /// </summary>
        public int CacheSize => _compatibilityResults.Count;

        /// <summary>
        /// When the cache was last initialized
        /// </summary>
        public DateTime LastInitialization
        {
            get
            {
                lock (_initializationLock)
                {
                    return _lastInitialization;
                }
            }
        }

        /// <summary>
        /// Initialize the animation compatibility cache
        /// Performs compatibility checks once during system startup
        /// </summary>
        public void Initialize()
        {
            lock (_initializationLock)
            {
                if (_isInitialized)
                {
                    Logger.Log("AnimationCompatibilityCache: Already initialized, skipping");
                    return;
                }

                try
                {
                    Logger.Log("AnimationCompatibilityCache: Starting initialization");

                    if (!_vpetAPI.IsAvailable)
                    {
                        Logger.Log("AnimationCompatibilityCache: VPet API not available, cannot initialize");
                        throw new InvalidOperationException("VPet API is not available for animation compatibility checking");
                    }

                    // Get current mode for compatibility checking
                    var currentMode = GetCurrentMode();
                    _lastMode = currentMode;

                    Logger.Log($"AnimationCompatibilityCache: Checking compatibility for {CommonAnimations.Length} animations in mode {currentMode}");

                    int compatibleCount = 0;
                    int incompatibleCount = 0;

                    // Check each common animation
                    foreach (var animation in CommonAnimations)
                    {
                        try
                        {
                            var isSupported = CheckAnimationCompatibility(animation, currentMode);
                            var result = new AnimationCompatibilityResult
                            {
                                AnimationName = animation,
                                IsSupported = isSupported,
                                LastChecked = DateTime.Now,
                                Mode = currentMode,
                                SupportedVariants = isSupported ? new[] { animation } : new string[0]
                            };

                            _compatibilityResults[animation.ToLower()] = result;

                            if (isSupported)
                            {
                                compatibleCount++;
                            }
                            else
                            {
                                incompatibleCount++;
                            }

                            Logger.Log($"AnimationCompatibilityCache: Animation '{animation}' - Supported: {isSupported}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"AnimationCompatibilityCache: Error checking animation '{animation}': {ex.Message}");

                            // Add as unsupported on error
                            var errorResult = new AnimationCompatibilityResult
                            {
                                AnimationName = animation,
                                IsSupported = false,
                                LastChecked = DateTime.Now,
                                Mode = currentMode,
                                ErrorMessage = ex.Message,
                                SupportedVariants = new string[0]
                            };

                            _compatibilityResults[animation.ToLower()] = errorResult;
                            incompatibleCount++;
                        }
                    }

                    _isInitialized = true;
                    _lastInitialization = DateTime.Now;

                    Logger.Log($"AnimationCompatibilityCache: Initialization complete - Compatible: {compatibleCount}, Incompatible: {incompatibleCount}, Total: {_compatibilityResults.Count}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"AnimationCompatibilityCache: Initialization failed: {ex.Message}");
                    throw new InvalidOperationException($"Failed to initialize animation compatibility cache: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Check if an animation is supported
        /// Uses cached results instead of re-checking each time
        /// </summary>
        /// <param name="animationName">Animation name to check</param>
        /// <returns>True if animation is supported, false otherwise</returns>
        public bool IsAnimationSupported(string animationName)
        {
            if (string.IsNullOrEmpty(animationName))
            {
                return true; // Null/empty animation name means no animation, which is always supported
            }

            try
            {
                // Ensure cache is initialized
                if (!IsInitialized)
                {
                    Logger.Log("AnimationCompatibilityCache: Cache not initialized, initializing now");
                    Initialize();
                }

                var key = animationName.ToLower();

                // Check if we have a cached result
                if (_compatibilityResults.TryGetValue(key, out var cachedResult))
                {
                    Logger.Log($"AnimationCompatibilityCache: Found cached result for '{animationName}': {cachedResult.IsSupported}");
                    return cachedResult.IsSupported;
                }

                // Animation not in cache, check it now and cache the result
                Logger.Log($"AnimationCompatibilityCache: Animation '{animationName}' not in cache, checking now");

                var currentMode = GetCurrentMode();
                var isSupported = CheckAnimationCompatibility(animationName, currentMode);

                var result = new AnimationCompatibilityResult
                {
                    AnimationName = animationName,
                    IsSupported = isSupported,
                    LastChecked = DateTime.Now,
                    Mode = currentMode,
                    SupportedVariants = isSupported ? new[] { animationName } : new string[0]
                };

                _compatibilityResults[key] = result;

                Logger.Log($"AnimationCompatibilityCache: Checked and cached '{animationName}': {isSupported}");
                return isSupported;
            }
            catch (Exception ex)
            {
                Logger.Log($"AnimationCompatibilityCache: Error checking animation '{animationName}': {ex.Message}");
                return false; // Default to unsupported on error
            }
        }

        /// <summary>
        /// Refresh the compatibility cache
        /// Re-checks all cached animations and updates results
        /// </summary>
        public void RefreshCache()
        {
            lock (_initializationLock)
            {
                try
                {
                    Logger.Log("AnimationCompatibilityCache: Starting cache refresh");

                    if (!_vpetAPI.IsAvailable)
                    {
                        Logger.Log("AnimationCompatibilityCache: VPet API not available, cannot refresh cache");
                        return;
                    }

                    var currentMode = GetCurrentMode();
                    var animationsToRefresh = _compatibilityResults.Keys.ToArray();

                    Logger.Log($"AnimationCompatibilityCache: Refreshing {animationsToRefresh.Length} cached animations for mode {currentMode}");

                    int refreshedCount = 0;
                    int changedCount = 0;

                    foreach (var key in animationsToRefresh)
                    {
                        try
                        {
                            if (_compatibilityResults.TryGetValue(key, out var oldResult))
                            {
                                var isSupported = CheckAnimationCompatibility(oldResult.AnimationName, currentMode);

                                var newResult = new AnimationCompatibilityResult
                                {
                                    AnimationName = oldResult.AnimationName,
                                    IsSupported = isSupported,
                                    LastChecked = DateTime.Now,
                                    Mode = currentMode,
                                    SupportedVariants = isSupported ? new[] { oldResult.AnimationName } : new string[0]
                                };

                                _compatibilityResults[key] = newResult;
                                refreshedCount++;

                                if (oldResult.IsSupported != isSupported)
                                {
                                    changedCount++;
                                    Logger.Log($"AnimationCompatibilityCache: Animation '{oldResult.AnimationName}' support changed: {oldResult.IsSupported} -> {isSupported}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"AnimationCompatibilityCache: Error refreshing animation '{key}': {ex.Message}");
                        }
                    }

                    _lastMode = currentMode;
                    _lastInitialization = DateTime.Now;

                    Logger.Log($"AnimationCompatibilityCache: Cache refresh complete - Refreshed: {refreshedCount}, Changed: {changedCount}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"AnimationCompatibilityCache: Cache refresh failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Clear the compatibility cache
        /// Removes all cached results and marks as uninitialized
        /// </summary>
        public void ClearCache()
        {
            lock (_initializationLock)
            {
                try
                {
                    var count = _compatibilityResults.Count;
                    _compatibilityResults.Clear();
                    _isInitialized = false;
                    _lastInitialization = DateTime.MinValue;

                    Logger.Log($"AnimationCompatibilityCache: Cache cleared - Removed {count} entries");
                }
                catch (Exception ex)
                {
                    Logger.Log($"AnimationCompatibilityCache: Error clearing cache: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Get all supported animations
        /// </summary>
        /// <returns>Array of supported animation names</returns>
        public string[] GetSupportedAnimations()
        {
            try
            {
                if (!IsInitialized)
                {
                    Initialize();
                }

                var supported = _compatibilityResults.Values
                    .Where(r => r.IsSupported)
                    .Select(r => r.AnimationName)
                    .ToArray();

                Logger.Log($"AnimationCompatibilityCache: Found {supported.Length} supported animations");
                return supported;
            }
            catch (Exception ex)
            {
                Logger.Log($"AnimationCompatibilityCache: Error getting supported animations: {ex.Message}");
                return new string[0];
            }
        }

        /// <summary>
        /// Get compatibility result for a specific animation
        /// </summary>
        /// <param name="animationName">Animation name</param>
        /// <returns>Compatibility result or null if not found</returns>
        public AnimationCompatibilityResult GetCompatibilityResult(string animationName)
        {
            if (string.IsNullOrEmpty(animationName))
            {
                return null;
            }

            try
            {
                var key = animationName.ToLower();
                return _compatibilityResults.TryGetValue(key, out var result) ? result : null;
            }
            catch (Exception ex)
            {
                Logger.Log($"AnimationCompatibilityCache: Error getting compatibility result for '{animationName}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get cache statistics
        /// </summary>
        /// <returns>Cache statistics</returns>
        public AnimationCacheStatistics GetStatistics()
        {
            try
            {
                var results = _compatibilityResults.Values.ToArray();

                return new AnimationCacheStatistics
                {
                    TotalAnimations = results.Length,
                    SupportedAnimations = results.Count(r => r.IsSupported),
                    UnsupportedAnimations = results.Count(r => !r.IsSupported),
                    LastInitialization = _lastInitialization,
                    IsInitialized = _isInitialized,
                    CurrentMode = _lastMode
                };
            }
            catch (Exception ex)
            {
                Logger.Log($"AnimationCompatibilityCache: Error getting statistics: {ex.Message}");
                return new AnimationCacheStatistics();
            }
        }

        /// <summary>
        /// Check if cache needs refresh due to mode change
        /// </summary>
        /// <returns>True if refresh is needed</returns>
        public bool NeedsRefresh()
        {
            try
            {
                if (!IsInitialized)
                {
                    return true;
                }

                var currentMode = GetCurrentMode();
                return currentMode != _lastMode;
            }
            catch (Exception ex)
            {
                Logger.Log($"AnimationCompatibilityCache: Error checking if refresh needed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get current VPet mode
        /// </summary>
        private IGameSave.ModeType GetCurrentMode()
        {
            try
            {
                if (_vpetAPI is VPetAPIWrapper wrapper)
                {
                    return wrapper.GetCurrentMode();
                }

                // Fallback to default mode
                return IGameSave.ModeType.Nomal;
            }
            catch (Exception ex)
            {
                Logger.Log($"AnimationCompatibilityCache: Error getting current mode: {ex.Message}");
                return IGameSave.ModeType.Nomal;
            }
        }

        /// <summary>
        /// Check animation compatibility using VPet API
        /// </summary>
        private bool CheckAnimationCompatibility(string animationName, IGameSave.ModeType mode)
        {
            try
            {
                if (_vpetAPI is VPetAPIWrapper wrapper)
                {
                    return wrapper.IsAnimationAvailable(animationName);
                }

                // Fallback: assume common animations are available
                return CommonAnimations.Contains(animationName.ToLower());
            }
            catch (Exception ex)
            {
                Logger.Log($"AnimationCompatibilityCache: Error checking compatibility for '{animationName}': {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Animation compatibility result
    /// </summary>
    public class AnimationCompatibilityResult
    {
        public string AnimationName { get; set; }
        public bool IsSupported { get; set; }
        public DateTime LastChecked { get; set; }
        public IGameSave.ModeType Mode { get; set; }
        public string[] SupportedVariants { get; set; }
        public string ErrorMessage { get; set; }

        public AnimationCompatibilityResult()
        {
            SupportedVariants = new string[0];
        }
    }

    /// <summary>
    /// Animation cache statistics
    /// </summary>
    public class AnimationCacheStatistics
    {
        public int TotalAnimations { get; set; }
        public int SupportedAnimations { get; set; }
        public int UnsupportedAnimations { get; set; }
        public DateTime LastInitialization { get; set; }
        public bool IsInitialized { get; set; }
        public IGameSave.ModeType CurrentMode { get; set; }

        public double SupportPercentage => TotalAnimations > 0 ? (double)SupportedAnimations / TotalAnimations * 100 : 0;
    }
}