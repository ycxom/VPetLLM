using System.Text.RegularExpressions;

namespace VPetLLM.Core.Data.Managers
{
    /// <summary>
    /// 「两行事实是不是同一件事」的字面判据，以及按行过滤重复事实。
    /// 溢出总结落库前的去重、记忆图谱里的「复述=加固」都用它，判据必须一致：
    /// 否则总结那边判成重复丢掉了，图谱这边却找不到可以加固的原子。
    /// </summary>
    public static class FactSimilarity
    {
        /// <summary>归一化：只留字母数字并转小写。列表符号、标点、空白、大小写都不影响比较。</summary>
        public static string Normalize(string line)
        {
            var builder = new StringBuilder(line.Length);
            foreach (var c in line)
            {
                if (char.IsLetterOrDigit(c))
                    builder.Append(char.ToLowerInvariant(c));
            }
            return builder.ToString();
        }

        /// <summary>
        /// 完全相同、被已有行包含、或长行字二元组 Dice ≥ 0.85 都算重复；
        /// 但数字序列不同绝不算重复（「生日 5月3日」改成「5月4日」是更新）。
        /// 两个参数都是 <see cref="Normalize"/> 之后的文本。
        /// </summary>
        public static bool IsSameFact(string candidate, string known)
        {
            if (candidate == known)
                return true;

            // 太短的行（「好的」「是」）不做模糊比较，误伤面太大
            if (candidate.Length < 8 || known.Length < 8)
                return false;

            // 「生日 5月3日」改成「5月4日」是事实更新，字面再像也不能当重复吞掉
            if (!DigitsOf(candidate).SequenceEqual(DigitsOf(known)))
                return false;

            if (known.Contains(candidate, StringComparison.Ordinal))
                return true;

            return candidate.Length >= 16 && BigramDice(candidate, known) >= 0.85;
        }

        /// <summary>形如「用户偏好：」的短标题，本身不携带事实。</summary>
        public static bool IsLabelLine(string line, string key)
        {
            var trimmed = line.Trim().TrimEnd('*').TrimEnd();
            return key.Length <= 12 && (trimmed.EndsWith(':') || trimmed.EndsWith('：') || trimmed.StartsWith('#'));
        }

        public static IEnumerable<char> DigitsOf(string text) => text.Where(char.IsDigit);

        public static double BigramDice(string a, string b)
        {
            var counts = new Dictionary<int, int>();
            for (var i = 0; i + 1 < a.Length; i++)
            {
                var gram = (a[i] << 16) | a[i + 1];
                counts[gram] = counts.TryGetValue(gram, out var n) ? n + 1 : 1;
            }

            var overlap = 0;
            for (var i = 0; i + 1 < b.Length; i++)
            {
                var gram = (b[i] << 16) | b[i + 1];
                if (counts.TryGetValue(gram, out var n) && n > 0)
                {
                    overlap++;
                    counts[gram] = n - 1;
                }
            }

            return 2.0 * overlap / ((a.Length - 1) + (b.Length - 1));
        }

        private static readonly Regex ListPrefix = new(@"^\s*(?:[-*•·]+|\d{1,2}[.)、])\s*", RegexOptions.Compiled);

        /// <summary>去掉行首的列表符号（「- 」「* 」「1. 」），给嵌入和记忆原子用。</summary>
        public static string StripListPrefix(string line) => ListPrefix.Replace(line, "").Trim();

        private static readonly string[] ChineseNegations = { "不", "没", "别", "无", "非", "未", "讨厌", "厌恶", "反感" };
        private static readonly Regex EnglishNegation = new(@"\b(?:not|no|never|dislike|hate)\b|n't", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 语义去重的护栏：向量很像不代表是同一件事。「喜欢猫」和「不喜欢猫」、
        /// 「周三开会」和「周四开会」在嵌入空间里几乎贴着，必须数字一致、否定词一致才允许判重复。
        /// 参数是原始行（不是归一化后的，否定词 n't 的撇号要留着）。
        /// </summary>
        public static bool AllowsSemanticMatch(string a, string b)
        {
            if (!DigitsOf(a).SequenceEqual(DigitsOf(b)))
                return false;
            return PolarityOf(a) == PolarityOf(b);
        }

        private static int PolarityOf(string line)
        {
            var mask = 0;
            for (var i = 0; i < ChineseNegations.Length; i++)
            {
                if (line.Contains(ChineseNegations[i], StringComparison.Ordinal))
                    mask |= 1 << i;
            }
            if (EnglishNegation.IsMatch(line))
                mask |= 1 << ChineseNegations.Length;
            return mask;
        }

        /// <summary>
        /// 去掉与 <paramref name="known"/> 里任一行重复的行，以及本身内部的重复行。
        ///
        /// 比的是归一化后的文本（见 <see cref="IsSameFact"/>）。新行比旧行更详细（旧行被它包含）时保留。
        /// 只剩标题、下面的内容全被去掉的标题行一并去掉。
        /// <paramref name="alsoDuplicate"/> 对通过了字面比较的行再判一次，返回它重复的那条已知行，不重复返回 null
        /// ——语义去重从这里接进来，标题/空行的处理与字面去重完全一致。
        /// </summary>
        public static FactFilterResult Filter(string text, IEnumerable<string> known, Func<string, string?>? alsoDuplicate = null)
        {
            var knownLines = new List<(string Key, string Raw)>();
            foreach (var entry in known)
            {
                foreach (var line in entry.Split('\n'))
                {
                    var key = Normalize(line);
                    if (key.Length > 0)
                        knownLines.Add((key, line.Trim()));
                }
            }

            var result = new FactFilterResult();
            var output = new List<string>();
            string? pendingLabel = null;
            foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.TrimEnd();
                var key = Normalize(line);
                if (key.Length == 0)
                {
                    pendingLabel = null;
                    if (output.Count > 0 && output[^1].Length > 0)
                        output.Add("");
                    continue;
                }

                if (IsLabelLine(line, key))
                {
                    pendingLabel = line;
                    continue;
                }

                string? matched = null;
                foreach (var k in knownLines)
                {
                    if (IsSameFact(key, k.Key))
                    {
                        matched = k.Raw;
                        break;
                    }
                }
                matched ??= alsoDuplicate?.Invoke(line);
                if (matched is not null)
                {
                    result.Duplicates.Add(new FactDuplicate(line.Trim(), matched));
                    continue;
                }

                if (pendingLabel is not null)
                {
                    output.Add(pendingLabel);
                    pendingLabel = null;
                }
                output.Add(line);
                result.Kept.Add(line.Trim());
                knownLines.Add((key, line.Trim()));
            }

            result.Text = string.Join("\n", output).Trim();
            return result;
        }
    }

    /// <summary>一条被判为重复的行，以及它重复的那条已知行。</summary>
    public readonly record struct FactDuplicate(string Line, string Matched);

    public sealed class FactFilterResult
    {
        /// <summary>过滤后的文本（含保留下来的标题行），可直接落库。</summary>
        public string Text { get; set; } = "";

        /// <summary>保留下来的事实行，不含标题行和空行。</summary>
        public List<string> Kept { get; } = new();

        /// <summary>被判为重复而丢掉的行。</summary>
        public List<FactDuplicate> Duplicates { get; } = new();
    }
}
