using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VPetLLM.Core.Services;
using VPetLLM.UI.Controls;

namespace VPetLLM.OllamaChecks;

internal static partial class Program
{
    /// <summary>窗口放在屏幕上，发真实的滚轮事件量帧间隔。屏幕外的窗口不合成，量不到特效的开销。不算检查项。</summary>
    private static void ScrollPerf(string[] options)
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        _settings = FreshSettings(Directory.CreateTempSubdirectory("vpetllm-ollama-ui-").FullName);

        if (options.Contains("no-effect"))
            EventManager.RegisterClassHandler(typeof(Border), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => ((Border)sender).Effect = null));

        var net = LibraryNet();
        net.Html = $"<html><body><ul>{string.Concat(Enumerable.Range(0, 266).Select(i => Card($"row{i:000}", "A long description of what this model does, wide enough to fill the whole line and trigger trimming in the row.", "vision 8b 12b")))}</ul></body></html>";
        for (var i = 0; i < 266; i++)
        {
            net.Sizes[$"row{i:000}:8b"] = 4 * GB;
            net.Sizes[$"row{i:000}:12b"] = 9 * GB;
        }

        var api = new FakeApi { Running = true };
        var installer = new FakeDeployInstaller(new OllamaInstallation(@"C:\Ollama", "0.40.13")) { Api = api, FreeSpace = 200 * GB };
        var library = options.Contains("real") ? new OllamaLibrary(cacheDirectory: NewLibraryCache()) : new OllamaLibrary(net, NewLibraryCache());
        var window = new ManagerWindow(installer, api, library: library, hardware: StrongHardware);
        window.Window.Left = 200;
        window.Window.Top = 100;

        var list = window.Find<ListBox>("Items_Library");
        if (options.Contains("off")) SmoothWheelScroll.SetEnabled(list, false);
        if (options.Contains("recycle")) VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        window.OpenLibrary();
        var settle = DateTime.UtcNow.AddSeconds(1);
        Pump(() => DateTime.UtcNow >= settle);

        var panel = Descendants(list).OfType<VirtualizingStackPanel>().First();
        var viewer = Descendants(list).OfType<ScrollViewer>().First();
        Console.WriteLine($"render tier={RenderCapability.Tier >> 16} rows={window.Library.Count} realized={window.RealizedRows()} options={string.Join(",", options)}");

        // 拿屏幕上第一行当参照：它实际移动的距离和滚轮该走的距离对不上，就是肉眼看到的跳
        (object? Row, double Top) Anchor()
        {
            var rows = Descendants(list).OfType<ListBoxItem>()
                .Select(item => (Row: item.DataContext, Top: item.TranslatePoint(new Point(0, 0), list).Y, item.ActualHeight))
                .Where(item => item.Top + item.ActualHeight > 0 && item.Top < list.ActualHeight)
                .OrderBy(item => item.Top).ToList();
            return rows.Count == 0 ? (null, 0) : (rows[0].Row, rows[0].Top);
        }

        double? Top(object? row)
        {
            var item = Descendants(list).OfType<ListBoxItem>().FirstOrDefault(i => ReferenceEquals(i.DataContext, row));
            return item?.TranslatePoint(new Point(0, 0), list).Y;
        }

        foreach (var (name, interval, notches, delta) in new[] { ("fast-down", 40, 60, -120), ("slow-down", 120, 25, -120), ("paced-down", 300, 20, -120), ("paced-up", 300, 20, 120), ("fast-up", 40, 60, 120) })
        {
            var shifts = new List<double>();
            var frames = new List<double>();
            var costs = new List<double>();
            var clock = Stopwatch.StartNew();
            var offsets = new List<double>();
            var events = 0;
            var lastFrame = TimeSpan.Zero;
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            EventHandler onFrame = (_, e) =>
            {
                // Rendering 一帧可能触发多次，按 RenderingTime 才是真实帧
                events++;
                var time = ((RenderingEventArgs)e).RenderingTime;
                if (time == lastFrame) return;
                lastFrame = time;
                frames.Add(clock.Elapsed.TotalMilliseconds);
                offsets.Add(viewer.VerticalOffset);
            };
            CompositionTarget.Rendering += onFrame;

            var start = viewer.VerticalOffset;
            var sent = 0;
            var done = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(interval) };
            timer.Tick += (_, _) =>
            {
                var (row, before) = Anchor();
                var cost = Stopwatch.StartNew();
                var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
                panel.RaiseEvent(preview);
                if (!preview.Handled) panel.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent });
                window.Window.UpdateLayout();
                costs.Add(cost.Elapsed.TotalMilliseconds);
                if (Top(row) is { } after) shifts.Add(Math.Abs(after - before));
                if (++sent < notches) return;
                timer.Stop();
                done.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(done);

            var settled = DateTime.UtcNow.AddMilliseconds(400);
            Pump(() => DateTime.UtcNow >= settled);
            CompositionTarget.Rendering -= onFrame;
            cpu = Process.GetCurrentProcess().TotalProcessorTime - cpu;
            var jump = offsets.Zip(offsets.Skip(1), (a, b) => Math.Abs(b - a)).DefaultIfEmpty(0).Max();
            var gaps = frames.Zip(frames.Skip(1), (a, b) => b - a).OrderBy(g => g).ToList();
            var ordered = costs.OrderBy(c => c).ToList();
            double At(List<double> values, double q) => values.Count == 0 ? 0 : values[(int)((values.Count - 1) * q)];
            Console.WriteLine($"{name}: step={(viewer.VerticalOffset - start) / notches:0.0}px frameJump={jump:0.0}px ui p50={At(ordered, .5):0.0} p95={At(ordered, .95):0.0} max={At(ordered, 1):0.0}ms | events={events} frames={frames.Count} cpu={cpu.TotalMilliseconds / clock.Elapsed.TotalMilliseconds * 100:0}% gap p50={At(gaps, .5):0.0} p95={At(gaps, .95):0.0} max={At(gaps, 1):0.0}ms over33={gaps.Count(g => g > 33)} realized={window.RealizedRows()} | shift off48={shifts.Count(s => Math.Abs(s - 48) > 1)}/{shifts.Count} max={shifts.DefaultIfEmpty(0).Max():0.0}");
        }

        var heights = Descendants(list).OfType<ListBoxItem>().Select(item => Math.Round(item.ActualHeight)).Distinct().OrderBy(h => h);
        Console.WriteLine($"row heights: {string.Join(",", heights)} viewport={viewer.ViewportHeight:0} extent={viewer.ExtentHeight:0}");
        window.Close();
        DeleteLibraryCaches();
    }
}
