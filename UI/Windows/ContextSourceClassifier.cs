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

        /// <summary>
        /// 手动截图：<c>[图片内容]</c>（+ 可选的 <c>[用户问题]</c>），由 MessageCombiner 拼出。
        ///
        /// 这条以前没人认，于是前置多模态/OCR 生成的几百字图片描述会顶着用户名显示，
        /// 看起来像用户自己敲的；也因为不算「程序灌入」而无法收纳，把编辑器撑得老长。
        /// 它和 AI 主动看屏幕的 <c>[屏幕内容]</c> 是两条入口、两套标记，但在编辑器眼里
        /// 都是「截图产生的内容」，该一视同仁。
        /// </summary>
        private static readonly Regex ImageContentRegex = new(
            @"\[图片内容\]",
            RegexOptions.Compiled);

        /// <summary>手动截图里用户自己补充的那句提问。</summary>
        private static readonly Regex UserQuestionRegex = new(
            @"\[用户问题\]\s*(?<question>[\s\S]*)$",
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

            if (ImageContentRegex.IsMatch(content))
            {
                Add("截图");
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

            // 手动截图那条消息是「机器写的图片描述 + 用户自己补的提问」拼起来的。
            // 收起时只剩一行，那一行应该给用户真正问的那句 —— 图片描述的前 80 字
            // 通常是「这是一张…的截图」，看了等于没看。
            var question = UserQuestionRegex.Match(content);
            if (question.Success)
            {
                var asked = question.Groups["question"].Value.Trim();
                if (asked.Length > 0)
                {
                    text = asked;
                }
            }

            // 标记本身已经由来源名表达了，摘要里重复一遍纯属浪费那 80 个字符
            text = PluginResultRegex.Replace(text, " ");
            text = SeeScreenRegex.Replace(text, " ");
            text = Regex.Replace(text, @"\[/屏幕内容\]", " ");
            text = ImageContentRegex.Replace(text, " ");
            text = Regex.Replace(text, @"\[用户问题\]", " ");
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
