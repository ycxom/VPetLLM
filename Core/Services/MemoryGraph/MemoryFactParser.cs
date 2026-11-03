using System.Text.RegularExpressions;
using VPetLLM.Core.Data.Managers;

namespace VPetLLM.Core.Services.MemoryGraph
{
    /// <summary>总结里标出来的一个实体：[[@名字]] 是人，[[名字]] 是话题/地点/物品。</summary>
    public readonly record struct FactMarker(string Label, bool IsPerson);

    public sealed class ParsedFact
    {
        /// <summary>去掉标记后的行（标记换回它包着的文字）。</summary>
        public string Text { get; init; } = "";
        public List<FactMarker> Markers { get; } = new();
    }

    /// <summary>
    /// 解析分段总结里的实体标记。
    ///
    /// 标记只用来建图：落库和注入 prompt 的都是去掉标记后的文本，
    /// 去重比较的也是去掉标记后的文本——同一件事这次标了、上次没标，不能因此算成两条。
    /// </summary>
    public static class MemoryFactParser
    {
        private static readonly Regex Marker = new(@"\[\[(@?)([^\[\]\r\n]{1,40})\]\]", RegexOptions.Compiled);

        /// <summary>
        /// 追加在分段总结提示词后面（Free 网关只认首段，追加不影响放行）。
        /// 例子里故意不用「用户」：模型会照抄例子，写出一句「[[@user]] 喜欢……」。
        /// </summary>
        public const string MarkerInstruction =
            "In each line, mark people with [[@Name]] (including the user and yourself, written the way the sentence " +
            "refers to them) and key topics, places, objects or activities with [[Name]], e.g. " +
            "\"- [[@Alice]] is a coworker and likes [[hiking]]\". Mark at most 6 items per line and keep the sentence " +
            "readable without the brackets.";

        public static string StripMarkers(string text)
            => string.IsNullOrEmpty(text) ? text : Marker.Replace(text, m => m.Groups[2].Value.Trim());

        public static List<ParsedFact> ParseLines(string text)
        {
            var result = new List<ParsedFact>();
            if (string.IsNullOrEmpty(text))
                return result;

            foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;

                var fact = new ParsedFact { Text = StripMarkers(rawLine).Trim() };
                foreach (Match m in Marker.Matches(rawLine))
                {
                    var label = m.Groups[2].Value.Trim();
                    if (label.Length > 0)
                        fact.Markers.Add(new FactMarker(label, m.Groups[1].Value == "@"));
                }
                result.Add(fact);
            }
            return result;
        }

