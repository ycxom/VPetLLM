using System.Threading;
// 本命名空间下裸写 System 会撞上 VPetLLM.Utils.System，计时器类型必须走别名
using SystemTimers = System.Timers;

namespace VPetLLM.Utils.Common
{
    /// <summary>
    /// 全局中断管理器 —— 一轮对话（用户提问 → LLM 请求 → 气泡/动作/TTS 输出）共用一个取消令牌，
    /// 用户点击侧边栏状态按钮时取消这一轮的全部后续工作。
    ///
    /// 令牌的生命周期由用户入口（TalkBox 的三个发送方法）划分：每次新提问 <see cref="BeginSession"/>
    /// 换一个新的 CTS，插件回灌（ChatCore.Chat(..., true)）等后续调用沿用同一个令牌，
    /// 这样中断一次就能把整条链路（含回灌触发的二次请求）一起停掉。
    /// </summary>
    public static class InterruptManager
    {
        private static readonly object _lock = new();
        private static CancellationTokenSource? _cts;
        private static bool _interrupted;

        /// <summary>会话代数，用于识别"自动复位定时器排队期间又开了新会话"的情况</summary>
        private static long _generation;
        private static SystemTimers.Timer? _autoResetTimer;

        /// <summary>
        /// 中断后自动复位的等待时间。
        ///
        /// 中断标记的作用范围只是"把被中断那一轮的尾巴掐掉"（在途的流式片段、排队的输出段、
        /// 插件回灌），这些最多再走几百毫秒。过了这段时间还不复位的话，那些不经过
        /// TalkBox（因而不会调 <see cref="BeginSession"/>）的调用方 —— 定时器插件、
        /// 截图 OCR 自动发送等 —— 会被永久拦在中断标记后面。
        /// </summary>
        private static readonly TimeSpan AutoResetDelay = TimeSpan.FromSeconds(3);

        /// <summary>
        /// 中断发生时触发。在调用 <see cref="Interrupt"/> 的线程上同步执行，
        /// 订阅方必须自行兜住异常。
        /// </summary>
        public static event Action? Interrupted;

        /// <summary>
        /// 当前这一轮是否已被用户中断。流式循环、分段输出等长流程据此提前退出。
        /// </summary>
        public static bool IsInterrupted
        {
            get { lock (_lock) return _interrupted; }
        }

        /// <summary>
        /// 当前会话的取消令牌，没有活动会话时为 <see cref="CancellationToken.None"/>。
        /// 传给 HttpClient 可以让在途请求立即断开，而不是等它跑完再丢结果。
        /// </summary>
        public static CancellationToken Token
        {
            get
            {
                lock (_lock)
                {
                    // CTS 已释放时 Token 访问会抛，退化为 None 而不是把调用方带崩
                    try { return _cts?.Token ?? CancellationToken.None; }
                    catch (ObjectDisposedException) { return CancellationToken.None; }
                }
            }
        }

        /// <summary>
        /// 开始新一轮用户会话：清除上一次的中断标记并换发新令牌。
        /// </summary>
        public static void BeginSession()
        {
            CancellationTokenSource? old;
            lock (_lock)
            {
                old = StartNewSessionLocked();
            }

            try { old?.Dispose(); } catch { }
            Logger.Log("InterruptManager: 新会话已开始，中断标记已清除");
        }

        /// <summary>
        /// 换发令牌并清除中断标记，返回需要释放的旧 CTS。调用方必须持有 <see cref="_lock"/>。
        /// </summary>
        private static CancellationTokenSource? StartNewSessionLocked()
        {
            var old = _cts;
            _cts = new CancellationTokenSource();
            _interrupted = false;
            _generation++;

            // 新会话作废掉上一次中断排下的自动复位
            if (_autoResetTimer is not null)
            {
                try { _autoResetTimer.Stop(); _autoResetTimer.Dispose(); } catch { }
                _autoResetTimer = null;
            }

            return old;
        }

