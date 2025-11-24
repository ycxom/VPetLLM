using System;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// Represents the execution context for an action, capturing all relevant information
    /// for logging, debugging, and monitoring state transitions.
    /// </summary>
    public class ActionExecutionContext
    {
        /// <summary>
        /// The name of the action being executed (e.g., "sleep", "touch_head")
        /// </summary>
        public string ActionName { get; set; }

        /// <summary>
        /// The category of the action (Interactive, StateBased, or Unknown)
        /// </summary>
        public ActionCategory Category { get; set; }

        /// <summary>
        /// The VPet state before the action was executed (null if state was not accessible)
        /// </summary>
        public object PreviousState { get; set; }

        /// <summary>
        /// The target VPet state after the action (null for interactive/unknown actions)
        /// </summary>
        public object TargetState { get; set; }

        /// <summary>
        /// The actual VPet state after the action was executed (null if state was not accessible)
        /// </summary>
        public object ActualState { get; set; }

        /// <summary>
        /// The timestamp when the action execution started
        /// </summary>
        public DateTime ExecutionTime { get; set; }

        /// <summary>
        /// Indicates whether the action executed successfully
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Error message if the action failed (null if successful)
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Duration of the action execution in milliseconds
        /// </summary>
        public long DurationMs { get; set; }

        /// <summary>
        /// Creates a new ActionExecutionContext for an action that is about to be executed
        /// </summary>
        /// <param name="actionName">The name of the action</param>
        /// <param name="category">The category of the action</param>
        /// <param name="previousState">The current state before execution</param>
        /// <param name="targetState">The target state (null for non-state-based actions)</param>
        /// <returns>A new ActionExecutionContext instance</returns>
        public static ActionExecutionContext Create(
            string actionName,
            ActionCategory category,
            object previousState = null,
            object targetState = null)
        {
            return new ActionExecutionContext
            {
                ActionName = actionName,
                Category = category,
                PreviousState = previousState,
                TargetState = targetState,
                ExecutionTime = DateTime.Now,
                Success = false,
                ErrorMessage = null,
                ActualState = null,
                DurationMs = 0
            };
        }

        /// <summary>
        /// Marks the action execution as successful and records the final state
        /// </summary>
        /// <param name="actualState">The actual state after execution</param>
        /// <param name="durationMs">The duration of the execution in milliseconds</param>
        public void MarkSuccess(object actualState, long durationMs)
        {
            Success = true;
            ActualState = actualState;
            DurationMs = durationMs;
            ErrorMessage = null;
        }

        /// <summary>
        /// Marks the action execution as failed and records the error
        /// </summary>
        /// <param name="errorMessage">The error message</param>
        /// <param name="actualState">The actual state after the failed execution (may differ from target)</param>
        /// <param name="durationMs">The duration before failure in milliseconds</param>
        public void MarkFailure(string errorMessage, object actualState = null, long durationMs = 0)
        {
            Success = false;
            ErrorMessage = errorMessage;
            ActualState = actualState;
            DurationMs = durationMs;
        }

        /// <summary>
        /// Returns a formatted string representation of the execution context for logging
        /// </summary>
        /// <returns>A detailed string describing the action execution</returns>
        public override string ToString()
        {
            var result = $"ActionExecutionContext: " +
                        $"Action='{ActionName}', " +
                        $"Category={Category}, " +
                        $"Time={ExecutionTime:yyyy-MM-dd HH:mm:ss.fff}, " +
                        $"Duration={DurationMs}ms, " +
                        $"Success={Success}";

            // Add state information if available
            if (PreviousState != null || TargetState != null || ActualState != null)
            {
                result += $", PreviousState={PreviousState ?? "null"}, " +
                         $"TargetState={TargetState ?? "null"}, " +
                         $"ActualState={ActualState ?? "null"}";
            }

            // Add error information if failed
            if (!Success && !string.IsNullOrEmpty(ErrorMessage))
            {
                result += $", Error='{ErrorMessage}'";
            }

            return result;
        }

        /// <summary>
        /// Returns a compact string representation suitable for inline logging
        /// </summary>
        /// <returns>A compact string describing the action execution</returns>
        public string ToCompactString()
        {
            var status = Success ? "✓" : "✗";
            var stateInfo = Category == ActionCategory.StateBased
                ? $" [{PreviousState}→{ActualState ?? TargetState}]"
                : "";

            return $"{status} {ActionName}{stateInfo} ({DurationMs}ms)";
        }

        /// <summary>
        /// Checks if the state transition was successful (for state-based actions)
        /// </summary>
        /// <returns>True if the actual state matches the target state, false otherwise</returns>
        public bool IsStateTransitionSuccessful()
        {
            if (Category != ActionCategory.StateBased)
                return true; // Non-state-based actions don't have state transitions

            if (TargetState == null || ActualState == null)
                return false; // Can't verify without both states

            return TargetState.ToString() == ActualState.ToString();
        }
    }
}
