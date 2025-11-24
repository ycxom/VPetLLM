using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// Defines the type of action handler
    /// </summary>
    public enum ActionType
    {
        /// <summary>
        /// State-related actions
        /// </summary>
        State,
        
        /// <summary>
        /// Body/physical actions
        /// </summary>
        Body,
        
        /// <summary>
        /// Talk/speech actions
        /// </summary>
        Talk,
        
        /// <summary>
        /// Plugin-provided actions
        /// </summary>
        Plugin,
        
        /// <summary>
        /// Tool/utility actions
        /// </summary>
        Tool
    }

    /// <summary>
    /// Categorizes actions based on their effect on VPet's state
    /// This distinction is critical for proper state management
    /// </summary>
    public enum ActionCategory
    {
        /// <summary>
        /// Interactive actions that only play animations without changing state
        /// Examples: touch_head, touch_body, pinch
        /// These actions preserve the current WorkingState
        /// </summary>
        Interactive,
        
        /// <summary>
        /// State-based actions that change both animation and WorkingState
        /// Examples: sleep, work, study
        /// These actions trigger state transitions through StateManager
        /// </summary>
        StateBased,
        
        /// <summary>
        /// Unknown or fallback actions that default to animation-only behavior
        /// These actions are treated like Interactive actions for safety
        /// </summary>
        Unknown
    }

    /// <summary>
    /// Interface for action handlers that execute VPet actions
    /// Action handlers can be state-based (changing WorkingState) or interactive (animation only)
    /// </summary>
    public interface IActionHandler
    {
        /// <summary>
        /// Gets the keyword that triggers this action handler
        /// </summary>
        string Keyword { get; }
        
        /// <summary>
        /// Gets the type of action this handler processes
        /// </summary>
        ActionType ActionType { get; }
        
        /// <summary>
        /// Gets the category of this action (Interactive, StateBased, or Unknown)
        /// This determines whether the action changes VPet's WorkingState
        /// </summary>
        ActionCategory Category { get; }
        
        /// <summary>
        /// Gets the description of this action handler for LLM prompts
        /// </summary>
        string Description { get; }
        
        /// <summary>
        /// Executes the action with an integer parameter
        /// </summary>
        /// <param name="value">The integer parameter for the action</param>
        /// <param name="mainWindow">The main window instance</param>
        /// <returns>A task representing the asynchronous operation</returns>
        Task Execute(int value, IMainWindow mainWindow);
        
        /// <summary>
        /// Executes the action with a string parameter (action name)
        /// </summary>
        /// <param name="value">The action name or string parameter</param>
        /// <param name="mainWindow">The main window instance</param>
        /// <returns>A task representing the asynchronous operation</returns>
        Task Execute(string value, IMainWindow mainWindow);
        
        /// <summary>
        /// Executes the action without parameters
        /// </summary>
        /// <param name="mainWindow">The main window instance</param>
        /// <returns>A task representing the asynchronous operation</returns>
        Task Execute(IMainWindow mainWindow);
        
        /// <summary>
        /// Gets the duration of an animation in milliseconds
        /// </summary>
        /// <param name="animationName">The name of the animation</param>
        /// <returns>The duration in milliseconds</returns>
        int GetAnimationDuration(string animationName);
    }
}