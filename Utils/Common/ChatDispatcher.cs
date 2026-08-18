using System.Threading;

namespace VPetLLM.Utils.Common
{
    /// <summary>
    /// 灌入优先级。合并成一条 prompt 时先按优先级、再按到达顺序拼接 ——
    /// 用户说的话排在触摸事件和插件回执前面，模型先看到"人说了什么"。
    /// </summary>
    public enum ChatPriority
    {
        /// <summary>用户主动说的话：输入框、语音识别、远程聊天</summary>
        User = 0,

        /// <summary>桌宠交互事件：触摸反馈、购买回执</summary>
        Interaction = 1,

        /// <summary>插件回执与插件主动发起的调用</summary>
        Plugin = 2,

        /// <summary>定时器、状态变化等系统事件</summary>
        System = 3
    }

    /// <summary>
    /// 对话灌入调度器 —— 所有"让 AI 开口"的入口统一从这里过闸。
    ///
    /// 它解决两件事：
    ///
    /// 并发：触摸反馈、插件回执、用户输入、语音识别过去各自直接调 ChatCore.Chat。
    ///       几路同时到达时就是几个并发的 LLM 请求：都读同一份历史、各自往里追加，
    ///       历史错乱，气泡和语音也互相抢。这里用一条单线工作队列串起来，
    ///       任何时刻只有一次请求在飞。
    ///
    /// 抖动：一次触摸往往连带状态变化、插件回执一起到，彼此只差几十毫秒。
    ///       逐条发就是逐条唤起 LLM。这里等一个静默窗口（默认 200ms），
    ///       窗口内到达的全部并成一条 prompt 一次发出 —— 即 AstrBot 的"等灌完再一起处理"。
    ///
    /// 与 <see cref="ResultAggregator"/> 的分工：那个聚合的是"一轮之内插件产生的回执"，
    /// 按会话收口；这个聚合的是"跨来源、跨轮次的所有灌入"，是最后一道闸门。
    ///
    /// 为什么不会把自己锁死：派发线程只 await <c>ChatCore.Chat</c> 本身。回复的输出处理
    /// （TalkBox.HandleResponse 是 async void，内部再 Task.Run）根本不在这条 await 链上，
    /// 所以由输出处理触发的回灌（ResultAggregator、插件调用）尽管排在队列里等，
    /// 也不会拦住正在跑的那一轮。另有 <see cref="QueueWaitTimeout"/> 兜底。
    /// </summary>
    public static class ChatDispatcher
    {
        private sealed class PendingChat
        {
            public string Text = "";
            public ChatPriority Priority;
            public IReadOnlyList<byte[]>? Images;
            public string Source = "";
            public long Seq;

            /// <summary>独占：不与别人合并，单独成批。需要拿回"自己这条"的回复时用。</summary>
            public bool Exclusive;

            public bool IsRetry;

            /// <summary>这条灌入开启新一轮对话（用户提问），派发前要换发中断令牌、重置流式状态。</summary>
            public bool NewRound;

            public readonly TaskCompletionSource<string> Completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static readonly object _lock = new();
        private static readonly List<PendingChat> _pending = new();
        private static readonly SemaphoreSlim _arrival = new(0);
        private static Task? _worker;
        private static long _seq;

        private const int DefaultWindowMs = 200;
        private const int DefaultMaxWaitMs = 1000;

        /// <summary>
        /// 排队等待的上限。超过还没轮到就绕开队列直接发 —— 万一某一轮卡死在网络超时里，
        /// 后面的灌入不该跟着一起陪葬。绕行只发生在"还没被取走"的项上，不会重复发送。
        /// </summary>
        private static readonly TimeSpan QueueWaitTimeout = TimeSpan.FromMinutes(3);

