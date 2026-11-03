namespace VPetLLM.Core.Services.MemoryGraph
{
    /// <summary>
    /// 记忆原子的类型，决定它能活多久、怎么衰减（见 <see cref="MemoryAtomScoring"/>）。
    /// 分类参照 astrbot_plugin_livingmemory：事件很快过时，身份类事实很久不变。
    /// </summary>
    public enum MemoryAtomType
    {
        Unknown = 0,

        /// <summary>发生过的一件事（「昨天去了海边」）。</summary>
        Episodic = 1,

        /// <summary>将来要做的事（「下周三去体检」），过了日子就没用了。</summary>
        Planned = 2,

        /// <summary>稳定的事实（名字、生日、住在哪）。</summary>
        Factual = 3,

        /// <summary>人与人之间的关系（「小林是主人的同事」）。</summary>
        Relational = 4,

        /// <summary>喜好与习惯（「不吃香菜」）。</summary>
        Preference = 5
    }

    public enum MemoryAtomStatus
    {
        Active,
        Fading,
        Expired
    }

    public static class MemoryAtomSource
    {
        /// <summary>来自溢出总结的一行，source_id = overflow_summaries.id。</summary>
        public const string Summary = "summary";

        /// <summary>来自重要记录，source_id = important_records.id。记录不分提供商，provider 存 ''。</summary>
        public const string Record = "record";
    }

    public static class MemoryEntityType
    {
        public const string Person = "person";
        public const string Topic = "topic";
    }

    /// <summary>
    /// 一条记忆原子：总结里的一行事实，或一条重要记录。
    /// 时间一律 UTC。
    /// </summary>
    public sealed class MemoryAtom
    {
        public long Id { get; set; }
        public string Provider { get; set; } = "";
        public string SourceKind { get; set; } = MemoryAtomSource.Summary;
        public long SourceId { get; set; }
        public string Content { get; set; } = "";

        /// <summary><see cref="VPetLLM.Core.Data.Managers.FactSimilarity.Normalize"/> 之后的内容，判「同一条事实」用。</summary>
        public string FactKey { get; set; } = "";

        public MemoryAtomType Type { get; set; }
        public double Confidence { get; set; }

        /// <summary>0~1。总结行默认 0.5，重要记录取 权重/10。</summary>
        public double Importance { get; set; }

        /// <summary>文本里提到的事件日期（本地日期），解析不出为 null。</summary>
        public DateTime? EventTime { get; set; }

        /// <summary>被后来的总结复述过几次。每复述一次寿命变长、衰减从头算。</summary>
        public int Reinforcement { get; set; }

        public DateTime CreatedAt { get; set; }

        /// <summary>最近一次被提到（创建或被复述）的时间，衰减从这里算起。</summary>
        public DateTime LastSeenAt { get; set; }

        public List<long> EntityIds { get; } = new();
    }

    public sealed class MemoryEntity
    {
        public long Id { get; set; }

        /// <summary>类型:归一化名称，例如 person:user、topic:徒步。</summary>
        public string Key { get; set; } = "";

        public string Type { get; set; } = MemoryEntityType.Topic;
        public string Label { get; set; } = "";
    }

    /// <summary>解析/识别出来、还没落库的实体。</summary>
    public readonly record struct EntityRef(string Key, string Type, string Label);

    /// <summary>某个提供商的全部原子（含不分提供商的记录原子）和它们引用到的实体。</summary>
    public sealed class MemoryGraphData
    {
        public List<MemoryAtom> Atoms { get; init; } = new();
        public Dictionary<long, MemoryEntity> Entities { get; init; } = new();

        /// <summary>当前仍在注入 prompt 的总结行 id。这些行的原子已经在上下文里，检索时不必再召回。</summary>
        public HashSet<int> ActiveSummaryIds { get; init; } = new();
    }
}
