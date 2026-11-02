using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VPetLLM.Utils.Localization;

namespace VPetLLM.UI.Windows
{
    public sealed record OllamaCatalogRow(OllamaModelInfo Model, string Name, string Size, OllamaFit Fit, string Badge, string Description, string ActionText, bool CanAct, bool Installed, int Rank);

    public sealed record OllamaLocalRow(string Name, string Size, string UseText, bool CanUse);

    public sealed record OllamaRowAction(string Text, bool Enabled, bool Installed);

    public sealed record OllamaTagOption(string Tag, string Model, long SizeBytes, OllamaFit Fit, string FitText, string Text);

    /// <summary>模型库里的一行。体积是滚到这行才查的，查到之后下拉项和默认规格都会变。</summary>
    public sealed class OllamaLibraryRow : INotifyPropertyChanged
    {
        private readonly Func<string, OllamaRowAction> _describeAction;
        private OllamaTagOption _selected;
        private bool _chosenByUser;

        public OllamaLibraryRow(OllamaLibraryModel model, string details, IReadOnlyList<OllamaTagOption> options, Func<string, OllamaRowAction> describeAction)
        {
            Model = model;
            Details = details;
            Options = options;
            _selected = options[0];
            _describeAction = describeAction;
            Action = describeAction(_selected.Model);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public OllamaLibraryModel Model { get; }
        public string Name => Model.Name;
        public string Description => Model.Description;
        public string Details { get; }
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

        // 快速滚动时划过去的行不发请求
        private static readonly TimeSpan SizeLookupDelay = TimeSpan.FromMilliseconds(200);

        private static winOllamaManager? _instance;

        private readonly Setting _settings;
        private readonly OllamaHardware _hardware;
        private readonly OllamaLibrary _library;
        private readonly OllamaModelInfo _recommended;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Stopwatch _speedClock = new();
        private readonly Func<string, MessageBoxImage, bool> _confirm;

        private OllamaManagerService _service;
        private Func<string, string, bool>? _onChosen;
        private string _currentModel;
        private OllamaStatus? _status;
        private IReadOnlyList<OllamaLocalModel> _models = Array.Empty<OllamaLocalModel>();
        private string? _latest;
        private CancellationTokenSource? _running;
        private long _speedBytes;
        private string _speedText = "";
        private long? _freeSpace;
        private IReadOnlyList<OllamaLibraryModel>? _libraryModels;
        private List<OllamaLibraryRow> _libraryRows = new();
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

            _instance = new winOllamaManager(settings, OllamaManagerService.Create(settings, url), currentModel, onChosen) { Owner = owner };
            _instance.Show();
        }

        /// <param name="confirm">升级、删除前的确认，返回 true 才继续。不传时弹系统确认框。</param>
        public winOllamaManager(Setting settings, OllamaManagerService service, string? currentModel, Func<string, string, bool> onChosen,
            Func<string, MessageBoxImage, bool>? confirm = null, OllamaLibrary? library = null, OllamaHardware? hardware = null)
        {
            InitializeComponent();
            _settings = settings;
            _library = library ?? OllamaLibrary.Shared;
            _hardware = hardware ?? OllamaHardware.Detect();
            _service = service;
            _currentModel = currentModel?.Trim() ?? "";
            _onChosen = onChosen;
            _confirm = confirm ?? ((text, image) => MessageBox.Show(this, text, Title, MessageBoxButton.YesNo, image) == MessageBoxResult.Yes);
            _recommended = OllamaModelCatalog.Recommend(_hardware);
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
                _service = next;
                _status = null;
                _models = Array.Empty<OllamaLocalModel>();
                _freeSpace = null;
                _ = RefreshAsync();
                if (_latest is null) _ = LoadLatestVersionAsync();
                if (_libraryModels is { } models) ShowLibrary(models);
            }
            ApplyState();
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

