using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using VPetLLM.Core.Abstractions.Base;
using VPetLLM.Core.Data.Database;
using VPetLLM.Core.Services.MemoryGraph;
using VPetLLM.UI.Controls;
using VPetLLM.Utils.Localization;

namespace VPetLLM.UI.Windows
{
    public class RecordEditorItem
    {
        public ImportantRecord OriginalRecord { get; }
        public int Id => OriginalRecord.Id;
        public string Content { get; set; }
        public int Weight { get; set; }
        public DateTime CreatedAt => OriginalRecord.CreatedAt;

        public RecordEditorItem(ImportantRecord originalRecord)
        {
            OriginalRecord = originalRecord;
            Content = originalRecord.Content;
            Weight = originalRecord.DisplayWeight;
        }

        /// <summary>
        /// 用户真的改过内容或权重。
        ///
        /// 分页之后只能按需回写：翻过的每一页都无差别 UpdateRecord 一遍，
        /// 等于把没碰过的记录也刷一次 updated_at。
        /// </summary>
        public bool IsDirty => Content != OriginalRecord.Content
                            || Weight != OriginalRecord.DisplayWeight;
    }

    /// <summary>
    /// Display model for overflow summary rows.
    /// </summary>
    public class OverflowSummaryItem
    {
        public int Id { get; set; }
        public string SummaryText { get; set; } = "";
        /// <summary>加载时的原始文本，保存时用于判断是否被编辑过</summary>
        public string OriginalSummaryText { get; set; } = "";
        public int SegmentStartIndex { get; set; }
        public int SegmentEndIndex { get; set; }
        public int TokenCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Kind { get; set; } = OverflowSummaryKind.Rolling;

        /// <summary>还在注入提示词的那批条目里。已合并、空片、被新行替换掉的旧版行都是 false。</summary>
        public bool IsActive { get; set; } = true;

        public string KindDisplay { get; set; } = "";

        public string RangeDisplay => $"[{SegmentStartIndex}..{SegmentEndIndex}]";
    }

    /// <summary>星云详情面板里的一行</summary>
    public sealed record NebulaListItem(string Text, string Meta);

    public partial class winRecordEditor : Window
    {
        /// <summary>每页条数。两张表统一，和上下文编辑器的分页模式对齐。</summary>
        private const int PageSize = 40;

        /// <summary>一张表的分页状态</summary>
        private sealed class PagerState
        {
            public int PageIndex;
            public int TotalCount;
            public int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
            public void Clamp() => PageIndex = Math.Clamp(PageIndex, 0, PageCount - 1);
        }

        private readonly VPetLLM _plugin;

        /// <summary>当前页的行，绑给表格</summary>
        public ObservableCollection<RecordEditorItem> DisplayRecords { get; set; } = new();
        public ObservableCollection<OverflowSummaryItem> DisplayOverflowSummaries { get; set; } = new();

        /// <summary>
        /// 翻过的每一行都留在这里（按 Id 索引），翻回去时复用同一个实例。
        ///
        /// 不这么做的话，改了第 1 页翻到第 2 页再翻回来，第 1 页会被重新从库里读出来，
        /// 还没保存的编辑就没了。保存时也是遍历它，而不是只看当前这一页。
        /// </summary>
        private readonly Dictionary<int, RecordEditorItem> _recordCache = new();
        private readonly Dictionary<int, OverflowSummaryItem> _summaryCache = new();

        private readonly PagerState _recordsPager = new();
        private readonly PagerState _overflowPager = new();

