using System;
using System.Collections.Generic;
using System.Linq;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// Manages work, study, and play activities for VPet
    /// Provides work list caching and fuzzy matching capabilities
    /// </summary>
    public static class WorkManager
    {
        private static List<object> _cachedWorks = null;
        private static List<object> _cachedStudies = null;
        private static List<object> _cachedPlays = null;
        private static DateTime _lastCacheTime = DateTime.MinValue;
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Gets all available works, studies, and plays from VPet
        /// Results are cached for 5 minutes
        /// </summary>
        public static void RefreshWorkLists(IMainWindow mainWindow)
        {
            if (mainWindow?.Main == null)
            {
                Logger.Log("WorkManager: mainWindow is null, cannot refresh work lists");
                return;
            }

            try
            {
                // Check if we need to refresh cache
                if (_cachedWorks != null && (DateTime.Now - _lastCacheTime) < CacheExpiration)
                {
                    Logger.Log("WorkManager: Using cached work lists");
                    return;
                }

                Logger.Log("WorkManager: Refreshing work lists from VPet");

                // Try to call WorkList method directly (without reflection for better compatibility)
                try
                {
                    // Try direct call first
                    List<VPet_Simulator.Core.GraphHelper.Work> works = null;
                    List<VPet_Simulator.Core.GraphHelper.Work> studies = null;
                    List<VPet_Simulator.Core.GraphHelper.Work> plays = null;
                    
                    mainWindow.Main.WorkList(out works, out studies, out plays);
                    
                    _cachedWorks = works?.Cast<object>().ToList() ?? new List<object>();
                    _cachedStudies = studies?.Cast<object>().ToList() ?? new List<object>();
                    _cachedPlays = plays?.Cast<object>().ToList() ?? new List<object>();
                    _lastCacheTime = DateTime.Now;

                    Logger.Log($"WorkManager: Loaded {_cachedWorks.Count} works, {_cachedStudies.Count} studies, {_cachedPlays.Count} plays");
                    return;
                }
                catch (Exception directEx)
                {
                    Logger.Log($"WorkManager: Direct call failed: {directEx.Message}, trying reflection");
                }

                // Fallback to reflection if direct call fails
                var mainType = mainWindow.Main.GetType();
                var workListMethod = mainType.GetMethod("WorkList");

                if (workListMethod == null)
                {
                    Logger.Log("WorkManager: WorkList method not found");
                    _cachedWorks = new List<object>();
                    _cachedStudies = new List<object>();
                    _cachedPlays = new List<object>();
                    return;
                }

                // Prepare out parameters for reflection
                var parameters = new object[] { null, null, null };
                workListMethod.Invoke(mainWindow.Main, parameters);

                _cachedWorks = (parameters[0] as System.Collections.IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
                _cachedStudies = (parameters[1] as System.Collections.IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
                _cachedPlays = (parameters[2] as System.Collections.IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
                _lastCacheTime = DateTime.Now;

                Logger.Log($"WorkManager: Loaded {_cachedWorks.Count} works, {_cachedStudies.Count} studies, {_cachedPlays.Count} plays (via reflection)");
            }
            catch (Exception ex)
            {
                Logger.Log($"WorkManager: Error refreshing work lists: {ex.Message}");
                _cachedWorks = new List<object>();
                _cachedStudies = new List<object>();
                _cachedPlays = new List<object>();
            }
        }

        /// <summary>
        /// Finds a work by name using fuzzy matching
        /// </summary>
        /// <param name="workName">The work name to search for</param>
        /// <param name="workType">The type of work (work, study, play)</param>
        /// <returns>The matching work object, or null if not found</returns>
        public static object FindWork(string workName, string workType = "work")
        {
            if (string.IsNullOrWhiteSpace(workName))
                return null;

            List<object> searchList = workType.ToLower() switch
            {
                "study" => _cachedStudies,
                "play" => _cachedPlays,
                _ => _cachedWorks
            };

            if (searchList == null || searchList.Count == 0)
            {
                Logger.Log($"WorkManager: No {workType} list available");
                return null;
            }

            Logger.Log($"WorkManager: Searching for '{workName}' in {workType} list ({searchList.Count} items)");

            // Try exact match first (Name or NameTrans)
            foreach (var work in searchList)
            {
                var name = GetWorkProperty(work, "Name") as string;
                var nameTrans = GetWorkProperty(work, "NameTrans") as string;

                if (name != null && name.Equals(workName, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log($"WorkManager: Found exact match by Name: {name}");
                    return work;
                }

                if (nameTrans != null && nameTrans.Equals(workName, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log($"WorkManager: Found exact match by NameTrans: {nameTrans}");
                    return work;
                }
            }

            // Try partial match
            foreach (var work in searchList)
            {
                var name = GetWorkProperty(work, "Name") as string;
                var nameTrans = GetWorkProperty(work, "NameTrans") as string;

                if (name != null && name.IndexOf(workName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Logger.Log($"WorkManager: Found partial match by Name: {name}");
                    return work;
                }

                if (nameTrans != null && nameTrans.IndexOf(workName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Logger.Log($"WorkManager: Found partial match by NameTrans: {nameTrans}");
                    return work;
                }
            }

            // Try reverse partial match (workName contains the work's name)
            foreach (var work in searchList)
            {
                var name = GetWorkProperty(work, "Name") as string;
                var nameTrans = GetWorkProperty(work, "NameTrans") as string;

                if (name != null && workName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Logger.Log($"WorkManager: Found reverse partial match by Name: {name}");
                    return work;
                }

                if (nameTrans != null && workName.IndexOf(nameTrans, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Logger.Log($"WorkManager: Found reverse partial match by NameTrans: {nameTrans}");
                    return work;
                }
            }

            Logger.Log($"WorkManager: No match found for '{workName}'");
            return null;
        }

        /// <summary>
        /// Starts a work activity using VPet's StartWork method
        /// </summary>
        public static bool StartWork(IMainWindow mainWindow, object work)
        {
            if (mainWindow?.Main == null || work == null)
            {
                Logger.Log("WorkManager: Invalid parameters for StartWork");
                return false;
            }

            try
            {
                var workName = GetWorkProperty(work, "NameTrans") ?? GetWorkProperty(work, "Name");
                Logger.Log($"WorkManager: Starting work: {workName}");

                // Try direct call first (if work is of type VPet_Simulator.Core.GraphHelper.Work)
                try
                {
                    if (work is VPet_Simulator.Core.GraphHelper.Work vpetWork)
                    {
                        bool directResult = mainWindow.Main.StartWork(vpetWork);
                        if (directResult)
                        {
                            Logger.Log($"WorkManager: Successfully started work: {workName} (direct call)");
                        }
                        else
                        {
                            Logger.Log($"WorkManager: Failed to start work: {workName} (direct call returned false)");
                        }
                        return directResult;
                    }
                }
                catch (Exception directEx)
                {
                    Logger.Log($"WorkManager: Direct call failed: {directEx.Message}, trying reflection");
                }

                // Fallback to reflection
                var mainType = mainWindow.Main.GetType();
                var startWorkMethod = mainType.GetMethod("StartWork", new[] { work.GetType() });

                if (startWorkMethod == null)
                {
                    Logger.Log("WorkManager: StartWork method not found with matching parameter type");
                    return false;
                }

                var result = startWorkMethod.Invoke(mainWindow.Main, new[] { work });
                bool success = result is bool b && b;

                if (success)
                {
                    Logger.Log($"WorkManager: Successfully started work: {workName} (via reflection)");
                }
                else
                {
                    Logger.Log($"WorkManager: Failed to start work: {workName} (via reflection returned false)");
                }

                return success;
            }
            catch (Exception ex)
            {
                Logger.Log($"WorkManager: Error starting work: {ex.Message}");
                Logger.Log($"WorkManager: Exception type: {ex.GetType().Name}");
                if (ex.InnerException != null)
                {
                    Logger.Log($"WorkManager: Inner exception: {ex.InnerException.Message}");
                }
                return false;
            }
        }

        /// <summary>
        /// Gets a property value from a work object using reflection
        /// </summary>
        private static object GetWorkProperty(object work, string propertyName)
        {
            try
            {
                var property = work.GetType().GetProperty(propertyName);
                return property?.GetValue(work);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets a formatted list of all available works for display in prompts
        /// </summary>
        public static string GetWorkListForPrompt(IMainWindow mainWindow)
        {
            RefreshWorkLists(mainWindow);

            var result = new System.Text.StringBuilder();

            if (_cachedWorks != null && _cachedWorks.Count > 0)
            {
                result.AppendLine("Available Works:");
                foreach (var work in _cachedWorks.Take(10)) // Limit to first 10
                {
                    var name = GetWorkProperty(work, "NameTrans") ?? GetWorkProperty(work, "Name");
                    result.AppendLine($"  - {name}");
                }
                if (_cachedWorks.Count > 10)
                    result.AppendLine($"  ... and {_cachedWorks.Count - 10} more");
            }

            if (_cachedStudies != null && _cachedStudies.Count > 0)
            {
                result.AppendLine("\nAvailable Studies:");
                foreach (var study in _cachedStudies.Take(10))
                {
                    var name = GetWorkProperty(study, "NameTrans") ?? GetWorkProperty(study, "Name");
                    result.AppendLine($"  - {name}");
                }
                if (_cachedStudies.Count > 10)
                    result.AppendLine($"  ... and {_cachedStudies.Count - 10} more");
            }

            if (_cachedPlays != null && _cachedPlays.Count > 0)
            {
                result.AppendLine("\nAvailable Plays:");
                foreach (var play in _cachedPlays.Take(10))
                {
                    var name = GetWorkProperty(play, "NameTrans") ?? GetWorkProperty(play, "Name");
                    result.AppendLine($"  - {name}");
                }
                if (_cachedPlays.Count > 10)
                    result.AppendLine($"  ... and {_cachedPlays.Count - 10} more");
            }

            return result.ToString();
        }

        /// <summary>
        /// Clears the work list cache, forcing a refresh on next access
        /// </summary>
        public static void ClearCache()
        {
            _cachedWorks = null;
            _cachedStudies = null;
            _cachedPlays = null;
            _lastCacheTime = DateTime.MinValue;
            Logger.Log("WorkManager: Cache cleared");
        }

        /// <summary>
        /// Calculates the maximum allowed rate for a work based on pet level and work level limit.
        /// Formula: Math.Min(4000, petLevel) / (workLevelLimit + 10)
        /// </summary>
        /// <param name="petLevel">The current pet level</param>
        /// <param name="workLevelLimit">The work's level limit requirement</param>
        /// <returns>The maximum allowed rate, minimum of 1</returns>
        public static int CalculateMaxRate(int petLevel, int workLevelLimit)
        {
            int effectiveLevel = Math.Min(4000, petLevel);
            int divisor = workLevelLimit + 10;
            int maxRate = effectiveLevel / divisor;
            return Math.Max(1, maxRate);
        }

        /// <summary>
        /// Clamps the requested rate to the valid range [1, maxRate].
        /// </summary>
        /// <param name="requestedRate">The rate requested by the user</param>
        /// <param name="maxRate">The maximum allowed rate</param>
        /// <returns>The clamped rate within [1, maxRate]</returns>
        public static int ClampRate(int requestedRate, int maxRate)
        {
            return Math.Max(1, Math.Min(requestedRate, maxRate));
        }

        /// <summary>
        /// Gets the maximum rate for a specific work based on pet level.
        /// </summary>
        /// <param name="mainWindow">The main window interface</param>
        /// <param name="work">The work object</param>
        /// <returns>The maximum allowed rate for this work</returns>
        public static int GetMaxRate(IMainWindow mainWindow, object work)
        {
            if (mainWindow?.GameSavesData?.GameSave == null || work == null)
            {
                Logger.Log("WorkManager: Invalid parameters for GetMaxRate, returning 1");
                return 1;
            }

            int petLevel = mainWindow.GameSavesData.GameSave.Level;
            int levelLimit = 0;

            // Get LevelLimit from work object
            var levelLimitValue = GetWorkProperty(work, "LevelLimit");
            if (levelLimitValue is int ll)
            {
                levelLimit = ll;
            }
            else if (levelLimitValue != null && int.TryParse(levelLimitValue.ToString(), out int parsedLimit))
            {
                levelLimit = parsedLimit;
            }

            return CalculateMaxRate(petLevel, levelLimit);
        }

        /// <summary>
        /// Starts a work activity with a specified rate.
        /// The rate will be clamped to the valid range [1, maxRate].
        /// </summary>
        /// <param name="mainWindow">The main window interface</param>
        /// <param name="work">The work object to start</param>
        /// <param name="requestedRate">The requested rate (will be clamped if necessary)</param>
        /// <returns>True if work started successfully, false otherwise</returns>
        public static bool StartWorkWithRate(IMainWindow mainWindow, object work, int requestedRate)
        {
            if (mainWindow?.Main == null || work == null)
            {
                Logger.Log("WorkManager: Invalid parameters for StartWorkWithRate");
                return false;
            }

            var workName = GetWorkProperty(work, "NameTrans") ?? GetWorkProperty(work, "Name");
            
            // If rate is 1, just use the original StartWork
            if (requestedRate <= 1)
            {
                Logger.Log($"WorkManager: Starting work '{workName}' with rate 1 (default)");
                return StartWork(mainWindow, work);
            }

            // Calculate max rate and clamp
            int maxRate = GetMaxRate(mainWindow, work);
            int clampedRate = ClampRate(requestedRate, maxRate);

            // Log rate clamping information
            if (clampedRate < requestedRate)
            {
                Logger.Log($"WorkManager: Rate clamped from {requestedRate} to maximum {clampedRate} for work '{workName}'");
            }
            else if (requestedRate < 1)
            {
                Logger.Log($"WorkManager: Rate clamped from {requestedRate} to minimum 1 for work '{workName}'");
            }

            // If clamped rate is 1, use original work
            if (clampedRate == 1)
            {
                Logger.Log($"WorkManager: Starting work '{workName}' with rate 1 (clamped from {requestedRate})");
                return StartWork(mainWindow, work);
            }

            try
            {
                // Create doubled work using VPet's Work.Double extension
                if (work is VPet_Simulator.Core.GraphHelper.Work vpetWork)
                {
                    var doubledWork = VPet_Simulator.Windows.Interface.ExtensionFunction.Double(vpetWork, clampedRate);
                    Logger.Log($"WorkManager: Starting work '{workName}' with rate {clampedRate}");
                    bool result = mainWindow.Main.StartWork(doubledWork);
                    if (result)
                    {
                        Logger.Log($"WorkManager: Successfully started work '{workName}' with rate {clampedRate}");
                    }
                    else
                    {
                        Logger.Log($"WorkManager: Failed to start work '{workName}' with rate {clampedRate}");
                    }
                    return result;
                }
                else
                {
                    // Fallback: try reflection for Work.Double
                    Logger.Log($"WorkManager: Work is not VPet_Simulator.Core.GraphHelper.Work type, trying reflection");
                    var extensionType = typeof(VPet_Simulator.Windows.Interface.ExtensionFunction);
                    var doubleMethod = extensionType.GetMethod("Double", new[] { work.GetType(), typeof(int) });
                    
                    if (doubleMethod != null)
                    {
                        var doubledWork = doubleMethod.Invoke(null, new object[] { work, clampedRate });
                        Logger.Log($"WorkManager: Starting work '{workName}' with rate {clampedRate} (via reflection)");
                        return StartWork(mainWindow, doubledWork);
                    }
                    else
                    {
                        Logger.Log($"WorkManager: Could not find Double method, falling back to rate 1");
                        return StartWork(mainWindow, work);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"WorkManager: Error starting work with rate: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Logger.Log($"WorkManager: Inner exception: {ex.InnerException.Message}");
                }
                // Fallback to starting without rate
                Logger.Log($"WorkManager: Falling back to starting work '{workName}' without rate");
                return StartWork(mainWindow, work);
            }
        }
    }
}
