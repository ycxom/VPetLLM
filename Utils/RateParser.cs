using System;

namespace VPetLLM.Utils
{
    /// <summary>
    /// Represents a parsed work action command with rate information
    /// </summary>
    public class WorkActionCommand
    {
        /// <summary>
        /// The type of work action: "work", "study", or "play"
        /// </summary>
        public string WorkType { get; set; }

        /// <summary>
        /// The name of the work/study/play activity
        /// </summary>
        public string WorkName { get; set; }

        /// <summary>
        /// The requested rate (default is 1)
        /// </summary>
        public int Rate { get; set; } = 1;

        /// <summary>
        /// Whether a rate was explicitly specified in the command
        /// </summary>
        public bool HasExplicitRate { get; set; }
    }

    /// <summary>
    /// Utility class for parsing work commands with rate parameters
    /// Supports formats: work:name, work:name:rate, study:name:rate, play:name:rate
    /// </summary>
    public static class RateParser
    {
        /// <summary>
        /// Parses a work command string to extract work name and rate
        /// </summary>
        /// <param name="command">Command string in format "name" or "name:rate"</param>
        /// <returns>Tuple of (workName, rate) where rate defaults to 1 if not specified</returns>
        public static (string workName, int rate) ParseWorkCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return (string.Empty, 1);
            }

            // Find the last colon to separate name from rate
            // This handles cases where work name might contain colons (though unlikely)
            int lastColonIndex = command.LastIndexOf(':');

            if (lastColonIndex == -1)
            {
                // No colon found, entire string is the work name
                return (command.Trim(), 1);
            }

            string potentialRate = command.Substring(lastColonIndex + 1).Trim();
            string potentialName = command.Substring(0, lastColonIndex).Trim();

            // Try to parse the part after the last colon as a rate
            int rate = ParseRate(potentialRate, -1);

            if (rate == -1)
            {
                // Failed to parse as rate, treat entire string as work name
                return (command.Trim(), 1);
            }

            // Successfully parsed rate
            return (potentialName, rate);
        }

        /// <summary>
        /// Parses a full action command string (e.g., "work:name:rate") into a WorkActionCommand
        /// </summary>
        /// <param name="actionType">The action type: "work", "study", or "play"</param>
        /// <param name="parameters">The parameters after the action type (e.g., "name:rate")</param>
        /// <returns>A WorkActionCommand with parsed values</returns>
        public static WorkActionCommand ParseFullCommand(string actionType, string parameters)
        {
            var result = new WorkActionCommand
            {
                WorkType = actionType?.ToLower() ?? "work"
            };

            if (string.IsNullOrWhiteSpace(parameters))
            {
                result.WorkName = string.Empty;
                result.Rate = 1;
                result.HasExplicitRate = false;
                return result;
            }

            var (workName, rate) = ParseWorkCommand(parameters);
            result.WorkName = workName;
            result.Rate = rate;
            
            // Check if rate was explicitly specified (parameters contains a valid rate after last colon)
            int lastColonIndex = parameters.LastIndexOf(':');
            if (lastColonIndex != -1)
            {
                string potentialRate = parameters.Substring(lastColonIndex + 1).Trim();
                result.HasExplicitRate = ParseRate(potentialRate, -1) != -1;
            }
            else
            {
                result.HasExplicitRate = false;
            }

            return result;
        }

        /// <summary>
        /// Parses a rate string to an integer value
        /// Handles both integer and decimal values (decimal values are truncated/floored)
        /// </summary>
        /// <param name="rateString">The rate string to parse</param>
        /// <param name="defaultRate">Default value to return if parsing fails (default is 1)</param>
        /// <returns>The parsed rate as an integer, or defaultRate if parsing fails</returns>
        public static int ParseRate(string rateString, int defaultRate = 1)
        {
            if (string.IsNullOrWhiteSpace(rateString))
            {
                return defaultRate;
            }

            string trimmed = rateString.Trim();

            // Try parsing as integer first
            if (int.TryParse(trimmed, out int intRate))
            {
                return intRate;
            }

            // Try parsing as decimal/double and floor it
            if (double.TryParse(trimmed, out double decimalRate))
            {
                return (int)Math.Floor(decimalRate);
            }

            // Parsing failed, return default
            return defaultRate;
        }

        /// <summary>
        /// Formats a work command from components (for round-trip testing)
        /// </summary>
        /// <param name="workType">The work type: "work", "study", or "play"</param>
        /// <param name="workName">The work name</param>
        /// <param name="rate">Optional rate (if null or <= 0, rate is omitted)</param>
        /// <returns>Formatted command string</returns>
        public static string FormatCommand(string workType, string workName, int? rate = null)
        {
            if (rate.HasValue && rate.Value >= 1)
            {
                return $"{workType}:{workName}:{rate.Value}";
            }
            return $"{workType}:{workName}";
        }
    }
}
