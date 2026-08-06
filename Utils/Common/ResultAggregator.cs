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

                    // 会话内不启用计时器，统一由会话结束时 FlushSession 触发一次
                    if (!IsSessionKey(key))
                    {
                        // 启动或重置计时器（仅非会话全局聚合）
                        if (!_timers.TryGetValue(key, out var timer))
                        {
                            timer = new SystemTimers.Timer(Window.TotalMilliseconds);
                            timer.AutoReset = false;
                            timer.Elapsed += (_, __) => Flush(key);
                            _timers[key] = timer;
                            timer.Start();
                        }
                        else
                        {
                            // 重置窗口：在持续输入的2秒后统一输出一次
                            timer.Stop();
                            timer.Interval = Window.TotalMilliseconds;
                            timer.Start();
                        }
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
                            await VPetLLM.Instance.TalkBox.SendChatWithImages(aggregated, images);
                        }
                        else
                        {
                            await VPetLLM.Instance.ChatCore.Chat(aggregated, true);
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
        /// 丢弃某个 key 的全部状态。调用方须持有 <see cref="_lock"/>。
        /// </summary>
        private static void Discard(string key)
        {
            _buffers.Remove(key);
            // 图像缓冲同样要清，否则一张整屏截图会一直占着内存
            _imageBuffers.Remove(key);
            _lastTouchUtc.Remove(key);

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