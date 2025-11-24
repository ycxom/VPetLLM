using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core;
using VPetLLM.Utils;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// Handles parsing and execution of record commands
    /// Supports new format:
    /// - <|record_begin|> text("content"), weight(5) <|record_end|>
    /// - <|record_modify_begin|> id(1), weight_delta(2) <|record_modify_end|>
    /// </summary>
    public class RecordCommandHandler : IActionHandler
    {
        private readonly RecordManager _recordManager;

        public string Keyword => "record";
        public ActionType ActionType => ActionType.Tool;
        public ActionCategory Category => ActionCategory.Unknown;
        public string Description => "Record important information with weight-based lifecycle management";

        public RecordCommandHandler(RecordManager recordManager)
        {
            _recordManager = recordManager;
        }

        /// <summary>
        /// Execute record command
        /// </summary>
        public async Task Execute(string commandValue, IMainWindow mainWindow)
        {
            try
            {
                Logger.Log($"RecordCommandHandler: Processing command: {commandValue}");

                // Determine if this is a create or modify command
                if (commandValue.Contains("text(") && commandValue.Contains("weight("))
                {
                    // Create command
                    var (content, weight) = ParseCreateCommand(commandValue);
                    
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        var recordId = _recordManager.CreateRecord(content, weight);
                        
                        if (recordId > 0)
                        {
                            Logger.Log($"RecordCommandHandler: Successfully created record #{recordId}");
                        }
                        else
                        {
                            Logger.Log("RecordCommandHandler: Failed to create record");
                        }
                    }
                    else
                    {
                        Logger.Log("RecordCommandHandler: Invalid create command - empty content");
                    }
                }
                else if (commandValue.Contains("id(") && commandValue.Contains("weight_delta("))
                {
                    // Modify command
                    var (id, delta) = ParseModifyCommand(commandValue);
                    
                    if (id > 0)
                    {
                        var success = _recordManager.ModifyRecordWeight(id, delta);
                        
                        if (success)
                        {
                            Logger.Log($"RecordCommandHandler: Successfully modified record #{id} weight by {delta}");
                        }
                        else
                        {
                            Logger.Log($"RecordCommandHandler: Failed to modify record #{id}");
                        }
                    }
                    else
                    {
                        Logger.Log("RecordCommandHandler: Invalid modify command - invalid ID");
                    }
                }
                else
                {
                    Logger.Log($"RecordCommandHandler: Unknown command format: {commandValue}");
                }

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Logger.Log($"RecordCommandHandler: Error executing command: {ex.Message}");
            }
        }

        public Task Execute(int value, IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }

        public Task Execute(IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }

        public int GetAnimationDuration(string animationName)
        {
            return 0; // Records don't have animations
        }

        /// <summary>
        /// Parse record creation command
        /// Format: text("content"), weight(5)
        /// </summary>
        private (string content, int weight) ParseCreateCommand(string commandValue)
        {
            try
            {
                // Extract text content
                var textMatch = Regex.Match(commandValue, @"text\s*\(\s*""([^""]*)""\s*\)");
                var content = textMatch.Success ? textMatch.Groups[1].Value : string.Empty;

                // Extract weight
                var weightMatch = Regex.Match(commandValue, @"weight\s*\(\s*(\d+)\s*\)");
                var weight = weightMatch.Success ? int.Parse(weightMatch.Groups[1].Value) : 5;

                // Clamp weight to valid range
                weight = Math.Clamp(weight, 0, 10);

                Logger.Log($"RecordCommandHandler: Parsed create command - Content: '{content}', Weight: {weight}");

                return (content, weight);
            }
            catch (Exception ex)
            {
                Logger.Log($"RecordCommandHandler: Error parsing create command: {ex.Message}");
                return (string.Empty, 5);
            }
        }

        /// <summary>
        /// Parse record modification command
        /// Format: id(1), weight_delta(2) or id(1), weight_delta(-1)
        /// </summary>
        private (int id, int delta) ParseModifyCommand(string commandValue)
        {
            try
            {
                // Extract ID
                var idMatch = Regex.Match(commandValue, @"id\s*\(\s*(\d+)\s*\)");
                var id = idMatch.Success ? int.Parse(idMatch.Groups[1].Value) : 0;

                // Extract weight delta (can be negative)
                var deltaMatch = Regex.Match(commandValue, @"weight_delta\s*\(\s*(-?\d+)\s*\)");
                var delta = deltaMatch.Success ? int.Parse(deltaMatch.Groups[1].Value) : 0;

                Logger.Log($"RecordCommandHandler: Parsed modify command - ID: {id}, Delta: {delta}");

                return (id, delta);
            }
            catch (Exception ex)
            {
                Logger.Log($"RecordCommandHandler: Error parsing modify command: {ex.Message}");
                return (0, 0);
            }
        }
    }

    /// <summary>
    /// Handler for record_modify command (alias for record with modify parameters)
    /// </summary>
    public class RecordModifyCommandHandler : IActionHandler
    {
        private readonly RecordCommandHandler _recordHandler;

        public string Keyword => "record_modify";
        public ActionType ActionType => ActionType.Tool;
        public ActionCategory Category => ActionCategory.Unknown;
        public string Description => "Modify weight of existing important records";

        public RecordModifyCommandHandler(RecordManager recordManager)
        {
            _recordHandler = new RecordCommandHandler(recordManager);
        }

        public Task Execute(string commandValue, IMainWindow mainWindow)
        {
            return _recordHandler.Execute(commandValue, mainWindow);
        }

        public Task Execute(int value, IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }

        public Task Execute(IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }

        public int GetAnimationDuration(string animationName)
        {
            return 0;
        }
    }
}
