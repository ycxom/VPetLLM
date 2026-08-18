using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VPetLLM.Utils.Common
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
        /// 判断一条命令是不是"要说的话"。say 和 talk 是同一件事的两种写法。
        /// </summary>
        public static bool IsSayCommand(string commandType)
        {
            return string.Equals(commandType, "say", StringComparison.OrdinalIgnoreCase)
                || string.Equals(commandType, "talk", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 从 say/talk 命令的参数里取出要说的文本。
        ///
        /// 这是全项目唯一一份"say 文本怎么取"的实现，显示气泡和 TTS 预加载都必须走它。
        /// 两边各写一套的代价是真出过问题：预加载那份曾经要求参数必须是 \w+、文本必须带引号，
        /// 于是带引号的文本、三个及以上参数的命令都提取不到，那条消息就不开独占会话，
        /// 气泡照常显示而语音退回事后捕获 —— 表现为"同样的设置偏偏某几句不同步"。
        /// </summary>
        /// <param name="parameters">命令标记之间的原始参数文本</param>
        /// <returns>要说的文本；取不到返回空字符串</returns>
        public static string ExtractSayText(string parameters)
        {
            if (string.IsNullOrEmpty(parameters))
                return string.Empty;

            // 解析 say("text", animation) 格式
            var match = Regex.Match(parameters, @"say\s*\(\s*""([^""]*)""\s*(?:,\s*([^)]*))?\s*\)");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            // 解析简单的 "text" 格式
            match = Regex.Match(parameters, @"""([^""]*)""");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            return string.Empty;
        }

        /// <summary>
        /// 取出一条消息里所有要说的话，按出现顺序。用于批量预加载和"要不要开独占会话"的判断。
        /// 与显示气泡走同一套解析，见 <see cref="ExtractSayText"/>。
        /// </summary>
        public static List<string> ExtractAllSayTexts(string text)
        {
            var texts = new List<string>();

            foreach (var command in Parse(text))
            {
                if (!IsSayCommand(command.CommandType))
                    continue;

                var sayText = ExtractSayText(command.Parameters);
                if (!string.IsNullOrWhiteSpace(sayText))
                    texts.Add(sayText);
            }

            return texts;
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
                Debug.WriteLine($"[CommandFormatParser] {message}");
            }
        }
    }
}