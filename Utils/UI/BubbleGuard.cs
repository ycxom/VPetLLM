using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using HarmonyLib;
using VPet_Simulator.Core;

namespace VPetLLM.Utils.UI
{
    /// <summary>
    /// 气泡独占守卫：LLM 正在把回复演出来的这段时间里，别人的气泡请求一律吞掉。
    ///
    /// 为什么要有它：宿主的所有气泡最终都落在同一个 <see cref="IMassageBar"/> 上，而
    /// <see cref="MessageBar.Show"/> 是"直接覆盖当前内容"的语义 —— 任何插件（或宿主自己的
    /// 闲置随机说话、打工结束播报）在这期间说一句，桌宠正在念的那段回复就会被整段顶掉，
    /// 文字没了、语音还在放。
    ///
    /// 为什么是"黑洞"而不是拦截：调用方拿不到任何异常也看不出被拦，
    /// <c>Main.Say(...)</c> 照常返回、动画照常播、它自己的状态机照常往下走 ——
    /// 只是那次显示没有落到气泡上。对别的 mod 来说这是一次成功的调用，
    /// 不会因为我们的介入而抛异常或卡在等待里。
    ///
    /// <b>为什么最后用了 Harmony</b>（这条路是一步步被堵出来的，别再走回头路）：
    ///
    /// 最早的做法是把 <c>Main.MsgBar</c>（公开可写字段，宿主自己在多人模式里也这么换）
    /// 换成一个实现了 <see cref="IMassageBar"/> 的包装器。当时判断"全仓没人把它强转成
    /// 具体类型，所以安全"—— 那次排查只覆盖了 VPet 仓和我们自己的插件，<b>没覆盖创意工坊的
    /// 第三方 MOD</b>。实际有人踩了：ThemeCreator 的
    /// <c>MessageBarResources.ApplyResources</c> 就是 <c>(MessageBar)main.Main.MsgBar</c>
    /// 直接强转，包装器让它当场 InvalidCastException，游戏起不来。
    /// 更糟的是宿主的全局错误框按栈里的 <c>VPet.Plugin.*</c> 认领作者，
    /// <b>我们捅的娄子会报到 ThemeCreator 作者头上</b>。
    ///
    /// 第二个想法是让守卫继承 <see cref="MessageBar"/>，这样强转就成立了。
    /// <b>WPF 不允许</b>：<c>MessageBar</c> 的构造函数会调 <c>InitializeComponent()</c>，
    /// 而它内部的 <c>Application.LoadComponent(this, uri)</c> 要求 <c>this</c> 的程序集
    /// 和 URI 里的程序集一致。子类在 VPetLLM.dll 里，URI 指向 VPet-Simulator.Core，
    /// 构造时直接抛"组件不具有由 URI 识别的资源"。跨程序集继承 XAML 控件这条路是死的。
    ///
    /// 剩下的就是 Harmony：给 <c>MessageBar</c> 的 <c>Show</c>/<c>ForceClose</c> 挂前置补丁。
    /// <c>Main.MsgBar</c> 自始至终是宿主原装的那个 <see cref="MessageBar"/>，
    /// 谁想怎么强转都行；而拦截比换实例更彻底 —— 连拿着具体类型引用调过来的也拦得住。
    /// </summary>
    public static class BubbleGuard
    {
        private static readonly object _lock = new();

        /// <summary>回复中的嵌套层数。&gt;0 表示 LLM 正在演出回复</summary>
        private static int _replyDepth;

        /// <summary>最外层那次进入回复状态的时刻，用于兜底解除（见 <see cref="MaxReplyHold"/>）</summary>
        private static DateTime _replyStartedAt;

        /// <summary>
        /// 放行清单：本插件自己马上要显示的文本 → 还剩几次没被消费。
        ///
        /// 为什么要按文本认领而不是用 AsyncLocal 之类的作用域标记：我们自己的气泡走的是
        /// <c>Main.Say(text, 动画名, force)</c>，宿主内部先 <c>Task.Run</c>，再把真正的
        /// <c>MsgBar.Show</c> 塞进 <c>Display(..., A_Start, 回调)</c> 里等动画起播才执行 ——
        /// 那个回调是被动画线程直接调用的普通委托，不带调用方的 ExecutionContext，
        /// 作用域标记到不了那里。文本是唯一能一路带过去的东西。
        /// </summary>
        private static readonly Dictionary<string, PendingBubble> _pending = new();

        private sealed class PendingBubble
        {
            public int Count;
            public DateTime LastRegisteredAt;
        }

