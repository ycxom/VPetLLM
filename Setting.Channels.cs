namespace VPetLLM
{
    /// <summary>
    /// 统一渠道模型。
    ///
    /// 存储层仍是五张按类型分开的节点表（OpenAINodes / GeminiNodes / …），这样旧版本读得回来、
    /// 其它按类型读节点的代码（识图节点、诊断、配置优化）也不必跟着改。统一发生在两处：
    /// 所有节点共用 <see cref="ChannelNodeBase"/>（Id / 优先级 / 权重 / 统计），
    /// 以及 <see cref="Core.Routing.ChannelRouter"/> 跨类型按优先级 + 权重挑渠道。
    /// </summary>
    public partial class Setting
    {
        /// <summary>
        /// 旧的"当前提供商 + 各提供商负载均衡 + 降级提供商列表"已折算成渠道的优先级/权重。
        /// 只迁移一次；之后节点列表就是唯一事实。
        /// </summary>
        public bool ChannelsUnified { get; set; } = false;

        /// <summary>启动时 provider_nodes 表是否读成功。读失败时不允许用空列表覆盖表。</summary>
        private bool _providerNodesLoaded;

        public enum ChannelKind
        {
            OpenAIChat,
            OpenAIResponses,
            Gemini,
            Ollama,
            LMStudio,
            Free
        }

        public enum OpenAIApiFormat
        {
            /// <summary>POST /v1/chat/completions</summary>
            ChatCompletions = 0,
            /// <summary>POST /v1/responses</summary>
            Responses = 1
        }

        /// <summary>
        /// 渠道运行统计。随设置一起持久化，但只在别的设置保存时顺带落盘 ——
        /// 每次请求都写库不值得，丢几次计数也无所谓。
        /// </summary>
        public class ChannelStats
        {
            public long RequestCount { get; set; }
            public long FailureCount { get; set; }
            public long TotalTokens { get; set; }
            public DateTime? LastUsedAt { get; set; }

            /// <summary>上次测试的耗时。只记测试，不记真实请求：真实请求的耗时主要取决于回复有多长。</summary>
            public int? LastTestLatencyMs { get; set; }
            public DateTime? LastTestedAt { get; set; }
            public bool? LastTestOk { get; set; }
            public string? LastTestError { get; set; }
        }

        /// <summary>
        /// 五种渠道节点的公共部分。子类只放各自独有的字段（地址、密钥、协议）。
        /// </summary>
        public abstract class ChannelNodeBase
        {
            /// <summary>跨类型唯一的渠道编号，仅用于显示和日志，不参与路由。</summary>
            public int Id { get; set; }

            public string Name { get; set; } = "";
            public bool Enabled { get; set; } = true;
            public string? Model { get; set; }
            public double Temperature { get; set; } = 0.7;
            public int MaxTokens { get; set; } = 2048;
            public bool EnableAdvanced { get; set; } = false;
            public bool EnableStreaming { get; set; } = false;
            public bool EnableVision { get; set; } = false;
            /// <summary>本节点的模型是否支持原生工具调用。需与全局 EnableNativeToolCall 同时开启。</summary>
            public bool EnableToolCall { get; set; } = false;
            /// <summary>本节点的思考强度（reasoning effort）。Default = 不发送该参数。</summary>
            public ThinkingEffort ThinkingEffort { get; set; } = ThinkingEffort.Default;
            public ChannelMode Mode { get; set; } = ChannelMode.Unrestricted;
            public string? PluginModeId { get; set; }
            public ChannelProxyMode ProxyMode { get; set; } = ChannelProxyMode.FollowDefault;

            /// <summary>优先级：数值大的先用；只有整层都失败才轮到下一层。</summary>
            public int Priority { get; set; } = 0;

            /// <summary>
            /// 同一优先级内的权重。全为 0 时均匀随机；有正有零时，0 的只作为同层的最后备选。
            /// </summary>
            public int Weight { get; set; } = 0;

            public ChannelStats Stats { get; set; } = new ChannelStats();

            [JsonIgnore] public abstract ChannelKind Kind { get; }

            /// <summary>请求地址，无地址的渠道（Free）为 null。</summary>
            [JsonIgnore] public virtual string? Endpoint => null;
        }

        /// <summary>渠道类型对应的旧"提供商"。</summary>
        public static LLMType FamilyOf(ChannelKind kind) => kind switch
        {
            ChannelKind.OpenAIChat or ChannelKind.OpenAIResponses => LLMType.OpenAI,
            ChannelKind.Gemini => LLMType.Gemini,
            ChannelKind.Ollama => LLMType.Ollama,
            ChannelKind.LMStudio => LLMType.LMStudio,
            _ => LLMType.Free
        };

        /// <summary>
        /// 所有渠道，按存储顺序（OpenAI、Gemini、Ollama、LM Studio、Free）。
        /// 返回快照，调用方可以一边遍历一边增删。
        /// </summary>
        public List<ChannelNodeBase> EnumerateChannels()
        {
            var all = new List<ChannelNodeBase>();
            if (OpenAI?.OpenAINodes is { } oa) all.AddRange(oa);
            if (Gemini?.GeminiNodes is { } gm) all.AddRange(gm);
            if (Ollama?.OllamaNodes is { } ol) all.AddRange(ol);
            if (LMStudio?.LMStudioNodes is { } lm) all.AddRange(lm);
            if (Free?.FreeNodes is { } fr) all.AddRange(fr);
            return all;
        }

        /// <summary>新建一个指定类型的渠道（未加入列表）。</summary>
        public static ChannelNodeBase CreateChannel(ChannelKind kind) => kind switch
        {
            ChannelKind.OpenAIChat => new OpenAINodeSetting { Model = "gpt-4o-mini" },
            ChannelKind.OpenAIResponses => new OpenAINodeSetting { Model = "gpt-4o-mini", ApiFormat = OpenAIApiFormat.Responses },
            ChannelKind.Gemini => new GeminiNodeSetting { Model = "gemini-1.5-flash" },
            ChannelKind.Ollama => new OllamaNodeSetting(),
            ChannelKind.LMStudio => new LMStudioNodeSetting(),
            _ => new FreeNodeSetting()
        };

        /// <summary>把渠道放进它所属类型的列表，并分配 Id。</summary>
        public void AddChannel(ChannelNodeBase node)
        {
            switch (node)
            {
                case OpenAINodeSetting n: OpenAI.OpenAINodes.Add(n); break;
                case GeminiNodeSetting n: Gemini.GeminiNodes.Add(n); break;
                case OllamaNodeSetting n: Ollama.OllamaNodes.Add(n); break;
                case LMStudioNodeSetting n: LMStudio.LMStudioNodes.Add(n); break;
                case FreeNodeSetting n: Free.FreeNodes.Add(n); break;
            }

            if (node.Id <= 0)
                node.Id = NextChannelId();
            SyncFreeContainer();
        }

        public bool RemoveChannel(ChannelNodeBase node)
        {
            var removed = node switch
            {
                OpenAINodeSetting n => OpenAI.OpenAINodes.Remove(n),
                GeminiNodeSetting n => Gemini.GeminiNodes.Remove(n),
                OllamaNodeSetting n => Ollama.OllamaNodes.Remove(n),
                LMStudioNodeSetting n => LMStudio.LMStudioNodes.Remove(n),
                FreeNodeSetting n => Free.FreeNodes.Remove(n),
                _ => false
            };
            if (removed) SyncFreeContainer();
            return removed;
        }

        /// <summary>
        /// 用另一个类型的新节点顶替旧节点（编辑页里把渠道类型改了的情况）。
        /// 公共字段和编号原样带过去；两者属于同一张表时直接原地替换，保住列表位置。
        /// </summary>
        public void ReplaceChannel(ChannelNodeBase oldNode, ChannelNodeBase newNode)
        {
            newNode.Id = oldNode.Id;
            if (oldNode.GetType() == newNode.GetType())
            {
                var list = ListOf(oldNode);
                var index = list?.IndexOf(oldNode) ?? -1;
                if (list is not null && index >= 0)
                {
                    list[index] = newNode;
                    SyncFreeContainer();
                    return;
                }
            }

            RemoveChannel(oldNode);
            AddChannel(newNode);
        }

        /// <summary>
        /// 渠道改类型：返回一个新类型的节点（不修改 <paramref name="source"/>）。
        ///
        /// 路由相关的公共字段（编号、启停、优先级、权重、用途、代理、统计…）原样带过去；
        /// 地址/密钥只在两边都有且不是旧类型的默认值时保留；模型只在 OpenAI 两种协议之间保留
        /// —— 换到别家服务，原来的模型名多半不存在。
        /// </summary>
        public static ChannelNodeBase ConvertChannel(ChannelNodeBase source, ChannelKind kind)
        {
            if (source.Kind == kind)
                return CloneChannel(source);

            // OpenAI 两种协议只是同一个节点的一个字段
            if (source is OpenAINodeSetting && (kind == ChannelKind.OpenAIChat || kind == ChannelKind.OpenAIResponses))
            {
                var same = (OpenAINodeSetting)CloneChannel(source);
                same.ApiFormat = kind == ChannelKind.OpenAIResponses ? OpenAIApiFormat.Responses : OpenAIApiFormat.ChatCompletions;
                return same;
            }

            var target = CreateChannel(kind);
            var defaultOfSource = CreateChannel(source.Kind);

            target.Id = source.Id;
            target.Name = source.Name == defaultOfSource.Name ? target.Name : source.Name;
            target.Enabled = source.Enabled;
            target.Temperature = source.Temperature;
            target.MaxTokens = source.MaxTokens;
            target.EnableAdvanced = source.EnableAdvanced;
            target.EnableStreaming = source.EnableStreaming;
            target.EnableVision = source.EnableVision;
            target.EnableToolCall = source.EnableToolCall;
            target.ThinkingEffort = source.ThinkingEffort;
            target.Mode = source.Mode;
            target.PluginModeId = source.PluginModeId;
            target.ProxyMode = source.ProxyMode;
            target.Priority = source.Priority;
            target.Weight = source.Weight;
            target.Stats = source.Stats;

            var sourceUrl = UrlOf(source);
            if (!string.IsNullOrWhiteSpace(sourceUrl) && sourceUrl != UrlOf(defaultOfSource))
                SetUrl(target, sourceUrl);

            var sourceKey = source switch { OpenAINodeSetting o => o.ApiKey, GeminiNodeSetting g => g.ApiKey, _ => null };
            switch (target)
            {
                case OpenAINodeSetting o: o.ApiKey = sourceKey; break;
                case GeminiNodeSetting g: g.ApiKey = sourceKey; break;
            }

            return target;
        }

        /// <summary>深拷贝一个渠道（含统计）。编辑页在副本上改，点保存才写回。</summary>
        public static ChannelNodeBase CloneChannel(ChannelNodeBase source)
            => (ChannelNodeBase)JsonConvert.DeserializeObject(JsonConvert.SerializeObject(source), source.GetType())!;

        /// <summary>
        /// 把编辑后的副本写回原节点（同类型）。统计不覆盖 —— 编辑期间后台请求可能已经更新了它。
        /// 原地写回而不是替换对象，路由为这个节点缓存的工作 core 才能继续用。
        /// </summary>
        public static void CopyChannelInto(ChannelNodeBase draft, ChannelNodeBase target)
        {
            // 先剔掉再灌：PopulateObject 会复用 target 现有的 Stats 实例、把副本里的旧数往里写
            var json = Newtonsoft.Json.Linq.JObject.FromObject(draft);
            json.Remove(nameof(ChannelNodeBase.Stats));
            json.Remove(nameof(ChannelNodeBase.Id));
            JsonConvert.PopulateObject(json.ToString(Formatting.None), target);
        }

        private static string? UrlOf(ChannelNodeBase node) => node switch
        {
            OpenAINodeSetting o => o.Url,
            GeminiNodeSetting g => g.Url,
            OllamaNodeSetting o => o.Url,
            LMStudioNodeSetting l => l.Url,
            _ => null
        };

        private static void SetUrl(ChannelNodeBase node, string url)
        {
            switch (node)
            {
                case OpenAINodeSetting o: o.Url = url; break;
                case GeminiNodeSetting g: g.Url = url; break;
                case OllamaNodeSetting o: o.Url = url; break;
                case LMStudioNodeSetting l: l.Url = url; break;
            }
        }

        private System.Collections.IList? ListOf(ChannelNodeBase node) => node switch
        {
            OpenAINodeSetting => OpenAI.OpenAINodes,
            GeminiNodeSetting => Gemini.GeminiNodes,
            OllamaNodeSetting => Ollama.OllamaNodes,
            LMStudioNodeSetting => LMStudio.LMStudioNodes,
            FreeNodeSetting => Free.FreeNodes,
            _ => null
        };

        private int NextChannelId()
        {
            var max = 0;
            foreach (var c in EnumerateChannels())
                if (c.Id > max) max = c.Id;
            return max + 1;
        }

        /// <summary>补齐缺失或重复的渠道编号。已有编号不动，保证显示稳定。</summary>
        public void EnsureChannelIds()
        {
            var channels = EnumerateChannels();
            var seen = new HashSet<int>();
            var next = channels.Count == 0 ? 1 : channels.Max(c => c.Id) + 1;
            foreach (var c in channels)
            {
                if (c.Stats is null) c.Stats = new ChannelStats();
                if (c.Id <= 0 || !seen.Add(c.Id))
                {
                    c.Id = next++;
                    seen.Add(c.Id);
                }
            }
        }

        /// <summary>
        /// 优先级最高的启用渠道所属的旧"提供商"，写回 <see cref="Provider"/>。
        ///
        /// <see cref="Provider"/> 已经不参与路由，但仍有少数只读方（诊断报告显示、
        /// 按提供商分开存的聊天历史）需要一个"主渠道"，这里给它们一个确定的答案。
        /// </summary>
        public void SyncPrimaryProvider()
        {
            var primary = EnumerateChannels()
                .Where(c => c.Enabled)
                .OrderByDescending(c => c.Priority)
                .ThenByDescending(c => c.Weight)
                .ThenBy(c => c.Id)
                .FirstOrDefault();
            if (primary is not null)
                Provider = FamilyOf(primary.Kind);
        }

        /// <summary>
        /// FreeChatCore 之外还有不少地方读 FreeSetting 顶层字段（识图前置、诊断），
        /// 它们代表"Free 渠道"。以第一个 Free 节点为准同步过去。
        /// </summary>
        public void SyncFreeContainer()
        {
            var node = Free?.FreeNodes?.FirstOrDefault();
            if (Free is null || node is null) return;

            Free.EnableStreaming = node.EnableStreaming;
            Free.EnableVision = node.EnableVision;
            Free.EnableAdvanced = node.EnableAdvanced;
            Free.EnableToolCall = node.EnableToolCall;
            Free.Temperature = node.Temperature;
            Free.MaxTokens = node.MaxTokens;
            Free.ThinkingEffort = node.ThinkingEffort;
            if (node.Model is not null) Free.Model = node.Model;
        }

        /// <summary>
        /// 一次性把旧的选路规则折算成渠道优先级：
        ///   · 当前提供商的渠道 → 优先级 100（未开负载均衡时，原先固定用的那个是 100，其余 99 往下排）
        ///   · 降级列表里启用的提供商 → 按列表顺序 50、40、30…
        ///   · 其余提供商的渠道 → 停用（它们原本就不会被用到，保持运行时行为不变）
        ///
        /// 旧版的"降级"只有设置界面、运行时从未接上；这里按用户当初的意图把它变成真正生效的备用渠道。
        /// </summary>
        internal bool UnifyChannelsIfNeeded()
        {
            if (ChannelsUnified)
            {
                EnsureChannelIds();
                return false;
            }

            Ollama ??= new OllamaSetting();
            LMStudio ??= new LMStudioSetting();
            Free ??= new FreeSetting();
            Ollama.OllamaNodes ??= new List<OllamaNodeSetting>();
            LMStudio.LMStudioNodes ??= new List<LMStudioNodeSetting>();
            Free.FreeNodes ??= new List<FreeNodeSetting>();

            // 这几种提供商在"没有节点"时会用容器上的旧字段临时造一个节点，统一后必须落成真节点
            if (Ollama.OllamaNodes.Count == 0 && (Provider == LLMType.Ollama || !string.IsNullOrWhiteSpace(Ollama.Model)))
            {
                Ollama.OllamaNodes.Add(new OllamaNodeSetting
                {
                    Url = Ollama.Url,
                    Model = Ollama.Model ?? "",
                    Temperature = Ollama.Temperature,
                    MaxTokens = Ollama.MaxTokens,
                    EnableAdvanced = Ollama.EnableAdvanced,
                    EnableStreaming = Ollama.EnableStreaming,
                    EnableVision = Ollama.EnableVision,
                    EnableToolCall = Ollama.EnableToolCall
                });
            }
            if (LMStudio.LMStudioNodes.Count == 0 && (Provider == LLMType.LMStudio || !string.IsNullOrWhiteSpace(LMStudio.Model)))
            {
                LMStudio.LMStudioNodes.Add(new LMStudioNodeSetting
                {
                    Url = LMStudio.Url,
                    Model = LMStudio.Model ?? "",
                    Temperature = LMStudio.Temperature,
                    MaxTokens = LMStudio.MaxTokens,
                    EnableAdvanced = LMStudio.EnableAdvanced,
                    EnableStreaming = LMStudio.EnableStreaming,
                    EnableVision = LMStudio.EnableVision,
                    EnableToolCall = LMStudio.EnableToolCall
                });
            }
            if (Free.FreeNodes.Count == 0)
            {
                Free.FreeNodes.Add(new FreeNodeSetting
                {
                    Model = Free.Model,
                    Temperature = Free.Temperature,
                    MaxTokens = Free.MaxTokens,
                    EnableAdvanced = Free.EnableAdvanced,
                    EnableStreaming = Free.EnableStreaming,
                    EnableVision = Free.EnableVision,
                    EnableToolCall = Free.EnableToolCall,
                    ThinkingEffort = Free.ThinkingEffort
                });
            }

            // 协议以前靠 URL 猜，现在显式存下来
            foreach (var n in OpenAI.OpenAINodes)
            {
                if (n.Url?.IndexOf("/responses", StringComparison.OrdinalIgnoreCase) >= 0)
                    n.ApiFormat = OpenAIApiFormat.Responses;
            }

            var fallbackOrder = new List<LLMType>();
            if (EnableFallback && FallbackProviders is not null)
            {
                foreach (var fp in FallbackProviders.Where(f => f.IsEnabled).OrderBy(f => f.Priority))
                {
                    if (Enum.TryParse<LLMType>(fp.ProviderType, out var t) && t != Provider && !fallbackOrder.Contains(t))
                        fallbackOrder.Add(t);
                }
            }

            var disabled = 0;
            foreach (LLMType family in Enum.GetValues(typeof(LLMType)))
            {
                var nodes = EnumerateChannels().Where(c => FamilyOf(c.Kind) == family).ToList();
                if (nodes.Count == 0) continue;

                if (family == Provider)
                {
                    var (lb, index) = LoadBalancingOf(family);
                    var enabled = nodes.Where(n => n.Enabled).ToList();
                    for (int i = 0; i < nodes.Count; i++)
                        nodes[i].Priority = lb ? 100 : 90;
                    if (!lb && enabled.Count > 0)
                    {
                        // 未开负载均衡：原先固定用 CurrentNodeIndex 那个，其余只是摆着
                        var chosen = enabled[index >= 0 && index < enabled.Count ? index : 0];
                        chosen.Priority = 100;
                    }
                }
                else if (fallbackOrder.IndexOf(family) is var rank && rank >= 0)
                {
                    foreach (var n in nodes)
                        n.Priority = 50 - rank * 10;
                }
                else
                {
                    foreach (var n in nodes)
                    {
                        if (n.Enabled) disabled++;
                        n.Enabled = false;
                        n.Priority = 0;
                    }
                }
            }

            // 旧版 OpenAI 开着负载均衡时，节点抛异常会转移到同类型的下一个节点。
            // 统一后"失败转移"由 EnableFallback 总控，这里保住那份行为。
            if (Provider == LLMType.OpenAI && OpenAI.EnableLoadBalancing && OpenAI.OpenAINodes.Count(n => n.Enabled) > 1)
                EnableFallback = true;

            EnsureChannelIds();
            SyncFreeContainer();
            ChannelsUnified = true;

            Logger.Log($"渠道统一迁移完成：主提供商 {Provider}，备用 [{string.Join(", ", fallbackOrder)}]，" +
                       $"停用了 {disabled} 个非当前提供商的渠道，失败转移={(EnableFallback ? "开" : "关")}");
            return true;
        }

        private (bool loadBalancing, int currentIndex) LoadBalancingOf(LLMType family) => family switch
        {
            LLMType.OpenAI => (OpenAI.EnableLoadBalancing, OpenAI.CurrentNodeIndex),
            LLMType.Gemini => (Gemini.EnableLoadBalancing, Gemini.CurrentNodeIndex),
            LLMType.Ollama => (Ollama.EnableLoadBalancing, Ollama.CurrentNodeIndex),
            LLMType.LMStudio => (LMStudio.EnableLoadBalancing, LMStudio.CurrentNodeIndex),
            _ => (Free.EnableLoadBalancing, Free.CurrentNodeIndex)
        };
    }
}
