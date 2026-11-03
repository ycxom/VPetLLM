using System.Text.RegularExpressions;

namespace VPetLLM.Core.Services.MemoryGraph
{
    public readonly record struct AtomClassification(MemoryAtomType Type, double Confidence, DateTime? EventTime);

    /// <summary>
    /// 按关键词给一行事实分类。不调模型：总结已经是模型写的，每行再问一次模型代价太大，
    /// 而类型只用来定寿命，分错的后果是早几天或晚几天淡出，不影响内容本身。
    ///
    /// 判定顺序：计划 &gt; 偏好 &gt; 关系 &gt; 身份事实 &gt; 事件 &gt; 一般事实 &gt; 未分类。
    /// 计划必须同时有时间词和将来标记、没有过去标记、且日期不在过去——
    /// 「上周约好了去爬山」是已经发生的事，不能当成还没到的计划一直挂着。
    /// </summary>
    public static class MemoryAtomClassifier
    {
        private static readonly string[] TimeWords =
            { "明天", "明早", "明晚", "后天", "下周", "下星期", "下礼拜", "下个月", "下月", "明年", "周末", "今晚", "今天", "过几天", "月底", "月初" };
        private static readonly Regex EnglishTime = Word("tomorrow|tonight|next week|next month|next year|this weekend|weekend");

        private static readonly string[] FutureMarkers =
            { "打算", "计划", "准备", "将要", "将会", "要去", "会去", "想去", "约好", "约了", "预定", "预约", "安排", "届时", "到时", "提醒", "记得", "即将", "待会", "等会" };
        private static readonly Regex EnglishFuture = Word("will|plans?|planning|planned to|going to|gonna|scheduled|remind|intends?|upcoming");

        private static readonly string[] PastMarkers =
            { "已经", "曾经", "昨天", "前天", "上周", "上星期", "上个月", "上月", "去年", "刚才", "刚刚", "之前", "那天", "当时" };
        private static readonly Regex EnglishPast = Word("yesterday|last week|last month|last year|last night|ago|already|went|did");

        private static readonly string[] PreferenceWords =
            { "喜欢", "爱吃", "爱喝", "爱看", "爱玩", "爱听", "偏好", "偏爱", "讨厌", "最爱", "喜爱", "热爱", "习惯", "口味", "受不了", "害怕", "不吃", "不喝", "忌口", "过敏" };
        private static readonly Regex EnglishPreference = Word("likes?|loves?|prefers?|preferred|hates?|dislikes?|favou?rite|enjoys?|allergic|can't stand");

        private static readonly string[] RelationWords =
            { "朋友", "同事", "同学", "室友", "男友", "女友", "对象", "老婆", "老公", "妻子", "丈夫", "父亲", "母亲", "爸爸", "妈妈",
              "哥哥", "姐姐", "弟弟", "妹妹", "儿子", "女儿", "家人", "老板", "上司", "领导", "老师", "邻居", "闺蜜", "兄弟", "亲戚", "认识", "关系" };
        private static readonly Regex EnglishRelation = Word(
            "friends?|colleagues?|coworkers?|boss|wife|husband|girlfriend|boyfriend|partner|mother|father|mom|dad|sister|brother|son|daughter|family|neighbou?rs?|roommates?|classmates?|teachers?|relationship");

        private static readonly string[] IdentityWords =
            { "生日", "名字", "叫做", "名叫", "住在", "来自", "老家", "职业", "年龄", "岁", "身高", "体重", "毕业", "就读", "专业", "籍贯", "星座", "血型" };
        private static readonly Regex EnglishIdentity = Word("birthday|name is|named|lives in|live in|born|works as|years old|age");

        private static readonly string[] EpisodicWords =
            { "昨天", "前天", "今天", "刚才", "刚刚", "上周", "那天", "曾经", "已经", "去了", "吃了", "看了", "玩了", "买了", "做了", "完成了", "聊了", "参加了", "发生", "经历", "遇到" };
        private static readonly Regex EnglishEpisodic = Word("yesterday|today|went|did|had|visited|watched|played|bought|finished|met|happened");

        private static readonly string[] WeakFactWords = { "是", "有", "在", "使用", "用的", "养了", "拥有", "属于" };
        private static readonly Regex EnglishWeakFact = Word("is|are|has|have|uses|owns");