        /// <summary>
        /// 本插件自己发起、还没落到气泡上的流式说话对象。
        ///
        /// 用 <see cref="ConditionalWeakTable{TKey, TValue}"/>：它本来就按引用身份索引
        /// （不看对象自己的 Equals），而且是弱引用 —— 登记了却一直没等到 Show 的
        /// （动画被打断、命令被中断）不会被我们钉在内存里。
        /// </summary>
        private static readonly ConditionalWeakTable<SayInfoWithStream, object> _pendingStreams = new();

        /// <summary>
        /// 兜底：回复状态最多持续这么久。正常情况下 <see cref="BeginReply"/> 的
        /// finally 一定会解除，这个上限只防"某处漏了收尾"导致别人的气泡被永久静音。
        /// 取得很宽松，一条回复真演这么久本身就不正常了。
        /// </summary>
        private static readonly TimeSpan MaxReplyHold = TimeSpan.FromMinutes(10);

        /// <summary>放行登记的保质期。登记了却一直没等到对应的 Show（动画被打断、命令被中断），
        /// 留着会让后面一句同样文本的别家气泡被误放行。</summary>
        private static readonly TimeSpan PendingTtl = TimeSpan.FromSeconds(30);

        /// <summary>功能开关，默认开启。读不到设置时按开启处理。</summary>
        public static bool IsEnabled => VPetLLM.Instance?.Settings?.EnableBubbleExclusive ?? true;

        /// <summary>当前是否处于"LLM 回复中"（供日志与诊断查看）</summary>
        public static bool IsReplying
        {
            get { lock (_lock) return IsHoldingLocked(); }
        }

        // ============================================================================
        // 安装 / 卸载
        // ============================================================================

        /// <summary>Harmony 实例。非 null 即表示补丁在位。</summary>
        private static Harmony? _harmony;

        private const string HarmonyId = "com.vpetllm.bubbleguard";

        /// <summary>
        /// 给宿主的气泡挂上守卫补丁。重复调用是安全的（幂等）。
        ///
        /// 补丁是进程级的，不绑定某个窗口、也不绑定某个气泡实例 —— 宿主进多人模式时会
        /// <c>Main.MsgBar = new MessageBar(Main)</c> 换掉实例，换了也照样拦得住，
        /// 所以不再需要"检查守卫还在不在、掉了就重装"那套自愈逻辑。
        /// </summary>
        public static void Install()
        {
            // 功能关掉了就一个字节都别动宿主。以前这里不查开关，
            // 结果"关掉气泡独占"只是让守卫不吞气泡，宿主该被动的还是被动了 —— 等于没有退路
            if (!IsEnabled) return;

            lock (_lock)
            {
                if (_harmony is not null) return;

                try
                {
                    var harmony = new Harmony(HarmonyId);

                    Patch(harmony, nameof(MessageBar.Show), nameof(ShowTextPrefix),
                          typeof(string), typeof(string), typeof(string), typeof(UIElement));
                    Patch(harmony, nameof(MessageBar.Show), nameof(ShowStreamPrefix),
                          typeof(string), typeof(SayInfoWithStream));
                    Patch(harmony, nameof(MessageBar.ForceClose), nameof(ForceClosePrefix));

                    _harmony = harmony;
                    Logger.Log("BubbleGuard: 气泡独占守卫已安装");
                }
                catch (Exception ex)
                {
                    // 补丁挂不上就是这个功能不可用，不该连累插件加载
                    Logger.Log($"BubbleGuard: 安装失败，气泡独占不可用: {ex.Message}");
                    _harmony = null;
                }
            }
        }

        /// <summary>挂一个前置补丁。目标方法找不到就出声，别静默地少拦一条路。</summary>
        private static void Patch(Harmony harmony, string targetName, string prefixName, params Type[] args)
        {
            var target = typeof(MessageBar).GetMethod(targetName,
                BindingFlags.Public | BindingFlags.Instance, binder: null, types: args, modifiers: null);

            if (target is null)
            {
                Logger.Log($"BubbleGuard: 宿主没有 MessageBar.{targetName}({string.Join(", ", args.Select(t => t.Name))})，" +
                           $"这条路拦不住（宿主内部结构可能变了）");
                return;
            }

            var prefix = typeof(BubbleGuard).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static)!;
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        }

        /// <summary>
        /// 摘掉守卫。插件卸载/禁用时必须调用 —— 补丁里的委托指向本程序集，
        /// 不摘的话宿主会一直调用一个已经卸载的程序集里的方法。
        /// </summary>
        public static void Uninstall()
        {
            lock (_lock)
            {
                try
                {
                    if (_harmony is not null)
                    {
                        _harmony.UnpatchAll(HarmonyId);
                        Logger.Log("BubbleGuard: 气泡独占守卫已卸载");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"BubbleGuard: 卸载失败: {ex.Message}");
                }
                finally
                {
                    _harmony = null;
                    _replyDepth = 0;
                    ClearPendingLocked();
                }
            }
        }

