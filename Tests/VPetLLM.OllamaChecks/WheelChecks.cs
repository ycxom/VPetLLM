using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using VPetLLM.Core.Services;
using VPetLLM.UI.Controls;
using VPetLLM.UI.Windows;

namespace VPetLLM.OllamaChecks;

internal static partial class Program
{
    /// <summary>和真实输入一样先走隧道，没人接才冒泡。返回有没有被 Preview 接走。</summary>
    private static bool Wheel(UIElement element, int delta)
    {
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        element.RaiseEvent(preview);
        if (!preview.Handled) element.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent });
        return preview.Handled;
    }

    private static bool Wheel(ContentElement element, int delta)
    {
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        element.RaiseEvent(preview);
        return preview.Handled;
    }

    private static void PumpFor(int milliseconds)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        Pump(() => DateTime.UtcNow >= end);
    }

    private static void CheckSmoothWheel()
    {
        Check("平滑：一帧只走一部分", SmoothWheelScroll.Approach(0, 48, 0.016) is > 0 and < 48, SmoothWheelScroll.Approach(0, 48, 0.016).ToString());
        Check("平滑：时间为零不动", SmoothWheelScroll.Approach(10, 48, 0) == 10);
        Check("平滑：卡顿时一帧走得更远", SmoothWheelScroll.Approach(0, 48, 0.1) > SmoothWheelScroll.Approach(0, 48, 0.016));
        Check("平滑：往回滚方向对", SmoothWheelScroll.Approach(48, 0, 0.016) is > 0 and < 48);
        Check("平滑：绝不冲过头", SmoothWheelScroll.Approach(0, 48, 100) == 48);
        Check("平滑：接近时落到目标", SmoothWheelScroll.Approach(47.6, 48, 0.001) == 48);
        Check("平滑：差得远时不提前落到目标", SmoothWheelScroll.Approach(0, 48, 0.001) != 48);
        Check("平滑：还差两像素时不提前落到目标", SmoothWheelScroll.Approach(46, 48, 0.001) != 48);
        Check("平滑：系统设置每格三行", SmoothWheelScroll.WheelStep(3, 340) == 48);
        Check("平滑：系统设置每格一行", SmoothWheelScroll.WheelStep(1, 340) == 16);
        Check("平滑：系统设置滚动一屏", SmoothWheelScroll.WheelStep(-1, 340) == 340);
        Check("平滑：系统设置不滚动", SmoothWheelScroll.WheelStep(0, 340) == 0);

        var api = new FakeApi { Running = true };
        var net = LibraryNet();
        net.Html = Page();
        var window = new ManagerWindow(new FakeDeployInstaller(new OllamaInstallation(@"C:\Ollama", "0.40.13")) { Api = api }, api, library: new OllamaLibrary(net, NewLibraryCache()), hardware: StrongHardware);
        window.OpenLibrary();
        var list = window.Find<ListBox>("Items_Library");
        var viewer = Descendants(list).OfType<ScrollViewer>().First();
        var panel = Descendants(list).OfType<VirtualizingStackPanel>().First();

        bool Moved(double offset)
        {
            Pump(() => viewer.VerticalOffset == offset);
            return viewer.VerticalOffset == offset;
        }

        Check("滚轮：列表接走事件", Wheel(panel, -120));
        window.Window.UpdateLayout();
        Check("滚轮：不是一下跳完", viewer.VerticalOffset == 0, viewer.VerticalOffset.ToString());
        Check("滚轮：一格走到 48", Moved(48), viewer.VerticalOffset.ToString());

        (List<double> Steps, long Milliseconds, bool Arrived) Animate(int delta, double target)
        {
            var steps = new List<double>();
            ScrollChangedEventHandler onStep = (_, e) => { if (e.VerticalChange != 0) steps.Add(Math.Abs(e.VerticalChange)); };
            viewer.ScrollChanged += onStep;
            var clock = Stopwatch.StartNew();
            Wheel(panel, delta);
            var arrived = Moved(target);
            clock.Stop();
            viewer.ScrollChanged -= onStep;
            return (steps, clock.ElapsedMilliseconds, arrived);
        }

        string Describe((List<double> Steps, long Milliseconds, bool Arrived) run)
            => $"{run.Steps.Count} steps in {run.Milliseconds}ms: {string.Join(" ", run.Steps.Select(s => s.ToString("0.0")))}";

        // 用户抱怨的就是一帧跳完，只看终点抓不到；隔得够久才能确认第一帧没把空闲的时间补上
        bool Smooth((List<double> Steps, long Milliseconds, bool Arrived) run) => run.Arrived && run.Steps.Count >= 3 && run.Steps.Max() < 48;

        PumpFor(400);
        var first = Animate(-120, 96);
        // 机器忙时一帧能卡半秒多，那一次本来就走不出几帧，所以多采几次
        for (var i = 0; i < 4 && !Smooth(first); i++)
        {
            Animate(120, 48);
            PumpFor(400);
            first = Animate(-120, 96);
        }
        Check("滚轮：一格分几帧走完", Smooth(first), Describe(first));

        // 帧率低于限频时看不出差别，而帧率随机器负载变，所以多采几次
        var runs = new[] { first, Animate(120, 48), Animate(-120, 96), Animate(120, 48), Animate(-120, 96) };
        var dense = runs.FirstOrDefault(r => r.Steps.Count > r.Milliseconds / 12 + 2);
        Check("滚轮：动画不比限频更密", dense.Steps is null, dense.Steps is null ? "" : Describe(dense));
        viewer.ScrollToVerticalOffset(48);
        window.Window.UpdateLayout();
        PumpFor(60);
        Check("滚轮：停下后不跟滚动条抢位置", viewer.VerticalOffset == 48, viewer.VerticalOffset.ToString());

        Wheel(panel, -120);
        Wheel(panel, -120);
        Check("滚轮：连滚两格都算数", Moved(144), viewer.VerticalOffset.ToString());

        Wheel(panel, -120);
        Wheel(panel, 120);
        Check("滚轮：中途反向接着算", Moved(144), viewer.VerticalOffset.ToString());

        Wheel(panel, -60);
        Check("滚轮：触控板的小步长按比例", Moved(168), viewer.VerticalOffset.ToString());

        viewer.ScrollToHome();
        window.Window.UpdateLayout();
        Wheel(panel, 120);
        Pump(() => true);
        Check("滚轮：顶部不再往上", viewer.VerticalOffset == 0, viewer.VerticalOffset.ToString());

        Wheel(panel, 120);
        Wheel(panel, 120);
        Wheel(panel, -120);
        Check("滚轮：顶上多滚的不用还回去", Moved(48), viewer.VerticalOffset.ToString());

        // 触控板事件比限频密得多，动画不能每来一个就从头算
        viewer.ScrollToHome();
        window.Window.UpdateLayout();
        PumpFor(50);
        for (var i = 0; i < 40; i++)
        {
            Wheel(panel, -30);
            PumpFor(4);
        }

        Check("滚轮：连续小步长时一直在走", viewer.VerticalOffset > 48, viewer.VerticalOffset.ToString());
        Check("滚轮：连续小步长的总量不丢", Moved(480), viewer.VerticalOffset.ToString());

        window.ScrollLibraryToEnd();
        var bottom = viewer.VerticalOffset;
        Wheel(panel, -120);
        Wheel(panel, -120);
        Pump(() => true);
        PumpFor(150);
        Wheel(panel, 120);
        Check("滚轮：到底后回滚一格立刻有反应", Moved(bottom - 48), $"{bottom} -> {viewer.VerticalOffset}");

        viewer.ScrollToHome();
        window.Window.UpdateLayout();
        PumpFor(50);
        var combo = Descendants(list).OfType<ComboBox>().First(c => c.Items.Count > 0);
        var row = (OllamaLibraryRow)combo.DataContext;
        var picked = row.Selected;
        Check("滚轮：光标在下拉框上也归列表", Wheel(combo, -120));
        Check("滚轮：光标在下拉框上时滚列表", Moved(48), viewer.VerticalOffset.ToString());
        Check("滚轮：不改下拉框选中项", ReferenceEquals(row.Selected, picked));

        viewer.ScrollToHome();
        window.Window.UpdateLayout();
        PumpFor(50);
        combo = Descendants(list).OfType<ComboBox>().First(c => c.Items.Count > 0);
        combo.IsDropDownOpen = true;
        PumpFor(50);
        var dropdown = (Popup)combo.Template.FindName("PART_Popup", combo);
        Check("滚轮：展开的下拉框不归列表", !Wheel(combo, -120));
        Check("滚轮：下拉列表里面不归列表", !Wheel(dropdown.Child, -120));
        PumpFor(80);
        Check("滚轮：展开时列表不动", viewer.VerticalOffset == 0, viewer.VerticalOffset.ToString());
        combo.IsDropDownOpen = false;
        Pump(() => true);

        // 切走时动画还在跑：不能卡在“运行中”，回来还要能滚
        Wheel(panel, -120);
        window.Find<TabControl>("Tabs").SelectedIndex = 0;
        PumpFor(100);
        window.OpenLibrary();
        PumpFor(400);
        list = window.Find<ListBox>("Items_Library");
        viewer = Descendants(list).OfType<ScrollViewer>().First();
        panel = Descendants(list).OfType<VirtualizingStackPanel>().First();
        var resumed = viewer.VerticalOffset;
        Wheel(panel, -120);
        Check("滚轮：切走再回来还能滚", Moved(resumed + 48), $"{resumed} -> {viewer.VerticalOffset}");
        window.Close();

        // 里层还能滚的，一格让给里层；里层到不了的，归外层
        var inner = new ScrollViewer { Height = 40, Content = new Border { Height = 200 } };
        var flat = new ScrollViewer { Height = 40, Content = new Border { Height = 10 } };
        var run = new Run("text");
        var outer = new ScrollViewer { Content = new StackPanel { Children = { inner, flat, new TextBlock(run), new Border { Height = 500 } } } };
        SmoothWheelScroll.SetEnabled(outer, true);
        var host = new Window { Width = 300, Height = 200, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false, Content = outer };
        host.Show();
        Pump(() => true);
        Check("滚轮：里层能滚时让给里层", !Wheel((UIElement)inner.Content, -120));
        Check("滚轮：里层滚不了时归外层", Wheel((UIElement)flat.Content, -120));
        Check("滚轮：文字内容元素上也能滚", Wheel(run, -120));
        SmoothWheelScroll.SetEnabled(outer, false);
        Check("滚轮：关掉后交还给 WPF", !Wheel((UIElement)flat.Content, -120));
        host.Close();

        // 关窗时动画在跑不能出错；"界面全程没有未处理异常"兜底
        var closing = new ManagerWindow(new FakeDeployInstaller(new OllamaInstallation(@"C:\Ollama", "0.40.13")) { Api = api }, api, library: new OllamaLibrary(LibraryNet(), NewLibraryCache()), hardware: StrongHardware);
        closing.OpenLibrary();
        Wheel(Descendants(closing.Find<ListBox>("Items_Library")).OfType<VirtualizingStackPanel>().First(), -120);
        closing.Close();
        PumpFor(50);

        // 这几个窗口的滚动区都要接上，漏一个就是那一页还一顿一顿的
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "VPetLLM.csproj"))) root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))!;
        foreach (var file in new[] { "winOllamaManager", "winSimpleSetting", "winOnboarding" })
        {
            var xaml = File.ReadAllText(Path.Combine(root, "UI", "Windows", file + ".xaml"));
            var tags = Regex.Matches(xaml, @"<(ScrollViewer|ListBox)\s[^>]*>").Select(m => m.Value).ToList();
            Check($"滚轮：{file} 有滚动区", tags.Count > 0);
            Check($"滚轮：{file} 全部接上", tags.All(t => t.Contains("controls:SmoothWheelScroll.Enabled=\"True\"")), string.Join(" ", tags.Where(t => !t.Contains("SmoothWheelScroll"))));
        }
    }
}
