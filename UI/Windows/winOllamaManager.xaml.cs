using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VPetLLM.Utils.Localization;

namespace VPetLLM.UI.Windows
{
    public sealed record OllamaCatalogRow(OllamaModelInfo Model, string Name, string Size, OllamaFit Fit, string Badge, string Description, string ActionText, bool CanAct, bool Installed, string StateText, int Rank)
    {
        public string Initial => Name[..1].ToUpperInvariant();
        public bool Highlighted => Rank < 0;
    }

    public sealed record OllamaLocalRow(string Name, long SizeBytes, string Size, OllamaFit Fit, string Badge, string Series, string UseText, bool CanUse, bool IsChat, bool Highlighted)
    {
        public string Initial => Name[..1].ToUpperInvariant();
    }

    public sealed record OllamaRowAction(string Text, bool Enabled, bool Installed);

    public sealed record OllamaTagOption(string Tag, string Model, long SizeBytes, string SizeText, OllamaFit Fit, string FitText, string Text);

    /// <summary>模型库的一排卡片。列表按排虚拟化，WPF 没有现成的虚拟化网格。</summary>
    public sealed record OllamaLibraryBlock(IReadOnlyList<OllamaLibraryRow> Rows, int Columns);

    /// <summary>选中的项数到 0 也要能点，不然退不回去。</summary>
    public sealed record OllamaFilterChip(string Text, int Count, bool Selected, Action Apply)
    {
        public bool CanPick => Count > 0 || Selected;
    }

    /// <summary>模型库里的一行。体积是滚到这行才查的，查到之后下拉项和默认规格都会变。</summary>
    public sealed class OllamaLibraryRow : INotifyPropertyChanged
    {
        private readonly Func<string, OllamaRowAction> _describeAction;
        private OllamaTagOption _selected;
        private bool _chosenByUser;