        // ============================================================================
        // 补丁本体：返回 false = 跳过宿主原方法（也就是把这次气泡吞掉）
        // ============================================================================

        /// <summary>参数由 Harmony 按名字注入，名字必须和宿主方法的形参一致。</summary>
        private static bool ShowTextPrefix(string text) => !SwallowExternal(() => ShouldSwallow(text, "文本"));

        /// <inheritdoc cref="ShowTextPrefix"/>
        private static bool ShowStreamPrefix(SayInfoWithStream sayInfoWithStream)
            // 吞掉它只是不去订阅它的更新事件，生产方那边照常往下跑，
            // 不会因为没人消费而卡住
            => !SwallowExternal(() => ShouldSwallowStream(sayInfoWithStream));

        /// <inheritdoc cref="ShowTextPrefix"/>
        private static bool ForceClosePrefix() => !SwallowExternal(ShouldSwallowClose);

        /// <summary>
        /// 只拦<b>外部</b>打进来的调用，气泡自己内部的调用一律放行。
        ///
        /// 这条规则不是保守，是在还原换实例那版的语义：那时候拦截点在 <c>Main.MsgBar</c>，
        /// 只有"通过字段调进来"的才会经过守卫，气泡自己方法之间的互相调用根本不经过。
        /// Harmony 打在方法上，内外一起拦，得手动把内部的择出去。
        ///
        /// 具体差在哪：<c>MessageBar.ForceClose</c> 有两个内部调用方 ——
        /// 双击气泡、右键菜单里的"关闭"，<b>都是用户亲手要关掉它</b>。
        /// 这种时候还拦着不放，就成了"桌宠说起话来关都关不掉"。
        /// </summary>
        private static bool SwallowExternal(Func<bool> decide)
        {
            // 先做便宜的判断：没在回复中就什么都不用管，走栈的代价一分不付
            lock (_lock)
            {
                if (!IsEnabled || !IsHoldingLocked()) return false;
            }

            // 显式声明的用户手势优先于走栈判断，理由见 EnterUserGesture
            if (InUserGesture) return false;

            if (IsCallFromMessageBarItself()) return false;

            return decide();
        }

        /// <summary>
        /// 当前线程正处于一次"用户亲手操作气泡"的调用里。用 <c>[ThreadStatic]</c> 而不是
        /// 全局标记：手势必然发生在 UI 线程，别让它顺带放行了后台线程上别人的气泡。
        /// </summary>
        [ThreadStatic] private static int _userGestureDepth;

        private static bool InUserGesture => _userGestureDepth > 0;

        /// <summary>
        /// 声明"接下来这段调用是用户亲手操作气泡"，其间的关闭一律放行。必须与
        /// <see cref="ExitUserGesture"/> 成对（用 try/finally 或 Harmony 的 prefix + finalizer）。
        ///
        /// <b>为什么不能只靠 <see cref="IsCallFromMessageBarItself"/></b>：那个方法认的是
        /// 栈上有没有 <see cref="MessageBar"/> 的方法。可一旦某个 MessageBar 的方法**自己被
        /// Harmony 打了补丁**（我们就在 <see cref="BubbleCloseInterrupt"/> 里打了
        /// <c>MenuItemClose_Click</c>），它在栈上就变成了一个动态方法，
        /// <c>DeclaringType</c> 不再是 MessageBar —— 走栈判断当场失效，
        /// 用户的关闭被当成外部调用吞掉，于是"点了关闭，气泡纹丝不动"。
        ///
        /// 打补丁的一方自己声明手势，是确定的；靠走栈去猜是脆的。
        /// </summary>
        public static void EnterUserGesture() => _userGestureDepth++;

        /// <inheritdoc cref="EnterUserGesture"/>
        public static void ExitUserGesture()
        {
            if (_userGestureDepth > 0) _userGestureDepth--;
        }

        /// <summary>调用是不是从 <see cref="MessageBar"/> 自己的方法里发出来的。</summary>
        private static bool IsCallFromMessageBarItself()
        {
            try
            {
                foreach (var frame in new StackTrace(fNeedFileInfo: false).GetFrames())
                {
                    var method = frame?.GetMethod();
                    if (method?.DeclaringType != typeof(MessageBar)) continue;

                    // 被打补丁的那一层自己不算 —— 它就在栈上，是我们进来的必经之路
                    if (method.Name is nameof(MessageBar.Show) or nameof(MessageBar.ForceClose)) continue;

                    return true;
                }
            }
            catch (Exception ex)
            {
                // 走栈失败就按"外部调用"处理：宁可多拦一次，也不放过一次顶掉回复的机会
                Logger.Log($"BubbleGuard: 判断调用来源失败，按外部调用处理: {ex.Message}");
            }

            return false;
        }

