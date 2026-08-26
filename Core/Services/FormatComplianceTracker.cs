namespace VPetLLM.Core.Services
{
    /// <summary>
    /// 记下上一次回复的格式问题，在**下一次请求**的系统提示词里提醒模型。
    ///
    /// 兜底逻辑（把标记外的正文捡回来当说话）能让功能不出错，但模型不会因此学乖 ——
    /// 它看不到自己被纠正过，下次照旧。所以纠正必须以提示词的形式回到模型眼前。
    ///
    /// 刻意只留一条待发提醒、发过即清：
    /// 累积计数、逐级升级那套在这里是负担 —— 提示词越长越贵，而重复违规
    /// 下一轮自然会重新置位，效果一样。
    /// </summary>
    public static class FormatComplianceTracker
    {
        public enum Violation
        {
            None,
            /// <summary>回复里有正文写在了标记之外。</summary>
            StrayText,
            /// <summary>整条回复一个标记都没有。</summary>
            NoMarkers
        }

        private static readonly object Gate = new();
        private static Violation _pending = Violation.None;

        /// <summary>
        /// 记一次违规。同一轮里多种违规时以"完全没有标记"为准 ——
        /// 那是更严重也更好描述的一种，混着说反而让模型抓不住重点。
        /// </summary>
        public static void Report(Violation violation)
        {
            if (violation == Violation.None) return;

            lock (Gate)
            {
                if (_pending == Violation.NoMarkers) return;
                _pending = violation;
            }
        }

        /// <summary>当前是否有待发提醒（不消费，供诊断用）。</summary>
        public static Violation Pending
        {
            get { lock (Gate) return _pending; }
        }

        /// <summary>
        /// 取出提醒并清空。没有待发提醒时返回 null。
        /// </summary>
        public static string? TakeReminder(string? language)
        {
            Violation violation;
            lock (Gate)
            {
                violation = _pending;
                _pending = Violation.None;
            }

            return violation == Violation.None ? null : Describe(violation, language);
        }

        /// <summary>测试与"切换 provider 重来"用：丢掉待发提醒。</summary>
        public static void Reset()
        {
            lock (Gate) _pending = Violation.None;
        }

        public static string Describe(Violation violation, string? language)
        {
            var zh = language is null || language.StartsWith("zh");

            return violation switch
            {
                Violation.StrayText => zh
                    ? "【格式纠正】你上一次回复把要说的话写在了指令标记之外。所有台词都必须包在 " +
                      "<|say_begin|> \"文本\" <|say_end|> 里；标记之外不要留任何正文。"
                    : "[FORMAT CORRECTION] Your previous reply put spoken text outside the command markers. " +
                      "All speech must be wrapped in <|say_begin|> \"text\" <|say_end|>; " +
                      "leave nothing outside the markers.",

                Violation.NoMarkers => zh
                    ? "【格式纠正】你上一次回复完全没有使用指令标记。回复必须由指令组成，" +
                      "说话请用 <|say_begin|> \"文本\" <|say_end|>，不要直接输出白话文。"
                    : "[FORMAT CORRECTION] Your previous reply used no command markers at all. " +
                      "Replies must consist of commands; speak with <|say_begin|> \"text\" <|say_end|> " +
                      "instead of writing prose directly.",

                _ => ""
            };
        }
    }
}
