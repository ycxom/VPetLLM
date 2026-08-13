using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VPetLLM.Utils.Localization;

namespace VPetLLM.UI.Windows
{
    /// <summary>
    /// 上下文编辑器（Open WebUI 风格）。
    ///
    /// 两种模式：
    /// * 简洁模式 —— 渲染 AI 的回复格式，台词直接可改，动作/插件调用以只读芯片呈现；
    /// * 完全模式 —— 一律不渲染，原样给出模型返回的整段文本供直接编辑。
    ///
    /// 系统提示词（人设、能力清单、状态注入等）不进入本窗口：它由 ChatCoreBase
    /// 在每次请求时现生成，本就不在历史里；万一历史中混入了 system 消息，
    /// 这里也只是原样保管、既不显示也不改动。
    /// </summary>
    public partial class winContextEditor : Window, INotifyPropertyChanged
    {
        private readonly VPetLLM _plugin;
        private readonly string _langCode;

        /// <summary>历史中的 system 消息：不展示、不编辑，保存时原样放回最前面</summary>
        private readonly List<Message> _systemMessages;

        /// <summary>打开编辑器那一刻的内容快照，用于判断是否有未保存改动</summary>
        private readonly int _snapshotCount;
        private readonly CancellationTokenSource _loadCts = new();
        private bool _closing;

        private bool _initialized;
        private bool _saved;

        /// <summary>每页条数；同时也是连续模式下每次向上补足的条数、以及单次取数的粒度</summary>
        private const int PageSize = 40;

        /// <summary>离顶端还剩这么多像素时就开始补更早的消息，别等真的顶到 0</summary>
        private const double LoadOlderThreshold = 160;

        /// <summary>
        /// 全部消息，按最终顺序排列。未进入窗口的只是空壳（<see cref="ContextEditorItem.IsLoaded"/> 为 false）。
        ///
        /// 不变式：下标 &lt; <see cref="_dbCount"/> 的项与数据库里的第 N 条一一对应——
        /// 新增只往末尾追加、删除只打标记不摘除，所以这个对应关系不会错位。
        /// <see cref="EnsureRangeLoadedAsync"/> 全靠它把取回来的消息放对位置。
        /// </summary>
        private readonly List<ContextEditorItem> _allItems = new();

        /// <summary>数据库里可编辑消息的条数（不含 system）</summary>
        private readonly int _dbCount;

        /// <summary>当前窗口，即真正交给 ListBox 的那一段 <see cref="_allItems"/></summary>
        public ObservableCollection<ContextEditorItem> Items { get; } = new();

        /// <summary>true = 分页模式；false = 连续模式（向上滚动续载）</summary>
        private bool _paged;

        /// <summary>连续模式：窗口首条在 <see cref="_allItems"/> 中的下标，窗口一直延伸到末尾</summary>
        private int _windowStart;

        /// <summary>分页模式：当前页号，从 0 起</summary>
        private int _pageIndex;

        private bool _loadingOlder;

        /// <summary>
        /// 换窗口/保存期间要屏蔽掉容器的 Unloaded。
        ///
        /// 换页时 Items.Clear() 会让所有容器卸载，但那是集合被换掉的副作用，
        /// 不是"滚出视口"；照着卸下去会把刚重新放进窗口的项又清空一遍。
        /// </summary>
        private bool _suppressUnload;

        /// <summary>
        /// 浏览位置缓存，只在窗口存活期间有效。
        ///
        /// 记的是"刚才看到哪儿"，不是用户设置——关掉编辑器就清空，
        /// 下次打开照旧停在最新消息上。
        /// </summary>
        private sealed class ViewPosition
        {
            /// <summary>正在看的那一条在 _allItems 中的下标；-1 表示还没记到过</summary>
            public int AnchorIndex = -1;

            /// <summary>锚点那一条的顶边相对视口顶部的位置（通常 ≤ 0，即已经滚过去一截），还原到像素靠它</summary>
            public double AnchorTop;

            /// <summary>连续模式上次的窗口起点：切回来时沿用，省得把之前一路往上展开的那段白丢掉</summary>
            public int ContinuousStart;

            /// <summary>分页模式上次停在第几页</summary>
            public int PageIndex;

            public void Clear()
            {
                AnchorIndex = -1;
                AnchorTop = 0;
                ContinuousStart = 0;
                PageIndex = 0;
            }
        }

        private readonly ViewPosition _view = new();

        /// <summary>还原位置期间产生的滚动是程序自己搞出来的，不能被当成用户浏览记下来</summary>
        private bool _restoringPosition;

