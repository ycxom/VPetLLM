using System.Text.RegularExpressions;

namespace VPetLLM.UI.Windows
{
    /// <summary>
    /// 判定一条 user 角色的历史消息到底是谁产生的。
    ///
    /// 背景：user 角色里混着两类内容 —— 用户真正打的字，以及插件回执、看屏幕结果、
    /// 触摸事件这类由程序灌进去的消息。两者在库里长得一样（<c>role = 'user'</c>），
    /// 唯一的区别是正文开头的来源标记。
    ///
    /// 为什么不用 <c>Message.MessageType</c>：那个字段只活在内存里 ——
    /// <c>chat_history</c> 表没有对应列（见 ChatHistoryDatabase 的建表语句），
    /// 编辑器从库里读出来的消息该字段恒为 null。所以只能从正文认。
    /// </summary>
    internal static class ContextSourceClassifier
    {
        /// <summary>插件回执：<c>[Plugin Result: 插件名] 内容</c>（PluginHandler / PluginTakeoverManager）</summary>
        private static readonly Regex PluginResultRegex = new(
            @"\[Plugin\s+Result:\s*([^\]]+)\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>看屏幕：<c>[屏幕内容]</c> 或 <c>[屏幕内容 - 文字识别]</c>（SeeScreenHandler）</summary>
        private static readonly Regex SeeScreenRegex = new(
            @"\[屏幕内容(?:\s*-\s*(?<variant>[^\]]+))?\]",
            RegexOptions.Compiled);

        /// <summary>系统事件：<c>[System] 内容</c>（TouchInteractionHandler 等）</summary>
        private static readonly Regex SystemRegex = new(
            @"^\s*\[System\]",
            RegexOptions.Compiled);

        /// <summary>
        /// 兼容形态：万一某条存的是 <c>DisplayContent</c> 拼出来的 JSON
        /// （<c>{"NowTime": ..., "Plugin": "[名字] 内容"}</c> / <c>{"System": ...}</c>）。
        /// 正常路径存的是原文，但读库时多认一种形态的代价只是一次正则。
        /// </summary>
        private static readonly Regex JsonPluginRegex = new(
            @"""Plugin""\s*:\s*""(?:\[(?<name>[^\]]+)\])?",
            RegexOptions.Compiled);

        private static readonly Regex JsonSystemRegex = new(
            @"""System""\s*:\s*""",
            RegexOptions.Compiled);

        /// <summary>摘要长度上限（字符）。一行放得下，又足够看出这条是什么。</summary>
        private const int PreviewLength = 80;

        /// <summary>
        /// 判定来源。
        /// </summary>
        /// <returns>
        /// <c>IsSystemGenerated</c>：是否为程序灌入；
        /// <c>SourceName</c>：气泡上显示的来源名（插件名或功能名），用户消息时为空串。
        /// </returns>
        public static (bool IsSystemGenerated, string SourceName) Classify(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return (false, "");
            }

            // 一条消息可能聚合了多个来源：ResultAggregator 会把一轮内的多个插件回执
            // 拼在一起，ChatDispatcher 还会把跨来源的灌入并成一条。按出现顺序收集去重，
            // 多于一个就标成「首个 等 N 项」，而不是随便挑一个当作全部。
            var names = new List<string>();

            void Add(string name)
            {
                var trimmed = name.Trim();
                if (trimmed.Length > 0 && !names.Contains(trimmed))
                {
                    names.Add(trimmed);
                }
            }

            foreach (Match m in PluginResultRegex.Matches(content))
            {
                Add(m.Groups[1].Value);
            }

            foreach (Match m in SeeScreenRegex.Matches(content))
            {
                // [屏幕内容 - 文字识别] 走的是 OCR，和视觉描述区分开更有信息量
                var variant = m.Groups["variant"].Value.Trim();
                Add(variant.Length > 0 ? $"看屏幕 · {variant}" : "看屏幕");
            }

            foreach (Match m in JsonPluginRegex.Matches(content))
            {
                var name = m.Groups["name"].Value;
                Add(name.Length > 0 ? name : "插件");
            }

            if (names.Count == 0 && (SystemRegex.IsMatch(content) || JsonSystemRegex.IsMatch(content)))
            {
                Add("系统事件");
            }

            if (names.Count == 0)
            {
                return (false, "");
            }

            return (true, names.Count == 1 ? names[0] : $"{names[0]} 等 {names.Count} 项");
        }

        /// <summary>
        /// 收起时那一行摘要：剥掉来源标记、压平空白、截断。
        ///
        /// 摘要只用于显示，绝不回写 —— 保存走的始终是 <c>Content</c> 原文。
        /// </summary>
        public static string BuildPreview(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return "";
            }

            var text = content;

            // 标记本身已经由来源名表达了，摘要里重复一遍纯属浪费那 80 个字符
            text = PluginResultRegex.Replace(text, " ");
            text = SeeScreenRegex.Replace(text, " ");
            text = Regex.Replace(text, @"\[/屏幕内容\]", " ");
            text = SystemRegex.Replace(text, " ");

            // 换行、制表压成单空格：收起态只有一行，留着换行会把它撑高
            text = Regex.Replace(text, @"\s+", " ").Trim();

            if (text.Length <= PreviewLength)
            {
                return text;
            }

            return text.Substring(0, PreviewLength).TrimEnd() + "…";
        }
    }
}