        /// <summary>按「去标记后的行」的归一化文本建索引，供去重之后按保留下来的行找回它的标记。</summary>
        public static Dictionary<string, List<FactMarker>> IndexMarkers(string text)
        {
            var index = new Dictionary<string, List<FactMarker>>(StringComparer.Ordinal);
            foreach (var fact in ParseLines(text))
            {
                if (fact.Markers.Count == 0)
                    continue;
                var key = FactSimilarity.Normalize(fact.Text);
                if (key.Length == 0)
                    continue;
                if (!index.TryGetValue(key, out var list))
                    index[key] = list = new List<FactMarker>();
                list.AddRange(fact.Markers);
            }
            return index;
        }
    }

    /// <summary>
    /// 把一行事实里提到的人和话题对到实体上。
    ///
    /// 来源有三处：模型打的标记、已知实体名在文本里出现、引号/书名号括起来的词。
    /// 用户和桌宠固定成 person:user / person:pet，不管模型这次管用户叫「主人」还是用户的名字，
    /// 否则一个人在图里会裂成好几个点。
    /// </summary>
    public sealed class MemoryEntityResolver
    {
        public const string UserKey = MemoryEntityType.Person + ":user";
        public const string PetKey = MemoryEntityType.Person + ":pet";

        private const int MaxEntitiesPerFact = 8;

        private readonly string _userLabel;
        private readonly string _petLabel;
        private readonly HashSet<string> _userAliases;
        private readonly HashSet<string> _petAliases;
        private readonly List<(string Label, EntityRef Ref)> _known;

        private static readonly Regex Quoted = new(@"[「“《『]([^「」“”《》『』\r\n]{2,20})[」”》』]", RegexOptions.Compiled);

        public MemoryEntityResolver(string? userName, string? aiName, IEnumerable<MemoryEntity> knownEntities)
        {
            _userLabel = string.IsNullOrWhiteSpace(userName) ? "主人" : userName.Trim();
            _petLabel = string.IsNullOrWhiteSpace(aiName) ? "桌宠" : aiName.Trim();

            _userAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _userLabel, "用户", "主人", "user" };
            _petAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _petLabel, "桌宠", "assistant" };

            // 长名优先：「小林老师」和「小林」都在时先认长的
            _known = knownEntities
                .Where(e => e.Key != UserKey && e.Key != PetKey && IsMatchableLabel(e.Label))
                .Select(e => (e.Label, new EntityRef(e.Key, e.Type, e.Label)))
                .OrderByDescending(e => e.Label.Length)
                .ToList();
        }

        public EntityRef User => new(UserKey, MemoryEntityType.Person, _userLabel);
        public EntityRef Pet => new(PetKey, MemoryEntityType.Person, _petLabel);

        public static string KeyOf(string type, string label) => type + ":" + FactSimilarity.Normalize(label);

        public List<EntityRef> Resolve(string text, IEnumerable<FactMarker>? markers)
        {
            var result = new List<EntityRef>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(EntityRef entity)
            {
                if (result.Count >= MaxEntitiesPerFact || entity.Key.EndsWith(':') || !seen.Add(entity.Key))
                    return;
                result.Add(entity);
            }

            if (markers is not null)
            {
                foreach (var marker in markers)
                {
                    if (_userAliases.Contains(marker.Label))
                        Add(User);
                    else if (_petAliases.Contains(marker.Label))
                        Add(Pet);
                    else
                    {
                        var type = marker.IsPerson ? MemoryEntityType.Person : MemoryEntityType.Topic;
                        Add(new EntityRef(KeyOf(type, marker.Label), type, marker.Label));
                    }
                }
            }

            // 单字的名字（「我」「喵」）拿来子串匹配会命中几乎每一行，只认标记
            text ??= "";
            if (_userAliases.Any(a => IsMatchableLabel(a) && ContainsTerm(text, a)))
                Add(User);
            if (_petAliases.Any(a => IsMatchableLabel(a) && ContainsTerm(text, a)))
                Add(Pet);

            foreach (var (label, entity) in _known)
            {
                if (ContainsTerm(text, label))
                    Add(entity);
            }

            foreach (Match m in Quoted.Matches(text))
            {
                var label = m.Groups[1].Value.Trim();
                if (_userAliases.Contains(label))
                    Add(User);
                else if (_petAliases.Contains(label))
                    Add(Pet);
                else if (IsMatchableLabel(label))
                    Add(new EntityRef(KeyOf(MemoryEntityType.Topic, label), MemoryEntityType.Topic, label));
            }

            return result;
        }

        /// <summary>太短的名字拿来做子串匹配误伤太大：中文至少 2 字，纯拉丁至少 3 个字母。</summary>
        private static bool IsMatchableLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return false;
            var key = FactSimilarity.Normalize(label);
            if (key.Length == 0)
                return false;
            var latin = key.All(c => c < 128);
            return latin ? key.Length >= 3 : key.Length >= 2;
        }

        /// <summary>中文按子串找；拉丁词要求两侧不是字母数字，免得「user」命中「username」。</summary>
        private static bool ContainsTerm(string text, string term)
        {
            if (string.IsNullOrEmpty(term))
                return false;

            var latin = term.All(c => c < 128);
            var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                if (!latin)
                    return true;
                var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
                var afterIndex = index + term.Length;
                var after = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);
                if (before && after)
                    return true;
                index = text.IndexOf(term, index + 1, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
    }
}