        public winRecordEditor(VPetLLM plugin)
        {
            InitializeComponent();
            _plugin = plugin;

            DataContext = this;
            DataGrid_Overflow.ItemsSource = DisplayOverflowSummaries;

            // 行编辑提交后刷一次底栏的"待保存"计数。RowEditEnding 在写回绑定之前就抛出来了，
            // 所以推到下一轮再读，否则数出来的是改动前的状态。
            DataGrid_Records.RowEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateDirtyHint));
            DataGrid_Overflow.RowEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateDirtyHint));

            // "删除选中"没选中就点不动——留着能点却什么都不发生比禁用更让人困惑
            DataGrid_Records.SelectionChanged += (_, _) =>
                Button_Delete.IsEnabled = DataGrid_Records.SelectedItem is RecordEditorItem;
            DataGrid_Overflow.SelectionChanged += (_, _) =>
                Button_Overflow_Delete.IsEnabled = DataGrid_Overflow.SelectedItem is OverflowSummaryItem;

            // 不生效的行只给看不给改：改了也不进提示词，反倒让人以为改动起了作用
            DataGrid_Overflow.BeginningEdit += (_, e) =>
            {
                if (e.Row.Item is OverflowSummaryItem { IsActive: false })
                    e.Cancel = true;
            };

            // 星云图第一次切过去才算：拆原子、跑布局都要时间，大多数人打开窗口只是来改记忆的。
            // 页签里的 DataGrid 等子控件的 SelectionChanged 也会冒泡到这里，只认 TabControl 自己发的
            TabControl_Main.SelectionChanged += (_, e) =>
            {
                if (e.OriginalSource == TabControl_Main && TabControl_Main.SelectedItem == TabItem_Nebula && _nebulaStale)
                    ReloadNebula(reloadData: true);
            };

            UpdateUIForLanguage();

            LoadRecordsPage(0);
            LoadOverflowPage(0);
        }

        // ── 分页 ────────────────────────────────────────────────────────────

        private void LoadRecordsPage(int page)
        {
            var manager = _plugin.ChatCore?.RecordManager;

            _recordsPager.TotalCount = manager?.GetRecordCountForEditing() ?? 0;
            _recordsPager.PageIndex = page;
            _recordsPager.Clamp();

            // 提交正在编辑的单元格再换页，否则那一格的输入会随着行被换掉而丢
            DataGrid_Records.CommitEdit(DataGridEditingUnit.Row, true);
            DisplayRecords.Clear();

            var records = manager?.GetRecordsPageForEditing(_recordsPager.PageIndex * PageSize, PageSize)
                          ?? new List<ImportantRecord>();
            foreach (var record in records)
            {
                if (!_recordCache.TryGetValue(record.Id, out var item))
                {
                    item = new RecordEditorItem(record);
                    _recordCache[record.Id] = item;
                }
                DisplayRecords.Add(item);
            }

            UpdateRecordsPagerUi();
        }

        private void LoadOverflowPage(int page)
        {
            var manager = (_plugin.ChatCore as ChatCoreBase)?.OverflowManager;
            var includeInactive = CheckBox_Overflow_ShowInactive.IsChecked == true;

            _overflowPager.TotalCount = manager?.GetSummaryCount(includeInactive) ?? 0;
            _overflowPager.PageIndex = page;
            _overflowPager.Clamp();

            DataGrid_Overflow.CommitEdit(DataGridEditingUnit.Row, true);
            DisplayOverflowSummaries.Clear();

            var summaries = manager?.GetSummariesPage(_overflowPager.PageIndex * PageSize, PageSize, includeInactive)
                            ?? new List<OverflowSummaryRecord>();
            var active = manager?.ActiveSummaryIds;
            foreach (var summary in summaries)
            {
                if (!_summaryCache.TryGetValue(summary.Id, out var item))
                {
                    item = new OverflowSummaryItem
                    {
                        Id = summary.Id,
                        SummaryText = summary.SummaryText,
                        OriginalSummaryText = summary.SummaryText,
                        SegmentStartIndex = summary.SegmentStartIndex,
                        SegmentEndIndex = summary.SegmentEndIndex,
                        TokenCount = summary.TokenCount,
                        CreatedAt = summary.CreatedAt
                    };
                    _summaryCache[summary.Id] = item;
                }

                // 生效与否每次重算：两次翻页之间后台可能刚做完一次合并
                item.Kind = summary.Kind;
                item.IsActive = !summary.Superseded && summary.Kind != OverflowSummaryKind.Empty
                                && (active is null || active.Contains(summary.Id));
                item.KindDisplay = KindDisplayOf(item);
                DisplayOverflowSummaries.Add(item);
            }

            UpdateOverflowPagerUi();
        }

        private string KindDisplayOf(OverflowSummaryItem item)
        {
            if (item.Kind == OverflowSummaryKind.Empty)
                return L("RecordEditor.KindEmpty", "空片");
            if (!item.IsActive)
                return item.Kind == OverflowSummaryKind.Rolling
                    ? L("RecordEditor.KindReplaced", "已替换")
                    : L("RecordEditor.KindSuperseded", "已合并");
            return item.Kind switch
            {
                OverflowSummaryKind.Segment => L("RecordEditor.KindSegment", "分段"),
                OverflowSummaryKind.Consolidated => L("RecordEditor.KindConsolidated", "合并"),
                _ => L("RecordEditor.KindRolling", "旧版")
            };
        }

        private void CheckBox_Overflow_ShowInactive_Click(object sender, RoutedEventArgs e) => LoadOverflowPage(0);

        private void UpdateRecordsPagerUi()
        {
            Text_Records_Page.Text = string.Format(_locPageInfo, _recordsPager.PageIndex + 1, _recordsPager.PageCount);
            Text_Records_Count.Text = string.Format(_locTotalCount, _recordsPager.TotalCount);

            // 数量挂在页签上：不用点进去就知道两边各有多少
            Text_Tab_Records.Text = $"{_locTabRecords} · {_recordsPager.TotalCount}";

            Button_Records_First.IsEnabled = Button_Records_Prev.IsEnabled = _recordsPager.PageIndex > 0;
            Button_Records_Next.IsEnabled = Button_Records_Last.IsEnabled =
                _recordsPager.PageIndex < _recordsPager.PageCount - 1;

            // 换页会清掉选中，删除按钮跟着回到禁用
            Button_Delete.IsEnabled = DataGrid_Records.SelectedItem is RecordEditorItem;
            UpdateDirtyHint();
        }

        private void UpdateOverflowPagerUi()
        {
            Text_Overflow_Page.Text = string.Format(_locPageInfo, _overflowPager.PageIndex + 1, _overflowPager.PageCount);
            Text_Overflow_Count.Text = string.Format(_locTotalCount, _overflowPager.TotalCount);

            Text_Tab_Overflow.Text = $"{_locTabOverflow} · {_overflowPager.TotalCount}";

            Button_Overflow_First.IsEnabled = Button_Overflow_Prev.IsEnabled = _overflowPager.PageIndex > 0;
            Button_Overflow_Next.IsEnabled = Button_Overflow_Last.IsEnabled =
                _overflowPager.PageIndex < _overflowPager.PageCount - 1;

            Button_Overflow_Delete.IsEnabled = DataGrid_Overflow.SelectedItem is OverflowSummaryItem;
            Button_Overflow_ClearAll.IsEnabled = _overflowPager.TotalCount > 0;
            UpdateDirtyHint();
        }

        /// <summary>
        /// 底栏的"N 项待保存"。
        ///
        /// 分页之后改动散在好几页上，光看当前页看不出还有没有没存的东西——
        /// 这一行就是补这个视野。
        /// </summary>
        private void UpdateDirtyHint()
        {
            var dirty = _recordCache.Values.Count(r => r.IsDirty)
                      + _summaryCache.Values.Count(s => s.SummaryText != s.OriginalSummaryText);

            Text_DirtyHint.Text = dirty == 0 ? "" : string.Format(_locDirtyHint, dirty);
        }

        private void RecordsFirstPage_Click(object sender, RoutedEventArgs e) => LoadRecordsPage(0);
        private void RecordsPrevPage_Click(object sender, RoutedEventArgs e) => LoadRecordsPage(_recordsPager.PageIndex - 1);
        private void RecordsNextPage_Click(object sender, RoutedEventArgs e) => LoadRecordsPage(_recordsPager.PageIndex + 1);
        private void RecordsLastPage_Click(object sender, RoutedEventArgs e) => LoadRecordsPage(_recordsPager.PageCount - 1);

        private void OverflowFirstPage_Click(object sender, RoutedEventArgs e) => LoadOverflowPage(0);
        private void OverflowPrevPage_Click(object sender, RoutedEventArgs e) => LoadOverflowPage(_overflowPager.PageIndex - 1);
        private void OverflowNextPage_Click(object sender, RoutedEventArgs e) => LoadOverflowPage(_overflowPager.PageIndex + 1);
        private void OverflowLastPage_Click(object sender, RoutedEventArgs e) => LoadOverflowPage(_overflowPager.PageCount - 1);

        private void Button_Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 提交尚未结束编辑状态的单元格，否则正在编辑的内容不会写入绑定对象
                DataGrid_Records.CommitEdit(DataGridEditingUnit.Row, true);
                DataGrid_Overflow.CommitEdit(DataGridEditingUnit.Row, true);

                // 遍历缓存而不是当前页：用户可能在好几页上都改过东西
                foreach (var item in _recordCache.Values)
                {
                    item.Weight = Math.Clamp(item.Weight, 0, 10);
                    if (!item.IsDirty) continue;

                    item.OriginalRecord.Content = item.Content;
                    item.OriginalRecord.Weight = item.Weight;
                    _plugin.ChatCore?.RecordManager?.UpdateRecord(item.OriginalRecord);
                }

                // 持久化被编辑过的溢出总结（经 OverflowManager 同步内存中的滚动总结）
                var overflowMgr = (_plugin.ChatCore as ChatCoreBase)?.OverflowManager;
                foreach (var item in _summaryCache.Values)
                {
                    if (item.SummaryText == item.OriginalSummaryText)
                        continue;

                    if (overflowMgr is not null)
                    {
                        overflowMgr.UpdateSummaryText(item.Id, item.SummaryText);
                    }
                    else
                    {
                        using var overflowDb = new OverflowDatabase(GetDatabasePath());
                        overflowDb.UpdateSummaryText(item.Id, item.SummaryText);
                    }
                    item.OriginalSummaryText = item.SummaryText;
                }

                MessageBox.Show(
                    LanguageHelper.Get("RecordEditor.SaveSuccess", _plugin.Settings.Language),
                    LanguageHelper.Get("RecordEditor.Title", _plugin.Settings.Language),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                Close();
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save records: {ex.Message}");
                MessageBox.Show($"Failed to save records: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Button_Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Button_Delete_Click(object sender, RoutedEventArgs e)
        {
            if (DataGrid_Records.SelectedItem is RecordEditorItem selectedItem)
            {
                var result = MessageBox.Show(
                    LanguageHelper.Get("RecordEditor.DeleteConfirm", _plugin.Settings.Language),
                    LanguageHelper.Get("RecordEditor.Delete", _plugin.Settings.Language),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    var database = new ImportantRecordsDatabase(GetDatabasePath());
                    database.DeleteRecord(selectedItem.Id);
                    _recordCache.Remove(selectedItem.Id);
                    _nebulaStale = true;

                    // 删掉一条会让后面所有行往前挪一格，本页得整页重取，
                    // 否则末尾会空出一行、而下一页的首行永远看不到
                    LoadRecordsPage(_recordsPager.PageIndex);
                }
            }
        }

        private void Button_OverflowDelete_Click(object sender, RoutedEventArgs e)
        {
            if (DataGrid_Overflow.SelectedItem is OverflowSummaryItem selected)
            {
                var result = MessageBox.Show(
                    $"确认删除溢出总结 #{selected.Id}？\n范围: {selected.RangeDisplay}",
                    "删除溢出总结",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    // 经 OverflowManager 删除，同步回滚内存中的滚动总结和检查点
                    var overflowMgr = (_plugin.ChatCore as ChatCoreBase)?.OverflowManager;
                    if (overflowMgr is not null)
                    {
                        overflowMgr.DeleteSummary(selected.Id);
                    }
                    else
                    {
                        using var overflowDb = new OverflowDatabase(GetDatabasePath());
                        overflowDb.DeleteSummary(selected.Id);
                    }
                    _summaryCache.Remove(selected.Id);
                    _nebulaStale = true;
                    LoadOverflowPage(_overflowPager.PageIndex);
                }
            }
        }

        private void Button_OverflowClearAll_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "确认清空所有溢出总结？此操作不可撤销。",
                "清空溢出总结",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                (_plugin.ChatCore as ChatCoreBase)?.OverflowManager?.ClearAll();
                _summaryCache.Clear();
                _nebulaStale = true;
                LoadOverflowPage(0);
            }
        }

        // ── 关系星云 ────────────────────────────────────────────────────────

        private MemoryNebulaView? _nebula;
        private MemoryGraphData? _nebulaData;
        private NebulaGraph _nebulaGraph = NebulaGraph.Empty;
        private bool _nebulaStale = true;
        private int _nebulaRequest;
        private int _nebulaSeed = 7;

        /// <summary>
        /// 读数据（可选）+ 跑布局，全部在后台线程做完再交给控件。
        /// 几百个节点 × 300 轮力导向在 UI 线程上跑会让宠物动画卡住。
        /// </summary>
        private async void ReloadNebula(bool reloadData)
        {
            if (_nebula is null)
            {
                _nebula = new MemoryNebulaView();
                _nebula.SelectionChanged += (_, node) => ShowNebulaDetail(node);
                Border_NebulaHost.Child = _nebula;
            }

            var manager = (_plugin.ChatCore as ChatCoreBase)?.OverflowManager;
            if (manager?.MemoryGraph is null)
            {
                _nebulaData = null;
                _nebulaGraph = NebulaGraph.Empty;
                _nebula.SetGraph(null);
                Text_Nebula_Stats.Text = "";
                Text_Nebula_Status.Text = L("RecordEditor.NebulaUnavailable",
                    "需要开启长上下文（溢出总结）才会整理出关系星云。");
                return;
            }

            // 连点「重新布局」时只认最后一次的结果
            var request = ++_nebulaRequest;
            var cached = reloadData ? null : _nebulaData;
            var records = _plugin.ChatCore?.RecordManager;
            var options = new NebulaOptions { IncludeFacts = CheckBox_Nebula_ShowFacts.IsChecked == true, Seed = _nebulaSeed };

            Text_Nebula_Status.Text = L("RecordEditor.NebulaLoading", "正在整理记忆…");
            Button_Nebula_Relayout.IsEnabled = false;
            CheckBox_Nebula_ShowFacts.IsEnabled = false;
            try
            {
                var (data, graph) = await Task.Run(() =>
                {
                    var loaded = cached ?? manager.LoadMemoryGraphData(records);
                    return (loaded, loaded is null ? NebulaGraph.Empty : MemoryGraphBuilder.Build(loaded, DateTime.UtcNow, options));
                });
                if (request != _nebulaRequest)
                    return;

                _nebulaData = data;
                _nebulaGraph = graph;
                _nebulaStale = false;
                _nebula.SetGraph(graph);

                Text_Nebula_Status.Text = graph.Nodes.Count == 0
                    ? L("RecordEditor.NebulaEmpty", "还没有可画的记忆。聊得足够长、触发过溢出总结之后，这里会慢慢亮起来。")
                    : "";
                Text_Nebula_Stats.Text = graph.Nodes.Count == 0
                    ? ""
                    : string.Format(L("RecordEditor.NebulaStats", "{0} 个实体 · {1} 条事实 · {2} 个社区"),
                        graph.EntityCount, graph.LiveAtomCount, graph.CommunityCount);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to build memory nebula: {ex.Message}");
                if (request == _nebulaRequest)
                    Text_Nebula_Status.Text = L("RecordEditor.NebulaFailed", "星云图加载失败，详情见日志。");
            }
            finally
            {
                if (request == _nebulaRequest)
                {
                    Button_Nebula_Relayout.IsEnabled = true;
                    CheckBox_Nebula_ShowFacts.IsEnabled = true;
                }
            }
        }

        private void ShowNebulaDetail(NebulaNode? node)
        {
            var now = DateTime.UtcNow;
            var items = new List<NebulaListItem>();

            if (node?.Entity is { } entity && _nebulaData is { } data)
            {
                var atoms = data.Atoms
                    .Where(a => a.EntityIds.Contains(entity.Id) && MemoryAtomScoring.StatusOf(a, now) != MemoryAtomStatus.Expired)
                    .OrderByDescending(a => MemoryAtomScoring.Decay(a, now) * (0.5 + a.Importance))
                    .ToList();

                Text_Nebula_Title.Text = entity.Label;
                Text_Nebula_Meta.Text = string.Format(L("RecordEditor.NebulaEntityMeta", "{0} · {1} 条相关记忆"),
                    entity.Type == MemoryEntityType.Person ? L("RecordEditor.NebulaPerson", "人物") : L("RecordEditor.NebulaTopic", "话题"),
                    atoms.Count);
                Text_Nebula_ListHeader.Text = L("RecordEditor.NebulaRelatedFacts", "相关记忆");
                items.AddRange(atoms.Take(60).Select(a => new NebulaListItem(FactText(a), AtomMeta(a, now))));
            }
            else if (node?.Atom is { } atom && _nebulaData is { } factData)
            {
                Text_Nebula_Title.Text = FactText(atom);
                Text_Nebula_Meta.Text = AtomMeta(atom, now);
                Text_Nebula_ListHeader.Text = L("RecordEditor.NebulaInvolves", "涉及");
                foreach (var id in atom.EntityIds)
                {
                    if (factData.Entities.TryGetValue(id, out var linked))
                        items.Add(new NebulaListItem(linked.Label,
                            linked.Type == MemoryEntityType.Person ? L("RecordEditor.NebulaPerson", "人物") : L("RecordEditor.NebulaTopic", "话题")));
                }
            }
            else
            {
                // 没选中时列出分量最重的实体，给个入口
                Text_Nebula_Title.Text = L("RecordEditor.NebulaOverview", "记忆概览");
                Text_Nebula_Meta.Text = L("RecordEditor.NebulaPickHint", "点一颗星查看它关联的记忆，再点一次取消。");
                Text_Nebula_ListHeader.Text = _nebulaGraph.Nodes.Count == 0 ? "" : L("RecordEditor.NebulaTopEntities", "分量最重的实体");
                items.AddRange(_nebulaGraph.Nodes
                    .Where(n => n.Kind == NebulaNodeKind.Entity)
                    .OrderByDescending(n => n.Weight)
                    .Take(15)
                    .Select(n => new NebulaListItem(n.Label,
                        string.Format(L("RecordEditor.NebulaFactCount", "{0} 条相关记忆"), n.FactCount))));
            }

            List_Nebula_Items.ItemsSource = items;
        }

        private static string FactText(MemoryAtom atom) => atom.Content.Trim().TrimStart('-', '*', '•', ' ');

        private string AtomMeta(MemoryAtom atom, DateTime now)
        {
            var type = atom.Type switch
            {
                MemoryAtomType.Episodic => L("RecordEditor.AtomEpisodic", "事件"),
                MemoryAtomType.Planned => L("RecordEditor.AtomPlanned", "计划"),
                MemoryAtomType.Factual => L("RecordEditor.AtomFactual", "事实"),
                MemoryAtomType.Relational => L("RecordEditor.AtomRelational", "关系"),
                MemoryAtomType.Preference => L("RecordEditor.AtomPreference", "偏好"),
                _ => L("RecordEditor.AtomUnknown", "其他")
            };
            var status = MemoryAtomScoring.StatusOf(atom, now) == MemoryAtomStatus.Fading
                ? L("RecordEditor.AtomFading", "渐淡") + " · "
                : "";
            var source = atom.SourceKind == MemoryAtomSource.Record ? " · " + L("RecordEditor.AtomFromRecord", "重要记忆") : "";
            var days = (int)Math.Floor((now - atom.LastSeenAt).TotalDays);
            var seen = days <= 0
                ? L("RecordEditor.SeenToday", "今天")
                : string.Format(L("RecordEditor.SeenDaysAgo", "{0} 天前"), days);
            var mentions = atom.Reinforcement > 0
                ? " · " + string.Format(L("RecordEditor.AtomMentions", "提到 {0} 次"), atom.Reinforcement + 1)
                : "";
            return $"{type} · {status}{seen}{mentions}{source}";
        }

        private void CheckBox_Nebula_ShowFacts_Click(object sender, RoutedEventArgs e) => ReloadNebula(reloadData: false);

        private void Button_Nebula_Relayout_Click(object sender, RoutedEventArgs e)
        {
            // 换个种子，同一份数据摆出另一种排布；顺带重读一次，后台可能刚总结完新内容
            _nebulaSeed++;
            ReloadNebula(reloadData: true);
        }

        private string L(string key, string fallback) => LanguageHelper.Get(key, _plugin.Settings.Language, fallback);

        // 带占位符或要拼接的文案，取一次留着用
        private string _locPageInfo = "第 {0} / {1} 页";
        private string _locTotalCount = "共 {0} 条";
        private string _locDirtyHint = "{0} 项待保存";
        private string _locTabRecords = "重要记忆";
        private string _locTabOverflow = "溢出总结";

        private void UpdateUIForLanguage()
        {
            var langCode = _plugin.Settings.Language;

            // 一律走带兜底的三参重载：两参版本查不到键会返回 "[键名]"，
            // 那玩意儿丢进 string.Format 就把占位符一起吞了
            string T(string key, string fallback) => LanguageHelper.Get(key, langCode, fallback);

            Title = T("RecordEditor.WindowTitle", "记忆管理");

            _locPageInfo = T("RecordEditor.PageInfo", "第 {0} / {1} 页");
            _locTotalCount = T("RecordEditor.TotalCount", "共 {0} 条");
            _locDirtyHint = T("RecordEditor.DirtyHint", "{0} 项待保存");
            _locTabRecords = T("RecordEditor.Title", "重要记忆");
            _locTabOverflow = T("RecordEditor.OverflowTitle", "溢出总结");

            Text_Tab_Records.Text = _locTabRecords;
            Text_Tab_Overflow.Text = _locTabOverflow;

            Text_Records_Hint.Text = T("RecordEditor.RecordsHint",
                "桌宠长期记住的事实，每轮对话都会随提示词一起带上。权重越高越优先保留。");
            Text_Overflow_Hint.Text = T("RecordEditor.OverflowHint",
                "上下文超出长度上限时，被压缩归档的旧对话。删除会连带回滚对应的检查点。");

            // 列上带 x:Name，XAML 已经生成了同名字段，直接赋值即可
            Column_Id.Header = T("RecordEditor.Id", "ID");
            Column_Content.Header = T("RecordEditor.Content", "内容");
            Column_Weight.Header = T("RecordEditor.Weight", "权重");
            Column_CreatedAt.Header = T("RecordEditor.CreatedAt", "创建时间");

            Column_Overflow_Id.Header = T("RecordEditor.Id", "ID");
            Column_Overflow_Summary.Header = T("RecordEditor.OverflowSummary", "总结内容");
            Column_Overflow_Status.Header = T("RecordEditor.OverflowStatus", "状态");
            Column_Overflow_Range.Header = T("RecordEditor.OverflowRange", "消息范围");
            Column_Overflow_Tokens.Header = T("RecordEditor.OverflowTokens", "Token");
            Column_Overflow_CreatedAt.Header = T("RecordEditor.CreatedAt", "创建时间");

            Button_Save.Content = T("RecordEditor.Save", "保存");
            Button_Cancel.Content = T("RecordEditor.Cancel", "取消");
            Button_Delete.Content = T("RecordEditor.Delete", "删除选中");
            Button_Overflow_Delete.Content = T("RecordEditor.Delete", "删除选中");
            Button_Overflow_ClearAll.Content = T("RecordEditor.ClearAll", "全部清空");
            CheckBox_Overflow_ShowInactive.Content = T("RecordEditor.ShowInactive", "显示已合并/空片");

            Text_Tab_Nebula.Text = T("RecordEditor.NebulaTitle", "关系星云");
            Text_Nebula_Hint.Text = T("RecordEditor.NebulaHint",
                "从溢出总结和重要记忆里拆出的人物、话题与事实。同色是一个社区；拖动平移、滚轮缩放、双击复位。");
            CheckBox_Nebula_ShowFacts.Content = T("RecordEditor.NebulaShowFacts", "显示事实");
            Button_Nebula_Relayout.Content = T("RecordEditor.NebulaRelayout", "重新布局");

            Button_Records_First.ToolTip = Button_Overflow_First.ToolTip = T("RecordEditor.FirstPage", "第一页");
            Button_Records_Prev.ToolTip = Button_Overflow_Prev.ToolTip = T("RecordEditor.PrevPage", "上一页");
            Button_Records_Next.ToolTip = Button_Overflow_Next.ToolTip = T("RecordEditor.NextPage", "下一页");
            Button_Records_Last.ToolTip = Button_Overflow_Last.ToolTip = T("RecordEditor.LastPage", "最后一页");
        }

        private string GetDatabasePath()
        {
            var docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dataPath = System.IO.Path.Combine(docPath, "VPetLLM", "Chat");
            return System.IO.Path.Combine(dataPath, "chat_history.db");
        }
    }
}