        public OllamaLibraryRow(OllamaLibraryModel model, string series, string capabilityText, IReadOnlyList<OllamaTagOption> options, Func<string, OllamaRowAction> describeAction)
        {
            Model = model;
            Series = series;
            CapabilityText = capabilityText;
            Options = options;
            _selected = options[0];
            _describeAction = describeAction;
            Action = describeAction(_selected.Model);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public OllamaLibraryModel Model { get; }
        public string Name => Model.Name;
        public string Description => Model.Description;
        public string Initial => Name[..1].ToUpperInvariant();
        public string Series { get; }
        public string CapabilityText { get; }
        public string Pulls => Model.Pulls.Length > 0 ? Model.Pulls : "—";
        public string SizeText => _selected.SizeText;
        public IReadOnlyList<OllamaTagOption> Options { get; private set; }
        public OllamaFit Fit => _selected.Fit;
        public string FitText => _selected.FitText;
        public OllamaRowAction Action { get; private set; }

        public OllamaTagOption Selected
        {
            get => _selected;
            // 换下拉项时 WPF 会先把选中项清空再写回来，这里不能当成用户选的
            set
            {
                if (value is null || value == _selected) return;
                _chosenByUser = true;
                Select(value);
            }
        }

        /// <summary>用户自己选过规格时不替用户改。</summary>
        public void SetOptions(IReadOnlyList<OllamaTagOption> options, string? bestTag)
        {
            var tag = _chosenByUser ? _selected.Tag : bestTag ?? _selected.Tag;
            var selected = options.First(o => o.Tag == tag);
            if (options.SequenceEqual(Options) && selected == _selected) return;

            // 必须先换列表再换选中项：换列表时 WPF 会重读 Selected，这时读到新项的话
            // SelectedItem 看着是对的，框里却是空白
            Options = options;
            Raise(nameof(Options));
            Select(selected);
        }

        public void RefreshAction()
        {
            Action = _describeAction(_selected.Model);
            Raise(nameof(Action));
        }

        private void Select(OllamaTagOption option)
        {
            _selected = option;
            Raise(nameof(Selected));
            Raise(nameof(SizeText));
            Raise(nameof(Fit));
            Raise(nameof(FitText));
            RefreshAction();
        }

        private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    /// <summary>
    /// 让没碰过命令行的用户装好 Ollama、下好模型。不用 ShowDialog：同线程模态会把桌宠主窗也禁用掉，
    /// 下载一跑几十分钟。选定的模型回传给打开它的面板，写回设置仍走面板自己的保存流程。
    /// </summary>
    public partial class winOllamaManager : Window
    {
        private const string SetupSize = "1.7 GB";
        private const double GiB = 1024 * 1024 * 1024;
        private const int MaxPopularModels = 4;

        // 同系列不到这么多个就归到“其他”，不然筛选栏全是只有一两个模型的按钮
        private const int MinSeriesModels = 3;
        private const double MinCardWidth = 240;
        private const double CardGap = 12;

        private static winOllamaManager? _instance;

        // 关掉窗口再开还记得，桌宠退出就忘：这里不写设置，代理只给安装包用
        private static Uri? _downloadProxy;

        private readonly Setting _settings;
        private readonly OllamaHardware _hardware;
        private readonly OllamaLibrary _library;
        private readonly OllamaModelInfo _recommended;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Stopwatch _speedClock = new();
        private readonly Func<string, MessageBoxImage, bool> _confirm;
        private readonly LocalProxyDetector _proxyDetector;
        private readonly Action? _saveSettings;
        private bool _loadingContext;

        /// <summary>上下文窗口的可选值。Ollama 默认 4096 装不下桌宠的系统提示词，所以从 8192 起。</summary>
        private static readonly int[] ContextChoices = { 8192, 16384, 32768 };
        /// <summary>下拉框里「自定义…」那一项的 Tag，不是真实窗口值</summary>
        private const int CustomContextTag = -1;
        internal const int MinContextLength = 2048;
        internal const int MaxContextLength = 1048576;

        private OllamaManagerService _service;
        private Func<string, string, bool>? _onChosen;
        private string _currentModel;
        private OllamaStatus? _status;
        private IReadOnlyList<OllamaLocalModel> _models = Array.Empty<OllamaLocalModel>();
        private string? _latest;
        private CancellationTokenSource? _running;
        private long _speedBytes;
        private string _speedText = "";
        private readonly Stopwatch _slowClock = new();
        private OllamaTaskProgress? _lastProgress;
        private OllamaTaskPhase? _slowPhase;
        private long _slowStartBytes;
        private bool _slowDismissed;
        private CancellationTokenSource? _proxyScan;
        private long? _freeSpace;
        private IReadOnlyList<OllamaLibraryModel>? _libraryModels;
        private List<OllamaLibraryRow> _libraryRows = new();
        private List<OllamaLibraryRow> _shownRows = new();
        private List<string> _librarySeries = new();
        private List<string> _libraryCapabilities = new();
        private HashSet<string> _downloaded = new();
        private string? _filterSeries;
        private string? _filterCapability;
        private bool _filterDownloaded;
        private int _libraryColumns;
        private OllamaFit? _catalogFit;
        private bool _catalogDownloaded;
        private OllamaFit? _localFit;
        private bool? _localChat;
        private List<OllamaModelInfo> _popular = new();
        private CancellationTokenSource? _popularLookup;
        private bool _libraryLoading;
        private bool _libraryOffline;

        private string Lang => _settings.Language ?? "zh-hans";

        /// <summary>模型跑在哪台电脑上就看哪台的配置，别的电脑的配置这里看不到。</summary>
        private OllamaHardware? Hardware => _service.IsLocal ? _hardware : null;

        private string T(string path, string fallback) => LanguageHelper.Get("OllamaManager." + path, Lang, fallback);

        /// <summary>打开管理器，已经开着时切到前台并换成新的回传目标。</summary>
        /// <param name="onChosen">用户选定模型时回调（模型名，接口地址），返回 false 表示对方已经填不进去了。</param>
        public static void Open(Window? owner, Setting settings, string url, string? currentModel, Func<string, string, bool> onChosen)
        {
            if (_instance is { } existing)
            {
                existing.Rebind(url, currentModel, onChosen);
                existing.Activate();
                return;
            }

            _instance = new winOllamaManager(settings, OllamaManagerService.Create(settings, url), currentModel, onChosen,
                saveSettings: settings.Save) { Owner = owner };
            _instance.Show();
        }

        /// <summary>快速滚动时划过去的行不发请求，停够这么久才查体积。</summary>
        public TimeSpan SizeLookupDelay { get; set; } = TimeSpan.FromMilliseconds(200);

        /// <summary>按这么长的一段时间算平均速度，太短会被一时的波动误报。</summary>
        public TimeSpan SlowPeriod { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>平均速度低于它就提示，剩下的量一段时间内就能下完时不提示。</summary>
        public long SlowBytesPerSecond { get; set; } = 512 * 1024;

        public static readonly DependencyProperty CardGridColumnsProperty =
            DependencyProperty.Register(nameof(CardGridColumns), typeof(int), typeof(winOllamaManager), new PropertyMetadata(1));

        /// <summary>推荐页和我的模型的列数，两页一样宽，共用一个值。</summary>
        public int CardGridColumns
        {
            get => (int)GetValue(CardGridColumnsProperty);
            set => SetValue(CardGridColumnsProperty, value);
        }

        /// <param name="confirm">升级、删除前的确认，返回 true 才继续。不传时弹系统确认框。</param>
        public winOllamaManager(Setting settings, OllamaManagerService service, string? currentModel, Func<string, string, bool> onChosen,
            Func<string, MessageBoxImage, bool>? confirm = null, OllamaLibrary? library = null, OllamaHardware? hardware = null,
            LocalProxyDetector? proxyDetector = null, Action? saveSettings = null)
        {
            InitializeComponent();
            // 小屏幕上窗口不能比屏幕还大
            Width = Math.Min(Width, SystemParameters.WorkArea.Width);
            Height = Math.Min(Height, SystemParameters.WorkArea.Height);
            _settings = settings;
            _library = library ?? OllamaLibrary.Shared;
            _hardware = hardware ?? OllamaHardware.Detect();
            _service = service;
            _currentModel = currentModel?.Trim() ?? "";
            _onChosen = onChosen;
            _confirm = confirm ?? ((text, image) => MessageBox.Show(this, text, Title, MessageBoxButton.YesNo, image) == MessageBoxResult.Yes);
            _recommended = OllamaModelCatalog.Recommend(_hardware);
            _proxyDetector = proxyDetector ?? new LocalProxyDetector();
            _service.UseDownloadProxy(_downloadProxy);
            // 不传就只改内存里的设置：测试不能碰真实的 settings.db
            _saveSettings = saveSettings;
            LoadContext();
            Loaded += Window_Loaded;
        }

        private void Rebind(string url, string? currentModel, Func<string, string, bool> onChosen)
        {
            _onChosen = onChosen;
            _currentModel = currentModel?.Trim() ?? "";

            // 任务进行中不换地址，服务对象和正在跑的任务要对得上
            var next = OllamaManagerService.Create(_settings, url);
            if (_running is null && next.Url != _service.Url)
            {
                next.UseDownloadProxy(_downloadProxy);
                _service = next;
                _status = null;
                _models = Array.Empty<OllamaLocalModel>();
                _freeSpace = null;
                _ = RefreshAsync();
                if (_latest is null) _ = LoadLatestVersionAsync();
                if (_libraryModels is { } models) ShowLibrary(models);
            }
            LoadContext();
            ApplyState();
        }

        /// <summary>按当前地址填上下文窗口下拉框。存的不是预设值（老版本或手改的）也原样列出来。</summary>
        private void LoadContext()
        {
            var current = _settings.Ollama.GetContextLength(_service.Url);
            _loadingContext = true;
            try
            {
                Cb_Context.Items.Clear();
                Cb_Context.Items.Add(new ComboBoxItem { Content = T("ContextDefault", "跟随 Ollama 默认"), Tag = 0 });
                foreach (var tokens in ContextChoices.Union(current > 0 ? new[] { current } : Array.Empty<int>()).OrderBy(t => t))
                {
                    var text = tokens == ContextChoices[0]
                        ? string.Format(T("ContextRecommended", "{0}（推荐）"), tokens)
                        : tokens.ToString();
                    Cb_Context.Items.Add(new ComboBoxItem { Content = text, Tag = tokens });
                }
                Cb_Context.Items.Add(new ComboBoxItem { Content = T("ContextCustom", "自定义…"), Tag = CustomContextTag });
                Cb_Context.SelectedItem = Cb_Context.Items.OfType<ComboBoxItem>().First(i => (int)i.Tag == current);
                Panel_ContextCustom.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _loadingContext = false;
            }
            ShowContextState(current, saved: false);
        }

        private void Context_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingContext || Cb_Context.SelectedItem is not ComboBoxItem { Tag: int tokens }) return;
            if (tokens == CustomContextTag)
            {
                // 只展开输入框，点了「应用」才算数
                var current = _settings.Ollama.GetContextLength(_service.Url);
                Tb_ContextCustom.Text = (current > 0 ? current : ContextChoices[0]).ToString();
                Panel_ContextCustom.Visibility = Visibility.Visible;
                Tb_ContextCustom.Focus();
                Tb_ContextCustom.SelectAll();
                ShowContextHint(string.Format(T("ContextCustomHint", "填 {0}～{1} 之间的整数，然后点「应用」。"), MinContextLength, MaxContextLength), warn: false);
                return;
            }
            Panel_ContextCustom.Visibility = Visibility.Collapsed;
            ApplyContext(tokens);
        }

        private void ContextApply_Click(object sender, RoutedEventArgs e) => ApplyCustomContext();

        private void ContextCustom_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            ApplyCustomContext();
        }

