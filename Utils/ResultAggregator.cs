using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;

namespace VPetLLM.Utils
{
    /// <summary>
    /// 将短时间内(默认2秒)来自插件/工具的回执聚合为一条消息再回灌给AI，避免连续多次唤起LLM
    /// </summary>
    public static class ResultAggregator
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<string, List<string>> _buffers = new();
        private static readonly Dictionary<string, System.Timers.Timer> _timers = new();

        /// <summary>
        /// 聚合窗口时长
        /// </summary>
        public static TimeSpan Window { get; set; } = TimeSpan.FromSeconds(2);

        private static string CurrentKey
        {
            get
            {
                var id = ExecutionContext.CurrentMessageId.Value;
                return id.HasValue ? $"session:{id.Value}" : "session:global";
            }
        }

        private static bool IsSessionKey(string key) => key.StartsWith("session:", StringComparison.Ordinal) && key != "session:global";

        /// <summary>
        /// 入队一条需要回灌给AI的片段（例如 [Plugin Result: X] ... 或 [Tool.X: "..."]）
        /// 会在窗口内与同Key的其他片段合并后，仅触发一次 ChatCore.Chat(..., true)
        /// </summary>
        public static void Enqueue(string payload)
        {
            try
            {
                var key = CurrentKey;

                lock (_lock)
                {
                    if (!_buffers.TryGetValue(key, out var list))
                    {
                        list = new List<string>();
                        _buffers[key] = list;
                    }
                    list.Add(payload);

                    // 会话内不启用计时器，统一由会话结束时 FlushSession 触发一次
                    if (!IsSessionKey(key))
                    {
                        // 启动或重置计时器（仅非会话全局聚合）
                        if (!_timers.TryGetValue(key, out var timer))
                        {
                            timer = new System.Timers.Timer(Window.TotalMilliseconds);
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
                Logger.Log($"ResultAggregator.Enqueue 异常: {ex.Message}");
            }
        }

        public static void FlushSession(Guid sessionId)
        {
            try
            {
                var key = $"session:{sessionId}";
                Flush(key);
            }
            catch (Exception ex)
            {
                Logger.Log($"ResultAggregator.FlushSession 异常: {ex.Message}");
            }
        }

        private static async void Flush(string key)
        {
            string aggregated = null;
            try
            {
                lock (_lock)
                {
                    if (!_buffers.TryGetValue(key, out var list) || list.Count == 0)
                        return;

                    // 按原顺序拼接
                    aggregated = string.Join("", list);
                    _buffers.Remove(key);

                    if (_timers.TryGetValue(key, out var t))
                    {
                        t.Dispose();
                        _timers.Remove(key);
                    }
                }

                if (!string.IsNullOrEmpty(aggregated) && VPetLLM.Instance?.ChatCore != null)
                {
                    Logger.Log($"ResultAggregator: 向AI回灌聚合内容: {aggregated}");
                    
                    // 开始活动会话，防止状态灯过早切换为Idle
                    VPetLLM.Instance.FloatingSidebarManager?.BeginActiveSession("ResultAggregator");
                    Logger.Log("ResultAggregator: 开始回灌会话");
                    
                    try
                    {
                        await VPetLLM.Instance.ChatCore.Chat(aggregated, true);
                    }
                    finally
                    {
                        // 回灌完成后结束会话
                        VPetLLM.Instance.FloatingSidebarManager?.EndActiveSession("ResultAggregator");
                        Logger.Log("ResultAggregator: 回灌会话结束");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"ResultAggregator.Flush 异常: {ex.Message}, key={key}, aggregated={aggregated}");
                // 确保异常时也结束会话
                VPetLLM.Instance?.FloatingSidebarManager?.EndActiveSession("ResultAggregator");
            }
        }
    }
}