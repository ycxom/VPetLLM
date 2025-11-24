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
    }
}
