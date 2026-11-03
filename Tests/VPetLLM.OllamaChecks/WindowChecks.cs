using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using VPetLLM.Core.Services;
using VPetLLM.UI.Controls;
using VPetLLM.UI.Windows;

namespace VPetLLM.OllamaChecks;

internal static partial class Program
{
    private const string Url = OllamaManagerService.DefaultUrl;

    // 结果不能跟着跑测试这台电脑的显卡变
    private static readonly OllamaHardware StrongHardware = new(32 * GB, 12 * GB, "Test GPU");

    private static readonly OllamaModelInfo Recommended = OllamaModelCatalog.Recommend(StrongHardware);

    /// <summary>窗口真的显示出来，按钮真的点。放到屏幕外，测试时不抢焦点。</summary>
    private sealed class ManagerWindow
    {
        public ManagerWindow(FakeDeployInstaller installer, FakeApi api, string? model = null, string url = Url,
            Func<string, string, bool>? chosen = null, OllamaLibrary? library = null, OllamaHardware? hardware = null)
        {
            var service = new OllamaManagerService(url, installer, api)
            {
                StartTimeout = TimeSpan.FromSeconds(2),
                PostInstallGrace = TimeSpan.FromMilliseconds(100)
            };
            Window = new winOllamaManager(_settings!, service, model, (name, target) =>
            {
                Chosen.Add((name, target));
                return chosen?.Invoke(name, target) ?? true;
            }, (text, _) =>
            {
                Confirms.Add(text);
                return Answer;
            }, library ?? new OllamaLibrary(new FakeOllamaCom { Offline = true }, NewLibraryCache()), hardware ?? StrongHardware)
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000
            };
            Window.Show();
            Pump(() => Text("Tb_State") != "正在检查…");
        }

        public winOllamaManager Window { get; }
        public List<(string Model, string Url)> Chosen { get; } = new();
        public List<string> Confirms { get; } = new();
        public bool Answer { get; set; }

        public T Find<T>(string name) where T : class => (T)Window.FindName(name);
        public string Text(string name) => Find<TextBlock>(name).Text;
        public bool Shown(string name) => Find<FrameworkElement>(name).Visibility == Visibility.Visible;
        public bool Enabled(string name) => Find<UIElement>(name).IsEnabled;
        public bool Idle => !Shown("Panel_Progress");

        public void Click(string name) => Press(Find<Button>(name));

        public List<OllamaFilterChip> Chips(string list) => ((IEnumerable<OllamaFilterChip>)Find<ItemsControl>(list).ItemsSource).ToList();

        public Button ChipButton(string list, string text)
            => Descendants(Find<ItemsControl>(list)).OfType<Button>().Single(b => ((OllamaFilterChip)b.Tag).Text == text);

        public void PressChip(string list, string text)
        {
            Press(ChipButton(list, text));
            Pump(() => true);
        }

        public void SortLibrary(string name)
        {
            var button = Find<RadioButton>(name);
            button.IsChecked = true;
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(() => true);
        }

        public List<OllamaLibraryBlock> Blocks => ((IEnumerable<OllamaLibraryBlock>)Find<ListBox>("Items_Library").ItemsSource).ToList();

        public ScrollViewer LibraryViewer => Descendants(Find<ListBox>("Items_Library")).OfType<ScrollViewer>().First();

        public ScrollViewer FilterViewer
        {
            get
            {
                DependencyObject element = Find<ItemsControl>("Items_SeriesChips");
                while (element is not ScrollViewer) element = VisualTreeHelper.GetParent(element);
                return (ScrollViewer)element;
            }
        }

        /// <summary>卡片在列表里的位置，含卡片之间的间距。</summary>
        public Rect CardBounds(string name)
        {
            var card = (FrameworkElement)LibraryRow(name);
            return new Rect(card.TranslatePoint(new Point(0, 0), Find<ListBox>("Items_Library")), new Size(card.ActualWidth, card.ActualHeight));
        }

        public List<string> CardTexts(string name) => Descendants(LibraryRow(name)).OfType<TextBlock>().Select(t => t.Text).ToList();

        public IReadOnlyList<OllamaCatalogRow> Catalog => ((IEnumerable<OllamaCatalogRow>)Find<ItemsControl>("Items_Catalog").ItemsSource).ToList();
        public IReadOnlyList<OllamaLocalRow> Local => ((IEnumerable<OllamaLocalRow>)Find<ItemsControl>("Items_Local").ItemsSource).ToList();
        public IReadOnlyList<OllamaLibraryRow> Library => (Find<ListBox>("Items_Library").ItemsSource as IEnumerable<OllamaLibraryBlock>)?.SelectMany(b => b.Rows).ToList() ?? new List<OllamaLibraryRow>();

        public void OpenLibrary(Func<bool>? until = null)
        {
            Find<TabControl>("Tabs").SelectedIndex = 2;
            Window.UpdateLayout();
            Pump(until ?? (() => Library.Count > 0 && RealizedRows() > 0));
        }

        public int RealizedRows() => Cards().Count();

        private IEnumerable<ContentPresenter> Cards()
            => Descendants(Find<ListBox>("Items_Library")).OfType<ContentPresenter>().Where(p => p.Content is OllamaLibraryRow);

        public void Search(string keyword)
        {
            Find<TextBox>("Tb_LibrarySearch").Text = keyword;
            Pump(() => true);
        }

        public void ScrollLibraryToEnd()
        {
            Descendants(Find<ListBox>("Items_Library")).OfType<ScrollViewer>().First().ScrollToEnd();
            Window.UpdateLayout();
        }

        public void ScrollLibraryToStart()
        {
            Descendants(Find<ListBox>("Items_Library")).OfType<ScrollViewer>().First().ScrollToHome();
            Window.UpdateLayout();
        }

        private DependencyObject LibraryRow(string name)
        {
            DependencyObject? row = null;
            Pump(() => (row = Cards().FirstOrDefault(card => ((OllamaLibraryRow)card.Content).Name == name)) is not null);
            return row!;
        }

        // 比对象没用：SelectedItem 对了，框里也可能是空的
        public string ComboText(string name)
        {
            var combo = Descendants(LibraryRow(name)).OfType<ComboBox>().Single();
            Window.UpdateLayout();
            Pump(() => true);
            return string.Join("|", Descendants(combo).OfType<TextBlock>().Select(t => t.Text).Where(t => t.Length > 0));
        }

        /// <summary>经由行里的下拉框选规格，直接设 row.Selected 测不到绑定能不能写回。</summary>
        public void PickLibrary(string name, int index)
        {
            Descendants(LibraryRow(name)).OfType<ComboBox>().Single().SelectedIndex = index;
            Pump(() => true);
        }

        public string LibraryBadge(string name)
        {
            var style = Window.FindResource("OllamaFitBadge");
            var badge = Descendants(LibraryRow(name)).OfType<TextBlock>().Single(t => ReferenceEquals(t.Style, style));
            var color = new[] { "SuccessBrush", "WarningBrush", "ErrorBrush" }.First(key => ReferenceEquals(badge.Foreground, Window.FindResource(key)));
            return $"{color}:{badge.Text}";
        }

        public string LibraryButton(string name) => (string)Descendants(LibraryRow(name)).OfType<Button>().Single().Content;

