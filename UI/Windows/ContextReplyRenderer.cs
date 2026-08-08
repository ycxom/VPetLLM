using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using VPetLLM.Utils.Common;

namespace VPetLLM.UI.Windows
{
    /// <summary>
    /// 简洁模式下一条消息被拆成的片段类型。
    /// </summary>
    public enum ContextSegmentKind
    {
        /// <summary>命令之外的裸文本（正常情况下应当为空，AI 若"话说到框外"就会出现）</summary>
        Text,

        /// <summary>say / talk 命令里真正被说出口的那句话——简洁模式下唯一可编辑的东西</summary>
        Speech,

        /// <summary>其余命令（动作、状态、插件调用……），以芯片形式只读展示</summary>
        Command,

        /// <summary>用户侧的 [Plugin Result: X] 工具回执</summary>
        PluginResult
    }

    /// <summary>
    /// 一条消息在简洁模式下的一个可视片段。
    ///
    /// 关键约束：<see cref="Prefix"/> + <see cref="Text"/> + <see cref="Suffix"/> 必须能原样拼回
    /// 源文本对应的那一段。只要用户没动 Text，重组结果就与原文逐字节相同——
    /// 简洁模式因此不会在用户"只是看了一眼"的情况下悄悄改写历史。
    /// </summary>
    public class ContextSegment : INotifyPropertyChanged
    {
        public ContextSegmentKind Kind { get; init; }

        /// <summary>重组时拼在 <see cref="Text"/> 前面的原文（含起始标签、左引号等）</summary>
        public string Prefix { get; init; } = "";

        /// <summary>重组时拼在 <see cref="Text"/> 后面的原文（含右引号、动画参数、结束标签等）</summary>
        public string Suffix { get; init; } = "";

