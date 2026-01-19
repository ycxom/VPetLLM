using System.Collections.Concurrent;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils.System;
using VPetLLM.Utils.UI;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// Helper class for managing VPet state transitions
    /// Ensures proper state changes with error handling and comprehensive logging
    /// Includes queueing mechanism for concurrent state change requests
    /// Integrates with AnimationStateChecker to respect important animations
    /// </summary>
    public static class StateManager
    {
        /// <summary>
        /// Queue for pending state transition requests
        /// </summary>
        private static readonly ConcurrentQueue<StateTransitionRequest> _stateTransitionQueue = new ConcurrentQueue<StateTransitionRequest>();

        /// <summary>
        /// Semaphore to ensure only one state transition executes at a time
        /// </summary>
        private static readonly SemaphoreSlim _transitionLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Flag to track if the queue processor is running
        /// </summary>
        private static int _isProcessing = 0;

        /// <summary>
        /// Represents a state transition request
        /// </summary>
        private class StateTransitionRequest
        {
            public IMainWindow MainWindow { get; set; }
            public object NewState { get; set; }
            public string ActionName { get; set; }
            public DateTime RequestTime { get; set; }
        }

        /// <summary>
        /// Helper method to get State (tries field first, then property)
        /// </summary>
        private static object GetState(IMainWindow mainWindow)
        {
            // Try field first (VPet uses field)
            var stateField = mainWindow.Main.GetType().GetField("State");
            if (stateField != null)
            {
                return stateField.GetValue(mainWindow.Main);
            }

            // Fallback to property
            var stateProperty = mainWindow.Main.GetType().GetProperty("State");
            if (stateProperty != null)
            {
                return stateProperty.GetValue(mainWindow.Main);
            }

            return null;
        }

        /// <summary>
        /// Helper method to set State (tries field first, then property)
        /// </summary>
        private static bool SetState(IMainWindow mainWindow, object newState)
        {
            // Try field first (VPet uses field)
            var stateField = mainWindow.Main.GetType().GetField("State");
            if (stateField != null)
            {
                stateField.SetValue(mainWindow.Main, newState);
                return true;
            }

            // Fallback to property
            var stateProperty = mainWindow.Main.GetType().GetProperty("State");
            if (stateProperty != null)
            {
                stateProperty.SetValue(mainWindow.Main, newState);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Helper method to get State type (tries field first, then property)
        /// </summary>
        private static Type GetStateType(IMainWindow mainWindow)
        {
            // Try field first (VPet uses field)
            var stateField = mainWindow.Main.GetType().GetField("State");
            if (stateField != null)
            {
                return stateField.FieldType;
            }

            // Fallback to property
            var stateProperty = mainWindow.Main.GetType().GetProperty("State");
            if (stateProperty != null)
            {
                return stateProperty.PropertyType;
            }

            return null;
        }
        /// <summary>
        /// Transitions the VPet to a new state with proper error handling and rollback
        /// This method queues the state transition to handle concurrent requests
        /// </summary>
        /// <param name="mainWindow">The main window instance</param>
        /// <param name="newState">The target state to transition to</param>
        /// <param name="actionName">The name of the action triggering this state change</param>
        public static void TransitionToState(IMainWindow mainWindow, object newState, string actionName)
        {
            if (mainWindow == null)
            {
                Logger.Log("StateManager: mainWindow is null, cannot queue state transition");
                return;
            }

            // Create a state transition request
            var request = new StateTransitionRequest
            {
                MainWindow = mainWindow,
                NewState = newState,
                ActionName = actionName,
                RequestTime = DateTime.Now
            };

            // Enqueue the request
            _stateTransitionQueue.Enqueue(request);
            Logger.Log($"StateManager: Queued state transition to {newState} (action: {actionName}) at {request.RequestTime:HH:mm:ss.fff}");

            // Start processing the queue if not already running
            ProcessQueueAsync();
        }

        /// <summary>
        /// Processes the state transition queue asynchronously
        /// Ensures only one state transition executes at a time
        /// </summary>
        private static async void ProcessQueueAsync()
        {
            // Check if already processing (atomic operation)
            if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0)
            {
                // Another thread is already processing the queue
                return;
            }

            try
            {
                while (_stateTransitionQueue.TryDequeue(out var request))
                {
                    Logger.Log($"StateManager: Processing queued state transition to {request.NewState} (action: {request.ActionName}) requested at {request.RequestTime:HH:mm:ss.fff}");

                    // Execute the state transition with the lock
                    await _transitionLock.WaitAsync();
                    try
                    {
                        ExecuteStateTransition(request.MainWindow, request.NewState, request.ActionName);
                    }
                    finally
                    {
                        _transitionLock.Release();
                    }

                    // Small delay to allow UI to update
                    await Task.Delay(50);
                }

                Logger.Log("StateManager: Queue processing completed - all pending state transitions executed");
            }
            finally
            {
                // Reset the processing flag
                Interlocked.Exchange(ref _isProcessing, 0);
            }
        }

        /// <summary>
        /// Executes a single state transition with proper error handling and rollback
        /// This is the internal implementation called by the queue processor
        /// Integrates with AnimationStateChecker to ensure important animations are not interrupted
        /// </summary>
        /// <param name="mainWindow">The main window instance</param>
        /// <param name="newState">The target state to transition to</param>
        /// <param name="actionName">The name of the action triggering this state change</param>
        private static void ExecuteStateTransition(IMainWindow mainWindow, object newState, string actionName)
        {
            if (mainWindow == null)
            {
                Logger.Log("StateManager: mainWindow is null, cannot transition state");
                return;
            }

            // Check with AnimationStateChecker if state transition should proceed
            if (!AnimationStateChecker.CanExecuteStateTransition(mainWindow, newState, actionName))
            {
                Logger.Log($"StateManager: AnimationStateChecker blocked state transition to {newState} (action: {actionName})");
                Logger.Log($"StateManager: Current state: {AnimationStateChecker.GetCurrentStateDescription(mainWindow)}");
                return;
            }

            object previousState = null;
            bool stateChanged = false;

            try
            {
                // Get the current state using helper method (tries field first, then property)
                previousState = GetState(mainWindow);
                var workingStateType = GetStateType(mainWindow);

                if (previousState == null || workingStateType == null)
                {
                    Logger.Log("StateManager: State field/property not found, using fallback mode for older VPet versions");
                    // Fallback: directly call display methods without state management
                    ExecuteStateTransitionFallback(mainWindow, newState, actionName);
                    return;
                }

                // Convert newState to the correct enum type if it's a string
                object targetState = newState;
                if (newState is string stateString)
                {
                    try
                    {
                        targetState = Enum.Parse(workingStateType, stateString, ignoreCase: true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"StateManager: Failed to parse state '{stateString}': {ex.Message}");
                        return;
                    }
                }

                Logger.Log($"StateManager: Transitioning from {previousState} to {targetState} (action: {actionName})");

                // Get the enum value names for comparison
                string targetStateName = targetState.ToString();
                string previousStateName = previousState?.ToString() ?? "Unknown";

                // Perform state-specific transitions
                switch (targetStateName)
                {
                    case "Sleep":
                        // Use DisplaySleep with force=true to change both animation and state
                        bool forceParameter = true;
                        Logger.Log($"StateManager: Calling DisplaySleep(force: {forceParameter})");
                        mainWindow.Main.DisplaySleep(force: forceParameter);
                        stateChanged = true;
                        Logger.Log($"StateManager: DisplaySleep completed with force={forceParameter}");
                        break;

                    case "Nomal":
                        // Transition to normal state
                        Logger.Log("StateManager: Calling DisplayToNomal() and setting State to Nomal");
                        mainWindow.Main.DisplayToNomal();
                        SetState(mainWindow, targetState);
                        stateChanged = true;
                        Logger.Log("StateManager: DisplayToNomal() completed and State set to Nomal");
                        break;

                    case "Work":
                        // Transition to work state
                        Logger.Log("StateManager: Setting State to Work");
                        SetState(mainWindow, targetState);
                        // Optionally trigger work animation if available
                        try
                        {
                            mainWindow.Main.Display("work", VPet_Simulator.Core.GraphInfo.AnimatType.Single, mainWindow.Main.DisplayToNomal);
                            Logger.Log("StateManager: Work animation triggered");
                        }
                        catch (Exception animEx)
                        {
                            Logger.Log($"StateManager: Work animation not available or failed: {animEx.Message}");
                        }
                        stateChanged = true;
                        Logger.Log("StateManager: State set to Work");
                        break;

                    case "Study":
                        // Transition to study state (if supported by VPet version)
                        Logger.Log("StateManager: Setting State to Study");
                        SetState(mainWindow, targetState);
                        // Optionally trigger study animation if available
                        try
                        {
                            mainWindow.Main.Display("study", VPet_Simulator.Core.GraphInfo.AnimatType.Single, mainWindow.Main.DisplayToNomal);
                            Logger.Log("StateManager: Study animation triggered");
                        }
                        catch (Exception animEx)
                        {
                            Logger.Log($"StateManager: Study animation not available or failed: {animEx.Message}");
                        }
                        stateChanged = true;
                        Logger.Log("StateManager: State set to Study");
                        break;

                    default:
                        // For other states, attempt direct state assignment
                        Logger.Log($"StateManager: Setting State directly to {targetStateName}");
                        SetState(mainWindow, targetState);
                        stateChanged = true;
                        Logger.Log($"StateManager: State set to {targetStateName}");
                        break;
                }

                // Verify the state change using the verification helper
                bool verificationPassed = VerifyStateAfterTransition(mainWindow, targetState, actionName);

                if (!verificationPassed)
                {
                    Logger.Log($"StateManager: Warning - State verification failed after transition to {targetStateName}");
                }
            }
            catch (Exception ex)
            {
                // Log error with full context
                Logger.Log($"StateManager: ERROR during state transition");
                Logger.Log($"StateManager: Context - Action: {actionName}, Target State: {newState}, Previous State: {previousState}");
                Logger.Log($"StateManager: Exception Type: {ex.GetType().Name}");
                Logger.Log($"StateManager: Exception Message: {ex.Message}");
                Logger.Log($"StateManager: Stack trace: {ex.StackTrace}");

                // Attempt rollback if state was changed
                if (stateChanged && previousState != null)
                {
                    try
                    {
                        Logger.Log($"StateManager: Attempting rollback to previous state {previousState}");
                        if (SetState(mainWindow, previousState))
                        {
                            Logger.Log($"StateManager: Successfully rolled back to {previousState}");
                        }
                        else
                        {
                            Logger.Log("StateManager: Rollback failed - State field/property not found");
                        }
                    }
                    catch (Exception rollbackEx)
                    {
                        Logger.Log($"StateManager: Rollback failed with exception: {rollbackEx.GetType().Name}");
                        Logger.Log($"StateManager: Rollback exception message: {rollbackEx.Message}");
                        Logger.Log("StateManager: State may be inconsistent - manual intervention may be required");
                    }
                }
                else
                {
                    Logger.Log($"StateManager: No rollback attempted (stateChanged={stateChanged}, previousState={previousState})");
                }
            }
        }

        /// <summary>
        /// Attempts to find and invoke work-related methods using reflection
        /// </summary>
        /// <param name="mainWindow">The main window instance</param>
        /// <param name="workType">The type of work (Work, Study, Play)</param>
        /// <returns>True if a work method was found and invoked, false otherwise</returns>
        private static bool TryInvokeWorkMethod(IMainWindow mainWindow, string workType)
        {
            try
            {
                var mainType = mainWindow.Main.GetType();

                // Try to find methods that might start work
                var possibleMethods = new[]
                {
                    $"Start{workType}",
                    $"Display{workType}",
                    $"Set{workType}",
                    "StartWork",
                    "SetWork"
                };

                foreach (var methodName in possibleMethods)
                {
                    var method = mainType.GetMethod(methodName);
                    if (method != null)
                    {
                        Logger.Log($"StateManager: Found method '{methodName}', attempting to invoke");
                        try
                        {
                            method.Invoke(mainWindow.Main, null);
                            Logger.Log($"StateManager: Successfully invoked '{methodName}'");
                            return true;
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"StateManager: Failed to invoke '{methodName}': {ex.Message}");
                        }
                    }
                }

                // Try to set NowWork property
                var nowWorkProperty = mainType.GetProperty("NowWork");
                if (nowWorkProperty != null && nowWorkProperty.CanWrite)
                {
                    Logger.Log("StateManager: Found NowWork property, attempting to create Work object");
                    // This would require creating a Work object, which is complex
                    // For now, just log that we found it
                    Logger.Log("StateManager: NowWork property found but requires Work object creation");
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"StateManager: Error in TryInvokeWorkMethod: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Executes state transition for older VPet versions that don't have State property
        /// Falls back to direct display method calls
        /// </summary>
        /// <param name="mainWindow">The main window instance</param>
        /// <param name="newState">The target state to transition to</param>
        /// <param name="actionName">The name of the action triggering this state change</param>
        private static void ExecuteStateTransitionFallback(IMainWindow mainWindow, object newState, string actionName)
        {
            try
            {
                string targetStateName = newState?.ToString() ?? "Unknown";
                Logger.Log($"StateManager: Fallback mode - executing display method for state '{targetStateName}' (action: {actionName})");

                switch (targetStateName)
                {
                    case "Sleep":
                        Logger.Log("StateManager: Fallback - Calling DisplaySleep(force: true)");
                        mainWindow.Main.DisplaySleep(force: true);
                        Logger.Log("StateManager: Fallback - DisplaySleep completed");
                        break;

                    case "Nomal":
                    case "Normal":
                        Logger.Log("StateManager: Fallback - Calling DisplayToNomal()");
                        mainWindow.Main.DisplayToNomal();
                        Logger.Log("StateManager: Fallback - DisplayToNomal completed");
                        break;

                    case "Work":
                        Logger.Log("StateManager: Fallback - Attempting to start work mode");

                        // Try to use WorkManager to start a default work
                        WorkManager.RefreshWorkLists(mainWindow);
                        var defaultWork = WorkManager.FindWork("", "work"); // Get first work

                        if (defaultWork != null && WorkManager.StartWork(mainWindow, defaultWork))
                        {
                            Logger.Log("StateManager: Fallback - Work mode started via StartWork");
                            break;
                        }

                        // If StartWork not available, try other methods
                        if (TryInvokeWorkMethod(mainWindow, "Work"))
                        {
                            Logger.Log("StateManager: Fallback - Work mode started via method invocation");
                            break;
                        }

                        // If no method found, fall back to animation
                        Logger.Log("StateManager: Fallback - No work method found, using animation (loop mode)");
                        try
                        {
                            // Use A_Start to loop the animation, making it appear as a continuous work state
                            mainWindow.Main.Display("work", VPet_Simulator.Core.GraphInfo.AnimatType.A_Start, mainWindow.Main.DisplayToNomal);
                            Logger.Log("StateManager: Fallback - Work animation triggered in loop mode");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"StateManager: Fallback - Work animation failed: {ex.Message}");
                            // Fallback to single animation if loop fails
                            try
                            {
                                mainWindow.Main.Display("work", VPet_Simulator.Core.GraphInfo.AnimatType.Single, mainWindow.Main.DisplayToNomal);
                                Logger.Log("StateManager: Fallback - Work animation triggered in single mode (fallback)");
                            }
                            catch (Exception ex2)
                            {
                                Logger.Log($"StateManager: Fallback - Work animation single mode also failed: {ex2.Message}");
                            }
                        }
                        break;

                    case "Study":
                        Logger.Log("StateManager: Fallback - Attempting to start study mode");

                        // Try to use WorkManager to start a default study
                        WorkManager.RefreshWorkLists(mainWindow);
                        var defaultStudy = WorkManager.FindWork("", "study"); // Get first study

                        if (defaultStudy != null && WorkManager.StartWork(mainWindow, defaultStudy))
                        {
                            Logger.Log("StateManager: Fallback - Study mode started via StartWork");
                            break;
                        }

                        // If StartWork not available, try other methods
                        if (TryInvokeWorkMethod(mainWindow, "Study"))
                        {
                            Logger.Log("StateManager: Fallback - Study mode started via method invocation");
                            break;
                        }

                        // If no method found, fall back to animation
                        Logger.Log("StateManager: Fallback - No study method found, using animation (loop mode)");
                        try
                        {
                            // Use A_Start to loop the animation, making it appear as a continuous study state
                            mainWindow.Main.Display("study", VPet_Simulator.Core.GraphInfo.AnimatType.A_Start, mainWindow.Main.DisplayToNomal);
                            Logger.Log("StateManager: Fallback - Study animation triggered in loop mode");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"StateManager: Fallback - Study animation failed: {ex.Message}");
                            // Fallback to single animation if loop fails
                            try
                            {
                                mainWindow.Main.Display("study", VPet_Simulator.Core.GraphInfo.AnimatType.Single, mainWindow.Main.DisplayToNomal);
                                Logger.Log("StateManager: Fallback - Study animation triggered in single mode (fallback)");
                            }
                            catch (Exception ex2)
                            {
                                Logger.Log($"StateManager: Fallback - Study animation single mode also failed: {ex2.Message}");
                            }
                        }
                        break;

                    default:
                        Logger.Log($"StateManager: Fallback - Unknown state '{targetStateName}', calling DisplayToNomal()");
                        mainWindow.Main.DisplayToNomal();
                        break;
                }

                Logger.Log($"StateManager: Fallback mode completed for state '{targetStateName}'");
            }
            catch (Exception ex)
            {
                Logger.Log($"StateManager: Fallback mode ERROR");
                Logger.Log($"StateManager: Context - Action: {actionName}, Target State: {newState}");
                Logger.Log($"StateManager: Exception Type: {ex.GetType().Name}");
                Logger.Log($"StateManager: Exception Message: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the current state of the VPet
        /// </summary>
        /// <param name="mainWindow">The main window instance</param>
        /// <returns>The current state as an object, or null if unavailable</returns>
        public static object GetCurrentState(IMainWindow mainWindow)
        {
            if (mainWindow == null)
            {
                Logger.Log("StateManager: mainWindow is null, cannot get current state");
                return null;
            }

            try
            {
                var state = GetState(mainWindow);
                if (state == null)
                {
                    Logger.Log("StateManager: State field/property not found");
                }
                return state;
            }
            catch (Exception ex)
            {
                Logger.Log($"StateManager: Error getting current state: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets the number of pending state transitions in the queue
        /// Useful for testing and monitoring concurrent state change handling
        /// </summary>
        /// <returns>The number of pending state transitions</returns>
        public static int GetQueueSize()
        {
            return _stateTransitionQueue.Count;
        }

        /// <summary>
        /// Waits for all pending state transitions to complete
        /// Useful for testing to ensure all state changes have been processed
        /// </summary>
        /// <param name="timeoutMs">Maximum time to wait in milliseconds (default: 5000)</param>
        /// <returns>True if all transitions completed within timeout, false otherwise</returns>
        public static async Task<bool> WaitForQueueCompletionAsync(int timeoutMs = 5000)
        {
            var startTime = DateTime.Now;
            while (_stateTransitionQueue.Count > 0 || _isProcessing == 1)
            {
                if ((DateTime.Now - startTime).TotalMilliseconds > timeoutMs)
                {
                    Logger.Log($"StateManager: WaitForQueueCompletion timed out after {timeoutMs}ms with {_stateTransitionQueue.Count} items remaining");
                    return false;
                }
                await Task.Delay(50);
            }
            Logger.Log("StateManager: All queued state transitions completed");
            return true;
        }

        /// <summary>
        /// Verifies that the current state matches the expected state after a transition
        /// This ensures state query consistency as per requirement 1.4
        /// </summary>
        /// <param name="mainWindow">The main window instance</param>
        /// <param name="expectedState">The expected state value</param>
        /// <param name="actionName">The name of the action that triggered the transition (for logging)</param>
        /// <returns>True if the current state matches the expected state, false otherwise</returns>
        public static bool VerifyStateAfterTransition(IMainWindow mainWindow, object expectedState, string actionName)
        {
            if (mainWindow == null)
            {
                Logger.Log("StateManager: mainWindow is null, cannot verify state");
                return false;
            }

            if (expectedState == null)
            {
                Logger.Log("StateManager: expectedState is null, cannot verify state");
                return false;
            }

            try
            {
                var currentState = GetState(mainWindow);
                var workingStateType = GetStateType(mainWindow);

                if (currentState == null || workingStateType == null)
                {
                    Logger.Log("StateManager: State field/property not found, cannot verify state");
                    return false;
                }

                // Convert expectedState to the correct enum type if it's a string
                object expectedStateValue = expectedState;
                if (expectedState is string stateString)
                {
                    try
                    {
                        expectedStateValue = Enum.Parse(workingStateType, stateString, ignoreCase: true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"StateManager: Failed to parse expected state '{stateString}': {ex.Message}");
                        return false;
                    }
                }

                string currentStateName = currentState?.ToString() ?? "null";
                string expectedStateName = expectedStateValue?.ToString() ?? "null";

                bool statesMatch = currentStateName.Equals(expectedStateName, StringComparison.OrdinalIgnoreCase);

                if (statesMatch)
                {
                    Logger.Log($"StateManager: State verification PASSED - Current state '{currentStateName}' matches expected state '{expectedStateName}' after action '{actionName}'");
                }
                else
                {
                    Logger.Log($"StateManager: State verification FAILED - Current state '{currentStateName}' does NOT match expected state '{expectedStateName}' after action '{actionName}'");
                }

                return statesMatch;
            }
            catch (Exception ex)
            {
                Logger.Log($"StateManager: Error verifying state after transition");
                Logger.Log($"StateManager: Context - Action: {actionName}, Expected State: {expectedState}");
                Logger.Log($"StateManager: Exception Type: {ex.GetType().Name}");
                Logger.Log($"StateManager: Exception Message: {ex.Message}");
                return false;
            }
        }
    }
}
