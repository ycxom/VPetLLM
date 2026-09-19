using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VPetLLM.Utils.Data;
using VPetLLM.Utils.Localization;

namespace VPetLLM.UI.Controls
{
    /// <summary>
    /// 统一渠道管理：一张跨类型的渠道表（排序、筛选、就地改优先级/权重、测试、启停），
    /// 点编辑进入二级页面改单个渠道。编辑在副本上进行，保存才写回。
    /// </summary>
    public partial class ChannelManagerView : UserControl
    {
        private Setting? _settings;
        private Func<RoutedChatCore?>? _coreProvider;
        private Action? _onChanged;

        private readonly ObservableCollection<ChannelRow> _rows = new();
        private ListCollectionView? _view;
        private DispatcherTimer? _clock;
        private bool _subscribed;

        // ── 编辑页状态 ──
        private Setting.ChannelNodeBase? _original; // 编辑中的原节点；新增时为 null
        private Setting.ChannelNodeBase? _draft;
        private bool _loadingForm;
        private CancellationTokenSource? _modelFetchCts;
        private ApiPresetData? _presets;

        private static readonly Setting.ChannelKind[] AllKinds =
        {
            Setting.ChannelKind.OpenAIChat,
            Setting.ChannelKind.OpenAIResponses,
            Setting.ChannelKind.Gemini,
            Setting.ChannelKind.Ollama,
            Setting.ChannelKind.LMStudio,
            Setting.ChannelKind.Free
        };

        public ChannelManagerView()
        {
            InitializeComponent();
            // 不在 XAML 根元素上挂事件：生成代码会写成 VPetLLM.UI...，被同名主类 VPetLLM.VPetLLM 截胡
            Loaded += (_, _) => Subscribe();
            Unloaded += UserControl_Unloaded;
        }

        private string Lang => _settings?.Language ?? "zh-hans";

        private string T(string key, string fallback) => LanguageHelper.Get("Channels." + key, Lang, fallback);

        /// <summary>
        /// 挂到设置上。<paramref name="onChanged"/> 在任何会改动设置的操作后调用（由窗口负责落盘）。
        /// </summary>
        public void Attach(Setting settings, Func<RoutedChatCore?> coreProvider, Action onChanged)
        {
            _settings = settings;
            _coreProvider = coreProvider;
            _onChanged = onChanged;

            if (_view is null)
            {
                _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_rows);
                _view.Filter = FilterRow;
                ApplySort(Col_Priority, "Priority", ListSortDirection.Descending);
                Grid_Channels.ItemsSource = _view;
            }

            CheckBox_Failover.IsChecked = settings.EnableFallback;
            RefreshLanguage();
            ReloadRows();
            Subscribe();
            ShowList();
        }

        private void Subscribe()
        {
            if (_subscribed || _settings is null) return;
            ChannelStatsRecorder.Changed += OnStatsChanged;
            _clock ??= new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _clock.Tick += Clock_Tick;
            _clock.Start();
            _subscribed = true;
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            // 统计事件挂在静态类上，不摘掉就会把整个设置窗口留在内存里
            if (!_subscribed) return;
            ChannelStatsRecorder.Changed -= OnStatsChanged;
            if (_clock is not null)
            {
                _clock.Tick -= Clock_Tick;
                _clock.Stop();
            }
            _subscribed = false;
        }

        private void Clock_Tick(object? sender, EventArgs e)
        {
            foreach (var row in _rows) row.RefreshAll();
        }