            Items_Catalog.ItemsSource = OllamaModelCatalog.Models.Concat(_popular).Select(BuildCatalogRow).OrderBy(r => r.Rank).ToList();
            foreach (var row in _libraryRows) row.RefreshAction();
            Items_Local.ItemsSource = _models.Select(BuildLocalRow).ToList();
            Tb_LocalEmpty.Text = running
                ? T("NoModels", "还没有下载模型。")
                : T("NotRunningModels", "Ollama 没有运行，看不到模型。可以到“部署”页启动。");
            Tb_LocalEmpty.Visibility = _models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private OllamaCatalogRow BuildCatalogRow(OllamaModelInfo model)
        {
            var action = DescribeAction(model.Name);
            var fit = OllamaModelCatalog.Assess(model.SizeBytes, Hardware);
            var recommended = ReferenceEquals(model, _recommended) && fit == OllamaFit.Smooth;

            return new OllamaCatalogRow(model, model.Name, FormatSize(model.SizeBytes), fit,
                recommended ? T("Recommended", "推荐") : DescribeFit(fit),
                LanguageHelper.Get(model.DescriptionKey, Lang, model.DefaultDescription), action.Text, action.Enabled, action.Installed,
                recommended ? -1 : (int)fit);
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
            return new OllamaLocalRow(model.Name, FormatSize(model.SizeBytes), text, model.IsChatModel && !inUse);
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
                Logger.Log($"Ollama 管理器操作失败: {ex}");
                ShowStatus(Describe(ex), false);
            }
            finally
            {
                _running = null;
                Panel_Progress.Visibility = Visibility.Collapsed;
            }

            if (!_lifetime.IsCancellationRequested) await RefreshAsync();
        }

        private void OnProgress(OllamaTaskProgress progress)
        {
            if (_running is null) return;

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
                _speedText = _speedBytes > 0 && perSecond > 0 ? $" · {FormatSize((long)perSecond)}/s" : "";
                _speedBytes = progress.Completed;
                _speedClock.Restart();
            }
            Tb_TaskDetail.Text = $"{FormatSize(progress.Completed)} / {FormatSize(progress.Total)}{_speedText}";
        }

        private void Choose(string model)
        {
            if (_onChosen?.Invoke(model, _service.Url) != true)
            {
                ShowStatus(T("ChooseLost", "设置页已经切到别的渠道，模型没有填进去。请回到设置页重新打开管理器。"), false);
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
            _libraryRows = models.Where(m => m.IsChatModel && !m.IsCloudOnly).Select(BuildLibraryRow).ToList();
            ApplyLibraryFilter();
            _ = LoadPopularAsync(models);
        }

        private OllamaLibraryRow BuildLibraryRow(OllamaLibraryModel model)
        {
            var details = model.Capabilities.Where(c => c != "cloud").ToList();
            if (model.Pulls.Length > 0) details.Add(string.Format(T("LibraryPulls", "{0} 次下载"), model.Pulls));

            return new OllamaLibraryRow(model, string.Join(" · ", details),
                model.Tags.Select(tag => BuildTagOption(model, tag, 0)).ToList(), DescribeAction);
        }

        private OllamaTagOption BuildTagOption(OllamaLibraryModel model, string tag, long size)
        {
            var fit = OllamaModelCatalog.Assess(size, Hardware);
            var text = $"{tag} · {(size > 0 ? FormatSize(size) : T("SizeUnknown", "大小未知"))}";
            return new OllamaTagOption(tag, $"{model.Name}:{tag}", size, fit, DescribeFit(fit), text);
        }

        private void ApplyLibraryFilter()
        {
            var keyword = Tb_LibrarySearch.Text.Trim();
            var rows = _libraryRows.Where(r => r.Model.Matches(keyword)).ToList();
            Items_Library.ItemsSource = rows;
            ShowLibraryState(rows.Count);
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

        private void LibrarySearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyLibraryFilter();

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
