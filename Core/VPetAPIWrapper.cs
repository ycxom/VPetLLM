using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Interfaces;
using VPetLLM.Utils.System;

namespace VPetLLM.Core
{
    /// <summary>
    /// Wrapper around VPet's public API to ensure consistent usage
    /// Eliminates reflection-based calls and provides direct API access
    /// </summary>
    public class VPetAPIWrapper : IVPetAPI
    {
        private readonly IMainWindow _mainWindow;
        private readonly object _plugin;

        public VPetAPIWrapper(IMainWindow mainWindow, object plugin)
        {
            _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        }

        /// <summary>
        /// Whether VPet API is available and ready
        /// </summary>
        public bool IsAvailable => _mainWindow?.Main != null;

        /// <summary>
        /// Display text bubble using VPet's Say method
        /// Direct API call without reflection
        /// </summary>
        public void Say(string text, string graphname = null, bool force = false)
        {
            if (!IsAvailable)
            {
                Logger.Log("VPetAPIWrapper: VPet API not available, cannot display bubble");
                throw new InvalidOperationException("VPet API is not available");
            }

            if (string.IsNullOrEmpty(text))
            {
                Logger.Log("VPetAPIWrapper: Text is null or empty, cannot display bubble");
                throw new ArgumentException("Text cannot be null or empty", nameof(text));
            }

            try
            {
                Logger.Log($"VPetAPIWrapper: Calling Say with text='{text}', graphname='{graphname}', force={force}");

                // Direct call to VPet's Say method - no reflection needed
                _mainWindow.Main.Say(text, graphname, force);

                Logger.Log("VPetAPIWrapper: Say method called successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetAPIWrapper: Error calling Say method: {ex.Message}");
                throw new InvalidOperationException($"Failed to call VPet Say method: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Display text bubble using SayInfoWithOutStream for synchronous operation
        /// </summary>
        public async Task SayInfoWithOutStreamAsync(string text, string graphname = null)
        {
            if (!IsAvailable)
            {
                Logger.Log("VPetAPIWrapper: VPet API not available, cannot display bubble with SayInfoWithOutStream");
                throw new InvalidOperationException("VPet API is not available");
            }

            if (string.IsNullOrEmpty(text))
            {
                Logger.Log("VPetAPIWrapper: Text is null or empty, cannot display bubble");
                throw new ArgumentException("Text cannot be null or empty", nameof(text));
            }

            try
            {
                Logger.Log($"VPetAPIWrapper: Creating SayInfoWithOutStream with text='{text}', graphname='{graphname}'");

                // Create SayInfoWithOutStream instance
                var sayInfo = new SayInfoWithOutStream(text, graphname, false);

                // Use VPet's Say method with SayInfo
                await Task.Run(() =>
                {
                    _mainWindow.Main.Say(sayInfo);
                });

                Logger.Log("VPetAPIWrapper: SayInfoWithOutStream displayed successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetAPIWrapper: Error displaying SayInfoWithOutStream: {ex.Message}");
                throw new InvalidOperationException($"Failed to display SayInfoWithOutStream: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Display text bubble using SayInfoWithStream for asynchronous streaming
        /// </summary>
        public async Task SayInfoWithStreamAsync(string text, string graphname = null)
        {
            if (!IsAvailable)
            {
                Logger.Log("VPetAPIWrapper: VPet API not available, cannot display bubble with SayInfoWithStream");
                throw new InvalidOperationException("VPet API is not available");
            }

            if (string.IsNullOrEmpty(text))
            {
                Logger.Log("VPetAPIWrapper: Text is null or empty, cannot display bubble");
                throw new ArgumentException("Text cannot be null or empty", nameof(text));
            }

            try
            {
                Logger.Log($"VPetAPIWrapper: Creating SayInfoWithStream with text='{text}', graphname='{graphname}'");

                // Create SayInfoWithStream instance
                var sayInfo = new SayInfoWithStream(graphname, false);

                // Start the streaming display
                await Task.Run(() =>
                {
                    _mainWindow.Main.Say(sayInfo);
                });

                // Update the text content
                sayInfo.UpdateAllText(text);

                // Mark as finished
                sayInfo.FinishGenerate();

                Logger.Log("VPetAPIWrapper: SayInfoWithStream displayed successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetAPIWrapper: Error displaying SayInfoWithStream: {ex.Message}");
                throw new InvalidOperationException($"Failed to display SayInfoWithStream: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get the main window instance
        /// </summary>
        public object GetMainWindow()
        {
            return _mainWindow;
        }

        /// <summary>
        /// Check if a specific animation is available
        /// </summary>
        public bool IsAnimationAvailable(string animationName)
        {
            if (!IsAvailable || string.IsNullOrEmpty(animationName))
            {
                return false;
            }

            try
            {
                var graph = _mainWindow.Main.Core.Graph;
                var mode = _mainWindow.Main.Core.Save.Mode;

                var foundGraph = graph.FindGraph(animationName, GraphInfo.AnimatType.A_Start, mode);
                return foundGraph != null;
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetAPIWrapper: Error checking animation availability: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get current VPet mode
        /// </summary>
        public IGameSave.ModeType GetCurrentMode()
        {
            if (!IsAvailable)
            {
                return IGameSave.ModeType.Nomal; // Default fallback
            }

            try
            {
                return _mainWindow.Main.Core.Save.Mode;
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetAPIWrapper: Error getting current mode: {ex.Message}");
                return IGameSave.ModeType.Nomal; // Default fallback
            }
        }

        /// <summary>
        /// Get available animations for current mode
        /// </summary>
        public string[] GetAvailableAnimations()
        {
            if (!IsAvailable)
            {
                return new string[0];
            }

            try
            {
                var graph = _mainWindow.Main.Core.Graph;
                var mode = _mainWindow.Main.Core.Save.Mode;

                // This is a simplified implementation
                // In a real scenario, you'd need to enumerate all available animations
                var commonAnimations = new[]
                {
                    "say", "happy", "sad", "angry", "surprised",
                    "sleep", "eat", "play", "work", "idle"
                };

                var availableAnimations = new List<string>();
                foreach (var animation in commonAnimations)
                {
                    if (IsAnimationAvailable(animation))
                    {
                        availableAnimations.Add(animation);
                    }
                }

                return availableAnimations.ToArray();
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetAPIWrapper: Error getting available animations: {ex.Message}");
                return new string[0];
            }
        }
    }
}