        public winContextEditor(VPetLLM plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            if (_plugin.ChatCore is null)
            {
                throw new InvalidOperationException("ChatCore is not initialized");
            }

            _langCode = _plugin.Settings?.Language ?? "zh-hans";

            InitializeComponent();

            var historyManager = _plugin.ChatCore.HistoryManager;
            _systemMessages = new List<Message>();

            var aiName = _plugin.Settings?.AiName ?? "Assistant";
            var userName = _plugin.Settings?.UserName ?? "You";

            _dbCount = historyManager.GetEditingMessageCount();
            for (var index = 0; index < _dbCount; index++)
            {
                var item = new ContextEditorItem(index, "user");
                item.SetDisplayNames(aiName, userName);
                _allItems.Add(item);
            }

            _snapshotCount = _allItems.Count;

            // 默认停在最新的一批上：和聊天窗口一致，打开就是刚说过的话
            _windowStart = Math.Max(0, _allItems.Count - PageSize);
            _pageIndex = PageCount - 1;
            _view.ContinuousStart = _windowStart;
            _view.PageIndex = _pageIndex;

            DataContext = this;
            _initialized = true;

            ApplyLocalization();
            ApplyMode(simple: true);
            RebuildWindow();
            UpdateStats();
            Loaded += Window_Loaded;
            Closed += Window_Closed;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => _ = LoadWindowAsync(toBottom: true)));
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            _closing = true;
            _loadCts.Cancel();
            _loadCts.Dispose();

