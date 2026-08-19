using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Windows;
using VPet_Simulator.Windows.Interface;

// 退出路径的回归检查。
//
// 起因：退出游戏时弹"游戏致命性错误 System.ComponentModel.Win32Exception (1400)
// 无效的窗口句柄"，栈全在 WPF 里 —— Dispatcher.ShutdownFinished →
// HwndSource.Dispose → HwndWrapper.DestroyWindow。没有任何 MOD 帧，所以不点名。
//
// 查出来的根：宿主关停时只调 MainPlugin.EndGame()，**它根本不认识 IDisposable**，
// 全仓也没有任何地方调过 VPetLLM.Dispose()。而我们以前没实现 EndGame ——
// 于是正常退出时本插件一点清理都不做，窗口一路活到 Environment.Exit，
// 等 WPF 挨个销毁时句柄已经没了。
//
// 宿主的顺序是 Window_Closed → EndGame() → Save() → Exit()，
// Save 在 EndGame 之后，所以关停不能碰配置存储。

static class Program
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  [PASS] {name}"); }
        else { _fail++; Console.WriteLine($"  [FAIL] {name}  {detail}"); }
    }

    const string Root = @"D:\CodeDesk\VPetLLM\VPetLLM";
    static string Read(string rel) => File.ReadAllText(Path.Combine(Root, rel));
    static readonly Type Plugin = typeof(VPetLLM.VPetLLM);

    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("被测程序集: " + Plugin.Assembly.Location);
        Console.WriteLine();

        Test_HookedIntoHostShutdown();
        Test_SaveOrdering();
        Test_NoModalOnExit();

        var ui = new Thread(Test_WindowSweep);
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();

        Console.WriteLine();
        Console.WriteLine($"===== 通过 {_pass} / 失败 {_fail} =====");
        return _fail == 0 ? 0 : 1;
    }

    // =================================================================
    // 1. 挂在宿主真正会调的那个回调上
    // =================================================================
    static void Test_HookedIntoHostShutdown()
    {
        Console.WriteLine("[1] 关停挂在宿主会调的回调上");

        var hostHook = typeof(MainPlugin).GetMethod("EndGame");
        Check("宿主确实提供 EndGame 这个关停回调", hostHook is not null && hostHook.IsVirtual);

        var ours = Plugin.GetMethod("EndGame");
        Check("★ 我们重写了 EndGame（以前没有 = 正常退出零清理）",
            ours is not null && ours.DeclaringType == Plugin,
            $"DeclaringType={ours?.DeclaringType?.Name}");

        // 宿主不认识 IDisposable，Dispose 只服务于"插件被禁用/重载"这条路。
        // 两条路必须收敛到同一套关停，否则又会出现"改了一条路另一条还是旧的"
        Check("Dispose 仍在（插件禁用/重载走这条）", Plugin.GetMethod("Dispose") is not null);

        var src = Read("VPetLLM.cs");
        Check("★ 两条路都走同一个 ShutdownUiAndHooks",
            src.Split("ShutdownUiAndHooks()").Length - 1 >= 3,
            "调用点少于 2 处");
        Check("关停是幂等的（EndGame 后再 Dispose 不会做两遍）",
            src.Contains("if (_shutdownDone) return;"));
        Check("★ 每一步都包了 Run(...)，一步失败不中断后面（少关一个窗口=一次崩溃）",
            src.Contains("Run(CloseOwnedWindows,"));

        Console.WriteLine();
    }

    // =================================================================
    // 2. EndGame 在 Save 之前 —— 关停不能拆掉存档要用的东西
    // =================================================================
    static void Test_SaveOrdering()
    {
        Console.WriteLine("[2] EndGame 在 Save 之前的时序约束");

        var src = Read("VPetLLM.cs");
        var shutdown = src[src.IndexOf("private void ShutdownUiAndHooks()")..];
        shutdown = shutdown[..shutdown.IndexOf("private static void Run(")];

        // 关停里绝不能出现这几样：Save() 之后才轮到它们
        foreach (var forbidden in new[] { "_container.Dispose", "_serviceManager.Dispose", "_configurationManager" })
        {
            Check($"★ 关停里不碰 {forbidden}（Save 还没跑）", !shutdown.Contains(forbidden));
        }

        Check("关停确实关窗口", shutdown.Contains("CloseOwnedWindows"));
        Check("关停确实摘补丁", shutdown.Contains("BubbleGuard.Uninstall"));
        Check("关停确实停定时器", shutdown.Contains("_syncTimer?.Stop()"));
        Check("关停确实关停动画协调器（否则后台队列循环还攥着死窗口）",
            shutdown.Contains("AnimationHelper.Shutdown"));
        Check("关停确实退订宿主事件", shutdown.Contains("UnregisterItemUseHook"));

        // 退出时的存档必须等落盘：Exit() 最后一句是 Environment.Exit(0)
        var save = src[src.IndexOf("public override void Save()")..];
        save = save[..save.IndexOf("private async Task SaveConfigurationsAsync")];
        Check("★ 退出路径上配置改成同步等待落盘（否则 Environment.Exit 截断）",
            save.Contains("_isShuttingDown") && save.Contains(".Wait(TimeSpan.FromSeconds(3))"));
        Check("★ 用 Task.Run 起头避开 UI 线程自等（Window_Closed 就在 UI 线程上）",
            save.Contains("Task.Run(() => _configurationManager.SaveAllAsync())"));
        Check("平时仍然是后台异步保存，不拖慢 UI",
            save.Contains("_ = SaveConfigurationsAsync();"));

        Console.WriteLine();
    }

    // =================================================================
    // 3. 退出时不能再弹模态
    // =================================================================
    static void Test_NoModalOnExit()
    {
        Console.WriteLine("[3] 退出时不弹确认框");

        Check("★ 插件暴露 IsExiting 供窗口判断", Plugin.GetProperty("IsExiting") is not null);

        var src = Read("VPetLLM.cs");
        Check("IsExiting 是实例属性不是静态的（多开时一只退出不代表另一只退出）",
            src.Contains("public bool IsExiting => _isShuttingDown;"));

        var editor = Read(@"UI\Windows\winContextEditor.xaml.cs");
        var closing = editor[editor.IndexOf("protected override void OnClosing")..];
        closing = closing[..closing.IndexOf("private bool HasUnsavedChanges")];

        Check("★ 上下文编辑器退出时跳过未保存确认", closing.Contains("if (_plugin.IsExiting)"));
        Check("★ 而且这个判断在弹框之前",
            closing.IndexOf("_plugin.IsExiting") < closing.IndexOf("MessageBox.Show"),
            "顺序反了，还是会弹");
        Check("平时仍然会提醒未保存（别把功能改没了）",
            closing.Contains("HasUnsavedChanges()") && closing.Contains("e.Cancel = true;"));

        Console.WriteLine();
    }

    // =================================================================
    // 4. 真跑：只关自己的窗口，别人的一个都别碰
    // =================================================================
    static void Test_WindowSweep()
    {
        Console.WriteLine("[4] 真跑：窗口清扫的范围");

        _ = new Application();

        // 我们自己的窗口：HotkeyCapture 是纯代码构建的，不用 XAML，最适合当样本
        var mine1 = new VPetLLM.UI.Windows.HotkeyCapture();
        var mine2 = new VPetLLM.UI.Windows.HotkeyCapture();

        // 别人的窗口：直接用 WPF 自己的 Window，程序集不是我们的
        var theirs = new Window();

        Check("三个窗口都进了 Application.Windows",
            Application.Current.Windows.Count == 3, $"实际 {Application.Current.Windows.Count}");

        // CloseOwnedWindows 只用到 Application.Current / 本程序集 / Logger，
        // 不需要一个真正初始化过的插件实例
        var plugin = FormatterServices.GetUninitializedObject(Plugin);
        var sweep = Plugin.GetMethod("CloseOwnedWindows", BindingFlags.Instance | BindingFlags.NonPublic);
        Check("找得到 CloseOwnedWindows", sweep is not null);
        if (sweep is null) { Console.WriteLine(); return; }

        try
        {
            sweep.Invoke(plugin, null);
        }
        catch (Exception ex)
        {
            Check("★ 清扫过程不抛异常（退出路径上抛出去就是又一个致命错误框）", false,
                (ex.InnerException ?? ex).ToString().Split('\n')[0]);
            Console.WriteLine();
            return;
        }

        Check("★ 清扫过程不抛异常（退出路径上抛出去就是又一个致命错误框）", true);

        var left = Application.Current.Windows.Cast<Window>().ToList();
        Check("★★ 自己的窗口全关掉了（这就是 1400 的正解）",
            !left.Any(w => w.GetType().Assembly == Plugin.Assembly),
            string.Join(", ", left.Select(w => w.GetType().Name)));
        Check("★★ 别人的窗口一个都没碰（绝不能替宿主/别的 MOD 关窗口）",
            left.Contains(theirs), "别人的窗口被我们关掉了");
        Check("确实是按程序集分的，不是按数量", left.Count == 1, $"剩 {left.Count} 个");

        // 再来一次：已经关掉的窗口不该让清扫翻车
        try { sweep.Invoke(plugin, null); Check("重复清扫是安全的", true); }
        catch (Exception ex) { Check("重复清扫是安全的", false, (ex.InnerException ?? ex).Message); }

        Application.Current.Shutdown();
        Console.WriteLine();
    }
}
