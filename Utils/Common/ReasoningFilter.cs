using System.Text.RegularExpressions;

namespace VPetLLM.Utils.Common
{
    /// <summary>
    /// 剥掉推理模型的"思考"内容。
    ///
    /// DeepSeek-R1 / Qwen / GLM 这类模型会把推理过程包在 &lt;think&gt;…&lt;/think&gt; 里
    /// 一起吐出来。这段东西**既不能念也不能解析**：
    ///   - 念出来 = 桌宠当众朗读自己的心理活动，动辄几百字
    ///   - 解析 = 思考里常常复述指令格式（"我应该用 &lt;|say_begin|&gt; …"），
    ///     会被当成真的指令执行
    ///
    /// 之前没有这一层也没出事，是因为标记之外的文本本来就被整段丢弃了；
    /// 现在那些文本会被捡回来当说话内容，这一层就成了必需品。
    /// </summary>
    public static class ReasoningFilter
    {
        /// <summary>
        /// 各家用的包裹标签。只认成对出现的这几个，不做通用 XML 剥离 ——
        /// 那样会把正文里正常出现的尖括号内容一起吃掉。
        /// </summary>
        private static readonly string[] Tags = { "think", "thinking", "reasoning", "reflection" };

        private static readonly Regex[] Paired = BuildPaired();
        private static readonly Regex[] Unclosed = BuildUnclosed();

        private static Regex[] BuildPaired()
        {
            var result = new Regex[Tags.Length];
            for (int i = 0; i < Tags.Length; i++)
            {
                result[i] = new Regex($@"<\s*{Tags[i]}\s*>.*?<\s*/\s*{Tags[i]}\s*>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
            return result;
        }

        private static Regex[] BuildUnclosed()
        {
            var result = new Regex[Tags.Length];
            for (int i = 0; i < Tags.Length; i++)
            {
                result[i] = new Regex($@"<\s*{Tags[i]}\s*>.*$",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
            return result;
        }

        /// <summary>
        /// 去掉思考块。没有思考块时原样返回（连 Trim 都不做，避免影响既有解析）。
        /// </summary>
        public static string Strip(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            if (text!.IndexOf('<') < 0) return text;   // 绝大多数回复走这条捷径

            var result = text;

            foreach (var regex in Paired)
            {
                if (regex.IsMatch(result)) result = regex.Replace(result, "");
            }

            // 开了标签却没闭合：被 max_tokens 截断，或者模型自己漏了 </think>。
            // 这种情况下后面全是思考，一路删到结尾 —— 保留反而更糟：
            // 半截思考会被当成正文念出来。
            foreach (var regex in Unclosed)
            {
                if (regex.IsMatch(result)) result = regex.Replace(result, "");
            }

            return result;
        }

        /// <summary>回复里是否含有思考块（用于日志/诊断，不影响处理）。</summary>
        public static bool ContainsReasoning(string? text)
            => !string.IsNullOrEmpty(text) && text!.IndexOf('<') >= 0 && Strip(text) != text;
    }
}
