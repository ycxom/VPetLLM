using SystemTimers = System.Timers;
using VPetLLMUtils = VPetLLM.Utils.System;

namespace VPetLLM.Utils.Common
{
    /// <summary>
    /// 将短时间内(默认2秒)来自插件/工具的回执聚合为一条消息再回灌给AI，避免连续多次唤起LLM
    /// </summary>
    public static class ResultAggregator
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<string, List<string>> _buffers = new();
        private static readonly Dictionary<string, SystemTimers.Timer> _timers = new();
        private static readonly Dictionary<string, DateTime> _lastTouchUtc = new();

        /// <summary>
        /// 非会话缓冲区第一次为"上一轮还在说话"让路的时刻，用于给让路加上限。
        /// </summary>
        private static readonly Dictionary<string, DateTime> _deferSinceUtc = new();

        /// <summary>
        /// 随本次回灌一并送出的图像。
        ///
        /// 存在的理由：原生多模态要求主模型看到真实像素，而不是先让它描述一遍再读自己的描述。
        /// 回灌时机由本类掌握（必须等本回合结束），但图像真正的发送仍然复用手动截图那条路
        /// —— TalkBox.SendChatWithImages —— 不另起炉灶。
        /// </summary>
        private static readonly Dictionary<string, List<byte[]>> _imageBuffers = new();

