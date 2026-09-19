using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace VPetLLM.UI.Controls
{
    /// <summary>
    /// 渠道表格的一行。只包一层显示逻辑，数据全在 <see cref="Node"/> 上（改了直接生效）。
    /// </summary>
    public sealed class ChannelRow : INotifyPropertyChanged
    {
        private readonly Func<string, string, string> _t;
        private bool _isTesting;

        public ChannelRow(Setting.ChannelNodeBase node, Func<string, string, string> localize)
        {
            Node = node;
            _t = localize;
        }

        public Setting.ChannelNodeBase Node { get; }

        /// <summary>优先级 / 权重在表格里被改动（已写回节点）。</summary>
        public event Action<ChannelRow>? RoutingEdited;

        public event PropertyChangedEventHandler? PropertyChanged;

        // ───── 基本 ─────
        public int Id => Node.Id;
        public string Name => string.IsNullOrWhiteSpace(Node.Name) ? $"#{Node.Id}" : Node.Name;
        public string ModelText => Node is Setting.FreeNodeSetting ? "auto" : (string.IsNullOrWhiteSpace(Node.Model) ? "—" : Node.Model!);
        public string? EndpointText => Node.Endpoint;

        // ───── 类型 ─────
        public string TypeText => TypeLabel(Node.Kind);
        public string TypeBadge => Node.Kind switch
        {
            Setting.ChannelKind.OpenAIChat => "AI",
            Setting.ChannelKind.OpenAIResponses => "AI",
            Setting.ChannelKind.Gemini => "G",
            Setting.ChannelKind.Ollama => "O",
            Setting.ChannelKind.LMStudio => "LM",
            _ => "F"
        };
        public Brush TypeBrush => BrushOf(Node.Kind switch
        {
            Setting.ChannelKind.OpenAIChat => "#10A37F",
            Setting.ChannelKind.OpenAIResponses => "#0E7C66",
            Setting.ChannelKind.Gemini => "#4285F4",
            Setting.ChannelKind.Ollama => "#F97316",
            Setting.ChannelKind.LMStudio => "#6366F1",
            _ => "#16A34A"
        });
        /// <summary>OpenAI 两种协议在徽标下多一行小字区分</summary>
        public string? TypeSubText => Node.Kind switch
        {
            Setting.ChannelKind.OpenAIChat => "Chat",
            Setting.ChannelKind.OpenAIResponses => "Responses",
            _ => null
        };

        public static string TypeLabel(Setting.ChannelKind kind) => kind switch
        {
            Setting.ChannelKind.OpenAIChat => "OpenAI",
            Setting.ChannelKind.OpenAIResponses => "OpenAI",
            Setting.ChannelKind.Gemini => "Gemini",
            Setting.ChannelKind.Ollama => "Ollama",
            Setting.ChannelKind.LMStudio => "LM Studio",
            _ => "Free"
        };

        // ───── 状态 ─────
        public bool Enabled => Node.Enabled;
        public string StatusText => Node.Enabled ? _t("Status.Enabled", "已启用") : _t("Status.Disabled", "已禁用");
        public Brush StatusForeground => BrushOf(Node.Enabled ? "#107C10" : "#D13438");
        public Brush StatusBackground => BrushOf(Node.Enabled ? "#E7F4E7" : "#FBEAEA");
        public string ToggleGlyph => ""; // Power
        public Brush ToggleBrush => BrushOf(Node.Enabled ? "#D13438" : "#107C10");
        public string ToggleTip => Node.Enabled ? _t("Action.Disable", "停用") : _t("Action.Enable", "启用");

        // ───── 用途（对应 NewAPI 的分组）─────
        public string ModeText => Node.Mode switch
        {
            Setting.ChannelMode.ChatOnly => _t("Mode.ChatOnly", "仅聊天"),
            Setting.ChannelMode.CompressionOnly => _t("Mode.CompressionOnly", "仅总结"),
            Setting.ChannelMode.PluginDefined => string.IsNullOrEmpty(Node.PluginModeId) ? _t("Mode.Plugin", "插件") : Node.PluginModeId!,
            _ => _t("Mode.Unrestricted", "全部")
        };
        public Brush ModeBrush => BrushOf(Node.Mode switch
        {
            Setting.ChannelMode.ChatOnly => "#8E44AD",
            Setting.ChannelMode.CompressionOnly => "#C26A00",
            Setting.ChannelMode.PluginDefined => "#605E5C",
            _ => "#0078D4"
        });

        // ───── 路由（可就地编辑）─────
        public int Priority => Node.Priority;
        public int Weight => Node.Weight;

        public string PriorityText
        {
            get => Node.Priority.ToString();
            set
            {
                if (!int.TryParse(value?.Trim(), out var v) || v == Node.Priority) { OnPropertyChanged(); return; }
                Node.Priority = v;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Priority));
                RoutingEdited?.Invoke(this);
            }
        }

        public string WeightText
        {
            get => Node.Weight.ToString();
            set
            {
                if (!int.TryParse(value?.Trim(), out var v) || v < 0 || v == Node.Weight) { OnPropertyChanged(); return; }
                Node.Weight = v;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Weight));
                RoutingEdited?.Invoke(this);
            }
        }

        // ───── 用量（本地统计）─────
        private Setting.ChannelStats Stats => Node.Stats ??= new Setting.ChannelStats();
        public long RequestCount => Stats.RequestCount;
        public string UsageText => string.Format(_t("Usage.Requests", "{0} 次"), Stats.RequestCount);
        public string UsageDetailText
        {
            get
            {
                var parts = new List<string>();
                if (Stats.FailureCount > 0) parts.Add(string.Format(_t("Usage.Failures", "失败 {0}"), Stats.FailureCount));
                if (Stats.TotalTokens > 0) parts.Add(FormatTokens(Stats.TotalTokens) + " tok");
                return parts.Count == 0 ? "" : string.Join(" · ", parts);
            }
        }
        public bool HasUsageDetail => UsageDetailText.Length > 0;
        public Brush UsageDetailBrush => BrushOf(Stats.FailureCount > 0 && Stats.FailureCount * 5 >= Stats.RequestCount ? "#D13438" : "#605E5C");

        // ───── 响应（最近一次测试）─────
        public bool IsTesting
        {
            get => _isTesting;
            set { _isTesting = value; RefreshAll(); }
        }

        /// <summary>排序用：未测试排最后，失败次之。</summary>
        public long LatencySort => Stats.LastTestOk switch
        {
            true => Stats.LastTestLatencyMs ?? long.MaxValue - 2,
            false => long.MaxValue - 1,
            _ => long.MaxValue
        };

        public string LatencyText
        {
            get
            {
                if (_isTesting) return _t("Test.Running", "测试中…");
                return Stats.LastTestOk switch
                {
                    true => string.Format(_t("Latency.Ms", "{0} 毫秒"), Stats.LastTestLatencyMs ?? 0),
                    false => _t("Latency.Failed", "失败"),
                    _ => _t("Latency.Untested", "未测试")
                };
            }
        }

        public Brush LatencyBrush
        {
            get
            {
                if (_isTesting) return BrushOf("#0078D4");
                if (Stats.LastTestOk == false) return BrushOf("#D13438");
                if (Stats.LastTestOk != true) return BrushOf("#A19F9D");
                var ms = Stats.LastTestLatencyMs ?? 0;
                return BrushOf(ms < 1500 ? "#107C10" : ms < 4000 ? "#C26A00" : "#D13438");
            }
        }

        public string? LatencyTip => Stats.LastTestOk == false ? Stats.LastTestError : null;

        public long LastTestSort => Stats.LastTestedAt?.Ticks ?? 0;
        public string LastTestText => Stats.LastTestedAt is { } t ? Relative(t) : "—";

        // ───── 刷新 ─────
        public void RefreshAll() => OnPropertyChanged(string.Empty);

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private string Relative(DateTime time)
        {
            var span = DateTime.Now - time;
            if (span.TotalMinutes < 1) return _t("Time.JustNow", "刚刚");
            if (span.TotalHours < 1) return string.Format(_t("Time.MinutesAgo", "{0} 分钟前"), (int)span.TotalMinutes);
            if (span.TotalDays < 1) return string.Format(_t("Time.HoursAgo", "{0} 小时前"), (int)span.TotalHours);
            return string.Format(_t("Time.DaysAgo", "{0} 天前"), (int)span.TotalDays);
        }

        private static string FormatTokens(long n) => n switch
        {
            >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
            >= 1_000 => $"{n / 1_000.0:0.#}K",
            _ => n.ToString()
        };

        private static readonly Dictionary<string, SolidColorBrush> BrushCache = new();

        private static Brush BrushOf(string hex)
        {
            lock (BrushCache)
            {
                if (!BrushCache.TryGetValue(hex, out var brush))
                {
                    brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                    brush.Freeze();
                    BrushCache[hex] = brush;
                }
                return brush;
            }
        }
    }
}