        private string _text = "";
        /// <summary>片段的可见文本。Speech / Text / PluginResult 可编辑，Command 不可编辑。</summary>
        public string Text
        {
            get => _text;
            set
            {
                if (_text != value)
                {
                    _text = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>原始命令类型，如 say / move / plugin_Sticker；非命令片段为空</summary>
        public string CommandType { get; init; } = "";

        /// <summary>命令的原始参数，用于芯片上的副标题</summary>
        public string Parameters { get; init; } = "";

        /// <summary>芯片主标题（已本地化/美化过的命令名）</summary>
        public string Label { get; init; } = "";

        public bool IsSpeech => Kind == ContextSegmentKind.Speech;
        public bool IsCommand => Kind == ContextSegmentKind.Command;
        public bool IsPluginResult => Kind == ContextSegmentKind.PluginResult;
        public bool IsText => Kind == ContextSegmentKind.Text;

        /// <summary>可编辑片段（Speech / Text / PluginResult）共用一套输入框</summary>
        public bool IsEditable => Kind != ContextSegmentKind.Command;

        /// <summary>插件调用芯片用另一种配色，与普通动作区分开</summary>
        public bool IsPluginCommand => IsCommand && CommandType.StartsWith("plugin_", StringComparison.OrdinalIgnoreCase);

        /// <summary>芯片是否需要显示参数行（无参命令就不占那一行）</summary>
        public bool HasParameters => !string.IsNullOrWhiteSpace(Parameters);

        /// <summary>把本片段还原为源文本</summary>
        public string Compose() => Prefix + Text + Suffix;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 把 AI 回复的命令格式（<c>&lt;|say_begin|&gt; "..." &lt;|say_end|&gt;</c>）翻译成可读片段，
    /// 以及把编辑后的片段拼回命令格式。
    ///
    /// 命令的识别复用 <see cref="CommandFormatParser"/>——那是全局唯一的命令语法来源，
    /// 这里再写一份正则就等于开了第二套语法，日后加命令必然漏改一边。
    /// </summary>
    public static class ContextReplyRenderer
    {
        /// <summary>拆出起始标签 / 主体 / 结束标签，用于定位可编辑区</summary>
        private static readonly Regex EnvelopeRegex = new(
            @"^(?<open><\|\s*\w+\s*_begin\s*\|>\s*)(?<body>.*?)(?<close>\s*<\|\s*\w+\s*_end\s*\|>)$",
            RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>主体若以引号包裹，取第一段引号内的内容为"说出口的话"</summary>
        private static readonly Regex QuotedBodyRegex = new(
            @"^""(?<t>(?:[^""\\]|\\.)*)""(?<rest>.*)$",
            RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>用户侧工具回执，与 Message.DisplayContent 中的判定保持一致</summary>
        private static readonly Regex PluginResultRegex = new(
            @"^\[Plugin Result:\s*(?<name>[^\]]+)\]\s*(?<body>.*)$",
            RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>会把参数当成台词渲染的命令</summary>
        private static bool IsSpeechCommand(string commandType)
            => commandType.Equals("say", StringComparison.OrdinalIgnoreCase)
            || commandType.Equals("talk", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 把一条消息的原始内容拆成简洁模式的片段序列。
        /// 返回的片段按顺序 Compose 拼接后等于入参 content。
        /// </summary>
        public static List<ContextSegment> Parse(string? content)
        {
            var segments = new List<ContextSegment>();
            var text = content ?? "";

            if (text.Length == 0)
            {
                return segments;
            }

            // 用户侧的工具回执不是命令格式，单独识别后就不必再走命令解析
            var pluginResult = PluginResultRegex.Match(text);
            if (pluginResult.Success)
            {
                var name = pluginResult.Groups["name"].Value.Trim();
                var body = pluginResult.Groups["body"].Value;
                segments.Add(new ContextSegment
                {
                    Kind = ContextSegmentKind.PluginResult,
                    Prefix = text.Substring(0, pluginResult.Groups["body"].Index),
                    Text = body,
                    Suffix = "",
                    Label = name,
                    CommandType = name
                });
                return segments;
            }

            var commands = CommandFormatParser.ParseNewFormat(text);
            var cursor = 0;

            foreach (var command in commands)
            {
                // 命令之间的裸文本原样保留为可编辑片段
                if (command.StartIndex > cursor)
                {
                    var gap = text.Substring(cursor, command.StartIndex - cursor);
                    if (gap.Length > 0)
                    {
                        segments.Add(new ContextSegment { Kind = ContextSegmentKind.Text, Text = gap });
                    }
                }

                segments.Add(BuildCommandSegment(command));
                cursor = command.EndIndex + 1;
            }

            if (cursor < text.Length)
            {
                segments.Add(new ContextSegment { Kind = ContextSegmentKind.Text, Text = text.Substring(cursor) });
            }

            return segments;
        }

        private static ContextSegment BuildCommandSegment(CommandMatch command)
        {
            if (IsSpeechCommand(command.CommandType))
            {
                var envelope = EnvelopeRegex.Match(command.FullMatch);
                if (envelope.Success)
                {
                    var open = envelope.Groups["open"].Value;
                    var body = envelope.Groups["body"].Value;
                    var close = envelope.Groups["close"].Value;

                    // 带引号：只把引号内的台词交给用户编辑，动画参数等原样留在 Suffix 里
                    var quoted = QuotedBodyRegex.Match(body);
                    if (quoted.Success)
                    {
                        return new ContextSegment
                        {
                            Kind = ContextSegmentKind.Speech,
                            Prefix = open + "\"",
                            Text = quoted.Groups["t"].Value,
                            Suffix = "\"" + quoted.Groups["rest"].Value + close,
                            CommandType = command.CommandType,
                            Label = command.CommandType
                        };
                    }

                    // 不带引号：整个主体都是台词
                    return new ContextSegment
                    {
                        Kind = ContextSegmentKind.Speech,
                        Prefix = open,
                        Text = body,
                        Suffix = close,
                        CommandType = command.CommandType,
                        Label = command.CommandType
                    };
                }
            }

            // 其余命令整体只读，用户要改就去完全模式
            return new ContextSegment
            {
                Kind = ContextSegmentKind.Command,
                Prefix = command.FullMatch,
                Text = "",
                Suffix = "",
                CommandType = command.CommandType,
                Parameters = command.Parameters,
                Label = FormatCommandLabel(command.CommandType)
            };
        }

        /// <summary>
        /// 芯片上显示的命令名。插件调用（plugin_XXX）拆成"插件 · XXX"，
        /// 否则一眼看过去全是 plugin_ 前缀，分不清是哪个插件。
        /// </summary>
        public static string FormatCommandLabel(string commandType)
        {
            if (string.IsNullOrEmpty(commandType))
            {
                return "";
            }

            const string pluginPrefix = "plugin_";
            if (commandType.StartsWith(pluginPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var pluginName = commandType.Substring(pluginPrefix.Length);
                return pluginName.Length > 0 ? $"插件 · {pluginName}" : "插件";
            }

            return commandType;
        }

        /// <summary>
        /// 把片段序列拼回消息原始内容。用户未编辑时逐字节等于原文。
        /// </summary>
        public static string Compose(IEnumerable<ContextSegment> segments)
        {
            var builder = new StringBuilder();
            foreach (var segment in segments)
            {
                builder.Append(segment.Compose());
            }
            return builder.ToString();
        }
    }
}