        // ============================================================================
        // 取真气泡：本插件自己要操作气泡时用
        // ============================================================================

        /// <summary>
        /// 取宿主真正的气泡实例。
        ///
        /// 守卫改成打补丁之后，<c>Main.MsgBar</c> 从头到尾都是宿主原装的 <see cref="MessageBar"/>，
        /// 这里已经没有"壳"要剥了。保留这两个方法是因为调用点有十几处，
        /// 而且它们表达的是"我要的是能反射私有字段的那个实例"这层意图，
        /// 比裸读 <c>Main.MsgBar</c> 清楚。
        /// </summary>
        public static object? Real(object? msgBar) => msgBar;

        /// <inheritdoc cref="Real(object?)"/>
        public static IMassageBar? Real(IMassageBar? msgBar) => msgBar;

        /// <summary>
        /// 宿主当前的气泡。本插件内部一律用这个，不要直接读 <c>Main.MsgBar</c>。
        /// </summary>
        public static IMassageBar? RealMsgBar => VPetLLM.Instance?.MW?.Main?.MsgBar;

        // ============================================================================
        // 回复状态
        // ============================================================================

        /// <summary>
        /// 进入"LLM 回复中"。用 using 包住整段回复演出，离开作用域自动退出。
        /// 可嵌套（流式回复会一条命令一个作用域），最外层退出才真正解除。
        /// </summary>
        public static IDisposable BeginReply()
        {
            lock (_lock)
            {
                if (_replyDepth == 0)
                {
                    _replyStartedAt = DateTime.Now;
                    ClearPendingLocked();
                }
                _replyDepth++;
            }

            // 也在这里装一次：用户可能是在插件加载之后才把开关打开的。
            // 补丁是进程级的，装过就直接返回，重复调用不花钱
            Install();

            return new ReplyScope();
        }

