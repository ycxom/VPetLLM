namespace VPetLLM.Core.Services
{
    /// <summary>
    /// 跟踪模型回复的格式是否合规，并据此给出**动态**的纠正提示。
    ///
    /// 兜底逻辑（把标记外的正文捡回来当说话）能让功能不出错，但模型不会因此学乖 ——
    /// 它看不到自己被纠正过，下次照旧。所以纠正必须以提示词的形式回到模型眼前。
    ///
    /// 为什么要有状态，而不是"违规一次、提醒一次"：
    ///   长对话后期的格式漂移是**被上下文带偏**的 —— 历史里已经躺着几条不带标记的回复，
    ///   模型会照着自己的"先例"写。这时提醒一次、下一轮格式对了就撤掉，再下一轮又被
    ///   先例带回去，来回拉锯。所以：
    ///     · 连续违规 → 措辞升级，并附上正确示例、明说"别学前面的错误回复"；
    ///     · 违规之后要**连续两次**合规才撤销提醒，中间那段用一句轻的"保持格式"稳住；
    ///     · 记账只在拿到完整回复时做（见 <see cref="ReplyFormatInspector"/>），
    ///       错误提示、插件通知这类不是模型写的文本不会被算进来。
    /// </summary>
    public static class FormatComplianceTracker
    {
        public enum Violation
        {
            None = 0,
            /// <summary>回复里有正文写在了标记之外。</summary>
            StrayText = 1,
            /// <summary>整条回复一个标记都没有。</summary>
            NoMarkers = 2,
            /// <summary>用了标记但写坏了：没闭合、拼错、少了竖线。</summary>
            BrokenMarker = 3
        }

        public enum Level
        {
            /// <summary>一切正常，不注入任何东西。</summary>
            None,
            /// <summary>刚纠正过、正在恢复：轻提醒一句保持格式。</summary>
            Recovering,
            /// <summary>上一条回复违规：指出具体问题。</summary>
            Correct,
            /// <summary>连续违规：升级措辞、给示例、点明别模仿历史里的错误回复。</summary>
            Escalated
        }

        /// <summary>违规后要连续合规这么多次才撤销提醒。</summary>
        public const int CleanRepliesToRecover = 2;

        /// <summary>连续违规达到这个次数就升级。</summary>
        public const int EscalateAfter = 2;

        private static readonly object Gate = new();
        private static Violation _last = Violation.None;
        private static int _consecutiveViolations;
        private static int _cleanSinceViolation;
        private static bool _active;

        /// <summary>两种违规同时出现时取更该纠正的那个。</summary>
        public static Violation Worse(Violation a, Violation b) => (Violation)Math.Max((int)a, (int)b);

        /// <summary>记下一条**完整的模型回复**的格式判定结果。</summary>
        public static void RecordReply(Violation violation)
        {
            lock (Gate)
            {
                if (violation != Violation.None)
                {
                    _active = true;
                    _last = violation;
                    _consecutiveViolations++;
                    _cleanSinceViolation = 0;
                    return;
                }

                if (!_active) return;

                _consecutiveViolations = 0;
                _cleanSinceViolation++;
                if (_cleanSinceViolation >= CleanRepliesToRecover)
                {
                    _active = false;
                    _last = Violation.None;
                    _cleanSinceViolation = 0;
                }
            }
        }

        public static Level CurrentLevel
        {
            get
            {
                lock (Gate)
                {
                    if (!_active) return Level.None;
                    if (_consecutiveViolations >= EscalateAfter) return Level.Escalated;
                    if (_consecutiveViolations >= 1) return Level.Correct;
                    return Level.Recovering;
                }
            }
        }

        /// <summary>
        /// 当前应注入的纠正提示；不需要时返回 null。
        ///
        /// 只读不消费：失败转移时同一轮会发好几次请求，每次都要带上同样的提醒。
        /// 状态只由 <see cref="RecordReply"/> 推进。
        /// </summary>
        public static string? CurrentReminder(string? language)
        {
            Level level;
            Violation last;
            int streak;
            lock (Gate)
            {
                level = CurrentLevel;
                last = _last;
                streak = _consecutiveViolations;
            }

            return level == Level.None ? null : Describe(level, last, streak, language);
        }

        /// <summary>测试与"切换 provider 重来"用：清空全部状态。</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                _active = false;
                _last = Violation.None;
                _consecutiveViolations = 0;
                _cleanSinceViolation = 0;
            }
        }

        public static string Describe(Level level, Violation violation, int streak, string? language)
        {
            var zh = language is null || language.StartsWith("zh");

            if (level == Level.Recovering)
            {
                return zh
                    ? "【格式提醒】继续保持：所有台词都放在 <|say_begin|> \"文本\" <|say_end|> 里，标记之外不留正文。"
                    : "[FORMAT] Keep it up: all speech inside <|say_begin|> \"text\" <|say_end|>, nothing outside the markers.";
            }

            var problem = violation switch
            {
                Violation.NoMarkers => zh
                    ? "上一次回复完全没有使用指令标记，直接输出了白话文。"
                    : "Your previous reply used no command markers at all and was plain prose.",
                Violation.BrokenMarker => zh
                    ? "上一次回复的指令标记写坏了（没有闭合、拼错或缺少竖线），无法被识别。"
                    : "Your previous reply had malformed markers (unclosed, misspelled or missing the |) that could not be parsed.",
                _ => zh
                    ? "上一次回复把要说的话写在了指令标记之外。"
                    : "Your previous reply put spoken text outside the command markers."
            };

            if (level == Level.Correct)
            {
                return zh
                    ? $"【格式纠正】{problem}所有台词都必须包在 <|say_begin|> \"文本\" <|say_end|> 里，每个标记成对出现，标记之外不要留任何正文。"
                    : $"[FORMAT CORRECTION] {problem} All speech must be wrapped in <|say_begin|> \"text\" <|say_end|>, " +
                      "every marker must be closed, and nothing may be left outside the markers.";
            }

            // Escalated：点明上下文里的错误先例，并给一个最小的正确样例
            return zh
                ? $"【格式纠正·第 {streak} 次】{problem}这已经连续发生了 {streak} 次。" +
                  "前面对话记录里如果有不带标记的回复，那是错误示范，不要模仿。" +
                  "本次回复必须严格按以下格式，只输出标记：\n" +
                  "<|say_begin|> \"要说的话\" <|say_end|>\n" +
                  "需要动作或其它指令时同样用成对的 <|xxx_begin|> … <|xxx_end|>，标记之外不写任何文字。"
                : $"[FORMAT CORRECTION #{streak}] {problem} This has now happened {streak} times in a row. " +
                  "Any earlier replies in the conversation that lack markers are mistakes — do not imitate them. " +
                  "This reply must strictly follow the format and contain only markers:\n" +
                  "<|say_begin|> \"what you want to say\" <|say_end|>\n" +
                  "Other commands use paired <|xxx_begin|> … <|xxx_end|> as well; write nothing outside the markers.";
        }
    }
}