        public void ClickLibrary(string name)
        {
            var row = LibraryRow(name);
            Press(Descendants(row).OfType<Button>().Single());
        }

        /// <summary>点下载模型页里写着指定文字的按钮。</summary>
        public void ClickCatalog(string model)
            => Press(RowButtons("Items_Catalog", 1, Catalog.Select(row => row.Name).ToList().IndexOf(model)).Single());

        public void ClickLocalUse(string model) => Press(LocalButtons(model)[0]);

        public void ClickLocalDelete(string model) => Press(LocalButtons(model)[1]);

        private List<Button> LocalButtons(string model)
            => RowButtons("Items_Local", 3, Local.Select(row => row.Name).ToList().IndexOf(model));

        private List<Button> RowButtons(string list, int tab, int index)
        {
            Find<TabControl>("Tabs").SelectedIndex = tab;
            Window.UpdateLayout();
            var items = Find<ItemsControl>(list);
            Pump(() => items.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated);

            var container = (DependencyObject)items.ItemContainerGenerator.ContainerFromIndex(index);
            return Descendants(container).OfType<Button>().ToList();
        }

        public void Finish() => Pump(() => Idle);

        public void Close()
        {
            Window.Close();
            Pump(() => true);
        }
    }

    private static Setting? _settings;

    private static void Press(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }

    /// <summary>让 Dispatcher 转起来，直到条件成立或超时。</summary>
    private static void Pump(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        } while (!done() && DateTime.UtcNow < deadline);
    }

    private static void CheckWindows()
    {
        var dir = Directory.CreateTempSubdirectory("vpetllm-ollama-ui-").FullName;
        var unhandled = new List<string>();

        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            unhandled.Add(e.Exception.ToString());
            e.Handled = true;
        };

        try
        {
            _settings = FreshSettings(dir);

            CheckFirstDeploy();
            CheckStoppedAndUpgrade();
            CheckReadyState();
            CheckExistingModelDeploy();
            CheckRemote();
            CheckBusyAndCancel();
            CheckInstallPhase();
            CheckUpgradeConfirm();
            CheckLocalModelActions();
            CheckRebind();
            CheckStaleRefresh();
            CheckSmoothWheel();
            CheckChooseLost();
            CheckFailureMessages();
            CheckCloseDuringPull();
            CheckLibraryWindow();
            CheckLibraryGrid();
            CheckCatalogGrid();
            CheckLocalGrid();
            CheckQuickPanel();
            CheckQuickDeploy();
            CheckChannelView();
            CheckChannelDeploy();
        }
        finally
        {
            Check("界面全程没有未处理异常", unhandled.Count == 0, string.Join("\n", unhandled));
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    private static Setting FreshSettings(string dir)
    {
        var s = new Setting(Path.Combine(dir, Guid.NewGuid().ToString("N")));
        s.OpenAI.OpenAINodes.Clear();
        s.Gemini.GeminiNodes.Clear();
        s.Ollama.OllamaNodes.Clear();
        s.LMStudio.LMStudioNodes.Clear();
        s.Free.FreeNodes.Clear();
        s.Ollama.OllamaNodes.Add(new Setting.OllamaNodeSetting { Model = "", Priority = 100 });
        s.Free.FreeNodes.Add(new Setting.FreeNodeSetting { Enabled = true });
        return s;
    }

    /// <summary>把几个状态渲染成图片，看排版用，不算检查项。</summary>
    private static void RenderWindows(string outputDirectory)
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var dir = Directory.CreateTempSubdirectory("vpetllm-ollama-ui-").FullName;
        _settings = FreshSettings(dir);

        void Save(ManagerWindow window, string name, int tab)
        {
            window.Find<TabControl>("Tabs").SelectedIndex = tab;
            Snapshot(window.Window, name);
        }

        void Snapshot(Window window, string name)
        {
            Pump(() => true);
            window.UpdateLayout();
            Pump(() => true);

            // 内容带外边距，按它算尺寸会切掉右边和下边，改用窗口客户区
            var content = (FrameworkElement)window.Content;
            var width = content.ActualWidth + content.Margin.Left + content.Margin.Right;
            var height = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var drawing = background.RenderOpen())
                drawing.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
            bitmap.Render(background);
            bitmap.Render(content);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(outputDirectory, name + ".png"));
            encoder.Save(file);
        }

        var fresh = new FakeApi();
        var missing = new ManagerWindow(new FakeDeployInstaller(null) { Api = fresh }, fresh);
        Pump(() => missing.Text("Tb_Version").Length > 0);
        Save(missing, "missing-deploy", 0);
        Save(missing, "missing-catalog", 1);
        Save(missing, "missing-local", 3);
        missing.Close();

        var api = new FakeApi
        {
            Running = true,
            PullGate = new TaskCompletionSource(),
            Models =
            {
                new OllamaLocalModel("qwen3:4b", 2_497_293_444, "qwen3"),
                new OllamaLocalModel("nomic-embed-text:latest", 300 * 1024 * 1024, "nomic-bert"),
                new OllamaLocalModel("hf.co/some-org/a-very-long-model-name-for-layout-GGUF:Q4_K_M", 9 * GB, "llama")
            }
        };
        var ready = new ManagerWindow(new FakeDeployInstaller(new OllamaInstallation(@"C:\Ollama", "0.30.0")) { Api = api }, api, "qwen3:4b");
        Pump(() => ready.Shown("Btn_Install"));
        Save(ready, "ready-deploy", 0);
        Save(ready, "ready-local", 3);

        ready.ClickCatalog(OllamaModelCatalog.Models[2].Name);
        Pump(() => api.Pulled.Count == 1);
        ready.Find<ProgressBar>("Bar_Task").IsIndeterminate = false;
        ready.Find<ProgressBar>("Bar_Task").Value = 42;
        ready.Find<TextBlock>("Tb_TaskTitle").Text = "正在下载模型…";
        ready.Find<TextBlock>("Tb_TaskDetail").Text = "2.2 GB / 5.2 GB · 18 MB/s";
        Save(ready, "ready-busy", 1);
        ready.Window.Width = ready.Window.MinWidth;
        ready.Window.Height = ready.Window.MinHeight;
        Save(ready, "ready-busy-min", 1);
        ready.Click("Btn_Cancel");
        ready.Finish();
        ready.Close();

        void SaveLibrary(string prefix, OllamaHardware hardware)
        {
            var net = LibraryNet();
            var libraryApi = new FakeApi { Running = true, Models = { new OllamaLocalModel("alpha:8b", 5 * GB, "llama") } };
            var installer = new FakeDeployInstaller(new OllamaInstallation(@"C:\Ollama", "0.40.13")) { Api = libraryApi, FreeSpace = 200 * GB };
            var window = new ManagerWindow(installer, libraryApi, "alpha:8b", library: new OllamaLibrary(net, NewLibraryCache()), hardware: hardware);
            Pump(() => window.Text("Tb_Hardware").Contains("模型盘"));
            window.OpenLibrary();
            Pump(() => Sized(window.Library[1]));
            var settle = DateTime.UtcNow.AddMilliseconds(600);
            Pump(() => DateTime.UtcNow >= settle);
            Save(window, prefix + "-library", 2);
            Save(window, prefix + "-catalog", 1);
            Save(window, prefix + "-local", 3);
            window.Window.Width = window.Window.MinWidth;
            window.Window.Height = window.Window.MinHeight;
            Save(window, prefix + "-library-min", 2);
            window.Close();
        }

        SaveLibrary("strong", StrongHardware);
        SaveLibrary("weak", new OllamaHardware(8 * GB, 0, "", 8, HasAvx2: false));

        // 本机留着真实缓存时多出几张：假数据的说明太短，看不出截断和 20 多个系列按钮的样子。缓存拷一份用，不写回去
        var realCache = Path.Combine(Path.GetTempPath(), "VPetLLM", "ollama");
        if (File.Exists(Path.Combine(realCache, "library.json")))
        {
            var copy = NewLibraryCache();
            Directory.CreateDirectory(copy);
            foreach (var file in Directory.GetFiles(realCache)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));

            var realApi = new FakeApi { Running = true, Models = { new OllamaLocalModel("qwen3:8b", 5 * GB, "qwen") } };
            var realInstaller = new FakeDeployInstaller(new OllamaInstallation(@"C:\Ollama", "0.40.13")) { Api = realApi, FreeSpace = 200 * GB };
            var window = new ManagerWindow(realInstaller, realApi, "qwen3:8b", library: new OllamaLibrary(new FakeOllamaCom { Offline = true }, copy), hardware: StrongHardware);
            Pump(() => window.Text("Tb_Hardware").Contains("模型盘"));
            window.OpenLibrary();
            var settle = DateTime.UtcNow.AddMilliseconds(600);
            Pump(() => DateTime.UtcNow >= settle);
            Save(window, "real-library", 2);
            window.PressChip("Items_CapabilityChips", "看图");
            window.PressChip("Items_SeriesChips", "qwen");
            Save(window, "real-filtered", 2);
            window.Click("Btn_LibraryReset");
            window.Window.Width = window.Window.MinWidth;
            window.Window.Height = window.Window.MinHeight;
            Save(window, "real-library-min", 2);
            window.Close();
        }

        var panelHost = new Window { Width = 560, Height = 520, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        var panel = new QuickChannelPanel();
        panel.Attach(_settings, () => null);
        ((RadioButton)panel.FindName("Rb_Local")).IsChecked = true;
        panelHost.Content = new ScrollViewer { Content = panel };
        panelHost.Show();
        Pump(() => true);
        panelHost.UpdateLayout();
        var render = new System.Windows.Media.Imaging.RenderTargetBitmap((int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var white = new DrawingVisual();
        using (var drawing = white.RenderOpen())
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, panel.ActualWidth, panel.ActualHeight));
        render.Render(white);
        render.Render(panel);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(render));
        using (var file = File.Create(Path.Combine(outputDirectory, "quick-panel.png")))
            png.Save(file);
        panelHost.Close();

        // 三处部署入口。宿主只要 Settings；标成退出中，关引导窗口时才不写“看过引导”
        var host = (global::VPetLLM.VPetLLM)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(global::VPetLLM.VPetLLM));
        typeof(global::VPetLLM.VPetLLM).GetField("_isShuttingDown", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, true);
        T Offscreen<T>(T window) where T : Window
        {
            window.Left = -20000;
            window.Top = -20000;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Show();
            return window;
        }

        var ready2 = FreshSettings(dir);
        ready2.Ollama.OllamaNodes[0].Url = "http://192.0.2.1:11434";
        ready2.Ollama.OllamaNodes[0].Model = "qwen3:4b";
        ready2.Gemini.GeminiNodes.Add(new Setting.GeminiNodeSetting { Name = "主渠道", ApiKey = "k", Model = "gemini-x", Priority = 1000 });
        host.Settings = ready2;
        var simple = Offscreen(new winSimpleSetting(host));
        Snapshot(simple, "entry-simple-collapsed");
        simple.Close();

        var unset = FreshSettings(dir);
        unset.Free.FreeNodes[0].Enabled = false;
        host.Settings = unset;
        simple = Offscreen(new winSimpleSetting(host));
        Snapshot(simple, "entry-simple-expanded");
        simple.Close();

        var onboarding = Offscreen(new winOnboarding(host, firstRun: true));
        Call(onboarding, "ShowStep", 1);
        Snapshot(onboarding, "entry-onboarding");
        ((ScrollViewer)onboarding.FindName("Scroll_Pages")).ScrollToEnd();
        Snapshot(onboarding, "entry-onboarding-end");
        onboarding.Close();

        var viewHost = Offscreen(new Window { Width = 900, Height = 560, Background = Brushes.White });
        var view = new ChannelManagerView();
        view.Attach(ready2, () => null, () => { });
        viewHost.Content = view;
        Snapshot(viewHost, "entry-channels");
        viewHost.Close();

        Directory.Delete(dir, recursive: true);
    }

    private static void CheckFirstDeploy()
    {
        var api = new FakeApi { PullAddsModel = true };
        var installer = new FakeDeployInstaller(null) { Api = api, InstallStartsServer = true };
        var window = new ManagerWindow(installer, api);

        Check("没装：说明状态", window.Text("Tb_State").Contains("还没有安装"), window.Text("Tb_State"));
        Check("没装：一键部署可点", window.Shown("Btn_Deploy") && window.Enabled("Btn_Deploy"));
        Check("没装：可以只安装", window.Shown("Btn_Install") && ((string)window.Find<Button>("Btn_Install").Content).Contains("只安装"));
        Check("没装：不显示启动", !window.Shown("Btn_Start"));
        Check("空闲：不显示进度", !window.Shown("Panel_Progress"));

        var plan = window.Text("Tb_Plan");
        Check("部署计划写明安装包体积", plan.Contains("1.7 GB"), plan);
        Check("部署计划写明推荐模型", plan.Contains(Recommended.Name), plan);
        Check("部署计划写明会填回设置", plan.Contains("填回设置"), plan);

        window.Click("Btn_Deploy");
        Pump(() => window.Chosen.Count > 0);
        window.Finish();
        Pump(() => window.Shown("Btn_Deploy") == false);

        Check("一键部署：先下载再安装", installer.Log.Contains("download 0.40.13") && installer.Log.Contains("install setup.exe"), string.Join(",", installer.Log));
        Check("一键部署：拉了推荐模型", api.Pulled.SequenceEqual(new[] { Recommended.Name }), string.Join(",", api.Pulled));
        Check("一键部署：模型和地址回传", window.Chosen.SequenceEqual(new[] { (Recommended.Name, Url) }), string.Join(",", window.Chosen));
        Check("一键部署：提示保存后生效", window.Text("Tb_Status").Contains("保存后生效"), window.Text("Tb_Status"));
        Check("部署完：状态变成运行中", window.Text("Tb_State").Contains("正在运行"), window.Text("Tb_State"));
        Check("部署完：进度条收起", !window.Shown("Panel_Progress"));
        Check("部署完：不再显示部署按钮", !window.Shown("Btn_Deploy"));
        Check("部署完：说明已就绪", window.Text("Tb_Plan").Contains(Recommended.Name), window.Text("Tb_Plan"));
        window.Close();
    }

    private static void CheckStoppedAndUpgrade()
    {
        var api = new FakeApi();
        var installer = new FakeDeployInstaller(new OllamaInstallation(@"C:\Ollama", "0.40.13")) { Api = api, StartStartsServer = true };
        var window = new ManagerWindow(installer, api);
        Pump(() => window.Text("Tb_Version").Contains("最新版本"));

        Check("装了没跑：说明状态", window.Text("Tb_State").Contains("没有运行"), window.Text("Tb_State"));
        Check("装了没跑：可以启动", window.Shown("Btn_Start"));
        Check("已是最新：不显示安装和升级", !window.Shown("Btn_Install"));
        Check("装了没跑：计划是启动而不是安装", window.Text("Tb_Plan").Contains("启动 Ollama") && !window.Text("Tb_Plan").Contains("1.7 GB"), window.Text("Tb_Plan"));

        window.Click("Btn_Start");
        Pump(() => window.Text("Tb_State").Contains("正在运行"));
        Check("启动：真的调了启动", installer.Log.Contains("start"), string.Join(",", installer.Log));
        Check("启动后：隐藏启动按钮", !window.Shown("Btn_Start"));
        window.Close();

        var oldApi = new FakeApi();
        var old = new FakeDeployInstaller(new OllamaInstallation(@"C:\Ollama", "0.30.0")) { Api = oldApi };
        var outdated = new ManagerWindow(old, oldApi);
        Pump(() => outdated.Shown("Btn_Install"));
        Check("旧版本：显示升级按钮", outdated.Shown("Btn_Install") && ((string)outdated.Find<Button>("Btn_Install").Content).Contains("升级到 0.40.13"));
        Check("打开窗口不会自动升级", !old.Log.Any(l => l.StartsWith("download") || l.StartsWith("install")), string.Join(",", old.Log));
        outdated.Close();
    }

    private static void CheckReadyState()
    {
        var api = new FakeApi
        {
            Running = true,
            Models =
            {
                new OllamaLocalModel("my-model:latest", 3 * GB, "qwen3"),
                new OllamaLocalModel("nomic-embed-text:latest", 300 * 1024 * 1024, "nomic-bert"),
                new OllamaLocalModel(Recommended.Name, Recommended.SizeBytes, "qwen3")
            }
        };
        var window = new ManagerWindow(new FakeDeployInstaller(new OllamaInstallation(@"C:\Ollama", "0.40.13")) { Api = api }, api, "my-model:latest");

        Check("已就绪：不给部署按钮，否则会换掉用户的模型", !window.Shown("Btn_Deploy"));
        Check("已就绪：说明正在用哪个", window.Text("Tb_Plan").Contains("my-model:latest"), window.Text("Tb_Plan"));
        Check("版本号显示出来", window.Text("Tb_Version").Contains("0.40.13"), window.Text("Tb_Version"));

        var local = window.Local;
        Check("我的模型：列出全部", local.Count == 3, local.Count.ToString());
        Check("我的模型：在用的不能再选", local[0] is { UseText: "使用中", CanUse: false }, local[0].ToString());
        Check("我的模型：嵌入模型不能聊天", local[1] is { UseText: "不能聊天", CanUse: false }, local[1].ToString());
        Check("我的模型：其他模型可以选", local[2] is { UseText: "使用", CanUse: true }, local[2].ToString());
        Check("我的模型：有内容时写数量和占用", window.Text("Tb_LocalState").StartsWith("共 3 个模型，占用 "), window.Text("Tb_LocalState"));

        var row = window.Catalog.Single(r => r.Name == Recommended.Name);
        Check("名单：已下载的显示使用", row is { ActionText: "使用", CanAct: true, Installed: true }, row.ToString());
        Check("名单：推荐标记只有一个", window.Catalog.Count(r => r.Badge == "推荐") == 1);
        Check("名单：显示中文说明", window.Catalog.All(r => r.Description.Length > 0));

        window.ClickCatalog(Recommended.Name);
        Check("名单：点使用只是选中，不下载", api.Pulled.Count == 0 && window.Chosen.SequenceEqual(new[] { (Recommended.Name, Url) }), string.Join(",", window.Chosen));
        Check("名单：选中后变成使用中", window.Catalog.Single(r => r.Name == Recommended.Name) is { ActionText: "使用中", CanAct: false });
        window.Close();
    }

    private static void CheckExistingModelDeploy()
    {
        var api = new FakeApi { Running = true, Models = { new OllamaLocalModel("old-model:latest", GB, "llama") } };
        var window = new ManagerWindow(new FakeDeployInstaller(null) { Api = api }, api, "gone:latest");

        Check("填的模型不存在：还能部署", window.Shown("Btn_Deploy"));
        Check("已有模型：计划不提下载", !window.Text("Tb_Plan").Contains("下载"), window.Text("Tb_Plan"));

        window.Click("Btn_Deploy");
        Pump(() => window.Chosen.Count > 0);
        window.Finish();
        Check("已有模型：直接选用不下载", api.Pulled.Count == 0 && window.Chosen.SequenceEqual(new[] { ("old-model:latest", Url) }), string.Join(",", window.Chosen));
        window.Close();
    }

    private static void CheckRemote()
    {
        const string remote = "http://192.168.1.50:11434";

        var api = new FakeApi();
        var installer = new FakeDeployInstaller(null) { Api = api };
        var window = new ManagerWindow(installer, api, url: remote);

        Check("远程连不上：说明要到那台电脑操作", window.Text("Tb_State").Contains("那台电脑"), window.Text("Tb_State"));
        Check("远程：不给安装", !window.Shown("Btn_Install"));
        Check("远程：不给启动", !window.Shown("Btn_Start"));
        Check("远程连不上：不能部署", !window.Enabled("Btn_Deploy"));
        Check("远程连不上：没有部署计划", window.Text("Tb_Plan").Length == 0, window.Text("Tb_Plan"));
        Check("远程：不查本机安装", !installer.Detected);
        Check("远程：不查最新版本", !installer.Log.Contains("latest"), string.Join(",", installer.Log));

        window.ClickCatalog(OllamaModelCatalog.Models[0].Name);
        window.Finish();
        Check("远程连不上时下载模型：提示到那台电脑", window.Text("Tb_Status").Contains("那台电脑"), window.Text("Tb_Status"));
        window.Close();

        var runningApi = new FakeApi { Running = true, PullAddsModel = true };
        var running = new ManagerWindow(new FakeDeployInstaller(null) { Api = runningApi }, runningApi, url: remote);
        Check("远程在跑：可以部署模型", running.Shown("Btn_Deploy") && running.Enabled("Btn_Deploy"));
        running.Click("Btn_Deploy");
        Pump(() => running.Chosen.Count > 0);
        running.Finish();
        Check("远程在跑：回传的是远程地址", running.Chosen.SequenceEqual(new[] { (Recommended.Name, remote) }), string.Join(",", running.Chosen));
        running.Close();
    }

    private static void CheckBusyAndCancel()
    {
        var api = new FakeApi { Running = true, PullGate = new TaskCompletionSource() };
        var window = new ManagerWindow(new FakeDeployInstaller(null) { Api = api }, api);
        var model = OllamaModelCatalog.Models[0].Name;

        window.ClickCatalog(model);
        Pump(() => api.Pulled.Count == 1);

        Check("进行中：显示进度", window.Shown("Panel_Progress"));
        Check("进行中：刷新不可点", !window.Enabled("Btn_Refresh"));
        Check("进行中：部署不可点", !window.Enabled("Btn_Deploy"));
        Check("进行中：名单不可点", !window.Enabled("Items_Catalog"));
        Check("进行中：我的模型不可点", !window.Enabled("Items_Local"));
        Check("进行中：可以取消", window.Enabled("Btn_Cancel"));

        window.Click("Btn_Deploy");
        Pump(() => true);
        Check("进行中：不会同时开第二个任务", api.Pulled.Count == 1, string.Join(",", api.Pulled));

        window.Click("Btn_Cancel");
        window.Finish();
        Check("取消：真的中止了请求", api.PullCancelled);
        Check("取消：提示可续传", window.Text("Tb_Status").Contains("已取消"), window.Text("Tb_Status"));
        Check("取消：没有回传模型", window.Chosen.Count == 0);
        Check("取消后：按钮恢复", window.Enabled("Btn_Refresh") && window.Enabled("Items_Catalog"));

        api.PullGate = null;
        api.PullAddsModel = true;
        window.ClickCatalog(model);
        window.Finish();
        Pump(() => window.Chosen.Count > 0);
        Check("取消后能重新开始", api.Pulled.Count == 2, string.Join(",", api.Pulled));
        Check("当前没选模型时，下载完自动选用", window.Chosen.SequenceEqual(new[] { (model, Url) }), string.Join(",", window.Chosen));
        window.Close();

        var picked = new FakeApi { Running = true, PullAddsModel = true };
        var other = new ManagerWindow(new FakeDeployInstaller(null) { Api = picked }, picked, "keep:latest");
        other.ClickCatalog(model);
        Pump(() => picked.Pulled.Count == 1);
        other.Finish();
        Check("已选了模型时，下载完不抢", other.Chosen.Count == 0, string.Join(",", other.Chosen));
        Check("已选了模型时，提示下载完成", other.Text("Tb_Status").Contains("下载完成"), other.Text("Tb_Status"));

        var bad = other.Find<TextBox>("Tb_CustomModel");
        bad.Text = "bad name; rm";
        var pullCustom = Descendants(other.Find<Grid>("Panel_Custom")).OfType<Button>().Single();
        Press(pullCustom);
        Pump(() => true);
        Check("自定义名不合法：不发请求", picked.Pulled.Count == 1, string.Join(",", picked.Pulled));
        Check("自定义名不合法：说明原因", other.Text("Tb_Status").Contains("模型名不对"), other.Text("Tb_Status"));

        bad.Text = " custom/model:7b ";
        Press(pullCustom);
        Pump(() => picked.Pulled.Count == 2);
        other.Finish();
        Check("自定义名：去掉空白后下载", picked.Pulled.LastOrDefault() == "custom/model:7b", string.Join(",", picked.Pulled));
        other.Close();
    }

    private static void CheckInstallPhase()
    {
        var api = new FakeApi();
        var gate = new TaskCompletionSource();
        var window = new ManagerWindow(new FakeDeployInstaller(null) { Api = api, InstallStartsServer = true, InstallGate = gate }, api);

        window.Click("Btn_Install");
        Pump(() => window.Text("Tb_TaskTitle").Contains("正在安装"));
        Check("安装中：报告安装阶段", window.Text("Tb_TaskTitle").Contains("正在安装"), window.Text("Tb_TaskTitle"));
        Check("安装中：不能取消，安装程序打断不了", !window.Enabled("Btn_Cancel"));

        gate.SetResult();
        window.Finish();
        Check("安装完：提示已装好", window.Text("Tb_Status").Contains("已安装好"), window.Text("Tb_Status"));
        Check("首次安装不用确认", window.Confirms.Count == 0, string.Join(",", window.Confirms));
        window.Close();
    }

    private static void CheckUpgradeConfirm()
    {
        var api = new FakeApi();
        var installer = new FakeDeployInstaller(new OllamaInstallation(@"C:\Ollama", "0.30.0"))
        {
            Api = api,
            InstallStartsServer = true,
            InstallCreates = new OllamaInstallation(@"C:\Ollama", "0.40.13")
        };
        var window = new ManagerWindow(installer, api);
        Pump(() => window.Shown("Btn_Install"));

        window.Click("Btn_Install");
        Pump(() => true);
        Check("升级：先说明会打断聊天", window.Confirms.Single().Contains("中断"), string.Join(",", window.Confirms));
        Check("升级：选否什么也不做", !installer.Log.Any(l => l.StartsWith("download")) && window.Idle, string.Join(",", installer.Log));

        window.Answer = true;
        window.Click("Btn_Install");
        window.Finish();
        Check("升级：选是才下载安装", installer.Log.Contains("download 0.40.13") && installer.Log.Contains("install setup.exe"), string.Join(",", installer.Log));
        Check("升级：完成后提示", window.Text("Tb_Status").Contains("已安装好"), window.Text("Tb_Status"));
        window.Close();
    }

    private static void CheckLocalModelActions()
    {
        var api = new FakeApi
        {
            Running = true,
            Models =
            {
                new OllamaLocalModel("a:latest", GB, "llama"),
                new OllamaLocalModel("b:latest", GB, "llama")
            }
        };
        var window = new ManagerWindow(new FakeDeployInstaller(null) { Api = api }, api, "a:latest");

        window.ClickLocalUse("b:latest");
        Check("我的模型：点使用回传", window.Chosen.SequenceEqual(new[] { ("b:latest", Url) }), string.Join(",", window.Chosen));
        Check("我的模型：点使用后变成使用中", window.Local.Single(r => r.Name == "b:latest") is { UseText: "使用中", CanUse: false });

        window.ClickLocalDelete("a:latest");
        Pump(() => true);
        Check("删除：先确认", window.Confirms.Single().Contains("确定删除 a:latest"), string.Join(",", window.Confirms));
        Check("删除：选否不删", api.Deleted.Count == 0 && window.Local.Count == 2, string.Join(",", api.Deleted));

        window.Answer = true;
        window.ClickLocalDelete("a:latest");
        window.Finish();
        Pump(() => window.Local.Count == 1);
        Check("删除：选是才删", api.Deleted.SequenceEqual(new[] { "a:latest" }), string.Join(",", api.Deleted));
        Check("删除：列表刷新", window.Local.Single().Name == "b:latest");
        Check("删除：提示已删除", window.Text("Tb_Status").Contains("已删除 a:latest"), window.Text("Tb_Status"));

        window.Answer = false;
        window.ClickLocalDelete("b:latest");
        Pump(() => true);
        Check("删除在用的模型：说明后果", window.Confirms.Last().Contains("正在使用"), window.Confirms.Last());
        window.Close();
    }

    private static void CheckRebind()
    {
        const string other = "http://127.0.0.1:9";
        Func<string, string, bool> chosen = (_, _) => true;
        string Bound(ManagerWindow w) => Field<OllamaManagerService>(w.Window, "_service").Url;

        var api = new FakeApi { Running = true, PullGate = new TaskCompletionSource() };
        var window = new ManagerWindow(new FakeDeployInstaller(null) { Api = api }, api);
        // 查到最新版本之后再换，否则换地址时会真的去查一次
        Pump(() => window.Text("Tb_Version").Contains("最新版本"));

        window.ClickCatalog(OllamaModelCatalog.Models[0].Name);
        Pump(() => api.Pulled.Count == 1);
        Call(window.Window, "Rebind", other, "", chosen);
        Check("任务进行中不换地址", Bound(window) == Url, Bound(window));

        window.Click("Btn_Cancel");
        window.Finish();
        Call(window.Window, "Rebind", other, "", chosen);
        Check("空闲时换成新地址", Bound(window) == other, Bound(window));
        window.Close();
    }

    private static void CheckStaleRefresh()
    {
        var stale = new FakeApi { Running = true, Models = { new OllamaLocalModel("stale:latest", GB, "llama") } };
        var window = new ManagerWindow(new FakeDeployInstaller(null) { Api = stale }, stale);
        var gate = new TaskCompletionSource();
        stale.TagsGate = gate;
        var served = stale.TagRequests;
        window.Click("Btn_Refresh");
        Pump(() => stale.TagRequests > served);

        // 旧地址的刷新还卡着，这时换到新地址并刷新完
        var fresh = new FakeApi();
        SetField(window.Window, "_service", new OllamaManagerService(Url, new FakeDeployInstaller(null) { Api = fresh }, fresh));
        window.Click("Btn_Refresh");
        Pump(() => window.Text("Tb_State").Contains("还没有安装"));

        gate.SetResult();
        var settle = DateTime.UtcNow.AddMilliseconds(300);
        Pump(() => DateTime.UtcNow >= settle);
        Check("旧地址的刷新晚到时被丢弃", window.Text("Tb_State").Contains("还没有安装") && window.Local.Count == 0, window.Text("Tb_State"));
        window.Close();
    }

    private static void CheckChooseLost()
    {
        var api = new FakeApi { Running = true, Models = { new OllamaLocalModel("a:latest", GB, "llama") } };
        var window = new ManagerWindow(new FakeDeployInstaller(null) { Api = api }, api, chosen: (_, _) => false);

        window.Click("Btn_Deploy");
        Pump(() => window.Chosen.Count > 0);
        window.Finish();
        Check("回传失败：不谎称已选用", window.Text("Tb_Status").Contains("切到别的渠道"), window.Text("Tb_Status"));
        Check("回传失败：仍可重试", window.Shown("Btn_Deploy"));
        window.Close();
    }

    private static void CheckFailureMessages()
    {
        string Status(FakeApi api, FakeDeployInstaller installer)
        {
            var window = new ManagerWindow(installer, api);
            var before = window.Text("Tb_Status");
            window.Click("Btn_Deploy");
            // 失败得太快，等不到“忙”的状态，要等状态文字变
            Pump(() => window.Idle && window.Text("Tb_Status") != before);
            var text = window.Text("Tb_Status");
            window.Close();
            return text;
        }

        var api = new FakeApi { Running = true, PullError = "pull model manifest: 412: this model requires a newer version of Ollama" };
        var text = Status(api, new FakeDeployInstaller(null) { Api = api });
        Check("模型要新版本：引导升级", text.Contains("升级"), text);

        api = new FakeApi { Running = true, PullError = "file does not exist" };
        text = Status(api, new FakeDeployInstaller(null) { Api = api });
        Check("其他接口错误：原话转告", text.Contains("file does not exist"), text);

        api = new FakeApi { Running = true };
        text = Status(api, new FakeDeployInstaller(null) { Api = api, FreeSpace = 1024 * 1024 });
        Check("磁盘不足：给出数字", text.Contains("磁盘空间不足") && text.Contains("1 MB"), text);
        Check("磁盘不足：没有发起下载", api.Pulled.Count == 0);

        foreach (var (failure, expected) in new[]
                 {
                     (OllamaFailure.Signature, "校验"),
                     (OllamaFailure.Download, "下载失败"),
                     (OllamaFailure.Setup, "安装失败")
                 })
        {
            api = new FakeApi();
            text = Status(api, new FakeDeployInstaller(null) { Api = api, DownloadThrows = new OllamaOperationException(failure, "5") });
            Check($"{failure}：写人话", text.Contains(expected) && !text.Contains(failure.ToString()), text);
        }

        api = new FakeApi();
        text = Status(api, new FakeDeployInstaller(null) { Api = api, Latest = null });
        Check("查不到版本：提示网络", text.Contains("检查网络"), text);

        api = new FakeApi();
        text = Status(api, new FakeDeployInstaller(new OllamaInstallation(@"C:\Ollama", "0.40.13")) { Api = api, StartThrows = true });
        Check("启动失败：给手动办法", text.Contains("开始菜单"), text);

        api = new FakeApi();
        var noServer = new ManagerWindow(new FakeDeployInstaller(null) { Api = api }, api);
        noServer.ClickCatalog(OllamaModelCatalog.Models[0].Name);
        noServer.Finish();
        Check("没装就下模型：引导去部署", noServer.Text("Tb_Status").Contains("一键部署"), noServer.Text("Tb_Status"));
        noServer.Close();
    }

    private static void CheckCloseDuringPull()
    {
        var api = new FakeApi { Running = true, PullGate = new TaskCompletionSource() };
        var window = new ManagerWindow(new FakeDeployInstaller(null) { Api = api }, api);
        window.Click("Btn_Deploy");
        Pump(() => api.Pulled.Count == 1);

        window.Window.Close();
        Pump(() => api.PullCancelled);
        Pump(() => true);
        Check("关窗：中止进行中的下载", api.PullCancelled);
        Check("关窗：不回传模型", window.Chosen.Count == 0);
    }

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetField(object target, string name, object? value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? Call(object target, string name, params object[] args)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static Window? ManagerInstance()
        => (Window?)typeof(winOllamaManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);

    /// <summary>点入口按钮，确认地址合法时开得出管理器。192.0.2.1 是文档保留地址，也不会触发查最新版本。</summary>
    private static void CheckEntryOpens(string name, TextBox urlBox, Button entry)
    {
        urlBox.Text = "http://192.0.2.1:11434";
        Press(entry);
        var opened = ManagerInstance();
        Check($"{name}：地址合法时打开管理器", opened is not null);
        opened?.Close();
        Pump(() => true);
    }

    private static void CheckQuickPanel()
    {
        var settings = FreshSettings(Path.GetTempPath());
        var panel = new QuickChannelPanel();
        panel.Attach(settings, () => null);

        T Get<T>(string name) where T : class => (T)panel.FindName(name);
        var manager = Get<FrameworkElement>("Panel_OllamaManager");
        var ollama = Get<RadioButton>("Rb_Ollama");

        Get<RadioButton>("Rb_Local").IsChecked = true;
        Check("简易界面：本地模型默认 Ollama 时有入口", manager.Visibility == Visibility.Visible);

        Get<RadioButton>("Rb_LMStudio").IsChecked = true;
        Check("简易界面：LM Studio 没有入口", manager.Visibility != Visibility.Visible);

        Get<RadioButton>("Rb_Free").IsChecked = true;
        Check("回填前在免费模式", Get<RadioButton>("Rb_Free").IsChecked == true);

        var ok = Call(panel, "ApplyOllamaChoice", "qwen3:4b", "http://localhost:11500");
        var status = Get<TextBlock>("Tb_Status");
        var draft = panel.BuildDraft();
        Check("回填：切到本地 Ollama", ok is true && Get<RadioButton>("Rb_Local").IsChecked == true && ollama.IsChecked == true);
        Check("回填：组出来的节点就是这个模型和地址", draft is Setting.OllamaNodeSetting { Model: "qwen3:4b", Url: "http://localhost:11500" },
            draft?.ToString() ?? "null");
        Check("回填：算作改动，保存时才写得进去", Field<bool>(panel, "_changed"));

        // 已经在本地 Ollama 上：单选和地址都没变，只有模型变了
        SetField(panel, "_changed", false);
        Call(panel, "ApplyOllamaChoice", "qwen3:8b", "http://localhost:11500");
        Check("回填：只换模型也算改动", Field<bool>(panel, "_changed"));
        Check("回填：状态提示保存后生效", status.Text.Contains("保存后生效") && status.Visibility == Visibility.Visible, status.Text);

        Get<TextBox>("Tb_LocalUrl").Text = "localhost:11434";
        Press(Get<Button>("Btn_OllamaManager"));
        Check("地址不合法：不开管理器", ManagerInstance() is null);
        Check("地址不合法：说明原因", status.Text.Contains("http://"), status.Text);

        CheckEntryOpens("简易界面", Get<TextBox>("Tb_LocalUrl"), Get<Button>("Btn_OllamaManager"));
    }

    /// <summary>确认部署窗口开着，且带的是这个地址和模型，然后关掉。</summary>
    private static void CheckDeployOpened(string name, string url, string model)
    {
        var window = ManagerInstance();
        Check($"{name}：打开部署窗口", window is not null);
        if (window is null) return;

        var (openedUrl, openedModel) = (Field<OllamaManagerService>(window, "_service").Url, Field<string>(window, "_currentModel"));
        Check($"{name}：带的地址和模型对", openedUrl == url && openedModel == model, $"{openedUrl} {openedModel}");
        window.Close();
        Pump(() => true);
    }

    /// <summary>外层窗口的入口常驻，面板可能选着别的服务，这时表单里的地址不是 Ollama 的。</summary>
    private static void CheckQuickDeploy()
    {
        const string ollamaUrl = "http://192.0.2.1:11434";
        var settings = FreshSettings(Path.GetTempPath());
        var node = settings.Ollama.OllamaNodes[0];
        node.Url = ollamaUrl;
        node.Model = "qwen3:4b";
        settings.LMStudio.LMStudioNodes.Add(new Setting.LMStudioNodeSetting { Url = "http://192.0.2.2:1234", Model = "lm-model", Priority = 50 });

        var panel = new QuickChannelPanel();
        panel.Attach(settings, () => null);
        T Get<T>(string name) where T : class => (T)panel.FindName(name);

        Get<RadioButton>("Rb_Local").IsChecked = true;
        Get<RadioButton>("Rb_LMStudio").IsChecked = true;
        Check("常驻入口：表单里确实是 LM Studio 的地址", Get<TextBox>("Tb_LocalUrl").Text == "http://192.0.2.2:1234", Get<TextBox>("Tb_LocalUrl").Text);
        Check("常驻入口：选着 LM Studio 也开得了", panel.OpenLocalDeploy());
        CheckDeployOpened("常驻入口(LM Studio)", ollamaUrl, "qwen3:4b");

        Get<RadioButton>("Rb_Free").IsChecked = true;
        panel.OpenLocalDeploy();
        CheckDeployOpened("常驻入口(免费服务)", ollamaUrl, "qwen3:4b");

        Check("回填前选着 LM Studio", Get<RadioButton>("Rb_LMStudio").IsChecked == true);
        Call(panel, "ApplyOllamaChoice", "qwen3:8b", ollamaUrl);
        Check("常驻入口：选用后换成 Ollama", panel.BuildDraft() is Setting.OllamaNodeSetting { Model: "qwen3:8b", Url: ollamaUrl }, panel.BuildDraft()?.ToString() ?? "null");

        // 选着 Ollama 时要用表单里刚改的，还没保存的节点是旧的
        Get<TextBox>("Tb_LocalUrl").Text = "http://192.0.2.3:11434";
        Get<ComboBox>("Cb_LocalModel").Text = "typed";
        panel.OpenLocalDeploy();
        CheckDeployOpened("常驻入口(Ollama 表单)", "http://192.0.2.3:11434", "typed");

        node.Url = "localhost:11434";
        Get<RadioButton>("Rb_LMStudio").IsChecked = true;
        Check("常驻入口：节点地址不对时不开", !panel.OpenLocalDeploy() && ManagerInstance() is null);
        Check("常驻入口：节点地址不对时说明原因", Get<TextBlock>("Tb_Status").Text.Contains("http://"), Get<TextBlock>("Tb_Status").Text);
    }

    private static void CheckChannelView()
    {
        var settings = FreshSettings(Path.GetTempPath());
        var view = new ChannelManagerView();
        view.Attach(settings, () => null, () => { });

        T Get<T>(string name) where T : class => (T)view.FindName(name);
        var entry = Get<Button>("Ed_OllamaManager");
        var draftField = view.GetType().GetField("_draft", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var openAi = new Setting.OpenAINodeSetting { Url = "https://example.com" };
        draftField.SetValue(view, openAi);
        Call(view, "LoadForm");
        Check("极客界面：OpenAI 渠道没有入口", entry.Visibility != Visibility.Visible);

        var ollama = new Setting.OllamaNodeSetting { Url = Url, Model = "" };
        draftField.SetValue(view, ollama);
        Call(view, "LoadForm");
        Check("极客界面：Ollama 渠道有入口", entry.Visibility == Visibility.Visible);

        var applied = Call(view, "ApplyOllamaChoice", ollama, "qwen3:4b", "http://localhost:11500");
        Check("极客界面：回填到表单", applied is true && Get<TextBox>("Ed_Url").Text == "http://localhost:11500" && Get<ComboBox>("Ed_Model").Text == "qwen3:4b");

        draftField.SetValue(view, openAi);
        Call(view, "LoadForm");
        var url = Get<TextBox>("Ed_Url").Text;
        var lost = Call(view, "ApplyOllamaChoice", ollama, "qwen3:8b", "http://localhost:11500");
        Check("极客界面：已切到别的渠道时不写", lost is false && Get<TextBox>("Ed_Url").Text == url && Get<ComboBox>("Ed_Model").Text != "qwen3:8b");

        draftField.SetValue(view, ollama);
        Call(view, "LoadForm");
        Get<TextBox>("Ed_Url").Text = "localhost:11434";
        Press(entry);
        Check("极客界面：地址不合法时不开管理器", ManagerInstance() is null);
        Check("极客界面：地址不合法时说明原因", Get<TextBlock>("Ed_ModelStatus").Text.Contains("接口地址不对"), Get<TextBlock>("Ed_ModelStatus").Text);

        CheckEntryOpens("极客界面", Get<TextBox>("Ed_Url"), entry);
    }

    /// <summary>列表页的入口：这时没有打开的渠道，选好模型才进编辑页，保存前什么都不写。</summary>
    private static void CheckChannelDeploy()
    {
        const string ollamaUrl = "http://192.0.2.1:11434";
        var settings = FreshSettings(Path.GetTempPath());
        var node = settings.Ollama.OllamaNodes[0];
        node.Url = ollamaUrl;
        node.Model = "qwen3:4b";

        var view = new ChannelManagerView();
        view.Attach(settings, () => null, () => { });
        T Get<T>(string name) where T : class => (T)view.FindName(name);
        Setting.ChannelNodeBase? Draft() => Field<Setting.ChannelNodeBase?>(view, "_draft");
        var entry = Get<Button>("Button_LocalDeploy");
        var editor = Get<FrameworkElement>("Page_Editor");
        var url = Get<TextBox>("Ed_Url");
        var model = Get<ComboBox>("Ed_Model");

        Press(entry);
        Check("列表入口：打开时还在列表页", editor.Visibility != Visibility.Visible && Draft() is null);
        CheckDeployOpened("列表入口", ollamaUrl, "qwen3:4b");

        var applied = Call(view, "ApplyDeployChoice", "qwen3:8b", "http://localhost:11500");
        Check("列表入口：选用后进这条渠道的编辑页", applied is true && editor.Visibility == Visibility.Visible
                                                   && ReferenceEquals(Field<Setting.ChannelNodeBase?>(view, "_original"), node));
        Check("列表入口：表单填好", url.Text == "http://localhost:11500" && model.Text == "qwen3:8b", $"{url.Text} {model.Text}");
        Check("列表入口：没保存前节点不变", node.Url == ollamaUrl && node.Model == "qwen3:4b");

        var first = Draft();
        var again = Call(view, "ApplyDeployChoice", "qwen3:14b", "http://localhost:11600");
        Check("列表入口：再选一次继续填同一张表", again is true && ReferenceEquals(Draft(), first) && model.Text == "qwen3:14b" && url.Text == "http://localhost:11600");

        Call(view, "ShowList");
        Call(view, "OpenEditor", null!, Setting.ChannelKind.OpenAIChat);
        url.Text = "https://example.com/typed";
        var lost = Call(view, "ApplyDeployChoice", "qwen3:4b", ollamaUrl);
        Check("列表入口：正在编辑别的类型时不换掉表单", lost is false && Draft() is Setting.OpenAINodeSetting && url.Text == "https://example.com/typed");

        Call(view, "ShowList");
        settings.Ollama.OllamaNodes.Clear();
        var created = Call(view, "ApplyDeployChoice", "qwen3:4b", ollamaUrl);
        Check("列表入口：没有渠道时开新草稿", created is true && Draft() is Setting.OllamaNodeSetting && Field<Setting.ChannelNodeBase?>(view, "_original") is null && model.Text == "qwen3:4b");
        Check("列表入口：新草稿保存前不进设置", settings.Ollama.OllamaNodes.Count == 0);

        Call(view, "ShowList");
        settings.Ollama.OllamaNodes.Add(new Setting.OllamaNodeSetting { Url = "localhost:11434", Model = "" });
        Press(entry);
        Check("列表入口：地址不对时不开窗口", ManagerInstance() is null);
        Check("列表入口：地址不对时进编辑页说明原因", editor.Visibility == Visibility.Visible && Get<TextBlock>("Ed_ModelStatus").Text.Contains("接口地址不对"), Get<TextBlock>("Ed_ModelStatus").Text);

        // 留空是走默认地址，真打开会连本机，这里只看判断本身
        var isDeployUrl = typeof(ChannelManagerView).GetMethod("IsDeployUrl", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check("列表入口：地址留空算合法", isDeployUrl.Invoke(null, new object[] { "" }) is true);
    }

    private static void CheckLocalization()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "VPetLLM.csproj")))
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
        if (root is null)
        {
            Check("找到仓库根目录", false);
            return;
        }

        var lang = JObject.Parse(File.ReadAllText(Path.Combine(root, "3000_VPetLLM", "plugin", "VPetLLM_lang", "Language.json")));
        var code = File.ReadAllText(Path.Combine(root, "UI", "Windows", "winOllamaManager.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "UI", "Windows", "winOllamaManager.xaml"));
        var quickXaml = File.ReadAllText(Path.Combine(root, "UI", "Controls", "QuickChannelPanel.xaml"));
        var quickCode = File.ReadAllText(Path.Combine(root, "UI", "Controls", "QuickChannelPanel.xaml.cs"));
        var channelXaml = File.ReadAllText(Path.Combine(root, "UI", "Controls", "ChannelManagerView.xaml"));
        var channelCode = File.ReadAllText(Path.Combine(root, "UI", "Controls", "ChannelManagerView.xaml.cs"));
        var simpleXaml = File.ReadAllText(Path.Combine(root, "UI", "Windows", "winSimpleSetting.xaml"));
        var onboardingXaml = File.ReadAllText(Path.Combine(root, "UI", "Windows", "winOnboarding.xaml"));

        // 取键和中文兜底，兜底要和 zh-hans 一致，不然语言文件没换时看到的是另一句话
        var used = new Dictionary<string, string>();
        void Scan(string source, string pattern, string prefix)
        {
            foreach (Match m in Regex.Matches(source, pattern))
                used[prefix + m.Groups[1].Value] = m.Groups[2].Value;
        }

        const string keyPath = @"KeyPath=([\w.]+), Default=([^}]*)\}";
        Scan(code, @"\bT\(""(\w+)"",\s*""([^""]*)""\)", "OllamaManager.");
        Scan(xaml, keyPath, "");
        foreach (var source in new[] { quickXaml, channelXaml, simpleXaml, onboardingXaml })
            Scan(source, keyPath.Replace(@"[\w.]+", @"(?:Channels\.Ollama|QuickSetup\.(?:Ollama|LocalDesc)|OllamaManager\.)\w*"), "");
        Scan(quickCode, @"\bT\(""(QuickSetup\.Ollama\w*)"",\s*""([^""]*)""\)", "");
        Scan(channelCode, @"\bT\(""(Ollama\w*)"",\s*""([^""]*)""\)", "Channels.");
        foreach (var model in OllamaModelCatalog.Models)
            used[model.DescriptionKey] = model.DefaultDescription;

        Check("扫到了管理器词条", used.Count > 70, used.Count.ToString());

        var languages = new[] { "en", "zh-hans", "zh-hant", "jp" };
        var incomplete = new List<string>();
        var drifted = new List<string>();
        var placeholders = new List<string>();
        foreach (var (key, fallback) in used)
        {
            var texts = languages.Select(l => (string?)lang.SelectToken(key)?[l]).ToList();
            if (texts.Any(string.IsNullOrWhiteSpace))
            {
                incomplete.Add(key);
                continue;
            }
            if (texts[1] != fallback) drifted.Add(key);
            var shapes = texts.Select(t => string.Join("", Regex.Matches(t!, @"\{\d\}").Select(m => m.Value).Order())).Distinct();
            if (shapes.Count() != 1) placeholders.Add(key);
        }

        Check("管理器词条四种语言齐全", incomplete.Count == 0, string.Join(",", incomplete));
        Check("代码里的中文兜底和语言文件一致", drifted.Count == 0, string.Join(",", drifted));
        Check("管理器词条占位符一致", placeholders.Count == 0, string.Join(",", placeholders));

        var section = (JObject)lang["OllamaManager"]!;
        var unused = section.Properties().Select(p => "OllamaManager." + p.Name)
            .Where(k => k != "OllamaManager.Model" && !used.ContainsKey(k)).ToList();
        Check("语言文件没有多余词条", unused.Count == 0, string.Join(",", unused));
    }
}
