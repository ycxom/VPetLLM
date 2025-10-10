using System;
using System.Collections.Generic;

namespace VPetLLM.Utils
{
    /// <summary>
    /// 简单线程安全滑动窗口限流器
    /// </summary>
    public static class RateLimiter
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<string, Queue<DateTime>> _windows = new();

        /// <summary>
        /// 尝试获取一次调用许可：在指定窗口内未超过最大次数则记录并返回true，否则返回false
        /// </summary>
        /// <param name="key">限流键（如 "tool"）</param>
        /// <param name="maxCount">窗口内最大允许次数</param>
        /// <param name="window">时间窗口</param>
        public static bool TryAcquire(string key, int maxCount, TimeSpan window)
        {
            lock (_lock)
            {
                if (!_windows.TryGetValue(key, out var q))
                {
                    q = new Queue<DateTime>();
                    _windows[key] = q;
                }

                var now = DateTime.UtcNow;

                // 移除窗口外的旧时间戳
                while (q.Count > 0 && (now - q.Peek()) > window)
                {
                    q.Dequeue();
                }

                if (q.Count >= maxCount)
                {
                    return false;
                }

                q.Enqueue(now);
                return true;
            }
        }
    }
}