        /// <summary>
        /// 聚合窗口时长
        /// </summary>
        public static TimeSpan Window { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// 为正在播出的回复让路时的重试间隔。
        /// </summary>
        private static readonly TimeSpan BusyRetryInterval = TimeSpan.FromSeconds(1);

        /// <summary>
        /// 让路的上限。超过这么久还没等到空闲就照常回灌。
        /// </summary>
        private static readonly TimeSpan MaxDeferral = TimeSpan.FromMinutes(2);

        /// <summary>
        /// 会话缓冲区的存活上限。
        ///
        /// session: 开头的 key 不带计时器，只能靠 FlushSession 清理；一旦会话因异常中断
        /// 而没走到 Flush，这个 key 就永远留在字典里。每个会话都是新 GUID，累积起来
        /// 就是一条只增不减的泄漏路径，所以这里给它一个兜底的过期时间。
        /// </summary>
        private static readonly TimeSpan SessionBufferTtl = TimeSpan.FromMinutes(10);

        private static string CurrentKey
        {
            get
            {
                var id = VPetLLMUtils.ExecutionContext.CurrentMessageId.Value;
                return id.HasValue ? $"session:{id.Value}" : "session:global";
            }
        }

        private static bool IsSessionKey(string key) => key.StartsWith("session:", StringComparison.Ordinal) && key != "session:global";

        /// <summary>
        /// 入队一条需要回灌给AI的片段（例如 [Plugin Result: X] ... 或 [Tool.X: "..."]）
        /// 会在窗口内与同Key的其他片段合并后，仅触发一次 ChatCore.Chat(..., true)
        /// </summary>
        public static void Enqueue(string payload) => Enqueue(payload, null);

        /// <summary>
        /// 强制以"非会话"身份入队，无视当前的 <c>CurrentMessageId</c>。
        ///
        /// 给后台任务用：任务是在某轮回复里发起的，<see cref="AsyncLocal{T}"/> 会把那轮的
        /// 会话 Id 一路带进延续里；可等它跑完时那轮早就 FlushSession 过了，
        /// 结果会落进一个没人再去 flush 的会话缓冲区，直到 TTL 到期被丢掉。
        /// 走非会话路径才会排上计时器，自己把自己冲出去。
        /// </summary>
        public static void EnqueueDetached(string payload)
        {
            var previous = VPetLLMUtils.ExecutionContext.CurrentMessageId.Value;
            try
            {
                // AsyncLocal 的赋值只影响当前执行流，不会回传给发起方
                VPetLLMUtils.ExecutionContext.CurrentMessageId.Value = null;
                Enqueue(payload, null);
            }
            finally
            {
                VPetLLMUtils.ExecutionContext.CurrentMessageId.Value = previous;
            }
        }

        /// <summary>
        /// 入队一条回执，并可附带图像。
        ///
        /// 带图时回灌会走 TalkBox.SendChatWithImages（与手动截图完全同一条路），
        /// 主模型看到的是真实图片；不带图则维持原来的 ChatCore.Chat 文本回灌。
        /// </summary>
        public static void Enqueue(string payload, IReadOnlyList<byte[]>? images)
        {
            try
            {
                // 中断之后产生的回执直接丢弃，别等到窗口到期才在 Flush 那里拦 ——
                // 聚合窗口可能比中断标记的自动复位还长，那时就拦不住了
                if (InterruptManager.IsInterrupted)
                {
                    VPetLLMUtils.Logger.Log("ResultAggregator: 本轮已被中断，丢弃回执片段");
                    return;
                }

                var key = CurrentKey;

                lock (_lock)
                {
                    PruneStaleSessions();

                    if (!_buffers.TryGetValue(key, out var list))
                    {
                        list = new List<string>();
                        _buffers[key] = list;
                    }
                    list.Add(payload);
                    _lastTouchUtc[key] = DateTime.UtcNow;

                    if (images is not null && images.Count > 0)
                    {
                        if (!_imageBuffers.TryGetValue(key, out var imageList))
                        {
                            imageList = new List<byte[]>();
                            _imageBuffers[key] = imageList;
                        }
                        imageList.AddRange(images.Where(i => i is not null && i.Length > 0));
                    }

                    // 会话内不启用计时器，统一由会话结束时 FlushSession 触发一次。
                    // 非会话（插件自发事件）才排计时器：重排等于重置窗口，
                    // 持续输入时在最后一条的 2 秒后统一输出一次
                    if (!IsSessionKey(key))
                    {
                        RearmTimerLocked(key, Window);
                    }
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"ResultAggregator.Enqueue 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 刷新会话缓冲区（异步版本，可等待）
        /// </summary>
        public static async Task FlushSessionAsync(Guid sessionId)
        {
            try
            {
                var key = $"session:{sessionId}";
                await FlushAsync(key);
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"ResultAggregator.FlushSessionAsync 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 刷新会话缓冲区（同步版本，兼容旧代码）
        /// </summary>
        public static void FlushSession(Guid sessionId)
        {
            try
            {
                var key = $"session:{sessionId}";
                Flush(key);
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"ResultAggregator.FlushSession 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 异步刷新缓冲区（可等待版本）
        /// </summary>
        private static async Task FlushAsync(string key)
        {
            string aggregated = null;
            List<byte[]>? images = null;
            try
            {
                lock (_lock)
                {
                    var hasContent = _buffers.TryGetValue(key, out var list) && list.Count > 0;

                    // 上一轮的回复还在播：先让路，缓冲区原样留着继续攒
                    if (hasContent && !IsSessionKey(key) && DeferForOngoingReplyLocked(key))
                        return;

                    // 无论有没有内容都要清干净这个 key，空缓冲区同样会把字典撑大
                    if (hasContent)
                    {
                        // 按原顺序拼接
                        aggregated = string.Join("", list!);
                    }

                    if (_imageBuffers.TryGetValue(key, out var imageList) && imageList.Count > 0)
                    {
                        images = new List<byte[]>(imageList);
                    }

                    Discard(key);

                    if (!hasContent)
                        return;
                }

                // 用户中断本轮后不再回灌：回灌会触发新一次 LLM 请求，等于中断没生效
                if (InterruptManager.IsInterrupted)
                {
                    VPetLLMUtils.Logger.Log("ResultAggregator: 本轮已被中断，放弃回灌");
                    return;
                }

                if (!string.IsNullOrEmpty(aggregated) && VPetLLM.Instance?.ChatCore is not null)
                {
                    VPetLLMUtils.Logger.Log($"ResultAggregator: 向AI回灌聚合内容: {aggregated}");

                    // 开始活动会话，防止状态灯过早切换为Idle
                    VPetLLM.Instance.FloatingSidebarManager?.BeginActiveSession("ResultAggregator");
                    VPetLLMUtils.Logger.Log("ResultAggregator: 开始回灌会话");

                    try
                    {
                        if (images is not null && images.Count > 0 && VPetLLM.Instance.TalkBox is not null)
                        {
                            // 原生多模态：走手动截图那条既有链路，图片真实进入主模型请求体。
                            // 这里不新起一套发送逻辑，只是把回灌时机接到已有入口上。
                            VPetLLMUtils.Logger.Log($"ResultAggregator: 携带 {images.Count} 张图片回灌（原生多模态）");
                            await VPetLLM.Instance.TalkBox.SendChatWithImages(aggregated, images, newRound: false);
                        }
                        else
                        {
                            // 过调度器：本轮的回执可能与用户新说的话、别的插件回执撞在一起，
                            // 交给它并成一次请求，而不是各自唤起一次 LLM
                            await ChatDispatcher.SubmitAsync(
                                aggregated, ChatPriority.Plugin, source: "ResultAggregator", isRetry: true);
                        }
                    }
                    finally
                    {
                        // 回灌完成后结束会话
                        VPetLLM.Instance.FloatingSidebarManager?.EndActiveSession("ResultAggregator");
                        VPetLLMUtils.Logger.Log("ResultAggregator: 回灌会话结束");
                    }
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"ResultAggregator.FlushAsync 异常: {ex.Message}, key={key}, aggregated={aggregated}");
                // 确保异常时也结束会话
                VPetLLM.Instance?.FloatingSidebarManager?.EndActiveSession("ResultAggregator");
            }
        }

        /// <summary>
        /// 同步刷新缓冲区（fire-and-forget 版本）
        /// </summary>
        private static async void Flush(string key)
        {
            await FlushAsync(key);
        }

        /// <summary>
        /// 插件自发的回执（不属于任何一轮对话，key 为 session:global）撞上正在播出的回复时先让路，
        /// 返回 true 表示本次不回灌。
        ///
        /// 会话内的回执有 FlushSession 兜着 —— 本回合的输出跑完才回灌，天然不会插队。
        /// 全局这条只有一个 2 秒计时器，到点就发，于是出现过这样的时序：用户的话刚回答到
        /// 第一句、语音还在放，前台窗口一变化就又灌进去一条，模型第二次开口 ——
        /// 两段话叠在一起，听感上就是"同一次互动回答了两遍"。
        ///
        /// 让路不丢内容：缓冲区原样留着、计时器重排，其间新到的回执继续并进同一条，
        /// 等这一轮的输出收干净了一起回灌。
        /// 调用方须持有 <see cref="_lock"/>。
        /// </summary>
        private static bool DeferForOngoingReplyLocked(string key)
        {
            var sidebar = VPetLLM.Instance?.FloatingSidebarManager;

            // ActiveSessionCount 覆盖"输出已经开始"，IsBusy 补上"请求还在飞、输出尚未开始"
            // 那一小段窗口 —— 只看前者的话，回灌会正好挤在上一轮拿到回复之前发出去
            bool busy = sidebar is not null
                && (sidebar.ActiveSessionCount > 0 || sidebar.IsBusy);

            if (!busy)
            {
                _deferSinceUtc.Remove(key);
                return false;
            }

            if (!_deferSinceUtc.TryGetValue(key, out var since))
            {
                // 只在第一次让路时记一笔：这里每 BusyRetryInterval 就会走一遍，
                // 每次都打日志会把一轮回复的日志刷成几十行等待
                _deferSinceUtc[key] = DateTime.UtcNow;
                VPetLLMUtils.Logger.Log("ResultAggregator: 上一轮回复仍在输出，推迟回灌直到它说完");
            }
            else if (DateTime.UtcNow - since >= MaxDeferral)
            {
                // 让路也要有上限：状态灯万一卡在非 Idle，回执不能就此石沉大海
                VPetLLMUtils.Logger.Log($"ResultAggregator: 已推迟 {MaxDeferral.TotalSeconds:F0}s，不再等待，照常回灌");
                _deferSinceUtc.Remove(key);
                return false;
            }

            RearmTimerLocked(key, BusyRetryInterval);
            return true;
        }

        /// <summary>
        /// 给某个 key 排一次性计时器（已有则重排）。调用方须持有 <see cref="_lock"/>。
        /// </summary>
        private static void RearmTimerLocked(string key, TimeSpan due)
        {
            if (!_timers.TryGetValue(key, out var timer))
            {
                timer = new SystemTimers.Timer(due.TotalMilliseconds);
                timer.AutoReset = false;
                timer.Elapsed += (_, __) => Flush(key);
                _timers[key] = timer;
                timer.Start();
                return;
            }

            timer.Stop();
            timer.Interval = due.TotalMilliseconds;
            timer.Start();
        }

        /// <summary>
        /// 丢弃某个 key 的全部状态。调用方须持有 <see cref="_lock"/>。
        /// </summary>
        private static void Discard(string key)
        {
            _buffers.Remove(key);
            // 图像缓冲同样要清，否则一张整屏截图会一直占着内存
            _imageBuffers.Remove(key);
            _lastTouchUtc.Remove(key);
            _deferSinceUtc.Remove(key);

            if (_timers.TryGetValue(key, out var timer))
            {
                timer.Dispose();
                _timers.Remove(key);
            }
        }

        /// <summary>
        /// 清掉超过 <see cref="SessionBufferTtl"/> 没再写入的会话缓冲区。
        /// 调用方须持有 <see cref="_lock"/>。
        /// </summary>
        private static void PruneStaleSessions()
        {
            if (_lastTouchUtc.Count == 0)
                return;

            var cutoff = DateTime.UtcNow - SessionBufferTtl;
            List<string>? stale = null;

            foreach (var pair in _lastTouchUtc)
            {
                if (IsSessionKey(pair.Key) && pair.Value < cutoff)
                {
                    (stale ??= new List<string>()).Add(pair.Key);
                }
            }

            if (stale is null)
                return;

            foreach (var key in stale)
            {
                Discard(key);
            }
            VPetLLMUtils.Logger.Log($"ResultAggregator: 清理了 {stale.Count} 个超时未回收的会话缓冲区");
        }
    }
}