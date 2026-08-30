using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using VPet_Simulator.Core;
using VPetLLM.Utils.UI;

// 气泡独占守卫的回归检查。
//
// 起因：创意工坊的 ThemeCreator 在 MessageBarResources.ApplyResources 里
// 直接 (MessageBar)main.Main.MsgBar 强转。守卫早先把 Main.MsgBar 换成了一个
// 只实现 IMassageBar 的包装器，于是它当场 InvalidCastException，游戏起不来 ——
// 而且宿主的崩溃框按栈里的 VPet.Plugin.* 认领作者，锅记在了 ThemeCreator 头上。
//
// 试过让守卫继承 MessageBar，WPF 不允许（跨程序集 LoadComponent 校验程序集名）。
// 最终改成 Harmony 前置补丁：Main.MsgBar 保持宿主原装，谁想怎么强转都行。
//
// 这份检查要钉住三件事：
//   1. 宿主气泡的身份没被动过（崩溃本体）；
//   2. 补丁真的挂上了，而且真的能吞（不是个静默失效的摆设）；
//   3. 气泡自己内部的调用不被拦 —— 双击/右键关闭是用户亲手要关，拦了就成了关不掉。

static class Program
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  [PASS] {name}"); }
        else { _fail++; Console.WriteLine($"  [FAIL] {name}  {detail}"); }
    }

    static readonly Assembly Asm = typeof(BubbleGuard).Assembly;
    const string Root = @"D:\CodeDesk\VPetLLM\VPetLLM";
    static readonly string Src = File.ReadAllText(Path.Combine(Root, @"Utils\UI\BubbleGuard.cs"));

    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("被测程序集: " + Asm.Location);
        Console.WriteLine();

        Test_HostBarIdentityUntouched();
        Test_PatchesApplied();
        Test_FieldLookup();
        Test_Switch();
        Test_CopyGuardPatches();

        var ui = new Thread(Test_RuntimeBehaviour);
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();

        // 剪贴板必须在 STA 线程上访问
        var clip = new Thread(Test_CopyGuardRuntime);
        clip.SetApartmentState(ApartmentState.STA);
        clip.Start();
        clip.Join();

        Console.WriteLine();
        Console.WriteLine($"===== 通过 {_pass} / 失败 {_fail} =====");
        return _fail == 0 ? 0 : 1;
    }

    // =================================================================
    // 1. 崩溃本体：宿主气泡的身份必须一直是宿主自己的类型
    // =================================================================
    static void Test_HostBarIdentityUntouched()
    {
        Console.WriteLine("[1] 宿主气泡身份没被动过（第三方强转安全）");

        // 只看真代码：注释里还留着"宿主自己会 Main.MsgBar = new MessageBar(Main)"这句说明
        var code = Src.Split('\n')
                      .Where(l => !l.TrimStart().StartsWith("//"))
                      .ToList();
        Check("★ 守卫不再往 Main.MsgBar 上写任何东西",
            !code.Any(l => l.Contains("MsgBar = ")),
            code.FirstOrDefault(l => l.Contains("MsgBar = "))?.Trim() ?? "");
        Check("★ 包装器/子类那套已经拆干净",
            Asm.GetType("VPetLLM.Utils.UI.GuardedMessageBar") is null);
        Check("不再有视觉树替换那套（换实例才需要）",
            !Src.Contains("UIGrid.Children"));

        // 反过来钉住：宿主的气泡类型本身没有任何继承/包装的余地被引入
        Check("MessageBar 仍是宿主原装类型",
            typeof(MessageBar).Assembly.GetName().Name == "VPet-Simulator.Core");
        Check("★ 全仓没有任何类型继承 MessageBar（继承这条路 WPF 不允许，别再试）",
            !Asm.GetTypes().Any(t => t.BaseType == typeof(MessageBar)));

        Console.WriteLine();
    }

    // =================================================================
    // 2. 补丁挂到了正确的方法上
    // =================================================================
    static void Test_PatchesApplied()
    {
        Console.WriteLine("[2] Harmony 补丁");

        BubbleGuard.Install();

        MethodInfo? Target(params Type[] args) => typeof(MessageBar).GetMethod(
            args.Length == 0 ? "ForceClose" : "Show",
            BindingFlags.Public | BindingFlags.Instance, null, args, null);

        var showText = Target(typeof(string), typeof(string), typeof(string), typeof(System.Windows.UIElement));
        var showStream = Target(typeof(string), typeof(SayInfoWithStream));
        var forceClose = Target();

        Check("找得到 MessageBar.Show(文本)", showText is not null);
        Check("找得到 MessageBar.Show(流式)", showStream is not null);
        Check("找得到 MessageBar.ForceClose", forceClose is not null);

        bool Patched(MethodInfo? m)
        {
            if (m is null) return false;
            var info = Harmony.GetPatchInfo(m);
            return info?.Prefixes?.Any(p => p.owner == "com.vpetllm.bubbleguard") == true;
        }

        Check("★ Show(文本) 挂上了前置补丁", Patched(showText));
        Check("★ Show(流式) 挂上了前置补丁", Patched(showStream));
        Check("★ ForceClose 挂上了前置补丁", Patched(forceClose));

        // 幂等：插件里 LoadPlugin 和每次 BeginReply 都会调 Install
        BubbleGuard.Install();
        BubbleGuard.Install();
        var prefixCount = Harmony.GetPatchInfo(showText!)!.Prefixes.Count(p => p.owner == "com.vpetllm.bubbleguard");
        Check("★ 重复 Install 不会叠补丁（BeginReply 每次都会调）", prefixCount == 1, $"实际 {prefixCount} 个");

        // 不该顺手把宿主别的方法也打了
        var ours = Harmony.GetAllPatchedMethods()
            .Where(m => Harmony.GetPatchInfo(m)!.Owners.Contains("com.vpetllm.bubbleguard"))
            .ToList();
        Check("只打了这三个方法", ours.Count == 3,
            string.Join(", ", ours.Select(m => m.DeclaringType?.Name + "." + m.Name)));

        Console.WriteLine();
    }

    // =================================================================
    // 3. 反射找字段（MessageBarHelper 的能力探测靠它）
    // =================================================================
    static void Test_FieldLookup()
    {
        Console.WriteLine("[3] MessageBarHelper 找得到气泡的私有成员");

        var find = typeof(MessageBarHelper).GetMethod("FindField", BindingFlags.Static | BindingFlags.NonPublic);
        Check("FindField 存在", find is not null);
        if (find is null) { Console.WriteLine(); return; }

        FieldInfo? Find(string name) => (FieldInfo?)find.Invoke(null, new object[] { typeof(MessageBar), name });

        foreach (var name in new[] { "outputtext", "outputtextsample", "oldsaystream", "LName" })
            Check($"私有字段 {name}", Find(name) is not null);

        foreach (var name in new[] { "ShowTimer", "EndTimer", "CloseTimer", "TText", "MessageBoxContent" })
            Check($"公共字段 {name}", Find(name) is not null);

        Check("不存在的字段老实返回 null（能力探测才不会误报可用）", Find("绝对没有这个字段") is null);

        Console.WriteLine();
    }

    // =================================================================
    // 4. 开关得是真开关
    // =================================================================
    static void Test_Switch()
    {
        Console.WriteLine("[4] EnableBubbleExclusive 关掉就不碰宿主");

        var install = Src[Src.IndexOf("public static void Install()")..];
        install = install[..install.IndexOf("private static void Patch(")];

        Check("★ Install 一进门就查开关（以前只有吞气泡的判断查，宿主照样被动）",
            install.Contains("if (!IsEnabled) return;"));
        Check("补丁失败不连累插件加载（catch 住并置空）",
            install.Contains("_harmony = null;"));
        Check("卸载时把补丁摘干净（补丁指向本程序集，不摘就是悬空调用）",
            Src.Contains("_harmony.UnpatchAll(HarmonyId);"));

        Console.WriteLine();
    }

    // =================================================================
    // 5. 真跑：补丁到底吞没吞
    //
    //    探针：MessageBar.Show 第一句就是 m.UIGrid，ForceClose 里会碰 m.DisplayType，
    //    而构造函数只把 Main 存起来不解引用 —— 所以传 null 造出来的气泡，
    //    只要真的走进宿主方法就一定 NRE。
    //      抛了 = 放行（落到宿主原方法）；没抛 = 吞掉了。
    // =================================================================
    static void Test_RuntimeBehaviour()
    {
        Console.WriteLine("[5] 真跑：补丁的拦截行为");

        MessageBar NewBar()
        {
            // 宿主自己的类型加载自己的 XAML，这条是通的
            // （守卫要是继承它，这里就会抛"组件不具有由 URI 识别的资源"）
            return new MessageBar(null!);
        }

        MessageBar bar;
        try { bar = NewBar(); }
        catch (Exception ex)
        {
            Check("能造出宿主气泡（测试前提）", false, ex.Message);
            Console.WriteLine();
            return;
        }
        Check("能造出宿主气泡（测试前提）", true);

        static bool ReachedHost(Action call)
        {
            try { call(); return false; }
            catch (NullReferenceException) { return true; }
        }

        Check("不在回复中：别人的气泡照常显示（走到宿主方法）",
            ReachedHost(() => bar.Show("别人", "别人的话")));

        using (BubbleGuard.BeginReply())
        {
            Check("★★ 回复中：别人的气泡被吞掉",
                !ReachedHost(() => bar.Show("别人", "别人的话")));

            Check("★★ 回复中：别人的 ForceClose 被吞掉",
                !ReachedHost(() => NewBar().ForceClose()));

            BubbleGuard.AllowOnce("我们自己的话");
            Check("★ 回复中：登记过的自己人放行",
                ReachedHost(() => bar.Show("桌宠", "我们自己的话")));

            Check("放行只管一次，第二次同样文本照吞",
                !ReachedHost(() => bar.Show("桌宠", "我们自己的话")));

            // ── 这条是 Harmony 相对"换实例"多出来的风险点 ──
            // 换实例那版拦不到气泡自己内部的调用，Harmony 打在方法上会内外一起拦。
            // 双击气泡 / 右键"关闭"都是用户亲手要关掉它，拦了就成了"说起话来关都关不掉"。
            foreach (var handler in new[] { "UserControl_MouseDoubleClick", "MenuItemClose_Click" })
            {
                var gestureBar = NewBar();
                var method = typeof(MessageBar).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance);
                if (method is null) { Check($"找得到手势处理器 {handler}", false); continue; }

                Check($"★★ 回复中：用户手势 {handler} 不被拦（放行到宿主）",
                    ReachedHost(() =>
                    {
                        try { method.Invoke(gestureBar, new object?[] { gestureBar, null }); }
                        catch (TargetInvocationException e) when (e.InnerException is not null)
                        {
                            throw e.InnerException;
                        }
                    }));
            }
        }

        Check("退出回复作用域后立刻恢复放行",
            ReachedHost(() => bar.Show("别人", "别人的话")));

        // 卸载之后补丁必须真的摘掉，否则宿主会调进一个已卸载的程序集
        BubbleGuard.Uninstall();
        var still = Harmony.GetAllPatchedMethods()
            .Any(m => Harmony.GetPatchInfo(m)!.Owners.Contains("com.vpetllm.bubbleguard"));
        Check("★ Uninstall 之后补丁全摘干净", !still);

        Console.WriteLine();
    }

    // =================================================================
    // 6. 气泡复制保护：宿主 MenuItemCopy_Click 里那句裸奔的 Clipboard.SetText
    //
    //    真实崩溃（用户报告）：
    //      COMException (0x800401D0): OpenClipboard 失败 (CLIPBRD_E_CANT_OPEN)
    //        at System.Windows.Clipboard.Flush()
    //        at System.Windows.Controls.MenuItem.InvokeClickAfterRender(Object arg)
    //    剪贴板是全局独占资源，别的程序占着的时候宿主这一行必炸，
    //    异常一路冒到宿主全局处理 → "游戏发生错误" 弹窗。
    // =================================================================
    static MethodInfo? CopyTarget() => typeof(MessageBar).GetMethod(
        "MenuItemCopy_Click",
        BindingFlags.NonPublic | BindingFlags.Instance,
        null, new[] { typeof(object), typeof(System.Windows.RoutedEventArgs) }, null);

    const string CopyOwner = "com.vpetllm.bubblecopyguard";

    static void Test_CopyGuardPatches()
    {
        Console.WriteLine("[6] 气泡复制保护：补丁");

        var target = CopyTarget();
        Check("找得到 MessageBar.MenuItemCopy_Click(object, RoutedEventArgs)", target is not null);
        if (target is null) { Console.WriteLine(); return; }

        BubbleCopyGuard.Install();
        var info = Harmony.GetPatchInfo(target);

        Check("★ 挂上了前置补丁（自己用带退路的方式复制）",
            info?.Prefixes?.Any(x => x.owner == CopyOwner) == true);
        Check("★ 挂上了 finalizer（前置放行时兜住宿主抛的异常）",
            info?.Finalizers?.Any(x => x.owner == CopyOwner) == true);

        BubbleCopyGuard.Install();
        BubbleCopyGuard.Install();
        var count = Harmony.GetPatchInfo(target)!.Prefixes.Count(x => x.owner == CopyOwner);
        Check("★ 重复 Install 不会叠补丁", count == 1, $"实际 {count} 个");

        var ours = Harmony.GetAllPatchedMethods()
            .Where(m => Harmony.GetPatchInfo(m)!.Owners.Contains(CopyOwner))
            .ToList();
        Check("只补了这一个宿主方法（别顺手动别的）", ours.Count == 1, $"实际补了 {ours.Count} 个");

        // finalizer 的契约：返回 null = 异常已处理，不再上抛
        var finalizer = typeof(BubbleCopyGuard).GetMethod(
            "CopyFinalizer", BindingFlags.NonPublic | BindingFlags.Static);
        Check("找得到 CopyFinalizer", finalizer is not null);
        if (finalizer is not null)
        {
            var boom = new System.Runtime.InteropServices.COMException(
                "OpenClipboard 失败 (0x800401D0 (CLIPBRD_E_CANT_OPEN))", unchecked((int)0x800401D0));
            var kept = finalizer.Invoke(null, new object?[] { boom });
            Check("★★ finalizer 咽掉剪贴板 COMException（这就是崩溃的正解）", kept is null);
            Check("没异常时 finalizer 也不无中生有",
                finalizer.Invoke(null, new object?[] { null }) is null);
        }

        Console.WriteLine();
    }

    static void Test_CopyGuardRuntime()
    {
        Console.WriteLine("[7] 气泡复制保护：真跑");

        // 空内容不该算失败：气泡还没说过话时右键菜单照样点得动
        Check("空文本不报错也不算失败",
            VPetLLM.Utils.Common.ClipboardHelper.TrySetText(null) &&
            VPetLLM.Utils.Common.ClipboardHelper.TrySetText(""));

        var marker = "VPetLLM 复制保护自检 " + Guid.NewGuid().ToString("N");

        // 先探一下这台机器此刻的剪贴板能不能写。写不了正好 ——
        // 那就是用户报的那个现场，下面可以真刀真枪地验"有没有保护"的区别。
        bool clipboardWritable;
        try { System.Windows.Clipboard.SetDataObject("probe", true); clipboardWritable = true; }
        catch { clipboardWritable = false; }
        Console.WriteLine($"  [环境] 剪贴板当前{(clipboardWritable ? "可写" : "被其它程序独占")}");

        MessageBar bar;
        try { bar = new MessageBar(null!); }
        catch (Exception ex)
        {
            Check("能造出宿主气泡（测试前提）", false, ex.Message);
            Console.WriteLine();
            return;
        }

        var tText = bar.FindName("TText") as System.Windows.Controls.TextBox;
        Check("拿得到气泡正文控件 TText", tText is not null);
        if (tText is not null) tText.Text = marker;

        var target = CopyTarget();
        if (target is null) { Console.WriteLine(); return; }

        Exception? Invoke()
        {
            try { target.Invoke(bar, new object?[] { bar, null }); return null; }
            catch (TargetInvocationException e) { return e.InnerException ?? e; }
            catch (Exception e) { return e; }
        }

        // ---- 没有保护时的宿主原始行为 ----
        BubbleCopyGuard.Uninstall();
        var bare = Invoke();
        if (!clipboardWritable)
        {
            // 复现成立：这一条正是用户贴的那个 COMException
            Check("★★ 复现：无保护时宿主复制确实抛 COMException",
                bare is System.Runtime.InteropServices.COMException,
                bare is null ? "居然没抛" : $"{bare.GetType().Name}");
        }
        else
        {
            Console.WriteLine("  [跳过] 剪贴板可写，这台机器上复现不了崩溃（换台占着剪贴板的机器再跑）");
        }

        // ---- 装上保护之后 ----
        BubbleCopyGuard.Install();
        var guarded = Invoke();
        Check("★★ 装上保护后点「复制」不再往外抛任何异常", guarded is null,
              guarded is null ? "" : $"{guarded.GetType().Name}: {guarded.Message}");

        if (clipboardWritable)
        {
            string actual;
            try { actual = System.Windows.Clipboard.GetText(); }
            catch { actual = "<读不出来>"; }
            Check("★ 保护没把功能弄丢：剪贴板里确实是气泡正文", actual == marker, $"实际 {actual}");
        }

        BubbleCopyGuard.Uninstall();
        var still = Harmony.GetAllPatchedMethods()
            .Any(m => Harmony.GetPatchInfo(m)!.Owners.Contains(CopyOwner));
        Check("★ Uninstall 之后补丁摘干净", !still);

        Console.WriteLine();
    }
}