        private void OnStatsChanged(Setting.ChannelNodeBase node)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _rows.FirstOrDefault(r => ReferenceEquals(r.Node, node))?.RefreshAll();
            });
        }

        // ═══════════════════════ 列表 ═══════════════════════

        public void ReloadRows()
        {
            if (_settings is null) return;

            var selected = (Grid_Channels.SelectedItem as ChannelRow)?.Node;
            foreach (var row in _rows) row.RoutingEdited -= Row_RoutingEdited;
            _rows.Clear();

            _settings.EnsureChannelIds();
            foreach (var node in _settings.EnumerateChannels())
            {
                var row = new ChannelRow(node, T);
                row.RoutingEdited += Row_RoutingEdited;
                _rows.Add(row);
            }

            if (selected is not null)
                Grid_Channels.SelectedItem = _rows.FirstOrDefault(r => ReferenceEquals(r.Node, selected));

            UpdateSummary();
        }

        private void UpdateSummary()
        {
            if (_settings is null) return;

            var all = _settings.EnumerateChannels();
            var enabled = all.Where(c => c.Enabled).ToList();
            var primary = enabled
                .OrderByDescending(c => c.Priority)
                .ThenByDescending(c => c.Weight)
                .ThenBy(c => c.Id)
                .FirstOrDefault();

            var text = string.Format(T("Summary", "共 {0} 个渠道，已启用 {1} 个。"), all.Count, enabled.Count);
            if (primary is not null)
            {
                var tier = enabled.Count(c => c.Priority == primary.Priority);
                text += " " + (tier > 1
                    ? string.Format(T("Summary.Tier", "当前首选层：优先级 {0}，{1} 个渠道按权重分担。"), primary.Priority, tier)
                    : string.Format(T("Summary.Primary", "当前首选：#{0} {1}（优先级 {2}）。"), primary.Id, primary.Name, primary.Priority));
            }
            else if (all.Count > 0)
            {
                text += " " + T("Summary.NoneEnabled", "没有启用的渠道，对话将无法进行。");
            }
            Text_Summary.Text = text;

            var anyVisible = _view is not null && !_view.IsEmpty;
            Panel_Empty.Visibility = anyVisible ? Visibility.Collapsed : Visibility.Visible;
            Text_Empty.Text = _rows.Count == 0
                ? T("Empty", "还没有渠道，点「添加渠道」开始。")
                : T("EmptyFiltered", "没有符合筛选条件的渠道");
        }

        private void Row_RoutingEdited(ChannelRow row)
        {
            _settings?.SyncPrimaryProvider();
            UpdateSummary();
            _onChanged?.Invoke();
            // 等这次编辑的焦点流程走完再重排，否则正在编辑的单元格会被回收
            Dispatcher.BeginInvoke(() => _view?.Refresh(), DispatcherPriority.Background);
        }

        private void CellNumberBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box) return;
            if (e.Key == Key.Enter)
            {
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        private bool FilterRow(object item)
        {
            if (item is not ChannelRow row) return false;

            if (ComboBox_TypeFilter.SelectedItem is ComboBoxItem { Tag: Setting.ChannelKind kind } && row.Node.Kind != kind)
                return false;

            var q = TextBox_Search.Text?.Trim();
            if (string.IsNullOrEmpty(q)) return true;
            return Contains(row.Name, q) || Contains(row.Node.Model, q) || Contains(row.Node.Endpoint, q) || row.Id.ToString() == q;

            static bool Contains(string? s, string q) => s?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            Text_SearchPlaceholder.Visibility = string.IsNullOrEmpty(TextBox_Search.Text) ? Visibility.Visible : Visibility.Collapsed;
            if (_view is null) return;
            _view.Refresh();
            UpdateSummary();
        }

        /// <summary>
        /// 点列头排序。自己接管是为了总带上"编号"作为第二排序键，保证同优先级时顺序稳定。
        /// </summary>
        private void Grid_Channels_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            var path = e.Column.SortMemberPath;
            if (string.IsNullOrEmpty(path)) return;

            var direction = e.Column.SortDirection == ListSortDirection.Descending
                ? ListSortDirection.Ascending
                : e.Column.SortDirection == ListSortDirection.Ascending
                    ? ListSortDirection.Descending
                    // 第一次点：数值类的列默认从大到小更有用
                    : path is "Priority" or "Weight" or "RequestCount" or "LastTestSort"
                        ? ListSortDirection.Descending
                        : ListSortDirection.Ascending;

            ApplySort(e.Column, path, direction);
        }

        private void ApplySort(DataGridColumn column, string path, ListSortDirection direction)
        {
            if (_view is null) return;
            foreach (var c in Grid_Channels.Columns) c.SortDirection = null;
            column.SortDirection = direction;

            using (_view.DeferRefresh())
            {
                _view.SortDescriptions.Clear();
                _view.SortDescriptions.Add(new SortDescription(path, direction));
                if (path != "Id")
                    _view.SortDescriptions.Add(new SortDescription("Id", ListSortDirection.Ascending));
            }
        }

        private void CheckBox_Failover_Click(object sender, RoutedEventArgs e)
        {
            if (_settings is null) return;
            _settings.EnableFallback = CheckBox_Failover.IsChecked == true;
            _onChanged?.Invoke();
        }

        // ── 添加 ──

        private void Button_Add_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = Button_Add, Placement = PlacementMode.Bottom };
            foreach (var kind in AllKinds)
            {
                var item = new MenuItem { Header = KindDisplayName(kind), Tag = kind };
                item.Click += (_, _) => OpenEditor(null, kind);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        // ── 行操作 ──

        private static ChannelRow? RowOf(object sender) => (sender as FrameworkElement)?.Tag as ChannelRow;

        private void Grid_Channels_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // 双击在输入框/按钮上不算
            if (e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) is null && FindAncestor<TextBox>(d) is null
                && Grid_Channels.SelectedItem is ChannelRow row)
            {
                OpenEditor(row.Node, null);
            }
        }

        private void RowEdit_Click(object sender, RoutedEventArgs e)
        {
            if (RowOf(sender) is { } row) OpenEditor(row.Node, null);
        }

        private async void RowTest_Click(object sender, RoutedEventArgs e)
        {
            if (RowOf(sender) is { } row) await TestRowAsync(row);
        }

        private void RowToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_settings is null || RowOf(sender) is not { } row) return;
            row.Node.Enabled = !row.Node.Enabled;
            _settings.SyncFreeContainer();
            _settings.SyncPrimaryProvider();
            row.RefreshAll();
            UpdateSummary();
            _onChanged?.Invoke();
        }

        private void RowMore_Click(object sender, RoutedEventArgs e)
        {
            if (_settings is null || RowOf(sender) is not { } row) return;

            var menu = new ContextMenu { PlacementTarget = sender as UIElement, Placement = PlacementMode.Bottom };

            var top = new MenuItem { Header = T("Action.MakeTop", "设为最高优先级") };
            top.Click += (_, _) =>
            {
                var others = _settings.EnumerateChannels().Where(c => !ReferenceEquals(c, row.Node)).ToList();
                row.PriorityText = ((others.Count == 0 ? row.Node.Priority : others.Max(c => c.Priority)) + 1).ToString();
            };
            menu.Items.Add(top);

            var copy = new MenuItem { Header = T("Action.Copy", "复制渠道") };
            copy.Click += (_, _) =>
            {
                var clone = Setting.CloneChannel(row.Node);
                clone.Id = 0;
                clone.Name = row.Node.Name + T("CopySuffix", " - 副本");
                clone.Stats = new Setting.ChannelStats();
                // 副本默认停用：原样复制出来立刻参与路由，等于悄悄改了流量分配
                clone.Enabled = false;
                _settings.AddChannel(clone);
                ReloadRows();
                _onChanged?.Invoke();
            };
            menu.Items.Add(copy);

            var resetStats = new MenuItem { Header = T("Action.ResetStats", "清空统计") };
            resetStats.Click += (_, _) =>
            {
                row.Node.Stats = new Setting.ChannelStats();
                row.RefreshAll();
                _onChanged?.Invoke();
            };
            menu.Items.Add(resetStats);

            menu.Items.Add(new Separator());

            var delete = new MenuItem { Header = T("Action.Delete", "删除"), Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)) };
            delete.Click += (_, _) =>
            {
                var confirm = MessageBox.Show(
                    string.Format(T("Confirm.Delete", "确定删除渠道 #{0}「{1}」吗？"), row.Id, row.Name),
                    T("Title", "渠道管理"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;
                _settings.RemoveChannel(row.Node);
                _settings.SyncPrimaryProvider();
                ReloadRows();
                _onChanged?.Invoke();
            };
            menu.Items.Add(delete);

            menu.IsOpen = true;
        }

        private async Task TestRowAsync(ChannelRow row)
        {
            var core = _coreProvider?.Invoke();
            if (core is null)
            {
                MessageBox.Show(T("NoCore", "聊天核心尚未就绪，稍后再试。"), T("Title", "渠道管理"));
                return;
            }

            row.IsTesting = true;
            try
            {
                await core.TestChannelAsync(row.Node);
            }
            finally
            {
                row.IsTesting = false;
                _onChanged?.Invoke();
            }
        }

        private async void Button_TestAll_Click(object sender, RoutedEventArgs e)
        {
            var targets = _rows.Where(r => r.Node.Enabled).ToList();
            if (targets.Count == 0) return;

            Button_TestAll.IsEnabled = false;
            try
            {
                // 限个并发：本地模型渠道同时压上去会互相拖慢，测出来的延迟没有参考价值
                using var gate = new SemaphoreSlim(3);
                await Task.WhenAll(targets.Select(async row =>
                {
                    await gate.WaitAsync();
                    try { await TestRowAsync(row); }
                    finally { gate.Release(); }
                }));
            }
            finally
            {
                Button_TestAll.IsEnabled = true;
            }
        }

        // ═══════════════════════ 编辑页 ═══════════════════════

        private void ShowList()
        {
            _modelFetchCts?.Cancel();
            Page_Editor.Visibility = Visibility.Collapsed;
            Page_List.Visibility = Visibility.Visible;
            _original = null;
            _draft = null;
        }

        private void OpenEditor(Setting.ChannelNodeBase? node, Setting.ChannelKind? newKind)
        {
            if (_settings is null) return;

            _original = node;
            if (node is not null)
            {
                _draft = Setting.CloneChannel(node);
            }
            else
            {
                var kind = newKind ?? Setting.ChannelKind.OpenAIChat;
                _draft = Setting.CreateChannel(kind);
                _draft.Name = string.Format(T("NewName", "{0} 渠道"), KindDisplayName(kind));
                _draft.Enabled = true;
            }

            Text_EditorTitle.Text = node is null
                ? T("Editor.AddTitle", "添加渠道")
                : string.Format(T("Editor.EditTitle", "编辑渠道 #{0} {1}"), node.Id, node.Name);

            LoadForm();
            Page_List.Visibility = Visibility.Collapsed;
            Page_Editor.Visibility = Visibility.Visible;
            Ed_Name.Focus();
            _ = EnsurePresetsAsync();
        }

        private void Button_Back_Click(object sender, RoutedEventArgs e) => ShowList();
        private void Breadcrumb_Click(object sender, MouseButtonEventArgs e) => ShowList();

        private void LoadForm()
        {
            if (_draft is null) return;
            _loadingForm = true;
            try
            {
                var d = _draft;
                var kind = d.Kind;
                var isOpenAI = d is Setting.OpenAINodeSetting;
                var isFree = d is Setting.FreeNodeSetting;

                Ed_Type.SelectedItem = Ed_Type.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (Setting.ChannelKind)i.Tag == kind);
                Ed_TypeHint.Text = KindHint(kind);
                Ed_Name.Text = d.Name;

                // 地址
                Ed_UrlRow.Visibility = isFree ? Visibility.Collapsed : Visibility.Visible;
                Ed_UrlPreset.Visibility = isOpenAI ? Visibility.Visible : Visibility.Collapsed;
                Ed_Url.Text = UrlOf(d) ?? "";
                SelectMatchingPreset();
                UpdateUrlPreview();

                // 密钥
                var key = KeyOf(d);
                Ed_KeyRow.Visibility = key is null && !(d is Setting.OpenAINodeSetting or Setting.GeminiNodeSetting)
                    ? Visibility.Collapsed : Visibility.Visible;
                Ed_KeyPassword.Password = key ?? "";
                Ed_KeyPlain.Text = key ?? "";
                // 多 Key 用明文多行框才看得清
                SetKeyPlainMode(key?.IndexOfAny(new[] { '\n', '\r' }) >= 0);

                // 模型
                Ed_ModelRow.Visibility = Visibility.Visible;
                Ed_RefreshModels.Visibility = isFree ? Visibility.Collapsed : Visibility.Visible;
                Ed_Model.IsEnabled = !isFree;
                Ed_Model.ItemsSource = isFree ? new List<string> { "auto" } : (CachedModels(UrlOf(d)) ?? new List<string>());
                Ed_Model.Text = isFree ? "auto" : d.Model ?? "";
                Ed_ModelStatus.Visibility = Visibility.Collapsed;

                Ed_GeminiOpenAIAuth.Visibility = d is Setting.GeminiNodeSetting ? Visibility.Visible : Visibility.Collapsed;
                Ed_GeminiOpenAIAuth.IsChecked = (d as Setting.GeminiNodeSetting)?.UseOpenAIAuth == true;

                Ed_FreeInfo.Visibility = isFree ? Visibility.Visible : Visibility.Collapsed;
                if (isFree) Ed_FreeInfo.Text = FreeInfoText();

                // 路由
                Ed_Enabled.IsChecked = d.Enabled;
                Ed_Priority.Text = d.Priority.ToString();
                Ed_Weight.Text = d.Weight.ToString();
                SelectByTag(Ed_Mode, d.Mode);
                if (Ed_Mode.SelectedItem is null)
                {
                    // 插件自定义用途只能由插件设置，这里原样保留、只做显示
                    Ed_Mode.Items.Add(new ComboBoxItem { Content = $"{T("Mode.Plugin", "插件")}: {d.PluginModeId}", Tag = d.Mode });
                    SelectByTag(Ed_Mode, d.Mode);
                }
                Ed_ProxyRow.Visibility = isFree ? Visibility.Collapsed : Visibility.Visible;
                SelectByTag(Ed_Proxy, d.ProxyMode);

                // 能力
                Ed_Streaming.IsChecked = d.EnableStreaming;
                Ed_Vision.IsChecked = d.EnableVision;
                // Free 端点后端不确定，工具调用由云端策略 + 本地探测决定，不给开关
                Ed_ToolCall.Visibility = isFree ? Visibility.Collapsed : Visibility.Visible;
                Ed_ToolCallTip.Visibility = Ed_ToolCall.Visibility;
                Ed_ToolCall.IsChecked = d.EnableToolCall;
                SelectByTag(Ed_Thinking, d.ThinkingEffort);

                // 生成参数
                Ed_Advanced.IsChecked = d.EnableAdvanced;
                Ed_AdvancedPanel.IsEnabled = d.EnableAdvanced;
                Ed_Temperature.Value = d.Temperature;
                Ed_TemperatureValue.Text = d.Temperature.ToString("0.00");
                Ed_MaxTokens.Text = d.MaxTokens.ToString();

                Ed_TestResult.Text = "";
            }
            finally
            {
                _loadingForm = false;
            }
        }

        /// <summary>把表单写回草稿。返回错误信息，null 表示没问题。</summary>
        private string? CollectForm(bool validate = true)
        {
            if (_draft is null) return "no draft";
            var d = _draft;

            d.Name = Ed_Name.Text.Trim();
            if (string.IsNullOrEmpty(d.Name)) d.Name = KindDisplayName(d.Kind);

            if (d is not Setting.FreeNodeSetting)
            {
                var url = Ed_Url.Text.Trim();
                SetUrl(d, url);
                if (validate && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
                    return T("Error.Url", "API 地址无效，需要以 http:// 或 https:// 开头");

                d.Model = Ed_Model.Text.Trim();
            }

            var key = Ed_KeyPlain.Visibility == Visibility.Visible ? Ed_KeyPlain.Text : Ed_KeyPassword.Password;
            switch (d)
            {
                case Setting.OpenAINodeSetting o: o.ApiKey = key.Trim(); break;
                case Setting.GeminiNodeSetting g:
                    g.ApiKey = key.Trim();
                    g.UseOpenAIAuth = Ed_GeminiOpenAIAuth.IsChecked == true;
                    break;
            }

            d.Enabled = Ed_Enabled.IsChecked == true;

            if (int.TryParse(Ed_Priority.Text.Trim(), out var priority)) d.Priority = priority;
            else if (validate) return T("Error.Priority", "优先级必须是整数");

            if (int.TryParse(Ed_Weight.Text.Trim(), out var weight) && weight >= 0) d.Weight = weight;
            else if (validate) return T("Error.Weight", "权重必须是不小于 0 的整数");

            if (Ed_Mode.SelectedItem is ComboBoxItem { Tag: Setting.ChannelMode mode }) d.Mode = mode;
            if (Ed_Proxy.SelectedItem is ComboBoxItem { Tag: Setting.ChannelProxyMode proxy }) d.ProxyMode = proxy;

            d.EnableStreaming = Ed_Streaming.IsChecked == true;
            d.EnableVision = Ed_Vision.IsChecked == true;
            d.EnableToolCall = d is not Setting.FreeNodeSetting && Ed_ToolCall.IsChecked == true;
            if (Ed_Thinking.SelectedItem is ComboBoxItem { Tag: Setting.ThinkingEffort effort }) d.ThinkingEffort = effort;

            d.EnableAdvanced = Ed_Advanced.IsChecked == true;
            d.Temperature = Math.Round(Ed_Temperature.Value, 2);
            if (int.TryParse(Ed_MaxTokens.Text.Trim(), out var maxTokens) && maxTokens > 0) d.MaxTokens = maxTokens;
            else if (validate && d.EnableAdvanced) return T("Error.MaxTokens", "最大输出必须是正整数");

            return null;
        }

        private void Ed_Type_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingForm || _draft is null) return;
            if (Ed_Type.SelectedItem is not ComboBoxItem { Tag: Setting.ChannelKind kind } || kind == _draft.Kind) return;

            CollectForm(validate: false);
            _draft = Setting.ConvertChannel(_draft, kind);
            LoadForm();
        }

        private void Ed_Url_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loadingForm) return;
            SelectMatchingPreset();
            UpdateUrlPreview();
        }

        /// <summary>地址下方实时显示"实际会请求哪里"，填 base URL 还是完整端点都一目了然。</summary>
        private void UpdateUrlPreview()
        {
            if (_draft is null) return;
            var url = Ed_Url.Text.Trim();
            string? actual = null;

            if (!string.IsNullOrEmpty(url))
            {
                switch (_draft)
                {
                    case Setting.OpenAINodeSetting o:
                        actual = OpenAIChatCore.BuildEndpointUrl(new Setting.OpenAINodeSetting { Url = url, ApiFormat = o.ApiFormat });
                        break;
                    case Setting.OllamaNodeSetting:
                        actual = url.TrimEnd('/') + "/api/chat";
                        break;
                    case Setting.LMStudioNodeSetting:
                    {
                        var b = url.TrimEnd('/');
                        actual = b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? b
                            : (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b : b + "/v1") + "/chat/completions";
                        break;
                    }
                }
            }

            Ed_UrlPreview.Text = actual is null
                ? (_draft is Setting.GeminiNodeSetting ? T("Hint.GeminiUrl", "填到版本号即可，例如 https://generativelanguage.googleapis.com/v1beta") : "")
                : string.Format(T("Hint.ActualUrl", "实际请求：{0}"), actual);
            Ed_UrlPreview.Visibility = string.IsNullOrEmpty(Ed_UrlPreview.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task EnsurePresetsAsync()
        {
            if (_presets is not null) return;
            try
            {
                _presets = await ApiPresetManager.GetPresetsAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"加载 API 预设失败: {ex.Message}");
                return;
            }

            _loadingForm = true;
            try
            {
                Ed_UrlPreset.Items.Clear();
                Ed_UrlPreset.Items.Add(new ComboBoxItem { Content = "--" });
                foreach (var category in _presets?.Categories ?? new List<ApiPresetCategory>())
                {
                    Ed_UrlPreset.Items.Add(new ComboBoxItem { Content = $"— {category.Name} —", IsEnabled = false });
                    foreach (var preset in category.Presets)
                        Ed_UrlPreset.Items.Add(new ComboBoxItem { Content = preset.Name, Tag = preset });
                }
                SelectMatchingPreset();
            }
            finally
            {
                _loadingForm = false;
            }
        }

        private void SelectMatchingPreset()
        {
            var url = Ed_Url.Text.Trim().TrimEnd('/');
            var wasLoading = _loadingForm;
            _loadingForm = true;
            try
            {
                Ed_UrlPreset.SelectedItem =
                    Ed_UrlPreset.Items.OfType<ComboBoxItem>().FirstOrDefault(i =>
                        i.Tag is ApiPresetItem p && !string.IsNullOrEmpty(p.Url) &&
                        url.StartsWith(p.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    ?? Ed_UrlPreset.Items.OfType<ComboBoxItem>().FirstOrDefault();
            }
            finally
            {
                _loadingForm = wasLoading;
            }
        }

        private void Ed_UrlPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingForm || Ed_UrlPreset.SelectedItem is not ComboBoxItem { Tag: ApiPresetItem preset }) return;

            _loadingForm = true;
            try
            {
                Ed_Url.Text = preset.Url;
                if (!string.IsNullOrEmpty(preset.DefaultModel))
                    Ed_Model.Text = preset.DefaultModel;
                if (preset.Models is { Count: > 0 } && (Ed_Model.ItemsSource as IEnumerable<string>)?.Any() != true)
                    Ed_Model.ItemsSource = preset.Models;
            }
            finally
            {
                _loadingForm = false;
            }
            UpdateUrlPreview();
        }

        private void Ed_KeyToggle_Click(object sender, RoutedEventArgs e)
            => SetKeyPlainMode(Ed_KeyPlain.Visibility != Visibility.Visible);

        private void SetKeyPlainMode(bool plain)
        {
            if (plain)
            {
                if (Ed_KeyPassword.Visibility == Visibility.Visible) Ed_KeyPlain.Text = Ed_KeyPassword.Password;
                Ed_KeyPassword.Visibility = Visibility.Collapsed;
                Ed_KeyPlain.Visibility = Visibility.Visible;
            }
            else
            {
                if (Ed_KeyPlain.Visibility == Visibility.Visible) Ed_KeyPassword.Password = Ed_KeyPlain.Text;
                Ed_KeyPlain.Visibility = Visibility.Collapsed;
                Ed_KeyPassword.Visibility = Visibility.Visible;
            }
            Ed_KeyToggle.Content = plain ? "" : ""; // Hide / View
        }

        private async void Ed_RefreshModels_Click(object sender, RoutedEventArgs e)
        {
            if (_draft is null) return;
            CollectForm(validate: false);

            var core = _coreProvider?.Invoke();
            if (core is null)
            {
                ShowModelStatus(T("NoCore", "聊天核心尚未就绪，稍后再试。"), error: true);
                return;
            }

            _modelFetchCts?.Cancel();
            _modelFetchCts = new CancellationTokenSource();
            var token = _modelFetchCts.Token;
            var draft = _draft;

            Ed_RefreshModels.IsEnabled = false;
            ShowModelStatus(T("Models.Fetching", "正在获取模型列表…"), error: false);
            try
            {
                var models = await core.FetchModelsAsync(draft, token);
                if (token.IsCancellationRequested || !ReferenceEquals(draft, _draft)) return;

                var current = Ed_Model.Text;
                Ed_Model.ItemsSource = models;
                Ed_Model.Text = string.IsNullOrWhiteSpace(current) && models.Count > 0 ? models[0] : current;
                SaveCachedModels(UrlOf(draft), models);
                ShowModelStatus(string.Format(T("Models.Fetched", "获取到 {0} 个模型"), models.Count), error: false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(draft, _draft))
                    ShowModelStatus(string.Format(T("Models.Failed", "获取失败：{0}"), ex.Message), error: true);
            }
            finally
            {
                Ed_RefreshModels.IsEnabled = true;
            }
        }

        private void ShowModelStatus(string text, bool error)
        {
            Ed_ModelStatus.Text = text;
            Ed_ModelStatus.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)) : (Brush)FindResource("SecondaryTextBrush");
            Ed_ModelStatus.Visibility = Visibility.Visible;
        }

        private void Ed_Advanced_Click(object sender, RoutedEventArgs e)
            => Ed_AdvancedPanel.IsEnabled = Ed_Advanced.IsChecked == true;

        private void Ed_Temperature_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (Ed_TemperatureValue is not null)
                Ed_TemperatureValue.Text = e.NewValue.ToString("0.00");
        }

        private async void Ed_Test_Click(object sender, RoutedEventArgs e)
        {
            if (_draft is null) return;
            var error = CollectForm();
            if (error is not null)
            {
                ShowTestResult(error, ok: false);
                return;
            }

            var core = _coreProvider?.Invoke();
            if (core is null)
            {
                ShowTestResult(T("NoCore", "聊天核心尚未就绪，稍后再试。"), ok: false);
                return;
            }

            Ed_Test.IsEnabled = false;
            ShowTestResult(T("Test.Running", "测试中…"), ok: null);
            try
            {
                // 测的是草稿：保存前就能验证改动对不对，不影响正在用的渠道
                var result = await core.TestChannelAsync(Setting.CloneChannel(_draft));
                ShowTestResult(result.Success
                        ? string.Format(T("Test.Ok", "✓ 可用 · {0} 毫秒 · 回复：{1}"), result.LatencyMs, Truncate(result.Reply, 40))
                        : string.Format(T("Test.Fail", "✗ 失败 · {0}"), result.Error),
                    result.Success);
            }
            finally
            {
                Ed_Test.IsEnabled = true;
            }
        }

        private void ShowTestResult(string text, bool? ok)
        {
            Ed_TestResult.Text = text;
            Ed_TestResult.ToolTip = text;
            Ed_TestResult.Foreground = ok switch
            {
                true => new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)),
                false => new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)),
                _ => (Brush)FindResource("SecondaryTextBrush")
            };
        }

        private void Ed_Save_Click(object sender, RoutedEventArgs e)
        {
            if (_settings is null || _draft is null) return;

            var error = CollectForm();
            if (error is not null)
            {
                ShowTestResult(error, ok: false);
                return;
            }

            Setting.ChannelNodeBase saved;
            if (_original is null)
            {
                _settings.AddChannel(_draft);
                saved = _draft;
            }
            else if (_original.GetType() == _draft.GetType())
            {
                // 原地写回：路由按节点对象缓存了工作 core，换对象就得重建
                Setting.CopyChannelInto(_draft, _original);
                saved = _original;
            }
            else
            {
                _settings.ReplaceChannel(_original, _draft);
                saved = _draft;
            }

            _settings.SyncFreeContainer();
            _settings.SyncPrimaryProvider();
            _onChanged?.Invoke();

            ShowList();
            ReloadRows();
            Grid_Channels.SelectedItem = _rows.FirstOrDefault(r => ReferenceEquals(r.Node, saved));
            Grid_Channels.ScrollIntoView(Grid_Channels.SelectedItem ?? _rows.FirstOrDefault());
        }

        // ═══════════════════════ 本地化 / 下拉项 ═══════════════════════

        public void RefreshLanguage()
        {
            Col_Name.Header = T("Col.Name", "名称");
            Col_Type.Header = T("Col.Type", "类型");
            Col_Status.Header = T("Col.Status", "状态");
            Col_Mode.Header = T("Col.Mode", "用途");
            Col_Priority.Header = T("Col.Priority", "优先级");
            Col_Weight.Header = T("Col.Weight", "权重");
            Col_Usage.Header = T("Col.Usage", "已用");
            Col_Latency.Header = T("Col.Latency", "响应");
            Col_LastTest.Header = T("Col.LastTest", "上次测试");
            Col_Actions.Header = T("Col.Actions", "操作");

            var filterKind = (ComboBox_TypeFilter.SelectedItem as ComboBoxItem)?.Tag;
            ComboBox_TypeFilter.Items.Clear();
            ComboBox_TypeFilter.Items.Add(new ComboBoxItem { Content = T("Filter.AllTypes", "全部类型") });
            foreach (var kind in AllKinds)
                ComboBox_TypeFilter.Items.Add(new ComboBoxItem { Content = KindDisplayName(kind), Tag = kind });
            ComboBox_TypeFilter.SelectedItem = ComboBox_TypeFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, filterKind))
                                               ?? ComboBox_TypeFilter.Items[0];

            Fill(Ed_Type, AllKinds.Select(k => (KindDisplayName(k), (object)k)));
            Fill(Ed_Mode, new (string, object)[]
            {
                (T("Mode.UnrestrictedLong", "全部（聊天和总结都用）"), Setting.ChannelMode.Unrestricted),
                (T("Mode.ChatOnlyLong", "仅聊天"), Setting.ChannelMode.ChatOnly),
                (T("Mode.CompressionOnlyLong", "仅总结 / 上下文压缩"), Setting.ChannelMode.CompressionOnly)
            });
            Fill(Ed_Proxy, new (string, object)[]
            {
                (LanguageHelper.Get("ChannelProxyMode.FollowDefault", Lang, "跟随默认"), Setting.ChannelProxyMode.FollowDefault),
                (LanguageHelper.Get("ChannelProxyMode.Direct", Lang, "直连"), Setting.ChannelProxyMode.Direct),
                (LanguageHelper.Get("ChannelProxyMode.ForceProxy", Lang, "强制代理"), Setting.ChannelProxyMode.ForceProxy)
            });
            Fill(Ed_Thinking, new (string, object)[]
            {
                (LanguageHelper.Get("ThinkingEffort.Default", Lang, "默认（不发送）"), Setting.ThinkingEffort.Default),
                (LanguageHelper.Get("ThinkingEffort.Minimal", Lang, "最小"), Setting.ThinkingEffort.Minimal),
                (LanguageHelper.Get("ThinkingEffort.Low", Lang, "低"), Setting.ThinkingEffort.Low),
                (LanguageHelper.Get("ThinkingEffort.Medium", Lang, "中"), Setting.ThinkingEffort.Medium),
                (LanguageHelper.Get("ThinkingEffort.High", Lang, "高"), Setting.ThinkingEffort.High)
            });

            foreach (var row in _rows) row.RefreshAll();
            UpdateSummary();
            if (_draft is not null) LoadForm();
        }

        private void Fill(ComboBox box, IEnumerable<(string Text, object Tag)> items)
        {
            var selected = (box.SelectedItem as ComboBoxItem)?.Tag;
            var wasLoading = _loadingForm;
            _loadingForm = true;
            try
            {
                box.Items.Clear();
                foreach (var (text, tag) in items)
                    box.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
                if (selected is not null) SelectByTag(box, selected);
            }
            finally
            {
                _loadingForm = wasLoading;
            }
        }

        private static void SelectByTag(ComboBox box, object tag)
            => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, tag));

        private string KindDisplayName(Setting.ChannelKind kind) => kind switch
        {
            Setting.ChannelKind.OpenAIChat => "OpenAI Chat Completions",
            Setting.ChannelKind.OpenAIResponses => "OpenAI Responses",
            Setting.ChannelKind.Gemini => "Gemini",
            Setting.ChannelKind.Ollama => "Ollama",
            Setting.ChannelKind.LMStudio => "LM Studio",
            _ => T("Kind.Free", "Free（免费）")
        };

        private string KindHint(Setting.ChannelKind kind) => kind switch
        {
            Setting.ChannelKind.OpenAIChat => T("KindHint.OpenAIChat", "POST /v1/chat/completions。绝大多数 OpenAI 兼容服务（中转站、DeepSeek、Qwen、vLLM…）用这个。"),
            Setting.ChannelKind.OpenAIResponses => T("KindHint.OpenAIResponses", "POST /v1/responses。OpenAI 新接口，推理模型和内置工具走这里；只有服务端支持 Responses 时才选。"),
            Setting.ChannelKind.Gemini => T("KindHint.Gemini", "Google Gemini 原生接口。"),
            Setting.ChannelKind.Ollama => T("KindHint.Ollama", "本地 Ollama（/api/chat）。"),
            Setting.ChannelKind.LMStudio => T("KindHint.LMStudio", "本地 LM Studio（OpenAI 兼容）。"),
            _ => T("KindHint.Free", "内置的免费服务，无需地址和密钥，模型由服务端分配。")
        };

        private string FreeInfoText()
        {
            try
            {
                var config = FreeConfigManager.GetChatConfig();
                if (config is null) return T("Free.NotReady", "免费服务配置尚未下载完成。");
                var desc = FreeConfigManager.GetDescription(config, Lang);
                var provider = FreeConfigManager.GetProviderInfo(config, Lang);
                return string.Join("\n", new[] { desc, provider }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            catch
            {
                return "";
            }
        }

        // ═══════════════════════ 小工具 ═══════════════════════

        private static string? UrlOf(Setting.ChannelNodeBase node) => node switch
        {
            Setting.OpenAINodeSetting o => o.Url,
            Setting.GeminiNodeSetting g => g.Url,
            Setting.OllamaNodeSetting o => o.Url,
            Setting.LMStudioNodeSetting l => l.Url,
            _ => null
        };

        private static void SetUrl(Setting.ChannelNodeBase node, string url)
        {
            switch (node)
            {
                case Setting.OpenAINodeSetting o: o.Url = url; break;
                case Setting.GeminiNodeSetting g: g.Url = url; break;
                case Setting.OllamaNodeSetting o: o.Url = url; break;
                case Setting.LMStudioNodeSetting l: l.Url = url; break;
            }
        }

        private static string? KeyOf(Setting.ChannelNodeBase node) => node switch
        {
            Setting.OpenAINodeSetting o => o.ApiKey,
            Setting.GeminiNodeSetting g => g.ApiKey,
            _ => null
        };

        /// <summary>模型列表缓存按"主机:端口"共享：同一个中转站的多个渠道不用各拉一遍。</summary>
        private static string? CacheKeyOf(string? url)
            => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Host}_{uri.Port}" : null;

        private List<string>? CachedModels(string? url)
        {
            var key = CacheKeyOf(url);
            if (key is null || _settings?.ModelCache?.Cache is not { } cache) return null;
            return cache.Values.FirstOrDefault(e => e.CacheKey == key)?.Models;
        }

        private void SaveCachedModels(string? url, List<string> models)
        {
            var key = CacheKeyOf(url);
            if (key is null || models.Count == 0 || _settings is null) return;
            _settings.ModelCache ??= new Setting.ModelCacheSetting();
            var cache = _settings.ModelCache.Cache;

            var entry = cache.Values.FirstOrDefault(e => e.CacheKey == key);
            if (entry is null)
            {
                entry = new Setting.ModelCacheEntry { CacheKey = key, CreatedAt = DateTime.Now };
                cache[key] = entry;
            }
            entry.Models = models;
            entry.UpdatedAt = DateTime.Now;
            _onChanged?.Invoke();
        }

        private static string Truncate(string? s, int max)
            => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max) + "…";

        private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d is not null)
            {
                if (d is T t) return t;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }
    }
}