            // 位置记录只服务于本次编辑；下次打开应当重新落在最新消息上
            _view.Clear();
        }

        // ── 模板里用到的本地化文案 ──────────────────────────────────────────

        public string LocDeleteMessage { get; private set; } = "删除这条消息";
        public string LocViewImage { get; private set; } = "点击查看大图";
        public string LocRemoveImage { get; private set; } = "移除图像";

        // 翻页条/提示条上的文案带占位符，取一次留着用
        private string _locPageInfo = "第 {0} / {1} 页";
        private string _locLoadingOlder = "正在载入更早的消息…";
        private string _locOlderRemaining = "↑ 上面还有 {0} 条更早的消息";

        // ── 模式切换 ────────────────────────────────────────────────────────

        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            // XAML 里 Radio_Simple 带 IsChecked="True"，构造期就会触发一次
            if (!_initialized) return;

            // 两种渲染下条目高度差得很远（片段 vs 整块原文框），
            // 不管的话切一次模式视线就漂到别处去了
            RecordPosition();
            ApplyMode(simple: Radio_Simple.IsChecked == true);

            // 等新模板量完再摆，否则拿到的还是旧高度
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!_closing) RestorePosition();
            }));
        }

        private void ListBoxItem_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ListBoxItem { DataContext: ContextEditorItem item })
            {
                // 按 PageSize 对齐取整段，免得每滚一条就发一次查询
                _ = EnsureRangeLoadedAsync(item.Index / PageSize * PageSize, PageSize);
            }
        }

        private void ListBoxItem_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_suppressUnload) return;

            if (sender is ListBoxItem { DataContext: ContextEditorItem item })
            {
                item.Unload();
            }
        }

        /// <summary>
        /// 把 <c>[offset, offset+count)</c> 这段里还没内容的壳填上。
        ///
        /// 只对这段中真正缺内容的最小连续区间发查询：已加载的、以及已经有请求在飞的
        /// （<see cref="ContextEditorItem.LoadRequested"/>）都跳过，
        /// 所以同一批壳被多个容器同时点名也只会落到一次取数上。
        /// </summary>
        private async Task EnsureRangeLoadedAsync(int offset, int count)
        {
            if (_closing || count <= 0) return;

            var start = Math.Max(0, offset);
            var end = Math.Min(_dbCount, offset + count);
            if (start >= end) return;

            var first = -1;
            var last = -1;
            for (var i = start; i < end; i++)
            {
                var candidate = _allItems[i];
                if (candidate.IsLoaded || candidate.LoadRequested) continue;
                if (first < 0) first = i;
                last = i;
            }
            if (first < 0) return;

            for (var i = first; i <= last; i++) _allItems[i].LoadRequested = true;

            try
            {
                var take = last - first + 1;
                var messages = await Task.Run(
                    () => _plugin.ChatCore?.HistoryManager.GetEditingMessagesPage(first, take)
                          ?? new List<Message>(), _loadCts.Token);
                if (_closing || _loadCts.IsCancellationRequested) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    var simple = Radio_Simple.IsChecked == true;
                    var aiName = _plugin.Settings?.AiName ?? "Assistant";
                    var userName = _plugin.Settings?.UserName ?? "You";

                    for (var i = 0; i < messages.Count && first + i < _dbCount; i++)
                    {
                        var item = _allItems[first + i];
                        // 用户改过的绝不覆盖：他可能翻走这一页又翻回来
                        if (item.IsLoaded || item.IsDirty)
                        {
                            continue;
                        }
                        item.LoadFrom(messages[i]);
                        item.SetDisplayNames(aiName, userName);
                        item.IsSimpleMode = simple;
                        item.MarkClean();
                    }
                    UpdateStats();
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Log($"上下文编辑器: 分段加载失败: {ex.Message}");
            }
            finally
            {
                for (var i = first; i <= last; i++) _allItems[i].LoadRequested = false;
            }
        }

        /// <summary>保存前用：窗口外的壳也得有内容，否则 ApplyTo 会把它们写成空消息</summary>
        private async Task EnsureAllItemsLoadedAsync()
        {
            for (var offset = 0; offset < _dbCount; offset += PageSize)
            {
                await EnsureRangeLoadedAsync(offset, PageSize);
            }
        }

        // ── 窗口：决定哪一段进 ListBox ───────────────────────────────────────

        private int PageCount => Math.Max(1, (_allItems.Count + PageSize - 1) / PageSize);

        /// <summary>当前窗口在 <see cref="_allItems"/> 中的范围</summary>
        private (int Start, int Count) CurrentWindow()
        {
            if (_allItems.Count == 0) return (0, 0);

            if (_paged)
            {
                _pageIndex = Math.Clamp(_pageIndex, 0, PageCount - 1);
                var pageStart = _pageIndex * PageSize;
                return (pageStart, Math.Min(PageSize, _allItems.Count - pageStart));
            }

            // 连续模式的窗口永远含尾部：新加的消息不必换窗口就能看见
            _windowStart = Math.Clamp(_windowStart, 0, _allItems.Count - 1);
            return (_windowStart, _allItems.Count - _windowStart);
        }

        private void RebuildWindow()
        {
            var (start, count) = CurrentWindow();

            _suppressUnload = true;
            Items.Clear();
            for (var i = start; i < start + count; i++) Items.Add(_allItems[i]);

            // Unloaded 在布局阶段（Loaded 优先级）才抛出来，得排在它后面解除屏蔽。
            // Input 比 Loaded 低一档，正好。
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _suppressUnload = false));

            UpdatePagerUi();
        }

        /// <summary>
        /// 预取当前窗口里即将露面的一段，然后把滚动条摆到位。
        ///
        /// 连续模式的窗口可能有上千条，不能一次全拉——只取会落进视口的那 <see cref="PageSize"/> 条，
        /// 其余照旧交给滚动时的懒加载。
        /// </summary>
        /// <param name="toBottom">true = 摆到最末尾（打开编辑器时）</param>
        /// <param name="restore">true = 摆回 <see cref="_view"/> 记下的位置；否则停在窗口顶部</param>
        private async Task LoadWindowAsync(bool toBottom = false, bool restore = false)
        {
            var (start, count) = CurrentWindow();
            if (count > 0)
            {
                var prefetch = Math.Min(count, PageSize);

                // 预取要覆盖真正会露面的那一段，否则摆过去的时候正对着一屏"加载中"
                var from = start;
                if (toBottom) from = start + count - prefetch;
                else if (restore && _view.AnchorIndex >= 0) from = _view.AnchorIndex;

                await EnsureRangeLoadedAsync(Math.Clamp(from, start, start + count - prefetch), prefetch);
            }
            if (_closing) return;

            if (toBottom) ScrollToBottom();
            else if (restore) RestorePosition();
            else ScrollToTop();
        }

        // ── 连续模式：向上续载 ──────────────────────────────────────────────

        private void List_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // 完全模式的原文框自带 ScrollViewer，它的 ScrollChanged 也会冒泡到这里
            if (!ReferenceEquals(e.OriginalSource, ListScrollViewer)) return;

            // 实时记：用户随时可能去点另一个模式，等到那一刻再算就晚了
            // ——切模式会先重建窗口，那时候容器都已经换掉，量不到"刚才看到哪儿"
            RecordPosition();

            if (_paged || _loadingOlder || _closing || _windowStart <= 0) return;

            // 只认向上滚。补完一批之后要把滚动位置往下推回去（见 LoadOlderAsync），
            // 那个向下的位移不能再次触发续载——否则一旦补偿量估小了，
            // 就会一路自己触发自己，把整段历史全拉进来。
            if (e.VerticalChange > 0) return;

            if (e.VerticalOffset <= LoadOlderThreshold)
            {
                _ = LoadOlderAsync();
            }
        }

        /// <summary>把窗口顶端再往前扩 <see cref="PageSize"/> 条</summary>
        private async Task LoadOlderAsync()
        {
            if (_paged || _loadingOlder || _closing || _windowStart <= 0) return;

            _loadingOlder = true;
            UpdateOlderHint();
            try
            {
                var newStart = Math.Max(0, _windowStart - PageSize);
                await EnsureRangeLoadedAsync(newStart, _windowStart - newStart);
                if (_closing) return;

                var viewer = ListScrollViewer;
                var extentBefore = viewer?.ExtentHeight ?? 0;
                var offsetBefore = viewer?.VerticalOffset ?? 0;

                // 插入前先钉住当前最上面那一条，插完把它摆回原处
                var (anchor, anchorTop) = FirstVisibleItem();

                _restoringPosition = true;
                _suppressUnload = true;
                for (var i = _windowStart - 1; i >= newStart; i--) Items.Insert(0, _allItems[i]);
                _windowStart = newStart;
                _view.ContinuousStart = newStart;

                // 头部插入会把原有内容整体推下去。不补偿的话滚动条位置不动、
                // 内容却变了，看上去就是"猛地跳到刚载入的那批上"。
                if (viewer is not null)
                {
                    viewer.UpdateLayout();

                    // 按锚点的实际位移回补是精确的；退路是拿总高增量估——
                    // 像素滚动下未实体化项的高度本就是估的，只在锚点丢了时才用
                    if (anchor is null || !AlignAnchor(anchor, anchorTop))
                    {
                        var delta = viewer.ExtentHeight - extentBefore;
                        if (delta > 0) viewer.ScrollToVerticalOffset(offsetBefore + delta);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"上下文编辑器: 载入更早消息失败: {ex.Message}");
            }
            finally
            {
                _loadingOlder = false;
                UpdateOlderHint();

                // 这两个标志要盖住随后那一轮布局：Unloaded 和补偿滚动的 ScrollChanged
                // 都在 Loaded 优先级抛出来，Input 比它低一档，正好排在后面。
                // 放 finally 里是为了半路抛异常也一定能解开，否则位置记录会就此僵死。
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    _suppressUnload = false;
                    _restoringPosition = false;
                }));
            }
        }

        // ── 加载模式 / 翻页 ─────────────────────────────────────────────────

        private void LoadMode_Changed(object sender, RoutedEventArgs e)
        {
            // Radio_Continuous 带 IsChecked="True"，构造期会先触发一次
            if (!_initialized) return;

            var paged = Radio_Paged.IsChecked == true;
            if (paged == _paged) return;

            // 先把离开这一侧时的位置定死，再换过去。
            // 优先用"看到第几条"——它比页号/窗口起点更贴近用户视线；
            // 只有一次都没记到过时才退回各自模式记下的粗位置（页号 / 窗口起点）。
            RecordPosition();
            var anchor = _view.AnchorIndex >= 0
                ? _view.AnchorIndex
                : (_paged ? _view.PageIndex * PageSize : _view.ContinuousStart);
            anchor = Math.Clamp(anchor, 0, Math.Max(0, _allItems.Count - 1));

            _paged = paged;

            if (_paged)
            {
                // 锚点落在哪一页就去哪一页——比"上次停的那一页"更贴合刚才看的内容
                _pageIndex = Math.Clamp(anchor / PageSize, 0, PageCount - 1);
                _view.PageIndex = _pageIndex;
            }
            else
            {
                // 窗口至少要盖住锚点；上次已经往上展开过更多的话就沿用，
                // 不然用户一路滚上去的那一大段会白展开一次
                _windowStart = Math.Clamp(Math.Min(_view.ContinuousStart, anchor),
                    0, Math.Max(0, _allItems.Count - 1));
                _view.ContinuousStart = _windowStart;
            }

            RebuildWindow();
            _ = LoadWindowAsync(restore: true);
        }

        private void FirstPage_Click(object sender, RoutedEventArgs e) => GoToPage(0);
        private void PrevPage_Click(object sender, RoutedEventArgs e) => GoToPage(_pageIndex - 1);
        private void NextPage_Click(object sender, RoutedEventArgs e) => GoToPage(_pageIndex + 1);
        private void LastPage_Click(object sender, RoutedEventArgs e) => GoToPage(PageCount - 1);

        private void GoToPage(int page)
        {
            if (!_paged) return;

            page = Math.Clamp(page, 0, PageCount - 1);
            if (page == _pageIndex) return;

            _pageIndex = page;
            _view.PageIndex = page;

            // 主动翻页就是要看新一页的开头。锚点同步挪过去，
            // 这样切回连续模式时接着看的是这一页，而不是翻页前的老位置。
            _view.AnchorIndex = page * PageSize;
            _view.AnchorTop = 0;

            RebuildWindow();
            _ = LoadWindowAsync();
        }

        private void UpdatePagerUi()
        {
            Panel_Pager.Visibility = _paged ? Visibility.Visible : Visibility.Collapsed;

            if (_paged)
            {
                Text_PageInfo.Text = string.Format(_locPageInfo, _pageIndex + 1, PageCount);
                Button_FirstPage.IsEnabled = Button_PrevPage.IsEnabled = _pageIndex > 0;
                Button_NextPage.IsEnabled = Button_LastPage.IsEnabled = _pageIndex < PageCount - 1;
            }

            UpdateOlderHint();
        }

        private void UpdateOlderHint()
        {
            if (_paged || _windowStart <= 0)
            {
                Panel_OlderHint.Visibility = Visibility.Collapsed;
                return;
            }

            Panel_OlderHint.Visibility = Visibility.Visible;
            Text_OlderHint.Text = _loadingOlder
                ? _locLoadingOlder
                : string.Format(_locOlderRemaining, _windowStart);
        }

        // ── 浏览位置：实时记录，切模式时还原 ────────────────────────────────

        /// <summary>
        /// 视口里最靠上的那一条，连同它顶边相对视口的位置。
        ///
        /// 遍历的是虚拟化面板的实体化子元素（几十个），不是整个窗口的项——
        /// 连续模式的窗口可能有上千条，每次滚动都拿容器去筛一遍太贵。
        /// </summary>
        private (ContextEditorItem? Item, double Top) FirstVisibleItem()
        {
            var viewer = ListScrollViewer;
            var panel = ListItemsHost;
            if (viewer is null || panel is null) return (null, 0);

            ContextEditorItem? best = null;
            var bestTop = double.MaxValue;

            try
            {
                foreach (var child in panel.Children)
                {
                    if (child is not FrameworkElement element ||
                        element.DataContext is not ContextEditorItem item ||
                        !element.IsVisible)
                    {
                        continue;
                    }

                    var top = element.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y;

                    // 底边还没滚出视口顶部的才算"看得见"；取其中最靠上的那条
                    if (top + element.ActualHeight > 0 && top < bestTop)
                    {
                        best = item;
                        bestTop = top;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // 容器正在被换掉时拿不到相对坐标；交给调用方的兜底锚点
                return (null, 0);
            }

            return best is null ? (null, 0) : (best, bestTop);
        }

        /// <summary>把当前看到的位置记进缓存。滚动、翻页、切模式时都要调。</summary>
        private void RecordPosition()
        {
            if (_restoringPosition || _closing) return;

            var (item, top) = FirstVisibleItem();
            if (item is not null)
            {
                _view.AnchorIndex = item.Index;
                _view.AnchorTop = top;
            }

            if (_paged) _view.PageIndex = _pageIndex;
            else _view.ContinuousStart = _windowStart;
        }

        /// <summary>
        /// 把滚动条摆回缓存里记下的那一条。
        ///
        /// 全程同步：虚拟化下没实体化的容器量不到位置，所以要
        /// UpdateLayout 生容器 → ScrollIntoView 让它进视口 → 再按记下的像素偏移精修。
        /// 拆成异步的话中间会露出一帧摆错位置的画面。
        /// </summary>
        private void RestorePosition()
        {
            var index = _view.AnchorIndex;
            if (index < 0) return;   // 还没记到过位置，就别动滚动条

            var (start, count) = CurrentWindow();
            if (index < start || index >= start + count)
            {
                // 锚点不在当前窗口里，摆不过去；退回窗口顶部
                ScrollToTop();
                return;
            }

            var anchor = _allItems[index];

            _restoringPosition = true;
            try
            {
                List_Messages.UpdateLayout();
                List_Messages.ScrollIntoView(anchor);
                List_Messages.UpdateLayout();
                AlignAnchor(anchor, _view.AnchorTop);
            }
            catch (Exception ex)
            {
                Logger.Log($"上下文编辑器: 还原浏览位置失败: {ex.Message}");
            }
            finally
            {
                // 还原自己滚出来的那几次 ScrollChanged 别记回缓存，否则记的是中间态
                Dispatcher.BeginInvoke(DispatcherPriority.Input,
                    new Action(() => _restoringPosition = false));
            }
        }

        /// <summary>
        /// 把锚点那一条挪回它原来相对视口顶部的位置。
        /// 容器还没实体化就没法量，返回 false 交给调用方兜底。
        /// </summary>
        private bool AlignAnchor(ContextEditorItem anchor, double top)
        {
            var viewer = ListScrollViewer;
            if (viewer is null) return false;
            if (List_Messages.ItemContainerGenerator.ContainerFromItem(anchor) is not FrameworkElement element)
            {
                return false;
            }

            try
            {
                var actual = element.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y;
                var delta = actual - top;
                if (Math.Abs(delta) > 0.5) viewer.ScrollToVerticalOffset(viewer.VerticalOffset + delta);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private void ScrollToTop() => ListScrollViewer?.ScrollToTop();

        private void ScrollToBottom()
        {
            var viewer = ListScrollViewer;
            if (viewer is null) return;

            viewer.UpdateLayout();
            viewer.ScrollToEnd();

            // 一次不够：滚到底的过程中新实体化的项会把估算的总高改掉，
            // 停下来的位置往往还差一截。等这轮布局走完再补一次。
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!_closing) viewer.ScrollToEnd();
            }));
        }

        /// <summary>
        /// 切模式前必须把当前模式的编辑成果落到另一边，否则用户在简洁模式改的台词
        /// 一切到完全模式就"消失"了（因为完全模式读的是 Content）。
        /// </summary>
        private void ApplyMode(bool simple)
        {
            // 遍历全量而不是当前窗口：翻页翻回来的时候得已经是新模式
            foreach (var item in _allItems)
            {
                if (!item.IsLoaded) continue;
                if (simple)
                {
                    // 完全模式可能整段重写过原文，片段必须按新原文重新解析
                    item.RebuildSegments();
                }
                else
                {
                    item.SyncToContent();
                }

                item.IsSimpleMode = simple;
            }

            UpdateStats();
        }

        // ── 增删 ────────────────────────────────────────────────────────────

        private void AddUserMessage_Click(object sender, RoutedEventArgs e) => AddMessage("user");

        private void AddAssistantMessage_Click(object sender, RoutedEventArgs e) => AddMessage("assistant");

        private void AddMessage(string role)
        {
            var message = new Message { Role = role, Content = "" };
            var item = new ContextEditorItem(message);
            item.SetDisplayNames(_plugin.Settings?.AiName ?? "Assistant", _plugin.Settings?.UserName ?? "You");
            item.IsSimpleMode = Radio_Simple.IsChecked == true;

            _allItems.Add(item);
            ReindexItems();

            if (_paged && _pageIndex != PageCount - 1)
            {
                // 加满一页会撑出新的一页，跟过去，否则新建的消息看不见
                _pageIndex = PageCount - 1;
                _view.PageIndex = _pageIndex;
                RebuildWindow();
                UpdateStats();

                // 新消息在末尾，直接摆到底；这里不能用 ScrollIntoView，
                // 它会被随后异步跑完的 LoadWindowAsync 再滚一次盖掉
                _ = LoadWindowAsync(toBottom: true);
                return;
            }

            // 连续模式的窗口本来就含尾部，直接追加即可
            Items.Add(item);
            UpdatePagerUi();
            UpdateStats();
            List_Messages.ScrollIntoView(item);
        }

        private void DeleteMessage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ContextEditorItem item })
            {
                item.MarkDeleted();
                UpdateStats();
            }
        }

        private void ReindexItems()
        {
            for (var i = 0; i < _allItems.Count; i++) _allItems[i].SetIndex(i);
        }

        // ── 滚轮 ────────────────────────────────────────────────────────────

        /// <summary>列表滚动条缓存；模板套用后才拿得到，故惰性获取</summary>
        private ScrollViewer? _listScrollViewer;
        private ScrollViewer? ListScrollViewer => _listScrollViewer ??= FindVisualChild<ScrollViewer>(List_Messages);

        /// <summary>虚拟化面板本体；它的 Children 就是当前实体化的那批容器</summary>
        private VirtualizingStackPanel? _listItemsHost;
        private VirtualizingStackPanel? ListItemsHost =>
            _listItemsHost ??= FindVisualChild<VirtualizingStackPanel>(List_Messages);

        /// <summary>
        /// 接管消息列表的滚轮。
        ///
        /// 不接管的话有两个毛病：
        /// 一是 ScrollUnit="Pixel" 下 VirtualizingStackPanel.LineUp() 字面意义就是"减 1 像素"，
        /// 而一格滚轮只调 3 次，于是滚一格才走 3px；
        /// 二是 TextBox 会吞掉滚轮（ScrollViewer.OnMouseWheel 只要能处理就无条件 Handled，
        /// 哪怕自己根本没得滚），而本窗口正文几乎全是 TextBox。
        ///
        /// 用 Preview（隧道，从外往里）就能在 TextBox 拿到事件之前先决定归属：
        /// 完全模式的原文框自己还能往这个方向滚时让给它，其余一律按正常步长滚列表。
        /// </summary>
        private void List_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (e.Handled || e.Delta == 0) return;

            if (EditorWantsWheel(e.OriginalSource as DependencyObject, e.Delta))
            {
                return;
            }

            var viewer = ListScrollViewer;
            if (viewer is null) return;

            e.Handled = true;
            viewer.ScrollToVerticalOffset(
                viewer.VerticalOffset - e.Delta / 120.0 * GetWheelStep(viewer));
        }

        /// <summary>
        /// 命中的原文框自己还有余量可滚时，把这一格让给它。
        /// 从事件源往上找，遇到列表就停——只关心气泡内部的输入框。
        /// </summary>
        private bool EditorWantsWheel(DependencyObject? source, int delta)
        {
            const double epsilon = 0.5;

            while (source is not null && source != List_Messages)
            {
                if (source is TextBox box)
                {
                    // TextBoxBase 直接暴露这几个量，不必去翻模板里的 PART_ContentHost
                    var maxOffset = box.ExtentHeight - box.ViewportHeight;
                    if (maxOffset <= epsilon) return false;

                    return delta < 0
                        ? box.VerticalOffset < maxOffset - epsilon
                        : box.VerticalOffset > epsilon;
                }

                // 命中的通常都是 Visual；ContentElement（如芯片里的 Run）走逻辑树兜底
                source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                    : LogicalTreeHelper.GetParent(source);
            }

            return false;
        }

        /// <summary>一格滚轮走多少像素，跟随系统"每次滚动行数"设置</summary>
        private static double GetWheelStep(ScrollViewer viewer)
        {
            var lines = SystemParameters.WheelScrollLines;

            // -1 表示系统设置为"滚动一屏"
            if (lines < 0) return Math.Max(viewer.ViewportHeight - 24, 48);

            const double lineHeight = 20.0;
            return Math.Max(lines, 1) * lineHeight;
        }

        private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T found) return found;

            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (FindVisualChild<T>(child) is T match) return match;
            }

            return null;
        }

        // ── 图像 ────────────────────────────────────────────────────────────

        private void Thumbnail_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.Image { DataContext: ContextEditorItem item })
            {
                var imageData = item.GetFullImage();
                if (imageData is null || imageData.Length == 0) return;

                var preview = new winImagePreview(imageData) { Owner = this };
                preview.ShowDialog();
            }
        }

        private void RemoveImage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ContextEditorItem item })
            {
                item.RemoveImage();
                Logger.Log("上下文编辑器: 图像已标记删除（保存后生效）");
            }
        }

        // ── 保存 / 取消 ─────────────────────────────────────────────────────

        private async void Button_Save_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin.ChatCore is null)
            {
                ShowError(LanguageHelper.Get("Error.ChatCoreNotInitialized", _langCode,
                    "Chat core is not initialized. Cannot save changes."));
                Logger.Log("上下文编辑器: ChatCore 为空，无法保存");
                return;
            }

            try
            {
                // 补全期间会 await 回 UI 线程，其间的布局可能把某些容器卸掉；
                // 卸掉就得重取，重取又要 await——先把卸载关掉，免得原地打转。
                _suppressUnload = true;
                await EnsureAllItemsLoadedAsync();

                _systemMessages.Clear();
                _systemMessages.AddRange(_plugin.ChatCore.HistoryManager.GetSystemMessagesForEditing());
                // 简洁模式下真相在片段里，先合回原文再落库
                if (Radio_Simple.IsChecked == true)
                {
                    foreach (var item in _allItems)
                    {
                        if (item.IsLoaded) item.SyncToContent();
                    }
                }

                // system 消息始终排在最前：多数 Provider 都要求 system 位于对话首部
                var newHistory = new List<Message>(_systemMessages);

                foreach (var item in _allItems)
                {
                    if (item.IsDeleted) continue;
                    item.ApplyTo();
                    newHistory.Add(item.OriginalMessage);
                }

                _plugin.ChatCore.SetChatHistory(newHistory);
                _saved = true;

                Logger.Log($"上下文编辑器: 保存成功，共 {newHistory.Count} 条（其中 system {_systemMessages.Count} 条未改动）");
                Close();
            }
            catch (Exception ex)
            {
                Logger.Log($"上下文编辑器: 保存失败: {ex.Message}");
                ShowError($"Failed to save context: {ex.Message}");
            }
            finally
            {
                _suppressUnload = false;
            }
        }

        private void Button_Cancel_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>
        /// 取消/关闭本身不需要"恢复"——全程都在副本上编辑，只有保存才写回。
        /// 这里只是拦一下，免得改了半天顺手点了叉。
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_saved && HasUnsavedChanges())
            {
                var result = MessageBox.Show(
                    LanguageHelper.Get("ContextEditor.UnsavedBody", _langCode, "有未保存的改动，确定要放弃吗？"),
                    LanguageHelper.Get("ContextEditor.UnsavedTitle", _langCode, "放弃改动"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }

                Logger.Log("上下文编辑器: 用户放弃改动，历史未变更");
            }

            base.OnClosing(e);
        }

        private bool HasUnsavedChanges()
        {
            // 比较前先把简洁模式的片段合回原文，否则刚改完台词就关窗会被判成"没动过"
            if (Radio_Simple.IsChecked == true)
            {
                foreach (var item in _allItems)
                {
                    if (item.IsLoaded) item.SyncToContent();
                }
            }

            if (_allItems.Count != _snapshotCount)
            {
                return true;
            }

            foreach (var item in _allItems)
            {
                if (item.IsDirty)
                {
                    return true;
                }
            }

            return false;
        }

        // ── 杂项 ────────────────────────────────────────────────────────────

        private void UpdateStats()
        {
            var tokens = 0;
            try
            {
                tokens = _plugin.ChatCore?.GetCurrentTokenCount() ?? 0;
            }
            catch (Exception ex)
            {
                // Token 估算失败不该挡住编辑器
                Logger.Log($"上下文编辑器: Token 估算失败: {ex.Message}");
            }

            var template = LanguageHelper.Get("ContextEditor.Stats", _langCode, "{0} 条消息 · 约 {1} tokens");
            var activeCount = _allItems.Count(item => !item.IsDeleted);
            Text_Stats.Text = string.Format(template, activeCount, tokens);

            Text_Empty.Visibility = activeCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyLocalization()
        {
            Title = LanguageHelper.Get("ContextEditor.Title", _langCode, "上下文编辑器");
            Text_Title.Text = Title;

            Radio_Simple.Content = LanguageHelper.Get("ContextEditor.SimpleMode", _langCode, "简洁模式");
            Radio_Full.Content = LanguageHelper.Get("ContextEditor.FullMode", _langCode, "完全模式");

            Radio_Continuous.Content = LanguageHelper.Get("ContextEditor.ContinuousMode", _langCode, "连续");
            Radio_Paged.Content = LanguageHelper.Get("ContextEditor.PagedMode", _langCode, "分页");
            Radio_Continuous.ToolTip = LanguageHelper.Get("ContextEditor.ContinuousHint", _langCode,
                "停在最新消息，向上滚动继续载入更早的");
            Radio_Paged.ToolTip = string.Format(
                LanguageHelper.Get("ContextEditor.PagedHint", _langCode, "每页 {0} 条，用底部翻页按钮切换"),
                PageSize);

            Button_FirstPage.ToolTip = LanguageHelper.Get("ContextEditor.FirstPage", _langCode, "第一页");
            Button_PrevPage.ToolTip = LanguageHelper.Get("ContextEditor.PrevPage", _langCode, "上一页");
            Button_NextPage.ToolTip = LanguageHelper.Get("ContextEditor.NextPage", _langCode, "下一页");
            Button_LastPage.ToolTip = LanguageHelper.Get("ContextEditor.LastPage", _langCode, "最后一页");

            _locPageInfo = LanguageHelper.Get("ContextEditor.PageInfo", _langCode, "第 {0} / {1} 页");
            _locLoadingOlder = LanguageHelper.Get("ContextEditor.LoadingOlder", _langCode, "正在载入更早的消息…");
            _locOlderRemaining = LanguageHelper.Get("ContextEditor.OlderRemaining", _langCode,
                "↑ 上面还有 {0} 条更早的消息");

            Text_Notice.Text = LanguageHelper.Get("ContextEditor.Notice", _langCode,
                "系统提示词与人设不在此展示，也不会被本窗口修改。");
            Text_Empty.Text = LanguageHelper.Get("ContextEditor.Empty", _langCode, "还没有对话记录");

            Button_AddUser.Content = LanguageHelper.Get("ContextEditor.AddUser", _langCode, "+ 用户消息");
            Button_AddAssistant.Content = LanguageHelper.Get("ContextEditor.AddAssistant", _langCode, "+ 助手消息");
            Button_Save.Content = LanguageHelper.Get("ContextEditor.Save", _langCode, "保存");
            Button_Cancel.Content = LanguageHelper.Get("ContextEditor.Cancel", _langCode, "取消");

            LocDeleteMessage = LanguageHelper.Get("ContextEditor.DeleteMessage", _langCode, "删除这条消息");
            LocViewImage = LanguageHelper.Get("ContextEditor.ViewImage", _langCode, "点击查看大图");
            LocRemoveImage = LanguageHelper.Get("ContextEditor.RemoveImage", _langCode, "移除图像");

            OnPropertyChanged(nameof(LocDeleteMessage));
            OnPropertyChanged(nameof(LocViewImage));
            OnPropertyChanged(nameof(LocRemoveImage));
        }

        private void ShowError(string message)
        {
            MessageBox.Show(
                message,
                LanguageHelper.Get("Error.Title", _langCode, "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