        private void ApplyCustomContext()
        {
            var raw = Tb_ContextCustom.Text.Trim().Replace(",", "").Replace("，", "");
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var tokens) ||
                tokens < MinContextLength || tokens > MaxContextLength)
            {
                ShowContextHint(string.Format(T("ContextCustomInvalid", "请填 {0}～{1} 之间的整数。"), MinContextLength, MaxContextLength), warn: true);
                return;
            }
            ApplyContext(tokens);
            // 重新列一遍：自定义值进下拉框并选中，输入框收起
            LoadContext();
            ShowContextState(tokens, saved: true);
        }

        private void ApplyContext(int tokens)
        {
            if (tokens == _settings.Ollama.GetContextLength(_service.Url))
            {
                ShowContextState(tokens, saved: false);
                return;
            }

            _settings.Ollama.SetContextLength(_service.Url, tokens);
            try
            {
                _saveSettings?.Invoke();
            }
            catch (Exception ex)
            {
                // 内存里已经改了，本次运行照样生效；只是重启后会丢
                Logger.Log($"保存 Ollama 上下文窗口失败: {ex.Message}");
            }
            Logger.Log($"Ollama 上下文窗口 {_service.Url} → {(tokens > 0 ? tokens.ToString() : "默认")}");
            ShowContextState(tokens, saved: true);
        }

        private void ShowContextState(int tokens, bool saved)
        {
            string text;
            if (tokens <= 0)
            {
                text = T("ContextDefaultWarning", "Ollama 默认通常只有 4096，装不下桌宠的角色设定和插件说明，对话时会报“上下文装不下”。建议选 8192。");
                Tb_ContextState.Foreground = (Brush)FindResource("WarningBrush");
            }
            else
            {
                text = T("ContextNote", "越大越占显存/内存。只影响桌宠发出的请求，不改 Ollama 本身的设置。");
                if (tokens < ContextChoices[0])
                    text = string.Format(T("ContextSmallWarning", "比推荐的 {0} 小，桌宠的设定可能还是装不下。"), ContextChoices[0]) + " " + text;
                if (saved) text = T("ContextSaved", "已保存，下次对话生效（Ollama 会先重新加载一次模型）。") + " " + text;
                ShowContextHint(text, warn: tokens < ContextChoices[0]);
                return;
            }
            ShowContextHint(text, warn: true);
        }

        private void ShowContextHint(string text, bool warn)
        {
            Tb_ContextState.Foreground = (Brush)FindResource(warn ? "WarningBrush" : "SecondaryTextBrush");
            Tb_ContextState.Text = text;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyState();
            _ = LoadLatestVersionAsync();
            await RefreshAsync();
        }

        protected override void OnClosed(EventArgs e)
        {
            // 下载和拉取都能续传，直接取消。安装程序已经在跑就让它跑完，这里只是不再等它
            _lifetime.Cancel();
            _onChosen = null;
            _instance = null;
            base.OnClosed(e);
        }

        private async Task LoadLatestVersionAsync()
        {
            if (!_service.IsLocal) return;
            try
            {
                _latest = await _service.GetLatestVersionAsync(_lifetime.Token);
                ApplyState();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Log($"查询 Ollama 最新版本失败: {ex.Message}");
            }
        }

        private async Task RefreshAsync()
        {
            var service = _service;
            try
            {
                var status = await service.GetStatusAsync(_lifetime.Token);
                var models = status.IsRunning
                    ? await service.ListModelsAsync(_lifetime.Token)
                    : Array.Empty<OllamaLocalModel>();
                var free = service.IsLocal ? await Task.Run(service.GetModelsFreeSpace, _lifetime.Token) : (long?)null;
                if (!ReferenceEquals(service, _service)) return;

                _status = status;
                _models = models;
                _freeSpace = free;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.Log($"刷新 Ollama 状态失败: {ex.Message}");
                ShowStatus(Describe(ex), false);
            }
            ApplyState();
        }

        /// <summary>界面全部由这几个字段算出来，状态变了调一次就行。</summary>
        private void ApplyState()
        {
            var status = _status;
            var busy = _running is not null;
            var local = _service.IsLocal;
            var running = status?.IsRunning == true;
            var installed = status?.Installation is not null;
            var upgrade = installed && status!.IsUpgradeAvailable(_latest);
            // 已经能用时不再给部署按钮，否则会把用户自己选的模型换掉
            var ready = running && _models.Any(m => m.Name == _currentModel);

            Tb_State.Text = status is null ? T("Checking", "正在检查…") : DescribeState(status);
            Tb_State.Foreground = (Brush)FindResource(running ? "SuccessBrush" : "PrimaryTextBrush");
            Tb_Version.Text = DescribeVersions(status);
            Tb_Plan.Text = status is null ? ""
                : ready ? string.Format(T("Ready", "已经部署好了，正在使用 {0}。"), _currentModel)
                : DescribePlan(status);
            var fit = OllamaModelCatalog.Assess(_recommended.SizeBytes, Hardware);
            Tb_Hardware.Text = DescribeHardware();
            Tb_Capacity.Text = DescribeCapacity(fit);
            Tb_SlowWarning.Text = fit == OllamaFit.TooLarge
                ? T("TooWeakWarning", "这台电脑的内存不够运行本地模型，下载了也可能跑不起来。建议改用免费渠道或 API Key。")
                : T("SlowWarning", "这台电脑配置偏低，本地模型回复可能比较慢。想要流畅体验可以改用免费渠道或 API Key。");
            Tb_SlowWarning.Visibility = fit is OllamaFit.Slow or OllamaFit.TooLarge ? Visibility.Visible : Visibility.Collapsed;

            Btn_Deploy.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            Btn_Deploy.IsEnabled = !busy && status is not null && (local || running);
            Btn_Refresh.IsEnabled = !busy;
            Btn_Start.IsEnabled = !busy;
            Btn_Start.Visibility = local && installed && !running ? Visibility.Visible : Visibility.Collapsed;
            Btn_Install.IsEnabled = !busy;
            Btn_Install.Visibility = (local && status is { Installation: null, IsRunning: false }) || upgrade ? Visibility.Visible : Visibility.Collapsed;
            Btn_Install.Content = upgrade
                ? string.Format(T("Upgrade", "升级到 {0}"), _latest)
                : string.Format(T("Install", "只安装 Ollama（{0}）"), SetupSize);

            Items_Catalog.IsEnabled = !busy;
            Items_Local.IsEnabled = !busy;
            Panel_Custom.IsEnabled = !busy;

            ShowCatalog();
            foreach (var row in _libraryRows) row.RefreshAction();
            var downloaded = _models.Select(m => m.Name.Split(':')[0]).ToHashSet();
            if (!downloaded.SetEquals(_downloaded))
            {
                _downloaded = downloaded;
                ApplyLibraryFilter();
            }
            ShowLocal();
        }

        private OllamaCatalogRow BuildCatalogRow(OllamaModelInfo model)
        {
            var action = DescribeAction(model.Name);
            var fit = OllamaModelCatalog.Assess(model.SizeBytes, Hardware);
            var recommended = ReferenceEquals(model, _recommended) && fit == OllamaFit.Smooth;

            return new OllamaCatalogRow(model, model.Name, FormatSize(model.SizeBytes), fit,
                recommended ? T("Recommended", "推荐") : DescribeFit(fit),
                LanguageHelper.Get(model.DescriptionKey, Lang, model.DefaultDescription), action.Text, action.Enabled, action.Installed,
                action.Installed ? T("LibraryDownloaded", "已下载") : T("CatalogNotDownloaded", "未下载"),
                recommended ? -1 : (int)fit);
        }

        private void ShowCatalog()
        {
            var rows = OllamaModelCatalog.Models.Concat(_popular).Select(BuildCatalogRow).OrderBy(r => r.Rank).ToList();
            // 别的电脑判断不了跑不跑得动，这组筛选不显示，之前选的也不算
            var fit = Hardware is null ? null : _catalogFit;

            OllamaFilterChip Chip(string text, int count, bool selected, Action apply) => new(text, count, selected, () =>
            {
                apply();
                ShowCatalog();
                Scroll_Catalog.ScrollToTop();
            });

            // 每组的数字不算本组的条件
            var byFit = rows.Where(r => !_catalogDownloaded || r.Installed).ToList();
            var byState = rows.Where(r => fit is null || r.Fit == fit).ToList();

            Panel_CatalogFit.Visibility = Hardware is null ? Visibility.Collapsed : Visibility.Visible;
            Items_CatalogFitChips.ItemsSource = FitChips(byFit.Select(r => r.Fit).ToList(), fit, Chip, f => _catalogFit = f);
            Items_CatalogStateChips.ItemsSource = new[]
            {
                Chip(T("LibraryAllModels", "所有模型"), byState.Count, !_catalogDownloaded, () => _catalogDownloaded = false),
                Chip(T("LibraryDownloaded", "已下载"), byState.Count(r => r.Installed), _catalogDownloaded, () => _catalogDownloaded = true)
            };

            var shown = byState.Where(r => !_catalogDownloaded || r.Installed).ToList();
            Items_Catalog.ItemsSource = shown;
            Btn_CatalogReset.IsEnabled = fit is not null || _catalogDownloaded;
            Tb_CatalogState.Text = shown.Count == 0
                ? T("LibraryNoMatch", "没有找到匹配的模型。")
                : string.Format(T("CatalogCount", "共 {0} 个模型"), shown.Count);
        }

        private List<OllamaFilterChip> FitChips(List<OllamaFit> fits, OllamaFit? selected, Func<string, int, bool, Action, OllamaFilterChip> chip, Action<OllamaFit?> pick)
            => new[] { OllamaFit.Smooth, OllamaFit.Slow, OllamaFit.TooLarge }
                .Select(f => chip(DescribeFit(f), fits.Count(x => x == f), selected == f, () => pick(f)))
                .Prepend(chip(T("CatalogAllFits", "所有情况"), fits.Count, selected is null, () => pick(null)))
                .ToList();

        private void ShowLocal()
        {
            var rows = _models.Select(BuildLocalRow).OrderByDescending(r => r.Highlighted).ToList();
            var fit = Hardware is null ? null : _localFit;
            var keyword = Tb_LocalSearch.Text.Trim();

            OllamaFilterChip Chip(string text, int count, bool selected, Action apply) => new(text, count, selected, () =>
            {
                apply();
                ShowLocal();
                Scroll_Local.ScrollToTop();
            });

            // 每组的数字不算本组的条件
            var searched = rows.Where(r => r.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) || r.Series.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            var byFit = searched.Where(r => _localChat is null || r.IsChat == _localChat).ToList();
            var byPurpose = searched.Where(r => fit is null || r.Fit == fit).ToList();

            Panel_LocalFit.Visibility = Hardware is null ? Visibility.Collapsed : Visibility.Visible;
            Items_LocalFitChips.ItemsSource = FitChips(byFit.Select(r => r.Fit).ToList(), fit, Chip, f => _localFit = f);
            Items_LocalPurposeChips.ItemsSource = new[]
            {
                Chip(T("LibraryAllModels", "所有模型"), byPurpose.Count, _localChat is null, () => _localChat = null),
                Chip(T("LocalChat", "能聊天"), byPurpose.Count(r => r.IsChat), _localChat == true, () => _localChat = true),
                Chip(T("NotChat", "不能聊天"), byPurpose.Count(r => !r.IsChat), _localChat == false, () => _localChat = false)
            };

            var shown = byPurpose.Where(r => _localChat is null || r.IsChat == _localChat).ToList();
            Items_Local.ItemsSource = shown;
            Btn_LocalReset.IsEnabled = keyword.Length > 0 || fit is not null || _localChat is not null;
            Tb_LocalState.Text = _models.Count == 0
                ? _status is { IsRunning: true } ? T("NoModels", "还没有下载模型。") : T("NotRunningModels", "Ollama 没有运行，看不到模型。可以到“部署”页启动。")
                : shown.Count == 0 ? T("LibraryNoMatch", "没有找到匹配的模型。")
                : string.Format(T("LocalCount", "共 {0} 个模型，占用 {1}"), shown.Count, FormatSize(shown.Sum(r => r.SizeBytes)));
        }

        private void LocalSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            ShowLocal();
            Scroll_Local.ScrollToTop();
        }

        private void LocalReset_Click(object sender, RoutedEventArgs e)
        {
            _localFit = null;
            _localChat = null;
            Tb_LocalSearch.Clear();
            ShowLocal();
            Scroll_Local.ScrollToTop();
        }

        private void CardScroll_SizeChanged(object sender, SizeChangedEventArgs e) => CardGridColumns = CardColumns(e.NewSize.Width);

        private void CatalogReset_Click(object sender, RoutedEventArgs e)
        {
            _catalogFit = null;
            _catalogDownloaded = false;
            ShowCatalog();
            Scroll_Catalog.ScrollToTop();
        }

        private OllamaRowAction DescribeAction(string model)
        {
            var installed = _models.Any(m => m.Name == model);
            var inUse = installed && model == _currentModel;
            var text = inUse ? T("InUse", "使用中") : installed ? T("Use", "使用") : T("Download", "下载");
            return new OllamaRowAction(text, !inUse && _running is null, installed);
        }

        private string DescribeFit(OllamaFit fit) => fit switch
        {
            OllamaFit.Smooth => T("FitSmooth", "能流畅运行"),
            OllamaFit.Slow => T("FitSlow", "能跑，但比较慢"),
            OllamaFit.TooLarge => T("FitTooLarge", "这台电脑带不动"),
            _ => ""
        };

        private OllamaLocalRow BuildLocalRow(OllamaLocalModel model)
        {
            var inUse = model.Name == _currentModel;
            var text = !model.IsChatModel ? T("NotChat", "不能聊天") : inUse ? T("InUse", "使用中") : T("Use", "使用");
            // 嵌入模型不聊天，跑得快慢无所谓
            var fit = model.IsChatModel ? OllamaModelCatalog.Assess(model.SizeBytes, Hardware) : OllamaFit.Unknown;
            return new OllamaLocalRow(model.Name, model.SizeBytes, FormatSize(model.SizeBytes), fit, DescribeFit(fit),
                model.Family.Length > 0 ? model.Family : "—", text, model.IsChatModel && !inUse, model.IsChatModel, inUse);
        }

        private string DescribeState(OllamaStatus status)
        {
            if (status.IsRunning) return T("StateRunning", "Ollama 正在运行");
            if (!status.IsLocal) return string.Format(T("StateRemoteDown", "连不上 {0}。这个地址不在本机，安装和启动要到那台电脑上操作。"), _service.Url);
            return status.Installation is null
                ? T("StateMissing", "这台电脑还没有安装 Ollama")
                : T("StateStopped", "Ollama 已安装，但没有运行");
        }

        private string DescribeVersions(OllamaStatus? status)
        {
            var parts = new List<string>();
            if (status?.Version is { } version) parts.Add(string.Format(T("CurrentVersion", "当前版本 {0}"), version));
            if (_latest is not null) parts.Add(string.Format(T("LatestVersion", "最新版本 {0}"), _latest));
            return string.Join(" · ", parts);
        }

        private string DescribePlan(OllamaStatus status)
        {
            if (!status.IsLocal && !status.IsRunning) return "";

            var steps = new List<string>();
            if (!status.IsRunning)
            {
                steps.Add(status.Installation is null
                    ? string.Format(T("PlanInstall", "安装 Ollama（下载约 {0}）"), SetupSize)
                    : T("PlanStart", "启动 Ollama"));
            }
            if (!_models.Any(m => m.IsChatModel))
                steps.Add(string.Format(T("PlanModel", "下载模型 {0}（{1}）"), _recommended.Name, FormatSize(_recommended.SizeBytes)));
            steps.Add(T("PlanFill", "把模型填回设置"));

            return string.Format(T("Plan", "一键部署会：{0}"), string.Join(" → ", steps));
        }

        private string DescribeHardware()
        {
            if (Hardware is not { } hardware)
                return string.Format(T("HardwareRemote", "模型装在 {0} 上，这里看不到那台电脑的配置，不判断跑不跑得动。"), _service.BaseUri.Host);

            var memory = string.Format(T("HardwareMemory", "内存 {0}"), FormatSize(hardware.MemoryBytes));
            var video = hardware.UsableVideoMemory > 0
                ? string.Format(T("HardwareVideo", "显卡 {0}，显存 {1}"), hardware.GpuName, FormatSize(hardware.UsableVideoMemory))
                : T("HardwareNoVideo", "没有检测到独立显卡");
            var text = string.Format(T("Hardware", "这台电脑：{0}，{1}"), memory, video);

            // 查不出来时 GetFreeSpace 给的是 long.MaxValue
            return _freeSpace is { } free and not long.MaxValue
                ? $"{text} · {string.Format(T("HardwareDisk", "模型盘剩余 {0}"), FormatSize(free))}"
                : text;
        }

        private string DescribeCapacity(OllamaFit fit)
        {
            if (Hardware is not { } hardware) return "";

            var usable = FormatSize(OllamaModelCatalog.UsableLimit(hardware));
            return fit switch
            {
                OllamaFit.Smooth => string.Format(T("CapacityFull", "能流畅运行约 {0} 以内的模型，约 {1} 以内能跑但比较慢。"),
                    FormatSize(OllamaModelCatalog.SmoothLimit(hardware)), usable),
                OllamaFit.Slow => string.Format(T("CapacitySlow", "本地模型只能慢慢跑，约 {0} 以内勉强可用。"), usable),
                _ => T("CapacityNone", "这台电脑配置偏低，不建议运行本地模型。")
            };
        }

        private string DescribePhase(OllamaTaskPhase phase) => phase switch
        {
            OllamaTaskPhase.DownloadingSetup => T("PhaseDownloadSetup", "正在下载 Ollama 安装包…"),
            OllamaTaskPhase.VerifyingSetup => T("PhaseVerifySetup", "正在校验安装包…"),
            OllamaTaskPhase.InstallingSetup => T("PhaseInstall", "正在安装 Ollama，请稍候…"),
            OllamaTaskPhase.StartingServer => T("PhaseStart", "正在启动 Ollama…"),
            OllamaTaskPhase.PullingManifest => T("PhaseManifest", "正在连接模型仓库…"),
            OllamaTaskPhase.PullingModel => T("PhasePull", "正在下载模型…"),
            OllamaTaskPhase.VerifyingModel => T("PhaseVerifyModel", "正在校验模型…"),
            _ => T("PhaseWrite", "正在写入模型…")
        };

        private string Describe(Exception ex)
        {
            if (ex is not OllamaOperationException failure)
                return string.Format(T("ErrorGeneric", "操作失败：{0}"), ex.Message);

            return failure.Failure switch
            {
                OllamaFailure.VersionLookup => T("ErrorVersion", "查不到 Ollama 最新版本，请检查网络后重试。"),
                OllamaFailure.Download => T("ErrorDownload", "安装包下载失败，请检查网络后重试。已下载的部分会保留。"),
                OllamaFailure.DiskSpace => string.Format(T("ErrorDisk", "磁盘空间不足：需要 {0}，只剩 {1}。"), FormatSize(failure.Required), FormatSize(failure.Available)),
                OllamaFailure.Signature => T("ErrorSignature", "安装包没有通过校验，不是 Ollama 官方签名的文件，已经删除。请重试，或到 ollama.com 手动下载。"),
                OllamaFailure.Setup => string.Format(T("ErrorSetup", "安装失败（{0}）。可以重试，或到 ollama.com 手动下载安装。"), failure.Message),
                OllamaFailure.NotInstalled => T("ErrorNotInstalled", "还没有安装 Ollama，请先到“部署”页点一键部署。"),
                OllamaFailure.ServerStart when !_service.IsLocal
                    => string.Format(T("StateRemoteDown", "连不上 {0}。这个地址不在本机，安装和启动要到那台电脑上操作。"), _service.Url),
                OllamaFailure.ServerStart => T("ErrorStart", "Ollama 启动失败。可以从开始菜单手动打开 Ollama，再点刷新。"),
                _ when failure.Message.Contains("newer version", StringComparison.OrdinalIgnoreCase)
                    => T("ErrorOutdated", "这个模型需要更新版本的 Ollama，请到“部署”页升级。"),
                _ => string.Format(T("ErrorApi", "Ollama 返回错误：{0}"), failure.Message)
            };
        }

        private static string FormatSize(long bytes)
            => bytes >= GiB ? $"{bytes / GiB:0.0} GB" : $"{bytes / (1024.0 * 1024):0} MB";

        // 慢的时候正要看清多慢，按 MB 取整会显示成 0 MB/s
        private static string FormatSpeed(double bytesPerSecond)
            => bytesPerSecond >= 1024 * 1024 ? $"{bytesPerSecond / (1024 * 1024):0.0} MB/s" : $"{bytesPerSecond / 1024:0} KB/s";

        private void ShowStatus(string? text, bool? ok)
        {
            Tb_Status.Text = text ?? "";
            Tb_Status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            Tb_Status.Foreground = (Brush)FindResource(ok switch
            {
                true => "SuccessBrush",
                false => "ErrorBrush",
                _ => "SecondaryTextBrush"
            });
        }

        /// <summary>同一时间只跑一个操作：装、拉、删互相之间都会打架。</summary>
        private async Task RunAsync(string title, Func<IProgress<OllamaTaskProgress>, CancellationToken, Task> work)
        {
            if (_running is not null) return;

            using var running = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _running = running;
            _speedClock.Restart();
            _speedBytes = 0;
            _speedText = "";

            ShowStatus(null, null);
            Tb_TaskTitle.Text = title;
            Tb_TaskDetail.Text = "";
            Bar_Task.IsIndeterminate = true;
            Btn_Cancel.IsEnabled = true;
            Panel_Progress.Visibility = Visibility.Visible;
            ApplyState();

            _lastProgress = null;
            _slowDismissed = false;
            ResetSlowWindow(null, 0);
            // 进度只在收到数据时才报，完全卡住时靠它发现
            var slowTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(1).Ticks, Math.Max(SlowPeriod.Ticks / 4, 1)))
            };
            slowTimer.Tick += (_, _) => CheckSlow();
            slowTimer.Start();

            try
            {
                await work(new Progress<OllamaTaskProgress>(OnProgress), running.Token);
            }
            catch (OperationCanceledException)
            {
                ShowStatus(T("Cancelled", "已取消。再次开始时会接着下载。"), null);
            }
            catch (Exception ex)
            {
                Logger.Log($"一键化本地部署操作失败: {ex}");
                ShowStatus(Describe(ex), false);
            }
            finally
            {
                _running = null;
                slowTimer.Stop();
                _proxyScan?.Cancel();
                Panel_Slow.Visibility = Visibility.Collapsed;
                Panel_Progress.Visibility = Visibility.Collapsed;
            }

            if (!_lifetime.IsCancellationRequested) await RefreshAsync();
        }

        private void OnProgress(OllamaTaskProgress progress)
        {
            if (_running is null) return;

            _lastProgress = progress;
            // 换了阶段或进度倒退（重连后各层重新上报）就重新计时
            if (progress.Phase != _slowPhase || progress.Completed < _slowStartBytes)
            {
                ResetSlowWindow(progress.Phase, progress.Completed);
                if (!IsSlowWatched(progress.Phase)) Panel_Slow.Visibility = Visibility.Collapsed;
            }

            Tb_TaskTitle.Text = DescribePhase(progress.Phase);
            // 安装程序不能中途打断，取消了它也会继续跑完
            Btn_Cancel.IsEnabled = progress.Phase is not (OllamaTaskPhase.VerifyingSetup or OllamaTaskPhase.InstallingSetup);

            var measurable = progress.Phase is OllamaTaskPhase.DownloadingSetup or OllamaTaskPhase.PullingModel && progress.Total > 0;
            Bar_Task.IsIndeterminate = !measurable;
            if (!measurable)
            {
                Tb_TaskDetail.Text = "";
                return;
            }

            Bar_Task.Value = progress.Completed * 100d / progress.Total;
            if (_speedClock.Elapsed >= TimeSpan.FromSeconds(1))
            {
                var perSecond = (progress.Completed - _speedBytes) / _speedClock.Elapsed.TotalSeconds;
                _speedText = _speedBytes > 0 && perSecond > 0 ? $" · {FormatSpeed(perSecond)}" : "";
                _speedBytes = progress.Completed;
                _speedClock.Restart();
            }
            Tb_TaskDetail.Text = $"{FormatSize(progress.Completed)} / {FormatSize(progress.Total)}{_speedText}";
        }

        private void Choose(string model)
        {
            if (_onChosen?.Invoke(model, _service.Url) != true)
            {
                ShowStatus(T("ChooseLost", "设置页已经切到别的渠道，模型没有填进去。请回到设置页重新打开一键化本地部署。"), false);
                return;
            }
            _currentModel = model;
            ShowStatus(string.Format(T("Chosen", "已选用 {0}，回到设置页保存后生效。"), model), true);
        }

        /// <summary>明显带不动的模型先问一句，下完几个 GB 才发现跑不起来太亏了。</summary>
        private bool ConfirmFit(string model, long size)
            => OllamaModelCatalog.Assess(size, Hardware) != OllamaFit.TooLarge
               || _confirm(string.Format(T("TooLargeConfirm", "{0} 大约 {1}，这台电脑的内存和显存放不下，下载了也可能跑不起来。还要继续吗？"), model, FormatSize(size)), MessageBoxImage.Warning);

        private Task PullAsync(string model, long size) => RunAsync(T("Preparing", "正在准备…"), async (progress, token) =>
        {
            // 手填的名字不知道体积。查到了磁盘检查和配置判断才有依据，查不到也照样下载
            if (size <= 0) size = await _library.GetSizeAsync(model, token) ?? 0;
            if (!ConfirmFit(model, size)) return;

            await _service.EnsureRunningAsync(progress, token);
            await _service.PullModelAsync(model, size, progress, token);

            if (string.IsNullOrWhiteSpace(_currentModel)) Choose(model);
            else ShowStatus(string.Format(T("Pulled", "{0} 下载完成。"), model), true);
        });

        private async void Deploy_Click(object sender, RoutedEventArgs e)
        {
            if (!_models.Any(m => m.IsChatModel) && !ConfirmFit(_recommended.Name, _recommended.SizeBytes)) return;

            await RunAsync(T("Preparing", "正在准备…"), async (progress, token) =>
                Choose(await _service.DeployAsync(_recommended, progress, token)));
        }

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            if (_status?.Installation is not null
                && !_confirm(T("UpgradeConfirm", "升级时 Ollama 会先关闭，正在进行的聊天会中断。现在升级吗？"), MessageBoxImage.Question))
                return;

            await RunAsync(T("Preparing", "正在准备…"), async (progress, token) =>
            {
                await _service.InstallLatestAsync(progress, token);
                await _service.EnsureRunningAsync(progress, token);
                ShowStatus(T("Installed", "Ollama 已安装好。"), true);
            });
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            await RunAsync(T("PhaseStart", "正在启动 Ollama…"), (progress, token) => _service.EnsureRunningAsync(progress, token));
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            ShowStatus(null, null);
            await RefreshAsync();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Btn_Cancel.IsEnabled = false;
            _running?.Cancel();
        }

        private static bool IsSlowWatched(OllamaTaskPhase? phase)
            => phase is OllamaTaskPhase.DownloadingSetup or OllamaTaskPhase.PullingModel;

        private void ResetSlowWindow(OllamaTaskPhase? phase, long completed)
        {
            _slowPhase = phase;
            _slowStartBytes = completed;
            _slowClock.Restart();
        }

        /// <summary>
        /// 每满一段 <see cref="SlowPeriod"/> 算一次平均速度。慢就提示，恢复了就收起；
        /// 用户点了“不用了”，这次任务就不再提示。
        /// </summary>
        private void CheckSlow()
        {
            if (_running is null || _slowDismissed || _lastProgress is not { } progress) return;
            if (!IsSlowWatched(progress.Phase) || progress.Total <= 0 || _slowClock.Elapsed < SlowPeriod) return;

            var perSecond = (progress.Completed - _slowStartBytes) / _slowClock.Elapsed.TotalSeconds;
            var remaining = progress.Total - progress.Completed;
            ResetSlowWindow(progress.Phase, progress.Completed);

            if (perSecond >= SlowBytesPerSecond || remaining <= SlowBytesPerSecond * SlowPeriod.TotalSeconds)
            {
                // 正在填代理时不收，免得字打到一半框没了
                if (!Panel_Slow.IsKeyboardFocusWithin) Panel_Slow.Visibility = Visibility.Collapsed;
                return;
            }

            var setup = progress.Phase == OllamaTaskPhase.DownloadingSetup;
            Tb_SlowHint.Text = setup
                ? string.Format(T("SlowSetup", "安装包下载很慢（{0}）。有代理的话可以填在这里，改经代理从 GitHub 下载，已下载的部分不会丢。"), FormatSpeed(perSecond))
                : string.Format(T("SlowPull", "模型下载很慢（{0}）。可以断开重连：Ollama 会留着已下载的部分，重新建立下载连接，速度通常能恢复。"), FormatSpeed(perSecond));
            if (Panel_Slow.Visibility == Visibility.Visible) return;

            Panel_Proxy.Visibility = setup ? Visibility.Visible : Visibility.Collapsed;
            Btn_UseProxy.Visibility = setup ? Visibility.Visible : Visibility.Collapsed;
            Btn_Reconnect.Visibility = setup ? Visibility.Collapsed : Visibility.Visible;
            Tb_ProxyFound.Visibility = Visibility.Collapsed;
            if (setup && string.IsNullOrWhiteSpace(Tb_Proxy.Text)) Tb_Proxy.Text = DefaultProxyText();
            Panel_Slow.Visibility = Visibility.Visible;
            // 不会填代理的用户也能点一下就用上
            if (setup && string.IsNullOrWhiteSpace(Tb_Proxy.Text)) _ = DetectProxyAsync(false);
        }

        /// <summary>找本机代理软件的端口，找到就填进框里。<paramref name="replace"/> 为 false 时不覆盖用户已经填的。</summary>
        private async Task DetectProxyAsync(bool replace)
        {
            _proxyScan?.Cancel();
            using var scan = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _proxyScan = scan;
            Btn_DetectProxy.IsEnabled = false;
            ShowProxyFound(T("ProxyDetecting", "正在查找本机的代理软件…"));
            try
            {
                var result = await _proxyDetector.DetectAsync(scan.Token);
                if (result.Found is { } found)
                {
                    if (replace || string.IsNullOrWhiteSpace(Tb_Proxy.Text)) Tb_Proxy.Text = found.ToString().TrimEnd('/');
                    ShowProxyFound(string.Format(T("ProxyDetected", "找到本机代理 {0}（{1} 常用的端口），点“使用代理”就行。"),
                        found.Authority, result.Software));
                }
                else if (result.SystemProxy is { } system)
                {
                    ShowProxyFound(string.Format(T("ProxyIsSystem", "系统代理已经开着（{0}），下载本来就经过它。还是慢的话，可以在代理软件里换个节点。"),
                        system.Authority));
                }
                else
                {
                    ShowProxyFound(T("ProxyNotFound", "没找到本机代理。开着代理软件的话，把它设置里的端口填进来，例如 http://127.0.0.1:7890。"));
                }
            }
            catch (OperationCanceledException)
            {
                // 下载结束、窗口关了或又点了一次检测
            }
            catch (Exception ex)
            {
                Logger.Log($"查找本机代理失败: {ex.Message}");
                Tb_ProxyFound.Visibility = Visibility.Collapsed;
            }
            finally
            {
                if (ReferenceEquals(_proxyScan, scan))
                {
                    _proxyScan = null;
                    Btn_DetectProxy.IsEnabled = true;
                }
            }
        }

        private void ShowProxyFound(string text)
        {
            Tb_ProxyFound.Text = text;
            Tb_ProxyFound.Visibility = Visibility.Visible;
        }

        private void DetectProxy_Click(object sender, RoutedEventArgs e) => _ = DetectProxyAsync(true);

        /// <summary>先填这次运行里用过的，其次是设置里开着的网络代理。</summary>
        private string DefaultProxyText()
        {
            if (_downloadProxy is { } used) return used.ToString().TrimEnd('/');

            var proxy = _settings.Proxy;
            if (proxy is null || !proxy.IsEnabled || proxy.FollowSystemProxy || string.IsNullOrWhiteSpace(proxy.Address)) return "";

            var address = proxy.Address.Trim();
            if (address.Contains("://", StringComparison.Ordinal)) return address;
            // 和聊天请求拼代理的写法一致
            var scheme = string.Equals(proxy.Protocol, "socks", StringComparison.OrdinalIgnoreCase) ? "socks5" : proxy.Protocol?.Trim();
            return $"{(string.IsNullOrEmpty(scheme) ? "http" : scheme)}://{address}";
        }

        private void UseProxy_Click(object sender, RoutedEventArgs e)
        {
            var proxy = OllamaInstaller.ParseProxy(Tb_Proxy.Text);
            if (proxy is null)
            {
                Tb_SlowHint.Text = T("ProxyInvalid", "代理地址不对。例如 http://127.0.0.1:7890 或 socks5://127.0.0.1:1080。");
                return;
            }

            _downloadProxy = proxy;
            _service.UseDownloadProxy(proxy);
            Panel_Slow.Visibility = Visibility.Collapsed;
            ResetSlowWindow(_lastProgress?.Phase, _lastProgress?.Completed ?? 0);
            // Authority 不带用户名密码
            ShowStatus(string.Format(T("ProxyApplied", "已改用代理 {0} 下载，接着已下载的部分继续。"), proxy.Authority), null);
        }

        private void Reconnect_Click(object sender, RoutedEventArgs e)
        {
            _service.ReconnectPull();
            Panel_Slow.Visibility = Visibility.Collapsed;
            ResetSlowWindow(_lastProgress?.Phase, _lastProgress?.Completed ?? 0);
            ShowStatus(T("Reconnected", "已断开重连，接着已下载的部分继续。"), null);
        }

        private void SlowDismiss_Click(object sender, RoutedEventArgs e)
        {
            _slowDismissed = true;
            Panel_Slow.Visibility = Visibility.Collapsed;
        }

        private async void CatalogAction_Click(object sender, RoutedEventArgs e)
        {
            var row = (OllamaCatalogRow)((Button)sender).Tag;
            if (row.Installed) UseModel(row.Name);
            else await PullAsync(row.Name, row.Model.SizeBytes);
        }

        private async void PullCustom_Click(object sender, RoutedEventArgs e)
        {
            var model = Tb_CustomModel.Text.Trim();
            if (!OllamaClient.IsValidModelName(model))
            {
                ShowStatus(T("InvalidName", "模型名不对。要填 ollama.com 上的名字，比如 qwen3:4b。"), false);
                return;
            }
            await PullAsync(model, 0);
        }

        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 用到才联网，只是来装 Ollama 的用户不用等这个
            if (_libraryModels is null && (Tab_Catalog.IsSelected || Tab_Library.IsSelected)) _ = LoadLibraryAsync();
        }

        private async Task LoadLibraryAsync()
        {
            if (_libraryLoading) return;

            _libraryLoading = true;
            _libraryOffline = false;
            ApplyLibraryFilter();
            try
            {
                var cached = await _library.LoadCachedAsync(_lifetime.Token);
                if (cached is not null) ShowLibrary(cached.Models);
                if (cached is { IsFresh: true }) return;

                var latest = await _library.RefreshAsync(_lifetime.Token);
                if (latest is not null) ShowLibrary(latest);
                else _libraryOffline = true;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _libraryLoading = false;
                ApplyLibraryFilter();
            }
        }

        private void ShowLibrary(IReadOnlyList<OllamaLibraryModel> models)
        {
            _libraryModels = models;
            var chatModels = models.Where(m => m.IsChatModel && !m.IsCloudOnly).ToList();
            _librarySeries = chatModels.Where(m => m.Series.Length > 0).GroupBy(m => m.Series)
                .Where(g => g.Count() >= MinSeriesModels).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
            _libraryCapabilities = chatModels.SelectMany(m => m.Capabilities).Where(c => c != "cloud")
                .GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
            _libraryRows = chatModels.Select(BuildLibraryRow).ToList();
            ApplyLibraryFilter();
            _ = LoadPopularAsync(models);
        }

        private OllamaLibraryRow BuildLibraryRow(OllamaLibraryModel model)
        {
            return new OllamaLibraryRow(model, _librarySeries.Contains(model.Series) ? model.Series : "",
                string.Join(" · ", model.Capabilities.Where(c => c != "cloud").Select(DescribeCapability)),
                model.Tags.Select(tag => BuildTagOption(model, tag, 0)).ToList(), DescribeAction);
        }

        private string DescribeCapability(string capability) => capability switch
        {
            "tools" => T("CapabilityTools", "工具调用"),
            "vision" => T("CapabilityVision", "看图"),
            "thinking" => T("CapabilityThinking", "深度思考"),
            "audio" => T("CapabilityAudio", "语音"),
            // 页面以后多出来的能力照原文显示，筛选不受影响
            _ => capability
        };

        private OllamaTagOption BuildTagOption(OllamaLibraryModel model, string tag, long size)
        {
            var fit = OllamaModelCatalog.Assess(size, Hardware);
            var text = $"{tag} · {(size > 0 ? FormatSize(size) : T("SizeUnknown", "大小未知"))}";
            return new OllamaTagOption(tag, $"{model.Name}:{tag}", size, size > 0 ? FormatSize(size) : "—", fit, DescribeFit(fit), text);
        }

        private void ApplyLibraryFilter()
        {
            var keyword = Tb_LibrarySearch.Text.Trim();
            var searched = _libraryRows.Where(r => r.Model.Matches(keyword)).ToList();

            bool InState(OllamaLibraryRow r) => !_filterDownloaded || _downloaded.Contains(r.Name);
            bool HasCapability(OllamaLibraryRow r) => _filterCapability is null || r.Model.Capabilities.Contains(_filterCapability);
            bool InSeries(OllamaLibraryRow r) => _filterSeries is null || r.Series == _filterSeries;

            OllamaFilterChip Chip(string text, int count, bool selected, Action apply) => new(text, count, selected, () =>
            {
                apply();
                FilterLibrary();
            });

            // 每组的数字不算本组的条件，不然选中一项后同组其他按钮全变成 0
            var byState = searched.Where(r => HasCapability(r) && InSeries(r)).ToList();
            var byCapability = searched.Where(r => InState(r) && InSeries(r)).ToList();
            var bySeries = searched.Where(r => InState(r) && HasCapability(r)).ToList();

            Items_StateChips.ItemsSource = new[]
            {
                Chip(T("LibraryAllModels", "所有模型"), byState.Count, !_filterDownloaded, () => _filterDownloaded = false),
                Chip(T("LibraryDownloaded", "已下载"), byState.Count(r => _downloaded.Contains(r.Name)), _filterDownloaded, () => _filterDownloaded = true)
            };
            Items_CapabilityChips.ItemsSource = _libraryCapabilities
                .Select(c => Chip(DescribeCapability(c), byCapability.Count(r => r.Model.Capabilities.Contains(c)), _filterCapability == c, () => _filterCapability = c))
                .Prepend(Chip(T("LibraryAllCapabilities", "所有能力"), byCapability.Count, _filterCapability is null, () => _filterCapability = null))
                .ToList();
            Items_SeriesChips.ItemsSource = _librarySeries.Append("")
                .Select(s => Chip(s.Length > 0 ? s : T("LibraryOtherSeries", "其他"), bySeries.Count(r => r.Series == s), _filterSeries == s, () => _filterSeries = s))
                .Prepend(Chip(T("LibraryAllSeries", "所有系列"), bySeries.Count, _filterSeries is null, () => _filterSeries = null))
                .ToList();

            var rows = byState.Where(InState);
            _shownRows = (Rb_SortName.IsChecked == true ? rows.OrderBy(r => r.Name, StringComparer.Ordinal) : rows).ToList();
            ShowLibraryCards();
            Btn_LibraryReset.IsEnabled = keyword.Length > 0 || _filterDownloaded || _filterCapability is not null || _filterSeries is not null;
            ShowLibraryState(_shownRows.Count);
        }

        private static int CardColumns(double width) => Math.Max(1, (int)((width - SystemParameters.VerticalScrollBarWidth) / (MinCardWidth + CardGap)));

        private int LibraryColumns() => CardColumns(Items_Library.ActualWidth);

        private void ShowLibraryCards()
        {
            _libraryColumns = LibraryColumns();
            Items_Library.ItemsSource = _shownRows.Chunk(_libraryColumns).Select(rows => new OllamaLibraryBlock(rows, _libraryColumns)).ToList();
        }

        private void Library_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (LibraryColumns() != _libraryColumns) ShowLibraryCards();
        }

        /// <summary>换了条件要回到开头。下载完刷新不走这里，不能把用户看到的位置弄丢。</summary>
        private void FilterLibrary()
        {
            ApplyLibraryFilter();
            if (Items_Library.Items.Count > 0) Items_Library.ScrollIntoView(Items_Library.Items[0]);
        }

        private void ShowLibraryState(int shown)
        {
            Tb_LibraryState.Text =
                _libraryModels is null
                    ? _libraryOffline
                        ? T("LibraryFailed", "暂时获取不到 ollama.com 的模型列表。“推荐模型”页不受影响。")
                        : T("LibraryLoading", "正在获取模型列表…")
                : shown == 0 ? T("LibraryNoMatch", "没有找到匹配的模型。")
                : _libraryOffline ? T("LibraryStale", "暂时连不上 ollama.com，显示的是上次保存的列表。")
                : string.Format(T("LibraryCount", "共 {0} 个模型，数据来自 ollama.com，说明只有英文。"), shown);
            Btn_LibraryRetry.Visibility = _libraryOffline ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LibrarySearch_TextChanged(object sender, TextChangedEventArgs e) => FilterLibrary();

        private void LibrarySort_Click(object sender, RoutedEventArgs e) => FilterLibrary();

        private void FilterChip_Click(object sender, RoutedEventArgs e) => ((OllamaFilterChip)((Button)sender).Tag).Apply();

        private void LibraryReset_Click(object sender, RoutedEventArgs e)
        {
            _filterDownloaded = false;
            _filterCapability = null;
            _filterSeries = null;
            Tb_LibrarySearch.Clear();
            FilterLibrary();
        }

        private async void LibraryRetry_Click(object sender, RoutedEventArgs e) => await LoadLibraryAsync();

        /// <summary>推荐页只放这台电脑能流畅运行的，模型库里挑几个最热门的补上。</summary>
        private async Task LoadPopularAsync(IReadOnlyList<OllamaLibraryModel> models)
        {
            _popularLookup?.Cancel();
            using var lookup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _popularLookup = lookup;

            _popular = new List<OllamaModelInfo>();
            ApplyState();
            if (Hardware is not { } hardware) return;

            try
            {
                foreach (var model in OllamaModelCatalog.PopularCandidates(models.Where(m => m.IsChatModel && !m.IsCloudOnly)))
                {
                    var sizes = await _library.GetSizesAsync(model, lookup.Token);
                    lookup.Token.ThrowIfCancellationRequested();

                    if (OllamaModelCatalog.ChooseTag(sizes, hardware) is not { } tag
                        || OllamaModelCatalog.Assess(sizes[tag], hardware) != OllamaFit.Smooth)
                        continue;

                    _popular.Add(new OllamaModelInfo($"{model.Name}:{tag}", sizes[tag], "", model.Description, Tiered: false));
                    ApplyState();
                    if (_popular.Count >= MaxPopularModels) break;
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (ReferenceEquals(_popularLookup, lookup)) _popularLookup = null;
            }
        }

        private async void LibraryRow_Loaded(object sender, RoutedEventArgs e)
        {
            var element = (FrameworkElement)sender;
            if (element.DataContext is not OllamaLibraryRow row) return;

            try
            {
                // 划走的行要等滚动停下约 500 毫秒才被虚拟化卸载，所以不能等卸载来取消，要看它是不是真的停在屏幕上
                do
                {
                    await Task.Delay(SizeLookupDelay, _lifetime.Token);
                    if (!element.IsLoaded) return;
                } while (!IsOnScreen(element));

                var sizes = await _library.GetSizesAsync(row.Model, _lifetime.Token);
                row.SetOptions(row.Model.Tags.Select(tag => BuildTagOption(row.Model, tag, sizes.GetValueOrDefault(tag))).ToList(),
                    OllamaModelCatalog.ChooseTag(sizes, Hardware));
            }
            catch (OperationCanceledException)
            {
            }
        }

        private bool IsOnScreen(FrameworkElement element)
        {
            // 已经从列表上摘下来、Unloaded 还没轮到时算不了坐标
            if (!Items_Library.IsAncestorOf(element)) return false;

            var top = element.TranslatePoint(new Point(0, 0), Items_Library).Y;
            return top < Items_Library.ActualHeight && top + element.ActualHeight > 0;
        }

        private async void LibraryAction_Click(object sender, RoutedEventArgs e)
        {
            var row = (OllamaLibraryRow)((Button)sender).Tag;
            if (row.Action.Installed) UseModel(row.Selected.Model);
            else await PullAsync(row.Selected.Model, row.Selected.SizeBytes);
        }

        private void UseModel_Click(object sender, RoutedEventArgs e)
            => UseModel(((OllamaLocalRow)((Button)sender).Tag).Name);

        private void UseModel(string model)
        {
            Choose(model);
            ApplyState();
        }

        private async void DeleteModel_Click(object sender, RoutedEventArgs e)
        {
            var row = (OllamaLocalRow)((Button)sender).Tag;
            var question = row.Name == _currentModel
                ? T("DeleteInUseConfirm", "{0} 正在使用，删除后聊天会报错，需要重新选模型。确定删除吗？")
                : T("DeleteConfirm", "确定删除 {0} 吗？之后要用还得重新下载。");

            if (!_confirm(string.Format(question, row.Name), MessageBoxImage.Warning)) return;

            await RunAsync(string.Format(T("Deleting", "正在删除 {0}…"), row.Name), async (_, token) =>
            {
                await _service.DeleteModelAsync(row.Name, token);
                ShowStatus(string.Format(T("Deleted", "已删除 {0}。"), row.Name), true);
            });
        }
    }
}