        private sealed class ReplyScope : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                lock (_lock)
                {
                    if (_replyDepth > 0) _replyDepth--;
                    if (_replyDepth == 0) ClearPendingLocked();
                }
            }
        }

        /// <summary>调用方需持有 <see cref="_lock"/>。</summary>
        private static bool IsHoldingLocked()
        {
            if (_replyDepth <= 0) return false;

            if (DateTime.Now - _replyStartedAt > MaxReplyHold)
            {
                Logger.Log($"BubbleGuard: 回复状态已持续超过 {MaxReplyHold.TotalMinutes} 分钟，" +
                           $"判定为异常残留并放开气泡（其他插件恢复正常显示）");
                _replyDepth = 0;
                ClearPendingLocked();
                return false;
            }

            return true;
        }

        // ============================================================================
        // 放行登记
        // ============================================================================

        /// <summary>
        /// 登记一条"接下来这段文本是本插件要显示的"，放它过守卫。
        /// 每次登记只放行一次，显示完即失效。
        /// </summary>
        public static void AllowOnce(string? text)
        {
            // 空文本也要登记。宿主的 Show("") 一样会把气泡清空并置为可见，
            // 也就是一样能顶掉正在念的回复；不登记的话我们自己那次空 Say 反而会被自己吞掉
            var key = text ?? string.Empty;

            lock (_lock)
            {
                PruneExpiredLocked();

                if (_pending.TryGetValue(key, out var entry))
                {
                    entry.Count++;
                    entry.LastRegisteredAt = DateTime.Now;
                }
                else
                {
                    _pending[key] = new PendingBubble { Count = 1, LastRegisteredAt = DateTime.Now };
                }
            }
        }

        /// <summary>
        /// 登记一个流式说话对象，放它过守卫。
        ///
        /// 流式不能只按文本认领：<see cref="SayInfoWithStream"/> 发起时正文往往还是空的，
        /// 内容是之后一段段推进来的，登记时拿不到能匹配的文本。按对象身份认最准。
        /// </summary>
        public static void AllowOnce(SayInfoWithStream? sayInfo)
        {
            if (sayInfo is null) return;

            lock (_lock)
            {
                _pendingStreams.AddOrUpdate(sayInfo, StreamMarker);
            }
        }

        /// <summary>放进弱引用表的占位值，本身不携带信息</summary>
        private static readonly object StreamMarker = new();

        /// <summary>清空所有放行登记。调用方需持有 <see cref="_lock"/>。</summary>
        private static void ClearPendingLocked()
        {
            _pending.Clear();
            _pendingStreams.Clear();
        }

        /// <summary>调用方需持有 <see cref="_lock"/>。</summary>
        private static void PruneExpiredLocked()
        {
            if (_pending.Count == 0) return;

            var deadline = DateTime.Now - PendingTtl;
            var stale = _pending.Where(kv => kv.Value.LastRegisteredAt < deadline)
                                .Select(kv => kv.Key)
                                .ToList();

            foreach (var key in stale)
            {
                _pending.Remove(key);
            }
        }

        // ============================================================================
        // 守卫判定（供补丁调用）
        // ============================================================================

        /// <summary>
        /// 这次气泡显示要不要吞掉。
        /// </summary>
        /// <param name="text">要显示的文本，用于认领本插件自己的气泡</param>
        /// <param name="source">日志里标记是哪个入口，便于排查</param>
        internal static bool ShouldSwallow(string? text, string source)
        {
            // 查找键要和 AllowOnce 的登记键一致：空文本也是能登记、也必须能认领的，
            // 否则我们自己那次空 Say 会被自己吞掉
            var key = text ?? string.Empty;

            lock (_lock)
            {
                if (!IsEnabled) return false;
                if (!IsHoldingLocked()) return false;

                // 是我们自己登记过的：放行并消费掉这一次
                if (_pending.TryGetValue(key, out var entry))
                {
                    if (--entry.Count <= 0)
                    {
                        _pending.Remove(key);
                    }
                    return false;
                }
            }

            var preview = text is null ? "(无文本)"
                : text.Length > 20 ? text.Substring(0, 20) + "..."
                : text;
            Logger.Log($"BubbleGuard: LLM 回复中，吞掉一次来自其它来源的气泡[{source}]: {preview}");
            return true;
        }

        /// <summary>
        /// 这次流式气泡要不要吞掉。按对象身份认领，认不出再退回按当前文本认。
        /// </summary>
        internal static bool ShouldSwallowStream(SayInfoWithStream? sayInfo)
        {
            lock (_lock)
            {
                if (!IsEnabled) return false;
                if (!IsHoldingLocked()) return false;

                if (sayInfo is not null && _pendingStreams.TryGetValue(sayInfo, out _))
                {
                    _pendingStreams.Remove(sayInfo);
                    return false;
                }
            }

            // 身份对不上再按文本认一次：流式对象可能是别处构造、经我们登记过文本才发出去的
            return ShouldSwallow(sayInfo?.CurrentText?.ToString(), "流式");
        }

        /// <summary>
        /// 关闭气泡的请求要不要吞掉。回复期间别人调 ForceClose 会把正在念的那段直接掐断，
        /// 和覆盖是一样的效果，所以同样黑洞掉。
        /// </summary>
        internal static bool ShouldSwallowClose()
        {
            lock (_lock)
            {
                if (!IsEnabled) return false;
                if (!IsHoldingLocked()) return false;
            }

            Logger.Log("BubbleGuard: LLM 回复中，吞掉一次来自其它来源的气泡强制关闭");
            return true;
        }
    }

    /// <summary>
    /// 本插件调用宿主说话的统一入口。
    ///
    /// 存在的唯一理由是"登记"和"说"必须成对：只要有一处漏了登记，那句话就会被自己的
    /// 守卫吞掉，表现为桌宠有语音没文字。所以本插件内部一律走这里，
    /// 不直接调 <c>Main.Say</c> —— 少一个能忘的地方。
    /// </summary>
    public static class GuardedSay
    {
        /// <inheritdoc cref="VPet_Simulator.Core.Main.Say(string, string, bool, string)"/>
        public static void SayGuarded(this Main main, string text, string? graphName = null,
                                      bool force = false, string? desc = null)
        {
            BubbleGuard.AllowOnce(text);
            main.Say(text, graphName, force, desc);
        }

        /// <inheritdoc cref="VPet_Simulator.Core.Main.Say(SayInfoWithOutStream)"/>
        public static void SayGuarded(this Main main, SayInfoWithOutStream sayInfo)
        {
            BubbleGuard.AllowOnce(sayInfo?.Text);
            main.Say(sayInfo!);
        }

        /// <summary>
        /// 流式说话。登记的是"发起时已有的文本"，与
        /// <see cref="BubbleGuard.ShouldSwallowStream"/> 的认领口径一致。
        /// </summary>
        public static void SayGuarded(this Main main, SayInfoWithStream sayInfo)
        {
            BubbleGuard.AllowOnce(sayInfo);
            main.Say(sayInfo!);
        }
    }

}
