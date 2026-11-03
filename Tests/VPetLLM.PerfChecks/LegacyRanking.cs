using System.Text;

namespace VPetLLM.PerfChecks;

/// <summary>
/// 改造前的词法打分原样留在这里当参照：新实现必须逐位等于它。别跟着主项目一起改。
/// </summary>
internal static class LegacyRanking
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    public static double[] Score(IReadOnlyList<IReadOnlyList<string>> docTokens, IReadOnlyList<string> queryTerms)
    {
        var n = docTokens.Count;
        var scores = new double[n];
        if (n == 0 || queryTerms.Count == 0)
            return scores;

        var termFreqs = new Dictionary<string, int>[n];
        double totalLength = 0;
        for (int i = 0; i < n; i++)
        {
            var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in docTokens[i])
                tf[t] = tf.TryGetValue(t, out var c) ? c + 1 : 1;
            termFreqs[i] = tf;
            totalLength += docTokens[i].Count;
        }

        var avgLength = totalLength / n;
        if (avgLength <= 0)
            return scores;

        foreach (var term in queryTerms)
        {
            var df = 0;
            for (int i = 0; i < n; i++)
                if (termFreqs[i].ContainsKey(term))
                    df++;

            if (df == 0)
                continue;

            var idf = Math.Log(1.0 + (n - df + 0.5) / (df + 0.5));

            for (int i = 0; i < n; i++)
            {
                if (!termFreqs[i].TryGetValue(term, out var tf))
                    continue;

                var docLen = docTokens[i].Count;
                var denominator = tf + K1 * (1.0 - B + B * docLen / avgLength);
                scores[i] += idf * (tf * (K1 + 1.0)) / denominator;
            }
        }

        return scores;
    }

    public static int CountMatches(string? text, List<string> keywords)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var count = 0;
        foreach (var kw in keywords)
        {
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                count++;
        }
        return count;
    }

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0.0;

        double dot = 0;
        for (int i = 0; i < a.Length; i++)
            dot += (double)a[i] * b[i];
        return dot;
    }

    public static List<string> Tokenize(string query)
    {
        var terms = new List<string>();
        if (string.IsNullOrWhiteSpace(query))
            return terms;

        var cjk = new StringBuilder();
        var latin = new StringBuilder();

        void AddTerm(string t)
        {
            if (t.Length > 0 && !StopWords.Contains(t))
                terms.Add(t);
        }

        void FlushCjk()
        {
            var s = cjk.ToString();
            cjk.Clear();
            if (s.Length == 0)
                return;

            if (s.Length == 1)
            {
                AddTerm(s);
                return;
            }

            for (int i = 0; i + 1 < s.Length; i++)
                AddTerm(s.Substring(i, 2));
        }

        void FlushLatin()
        {
            var s = latin.ToString();
            latin.Clear();
            if (s.Length >= 2)
                AddTerm(s);
        }

        foreach (var c in query)
        {
            if (IsCjk(c))
            {
                FlushLatin();
                cjk.Append(c);
            }
            else if (char.IsLetterOrDigit(c))
            {
                FlushCjk();
                latin.Append(char.ToLowerInvariant(c));
            }
            else
            {
                FlushCjk();
                FlushLatin();
            }
        }
        FlushCjk();
        FlushLatin();

        return terms;
    }

    private static bool IsCjk(char c) => c >= '一' && c <= '鿿';

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "could",
        "should", "may", "might", "can", "shall", "to", "of", "in", "for",
        "on", "with", "at", "by", "from", "as", "into", "through", "during",
        "before", "after", "above", "below", "between", "and", "but", "or",
        "nor", "not", "so", "yet", "both", "either", "neither", "each", "every",
        "all", "any", "few", "more", "most", "other", "some", "such", "no",
        "only", "own", "same", "than", "too", "very", "just", "about", "now",

        "的", "了", "在", "是", "我", "有", "和", "就", "不", "都", "一",
        "个", "上", "也", "很", "到", "说", "要", "去", "你", "会", "着",
        "看", "好", "这", "他", "她", "它", "那", "吗", "吧", "呢", "啊",
        "哦", "嗯", "呀", "哈",

        "没有", "自己", "们的", "什么", "怎么", "如何", "可以", "这个", "那个",
        "我们", "你们", "他们", "她们", "记得", "还记", "知道", "觉得", "认为",
        "因为", "所以", "但是", "然后", "还有", "或者", "如果", "就是", "这样",
        "那样", "一个", "一下", "一些", "现在", "已经", "曾经", "以及", "而且",
    };
}