        /// <summary>
        /// 当前有没有一轮可以被中断的会话。
        ///
        /// 给"顺手触发中断"的入口用（比如关掉气泡）：这些动作在没有会话时也会频繁发生，
        /// 直接调 <see cref="Interrupt"/> 虽然安全，但每次都会往日志里写一行
        /// "当前没有可中断的会话"，纯属噪声。
        /// </summary>
        public static bool HasActiveSession
        {
            get { lock (_lock) return _cts is not null && !_interrupted; }
        }

        /// <summary>
        /// 请求中断当前会话。
        /// </summary>
        /// <returns>true 表示本次调用真正触发了中断；false 表示当前没有可中断的会话（或已中断过）。</returns>
        public static bool Interrupt()
        {
            CancellationTokenSource? cts;
            lock (_lock)
            {
                if (_cts is null || _interrupted)
                    return false;

                _interrupted = true;
                cts = _cts;
                ScheduleAutoResetLocked(_generation);
            }

            try { cts.Cancel(); }
            catch (Exception ex) { Logger.Log($"InterruptManager: 取消令牌失败: {ex.Message}"); }

            Logger.Log("InterruptManager: 用户中断了当前会话");

            try { Interrupted?.Invoke(); }
            catch (Exception ex) { Logger.Log($"InterruptManager: 中断回调异常: {ex.Message}"); }

            return true;
        }

        /// <summary>
        /// 可被中断打断的等待。
        ///
        /// 输出管线里到处是"等气泡显示完""等音频播完""等会话计数归零"这类等待，
        /// 短的几十毫秒，长的几十秒。用普通 Task.Delay 的话，中断之后这些任务还会各自
        /// 睡到自然醒才退出 —— 表现为点了中断，线程池里还挂着一堆任务慢慢回收。
        /// 这里挂上会话令牌，中断瞬间全部立即返回。
        /// </summary>
        public static async Task Delay(int milliseconds)
        {
            if (milliseconds <= 0)
                return;

            try
            {
                await Task.Delay(milliseconds, Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 中断即预期结果，调用方靠 IsInterrupted 决定后续走向
            }
        }

        /// <summary>
        /// 返回一个"中断时完成"的任务，当前已中断则立即完成。
        ///
        /// 用来把「等某件事做完」改写成「等它做完，或者等到中断」：
        /// 中断会让一部分工作根本不再发生，那些以为自己在等它的 await 就永远等不到了。
        /// </summary>
        public static Task WhenInterruptedAsync()
        {
            if (IsInterrupted)
                return Task.CompletedTask;

            var token = Token;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!token.CanBeCanceled)
                return tcs.Task; // 没有活动会话：永不完成，等价于只等原任务

            var registration = token.Register(state => ((TaskCompletionSource<bool>)state).TrySetResult(true), tcs);

            // 完成后立刻注销，别把回调挂在令牌上直到会话结束
            _ = tcs.Task.ContinueWith(
                (_, state) => ((CancellationTokenRegistration)state).Dispose(),
                registration,
                TaskScheduler.Default);

            return tcs.Task;
        }

        /// <summary>
        /// 排定中断后的自动复位。调用方必须持有 <see cref="_lock"/>。
        /// </summary>
        /// <param name="interruptedGeneration">中断发生时的会话代数</param>
        private static void ScheduleAutoResetLocked(long interruptedGeneration)
        {
            if (_autoResetTimer is not null)
            {
                try { _autoResetTimer.Stop(); _autoResetTimer.Dispose(); } catch { }
            }

            var timer = new SystemTimers.Timer(AutoResetDelay.TotalMilliseconds)
            {
                AutoReset = false
            };
            timer.Elapsed += (_, _) =>
            {
                CancellationTokenSource? old = null;
                lock (_lock)
                {
                    // 期间用户已经发了新消息（代数变了）就什么都不做，
                    // 否则会把正在跑的那一轮的令牌换掉
                    if (_generation != interruptedGeneration || !_interrupted)
                        return;

                    old = StartNewSessionLocked();
                }

                try { old?.Dispose(); } catch { }
                Logger.Log("InterruptManager: 中断标记已自动复位，可以开始新对话");
            };
            _autoResetTimer = timer;
            timer.Start();
        }

    }
}