        /// <summary>
        /// 替代真正 ChatCore 调用的测试替身，见 <see cref="SendAsync"/>。生产运行时恒为 null。
        /// </summary>
        // CS0649：本程序集内确实没有任何地方给它赋值 —— 唯一的写入方是
        // Tests/VPetLLM.ChatDispatcherChecks，它按字段名反射注入。编译器看不到那次赋值，
        // 警告本身没说错，所以就地关掉并说明，而不是为了消警告给它加个生产代码用不到的 setter。
#pragma warning disable CS0649
        private static Func<string, IReadOnlyList<byte[]>?, bool, Task<string>>? _sendStub;
#pragma warning restore CS0649

        private static Setting? Settings => VPetLLM.Instance?.Settings;

        /// <summary>关掉即完全旁路（回到各入口直接调 ChatCore 的旧行为），用于排障。</summary>
        public static bool IsEnabled => Settings?.EnableChatCoalescing ?? true;

        /// <summary>静默窗口：这么久没有新灌入就收批发出。0 表示不等待，只做串行化。</summary>
        private static int WindowMs => Math.Max(0, Settings?.ChatCoalesceWindowMs ?? DefaultWindowMs);

        /// <summary>持续灌入时的收批上限，防止窗口被无限续期导致永远发不出去。</summary>
        private static int MaxWaitMs => Math.Max(WindowMs, Settings?.ChatCoalesceMaxWaitMs ?? DefaultMaxWaitMs);

        /// <summary>队列中尚未派发的灌入条数（含正在等窗口的）。</summary>
        public static int PendingCount
        {
            get { lock (_lock) return _pending.Count; }
        }

        /// <summary>
        /// 提交一条灌入，返回本批次的模型回复。
        ///
        /// 同一批合并发出时，批内所有调用方拿到的是同一份回复文本 —— 因为它们本来就
        /// 被并成了一次请求。需要"只属于自己那条"的回复时传 <paramref name="exclusive"/>。
        /// </summary>
        /// <param name="text">要送给模型的内容</param>
        /// <param name="priority">合并时的排序优先级</param>
        /// <param name="images">随本条一起送出的图片（原生多模态），可为空</param>
        /// <param name="source">来源标识，仅用于日志</param>
        /// <param name="exclusive">true 表示单独成批，不与其他灌入合并</param>
        /// <param name="isRetry">透传给 ChatCore.Chat 的重试守卫，见 IChatCore 注释</param>
        /// <param name="newRound">
        /// 这条灌入是否开启新一轮对话。true 时会在真正派发前调用 <see cref="BeginRound"/>。
        /// 插件回执、工具回灌传 false —— 它们是上一轮的延续，共用同一个中断令牌。
        /// </param>
        public static async Task<string> SubmitAsync(
            string text,
            ChatPriority priority,
            IReadOnlyList<byte[]>? images = null,
            string? source = null,
            bool exclusive = false,
            bool isRetry = false,
            bool newRound = false)
        {
            if (string.IsNullOrWhiteSpace(text) && (images is null || images.Count == 0))
                return "";

            source ??= priority.ToString();

            if (!IsEnabled)
            {
                Logger.LogVerbose($"ChatDispatcher: 合并已关闭，{source} 直接发送");
                if (newRound) BeginRound();
                return await Task.Run(() => SendAsync(text, images, isRetry)).ConfigureAwait(false);
            }

            var item = new PendingChat
            {
                Text = text ?? "",
                Priority = priority,
                Images = images,
                Source = source,
                Exclusive = exclusive,
                IsRetry = isRetry,
                NewRound = newRound
            };

            lock (_lock)
            {
                item.Seq = ++_seq;
                _pending.Add(item);
                EnsureWorkerLocked();
            }

            // Release 放在锁外：SemaphoreSlim 释放时可能同步续跑等待方的延续，
            // 在锁内做就有把工作线程拖进本锁的风险
            _arrival.Release();

            Logger.LogVerbose($"ChatDispatcher: {source} 入队（优先级 {priority}，队列 {PendingCount} 条）");

            // 定时器随派发完成一起取消，否则每条灌入都会留一个 3 分钟才到期的计时器
            using var timeoutCts = new CancellationTokenSource();
            var finished = await Task.WhenAny(
                item.Completion.Task,
                Task.Delay(QueueWaitTimeout, timeoutCts.Token)).ConfigureAwait(false);
            timeoutCts.Cancel();

            if (finished != item.Completion.Task)
            {
                // 还在队列里说明前面那轮卡住了；已被取走的话就老实等着，那是正常的 LLM 耗时
                bool stillQueued;
                lock (_lock)
                {
                    stillQueued = _pending.Remove(item);
                }

                if (stillQueued)
                {
                    Logger.Log($"ChatDispatcher: {source} 排队超过 {QueueWaitTimeout.TotalSeconds:F0}s，绕开队列直接发送");
                    if (newRound) BeginRound();
                    return await Task.Run(() => SendAsync(text, images, isRetry)).ConfigureAwait(false);
                }
            }

            return await item.Completion.Task.ConfigureAwait(false);
        }

