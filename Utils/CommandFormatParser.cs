using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace VPetLLM.Utils
{
    /// <summary>
    /// Format types for commands
    /// </summary>
    public enum CommandFormat
    {
        /// <summary>
        /// New format: &lt;|xxx_begin|&gt; ... &lt;|xxx_end|&gt;
        /// </summary>
        New,
        
        /// <summary>
        /// Legacy format: [:xxx] (deprecated, will be rejected)
        /// </summary>
        Legacy
    }

    /// <summary>
    /// Represents a parsed command with metadata
    /// </summary>
    public class CommandMatch
    {
        /// <summary>
        /// The command type (e.g., "say", "move", "plugin")
        /// </summary>
        public string CommandType { get; set; }
        
        /// <summary>
        /// The parameters/content between tags
        /// </summary>
        public string Parameters { get; set; }
        
        /// <summary>
        /// The complete matched text including delimiters
        /// </summary>
        public string FullMatch { get; set; }
        
        /// <summary>
        /// Start position in the original text
        /// </summary>
        public int StartIndex { get; set; }
        
        /// <summary>
        /// End position in the original text
        /// </summary>
        public int EndIndex { get; set; }
        
        /// <summary>
        /// Which format was used (new or legacy)
        /// </summary>
        public CommandFormat Format { get; set; }
    }

    /// <summary>
    /// Utility class for parsing command formats
    /// </summary>
    public static class CommandFormatParser
    {
        /// <summary>
        /// Detects which format(s) are present in the text
        /// </summary>
        /// <param name="text">Text to analyze</param>
        /// <returns>The detected format type</returns>
        public static CommandFormat DetectFormat(string text)
        {
            if (string.IsNullOrEmpty(text))
                return CommandFormat.New; // Default to new format
            
            bool hasLegacyFormat = text.Contains("[:");
            
            if (hasLegacyFormat)
                return CommandFormat.Legacy;
            else
                return CommandFormat.New;
        }
        
        /// <summary>
        /// Checks if the text contains legacy format commands
        /// </summary>
        /// <param name="text">Text to check</param>
        /// <returns>True if legacy format is detected</returns>
        public static bool ContainsLegacyFormat(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            
            return text.Contains("[:");
        }

        /// <summary>
        /// Parses commands in the new format: &lt;|command_type_begin|&gt; parameters &lt;|command_type_end|&gt;
        /// </summary>
        /// <param name="text">Text to parse</param>
        /// <returns>List of parsed commands</returns>
        public static List<CommandMatch> ParseNewFormat(string text)
        {
            var commands = new List<CommandMatch>();
            
            if (string.IsNullOrEmpty(text))
                return commands;
            
            // Regex pattern for new format: <|command_type_begin|> ... <|command_type_end|>
            // This pattern handles whitespace variations and extracts command type and parameters
            var regex = new Regex(@"<\|\s*(\w+)\s*_begin\s*\|>(.*?)<\|\s*\1\s*_end\s*\|>", 
                RegexOptions.Compiled | RegexOptions.Singleline);
            
            var matches = regex.Matches(text);
            
            foreach (Match match in matches)
            {
                string commandType = match.Groups[1].Value.Trim();
                string parameters = match.Groups[2].Value.Trim();
                
                commands.Add(new CommandMatch
                {
                    CommandType = commandType,
                    Parameters = parameters,
                    FullMatch = match.Value,
                    StartIndex = match.Index,
                    EndIndex = match.Index + match.Length - 1,
                    Format = CommandFormat.New
                });
            }
            
            return commands;
        }



        /// <summary>
        /// Unified parsing method that only parses new format and logs warnings for legacy
        /// </summary>
        /// <param name="text">Text to parse</param>
        /// <returns>List of all parsed commands in new format</returns>
        public static List<CommandMatch> Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
                return new List<CommandMatch>();
            
            // Check for legacy format and log deprecation warning
            if (ContainsLegacyFormat(text))
            {
                LogLegacyFormatWarning(text);
            }
            
            // Only parse new format
            return ParseNewFormat(text);
        }
        
        /// <summary>
        /// Logs a deprecation warning when legacy format is detected
        /// </summary>
        /// <param name="text">Text containing legacy format</param>
        private static void LogLegacyFormatWarning(string text)
        {
            // Extract legacy format examples for the warning
            var legacyPattern = new Regex(@"\[:[^\]]+\]", RegexOptions.Compiled);
            var matches = legacyPattern.Matches(text);
            
            if (matches.Count > 0)
            {
                var examples = new List<string>();
                for (int i = 0; i < Math.Min(3, matches.Count); i++)
                {
                    examples.Add(matches[i].Value);
                }
                
                string exampleText = string.Join(", ", examples);
                string message = $"DEPRECATED: Legacy command format detected: {exampleText}. " +
                                $"Please use the new format: <|command_type_begin|> parameters <|command_type_end|>. " +
                                $"Legacy format is no longer supported and will be rejected.";
                
                // Log to console and debug output
                Console.WriteLine($"[CommandFormatParser] {message}");
                System.Diagnostics.Debug.WriteLine($"[CommandFormatParser] {message}");
            }
        }
    }
}