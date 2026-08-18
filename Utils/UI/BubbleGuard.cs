using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;

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
    /// 实现方式是把 <c>Main.MsgBar</c>（公开可写字段，宿主自己在多人模式里也这么换）
    /// 换成一层 <see cref="GuardedMessageBar"/> 装饰器，而不是 IL 补丁。
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

        /// <summary>
        /// 把守卫装到宿主的气泡上。重复调用是安全的。
        /// </summary>
        public static void Install(IMainWindow? mainWindow)
        {
            var main = mainWindow?.Main;
            if (main is null) return;

            try
            {
                lock (_lock)
                {
                    // 已经装过了 / 宿主还没建出气泡来
                    if (main.MsgBar is GuardedMessageBar || main.MsgBar is null) return;

                    main.MsgBar = new GuardedMessageBar(main.MsgBar);
                }

                Logger.Log("BubbleGuard: 气泡独占守卫已安装");
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleGuard: 安装失败，气泡独占不可用: {ex.Message}");
            }
        }

        /// <summary>
        /// 摘掉守卫，把真正的气泡还给宿主。插件卸载/禁用时必须调用，
        /// 否则宿主会一直拿着一个指向已卸载程序集的装饰器。
        /// </summary>
        public static void Uninstall(IMainWindow? mainWindow)
        {
            var main = mainWindow?.Main;

            try
            {
                lock (_lock)
                {
                    if (main?.MsgBar is GuardedMessageBar guard)
                    {
                        main.MsgBar = guard.Inner;
                        Logger.Log("BubbleGuard: 气泡独占守卫已卸载");
                    }

                    _replyDepth = 0;
                    ClearPendingLocked();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleGuard: 卸载失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 确认守卫还在位上，被换掉了就重新装。
        ///
        /// 宿主进多人模式时会执行 <c>Main.MsgBar = new MessageBar(Main)</c>，
        /// 直接把我们的装饰器顶掉；不自愈的话此后气泡独占就静默失效了。
        /// </summary>
        private static void EnsureInstalled()
        {
            var main = VPetLLM.Instance?.MW?.Main;
            if (main is null) return;

            if (main.MsgBar is GuardedMessageBar) return;

            Logger.Log("BubbleGuard: 检测到宿主更换了气泡实例，重新安装守卫");
            Install(VPetLLM.Instance?.MW);
        }

        // ============================================================================
        // 解包：本插件自己要操作真气泡时用
        // ============================================================================

        /// <summary>
        /// 取回被包在装饰器里的真气泡。
        ///
        /// 本插件有大量代码是反射进 <c>MessageBar</c> 的私有字段（定时器、打字机缓冲区）
        /// 来精修气泡的，那些代码必须拿到真实例：拿到装饰器的话反射一个字段都找不到，
        /// <see cref="MessageBarHelper"/> 还会把"不支持"的结论缓存进静态字段，
        /// 之后整个进程都降级运行。
        /// </summary>
        public static object? Real(object? msgBar)
            => msgBar is GuardedMessageBar guard ? guard.Inner : msgBar;

        /// <inheritdoc cref="Real(object?)"/>
        public static IMassageBar? Real(IMassageBar? msgBar)
            => msgBar is GuardedMessageBar guard ? guard.Inner : msgBar;

        /// <summary>
        /// 宿主当前的真气泡（已解包）。本插件内部一律用这个，不要直接读 <c>Main.MsgBar</c>。
        /// </summary>
        public static IMassageBar? RealMsgBar => Real(VPetLLM.Instance?.MW?.Main?.MsgBar);

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

            // 装在这里而不是只在插件加载时装一次：宿主可能中途换过气泡实例
            if (IsEnabled)
            {
                EnsureInstalled();
            }

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
        // 守卫判定（供装饰器调用）
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
        /// <see cref="GuardedMessageBar.Show(string, SayInfoWithStream)"/> 的认领口径一致。
        /// </summary>
        public static void SayGuarded(this Main main, SayInfoWithStream sayInfo)
        {
            BubbleGuard.AllowOnce(sayInfo);
            main.Say(sayInfo!);
        }
    }

    /// <summary>
    /// 装在宿主 <c>Main.MsgBar</c> 上的一层壳：平时原样转发，
    /// LLM 回复期间把别人的显示/关闭请求丢进黑洞（正常返回，不抛异常）。
    /// </summary>
    internal sealed class GuardedMessageBar : IMassageBar
    {
        /// <summary>被包住的真气泡</summary>
        public IMassageBar Inner { get; }

        public GuardedMessageBar(IMassageBar inner)
        {
            Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public void Show(string name, string text, string? graphName = null, UIElement? msgContent = null)
        {
            if (BubbleGuard.ShouldSwallow(text, "文本")) return;
            Inner.Show(name, text, graphName, msgContent);
        }

        public void Show(string name, SayInfoWithStream sayInfoWithStream)
        {
            // 吞掉它只是不去订阅它的更新事件，生产方那边照常往下跑，
            // 不会因为没人消费而卡住
            if (BubbleGuard.ShouldSwallowStream(sayInfoWithStream)) return;
            Inner.Show(name, sayInfoWithStream!);
        }

        public void ForceClose()
        {
            if (BubbleGuard.ShouldSwallowClose()) return;
            Inner.ForceClose();
        }

        // 以下都原样转发：位置、可见性、控件本身、关闭事件都属于宿主自己的生命周期，
        // 拦下来的收益远小于把宿主状态机搞乱的风险
        public void SetPlaceIN() => Inner.SetPlaceIN();

        public void SetPlaceOUT() => Inner.SetPlaceOUT();

        public Visibility Visibility
        {
            get => Inner.Visibility;
            set => Inner.Visibility = value;
        }

        public Control This => Inner.This;

        public event Action EndAction
        {
            add => Inner.EndAction += value;
            remove => Inner.EndAction -= value;
        }

        public void Dispose() => Inner.Dispose();
    }
}