        /// <summary>调用方必须持有 <see cref="_lock"/>。</summary>
        private static void EnsureWorkerLocked()
        {
            if (_worker is not null)
                return;

            _worker = Task.Run(RunAsync);
        }

        private static async Task RunAsync()
        {
            while (true)
            {
                try
                {
                    await _arrival.WaitAsync().ConfigureAwait(false);

                    // 抖动窗口：等这一串灌完再收批
                    await WaitForQuietWindowAsync().ConfigureAwait(false);

                    List<PendingChat> batch;
                    lock (_lock)
                    {
                        batch = TakeBatchLocked();

                        // 本批 N 条，进循环时已经消费掉 1 个计数，这里补吃剩下的 N-1 个。
                        // 只能吃这么多：独占项会把它自己和它后面的都留在队列里，
                        // 多吃一个就等于吞掉一次唤醒，那些项要等到下一条灌入才轮得到
                        for (int i = 1; i < batch.Count; i++)
                        {
                            if (!_arrival.Wait(0))
                                break;
                        }
                    }

                    if (batch.Count == 0)
                        continue;

                    await DispatchBatchAsync(batch).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 工作循环一旦退出，后面所有灌入都会挂在队列上等到超时才绕行。
                    // 任何异常都必须就地咽下，循环不能停
                    Logger.Log($"ChatDispatcher: 派发循环异常: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 等一个"没有新灌入"的静默窗口。窗口内每来一条就重新计时，
        /// 但整体不超过 <see cref="MaxWaitMs"/> —— 否则持续的灌入会把这批永远推迟下去。
        /// </summary>
        private static async Task WaitForQuietWindowAsync()
        {
            var window = WindowMs;
            if (window <= 0)
                return;

            var deadline = DateTime.UtcNow.AddMilliseconds(MaxWaitMs);
            int lastCount = PendingCount;

            while (true)
            {
                // 刻意不用 InterruptManager.Delay：这段等待属于调度本身，
                // 被中断令牌提前唤醒会让合并窗口形同虚设
                await Task.Delay(window).ConfigureAwait(false);

                int current = PendingCount;
                if (current == lastCount)
                    return;

                if (DateTime.UtcNow >= deadline)
                {
                    Logger.LogVerbose($"ChatDispatcher: 灌入持续超过 {MaxWaitMs}ms，先发一批（{current} 条）");
                    return;
                }

                lastCount = current;
            }
        }

        /// <summary>
        /// 按到达顺序取走一批。遇到独占项则以它为界：它要么自己单独成批，
        /// 要么留到下一批，绝不与别人合并。调用方必须持有 <see cref="_lock"/>。
        /// </summary>
        private static List<PendingChat> TakeBatchLocked()
        {
            if (_pending.Count == 0)
                return new List<PendingChat>();

            _pending.Sort((a, b) => a.Seq.CompareTo(b.Seq));

            List<PendingChat> batch;
            if (_pending[0].Exclusive)
            {
                batch = new List<PendingChat> { _pending[0] };
            }
            else
            {
                batch = _pending.TakeWhile(p => !p.Exclusive).ToList();
            }

            _pending.RemoveRange(0, batch.Count);
            return batch;
        }

        /// <summary>
        /// 把一批灌入并成一次请求发出，再把同一份回复交给批内所有等待方。
        /// </summary>
        private static async Task DispatchBatchAsync(List<PendingChat> batch)
        {
            var ordered = batch
                .OrderBy(p => (int)p.Priority)
                .ThenBy(p => p.Seq)
                .ToList();

            var text = string.Join("\n", ordered
                .Select(p => p.Text?.Trim() ?? "")
                .Where(t => t.Length > 0));

            var images = ordered
                .Where(p => p.Images is not null)
                .SelectMany(p => p.Images!)
                .Where(i => i is { Length: > 0 })
                .ToList();

            // 整批都是回执/重试时才透传重试守卫；只要掺了一条真正的用户灌入，
            // 就该按用户请求对待（失败可以自动重试一次）
            var isRetry = ordered.All(p => p.IsRetry);

            if (ordered.Count > 1)
            {
                Logger.Log($"ChatDispatcher: 合并 {ordered.Count} 条灌入为一次请求 " +
                           $"[{string.Join(" + ", ordered.Select(p => p.Source))}]");
            }

            // 开新一轮必须卡在这个位置，不能放在各入口提交的时候：那时上一轮多半还在飞，
            // 提前换令牌会把在途请求的中断链路掐断、把流式状态机重置到 Idle，
            // 于是上一轮剩下的分片被当成新回复的开头处理 —— 这正是消息串轮的来源
            if (ordered.Any(p => p.NewRound))
                BeginRound();

            string result = "";
            Exception? error = null;

            try
            {
                result = await SendAsync(text, images, isRetry).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex;
                Logger.Log($"ChatDispatcher: 派发失败: {ex.Message}");
            }

            foreach (var item in ordered)
            {
                if (error is null)
                    item.Completion.TrySetResult(result);
                else
                    item.Completion.TrySetException(error);
            }
        }

        /// <summary>
        /// 开启新一轮对话：换发中断令牌、清掉上一轮残留的流式状态。
        ///
        /// 由调度器在真正派发前调用，各入口不要自己调 —— 唯一的例外是不经过调度器的
        /// 旁路（TalkBox 的 Debug 模式直接把用户输入当成模型输出处理）。
        /// </summary>
        public static void BeginRound()
        {
            InterruptManager.BeginSession();

            try
            {
                VPetLLM.Instance?.TalkBox?.ResetStreamingState();
            }
            catch (Exception ex)
            {
                Logger.Log($"ChatDispatcher: 重置流式状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 真正发出去的那一步。带图走多模态入口，否则走普通文本入口。
        /// </summary>
        private static async Task<string> SendAsync(string text, IReadOnlyList<byte[]>? images, bool isRetry)
        {
            // 测试替身：只有 Tests/VPetLLM.ChatDispatcherChecks 通过反射设置它，
            // 让批次合并/串行化这些纯调度行为能脱离宿主与网络单独验证。生产路径永远为 null
            var stub = _sendStub;
            if (stub is not null)
                return await stub(text, images, isRetry).ConfigureAwait(false);

            var core = VPetLLM.Instance?.ChatCore;
            if (core is null)
            {
                Logger.Log("ChatDispatcher: ChatCore 未初始化，本次灌入丢弃");
                return "";
            }

            if (images is { Count: > 0 })
                return await core.ChatWithImages(text, images).ConfigureAwait(false) ?? "";

            return await core.Chat(text, isRetry).ConfigureAwait(false) ?? "";
        }
    }
}