        private static Regex Word(string alternatives)
            => new($@"\b(?:{alternatives})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static bool Has(string text, string[] words, Regex english)
        {
            foreach (var w in words)
            {
                if (text.Contains(w, StringComparison.Ordinal))
                    return true;
            }
            return english.IsMatch(text);
        }

        /// <param name="nowLocal">本地时间的「现在」。总结里的日期是按本地时间写的。</param>
        public static AtomClassification Classify(string text, DateTime nowLocal)
        {
            text ??= "";
            var eventTime = ParseEventTime(text, nowLocal);
            var today = nowLocal.Date;

            var hasTime = eventTime is not null || Has(text, TimeWords, EnglishTime);
            if (hasTime && Has(text, FutureMarkers, EnglishFuture) && !Has(text, PastMarkers, EnglishPast)
                && (eventTime is null || eventTime.Value >= today))
                return new(MemoryAtomType.Planned, 0.85, eventTime);

            if (Has(text, PreferenceWords, EnglishPreference))
                return new(MemoryAtomType.Preference, 0.82, eventTime);

            if (Has(text, RelationWords, EnglishRelation))
                return new(MemoryAtomType.Relational, 0.80, eventTime);

            if (Has(text, IdentityWords, EnglishIdentity))
                return new(MemoryAtomType.Factual, 0.78, eventTime);

            if (Has(text, EpisodicWords, EnglishEpisodic) || (eventTime is not null && eventTime.Value < today))
                return new(MemoryAtomType.Episodic, 0.75, eventTime);

            if (Has(text, WeakFactWords, EnglishWeakFact))
                return new(MemoryAtomType.Factual, 0.70, eventTime);

            return new(MemoryAtomType.Unknown, 0.60, eventTime);
        }

        private static readonly Regex FullDate = new(@"(\d{4})\s*[-/.年]\s*(\d{1,2})\s*[-/.月]\s*(\d{1,2})", RegexOptions.Compiled);
        private static readonly Regex MonthDay = new(@"(\d{1,2})\s*月\s*(\d{1,2})\s*[日号]", RegexOptions.Compiled);
        private static readonly Regex Weekday = new(@"(下个?(?:周|星期|礼拜)|(?:这|本)?(?:周|星期|礼拜))([一二三四五六日天])", RegexOptions.Compiled);
        private static readonly Regex EnglishTomorrow = Word("tomorrow");
        private static readonly Regex EnglishDayAfterTomorrow = Word("day after tomorrow");
        private static readonly Regex EnglishNextWeek = Word("next week");
        private static readonly Regex EnglishNextMonth = Word("next month");

        /// <summary>
        /// 解析文本里提到的日期，返回本地日期（时分为 0）。
        /// 总结提示词要求把相对时间换成具体日期，所以先认完整日期，相对说法只是兜底。
        /// 只写了月日时取今年；比今天早两个月以上的，认为说的是明年。
        /// </summary>
        public static DateTime? ParseEventTime(string text, DateTime nowLocal)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            var today = nowLocal.Date;

            var full = FullDate.Match(text);
            if (full.Success && TryDate(full.Groups[1].Value, full.Groups[2].Value, full.Groups[3].Value, out var date))
                return date;

            var md = MonthDay.Match(text);
            if (md.Success && TryDate(today.Year.ToString(), md.Groups[1].Value, md.Groups[2].Value, out date))
                return date < today.AddDays(-60) ? date.AddYears(1) : date;

            if (text.Contains("大后天", StringComparison.Ordinal))
                return today.AddDays(3);
            if (text.Contains("后天", StringComparison.Ordinal) || EnglishDayAfterTomorrow.IsMatch(text))
                return today.AddDays(2);
            if (text.Contains("明天", StringComparison.Ordinal) || text.Contains("明早", StringComparison.Ordinal)
                || text.Contains("明晚", StringComparison.Ordinal) || EnglishTomorrow.IsMatch(text))
                return today.AddDays(1);

            var weekday = Weekday.Match(text);
            if (weekday.Success)
            {
                var target = "一二三四五六日天".IndexOf(weekday.Groups[2].Value[0]) + 1;
                if (target == 8) target = 7;   // 天 = 日
                var todayIso = ((int)today.DayOfWeek + 6) % 7 + 1;
                var diff = weekday.Groups[1].Value.StartsWith('下')
                    ? (8 - todayIso) + (target - 1)
                    : (target - todayIso + 7) % 7;
                return today.AddDays(diff);
            }

            if (text.Contains("下周", StringComparison.Ordinal) || text.Contains("下星期", StringComparison.Ordinal) || EnglishNextWeek.IsMatch(text))
                return today.AddDays(7);
            if (text.Contains("下个月", StringComparison.Ordinal) || text.Contains("下月", StringComparison.Ordinal) || EnglishNextMonth.IsMatch(text))
                return today.AddMonths(1);
            if (text.Contains("今天", StringComparison.Ordinal) || text.Contains("今晚", StringComparison.Ordinal))
                return today;

            return null;
        }

        private static bool TryDate(string year, string month, string day, out DateTime date)
        {
            date = default;
            if (!int.TryParse(year, out var y) || !int.TryParse(month, out var m) || !int.TryParse(day, out var d))
                return false;
            if (y < 1900 || y > 2200 || m < 1 || m > 12 || d < 1 || d > DateTime.DaysInMonth(y, m))
                return false;
            date = new DateTime(y, m, d);
            return true;
        }
    }
}
