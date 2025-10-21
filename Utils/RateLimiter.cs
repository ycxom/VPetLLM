using System;
using System.Collections.Generic;
using System.Linq;

namespace VPetLLM.Utils
{
    /// <summary>
    /// 线程安全滑动窗口限流器，支持配置、监控和重置
    /// </summary>
    public static class RateLimiter
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<string, Queue<DateTime>> _windows = new();
        private static readonly Dictionary<string, RateLimitConfig> _configs = new();
        private static readonly Dictionary<string, RateLimitStats> _stats = new();

        /// <summary>
        /// 限流配置
        /// </summary>
        public class RateLimitConfig
        {
            public int MaxCount { get; set; }
            public TimeSpan Window { get; set; }
            public bool Enabled { get; set; } = true;
            public string Description { get; set; } = "";
        }

        /// <summary>
        /// 限流统计信息
        /// </summary>
        public class RateLimitStats
        {
            public int TotalRequests { get; set; }
            public int BlockedRequests { get; set; }
            public DateTime? LastRequest { get; set; }
            public DateTime? LastBlocked { get; set; }
            public int CurrentCount { get; set; }
            public int RemainingCount { get; set; }
            public DateTime? WindowStart { get; set; }
        }

        /// <summary>
        /// 初始化默认配置
        /// </summary>
        static RateLimiter()
        {
            // 默认配置
            SetConfig("tool", new RateLimitConfig
            {
                MaxCount = 5,
                Window = TimeSpan.FromMinutes(2),
                Enabled = true,
                Description = "Tool调用限流"
            });

            SetConfig("ai-plugin", new RateLimitConfig
            {
                MaxCount = 5,
                Window = TimeSpan.FromMinutes(2),
                Enabled = true,
                Description = "Plugin调用限流"
            });
        }

        /// <summary>
        /// 设置限流配置
        /// </summary>
        public static void SetConfig(string key, RateLimitConfig config)
        {
            lock (_lock)
            {
                _configs[key] = config;
                if (!_stats.ContainsKey(key))
                {
                    _stats[key] = new RateLimitStats();
                }
            }
        }

        /// <summary>
        /// 获取限流配置
        /// </summary>
        public static RateLimitConfig GetConfig(string key)
        {
            lock (_lock)
            {
                return _configs.TryGetValue(key, out var config) ? config : null;
            }
        }

        /// <summary>
        /// 尝试获取一次调用许可：在指定窗口内未超过最大次数则记录并返回true，否则返回false
        /// </summary>
        /// <param name="key">限流键（如 "tool"）</param>
        /// <param name="maxCount">窗口内最大允许次数（如果已配置则使用配置值）</param>
        /// <param name="window">时间窗口（如果已配置则使用配置值）</param>
        public static bool TryAcquire(string key, int maxCount, TimeSpan window)
        {
            lock (_lock)
            {
                // 使用配置的值（如果存在）
                if (_configs.TryGetValue(key, out var config))
                {
                    if (!config.Enabled)
                    {
                        // 限流已禁用，直接通过
                        return true;
                    }
                    maxCount = config.MaxCount;
                    window = config.Window;
                }
                else
                {
                    // 自动创建配置
                    SetConfig(key, new RateLimitConfig
                    {
                        MaxCount = maxCount,
                        Window = window,
                        Enabled = true,
                        Description = $"{key}限流"
                    });
                }

                if (!_windows.TryGetValue(key, out var q))
                {
                    q = new Queue<DateTime>();
                    _windows[key] = q;
                }

                if (!_stats.TryGetValue(key, out var stats))
                {
                    stats = new RateLimitStats();
                    _stats[key] = stats;
                }

                var now = DateTime.UtcNow;

                // 移除窗口外的旧时间戳
                while (q.Count > 0 && (now - q.Peek()) > window)
                {
                    q.Dequeue();
                }

                // 更新统计信息
                stats.TotalRequests++;
                stats.LastRequest = now;
                stats.CurrentCount = q.Count;
                stats.RemainingCount = Math.Max(0, maxCount - q.Count);
                if (q.Count > 0)
                {
                    stats.WindowStart = q.Peek();
                }

                if (q.Count >= maxCount)
                {
                    stats.BlockedRequests++;
                    stats.LastBlocked = now;
                    Logger.Log($"RateLimiter: [{key}] 触发限流 - 当前: {q.Count}/{maxCount}, 窗口: {window.TotalSeconds}秒");
                    return false;
                }

                q.Enqueue(now);
                return true;
            }
        }

        /// <summary>
        /// 获取限流统计信息
        /// </summary>
        public static RateLimitStats GetStats(string key)
        {
            lock (_lock)
            {
                if (_stats.TryGetValue(key, out var stats))
                {
                    // 返回副本以避免外部修改
                    return new RateLimitStats
                    {
                        TotalRequests = stats.TotalRequests,
                        BlockedRequests = stats.BlockedRequests,
                        LastRequest = stats.LastRequest,
                        LastBlocked = stats.LastBlocked,
                        CurrentCount = stats.CurrentCount,
                        RemainingCount = stats.RemainingCount,
                        WindowStart = stats.WindowStart
                    };
                }
                return null;
            }
        }

        /// <summary>
        /// 获取所有限流键的统计信息
        /// </summary>
        public static Dictionary<string, RateLimitStats> GetAllStats()
        {
            lock (_lock)
            {
                return _stats.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new RateLimitStats
                    {
                        TotalRequests = kvp.Value.TotalRequests,
                        BlockedRequests = kvp.Value.BlockedRequests,
                        LastRequest = kvp.Value.LastRequest,
                        LastBlocked = kvp.Value.LastBlocked,
                        CurrentCount = kvp.Value.CurrentCount,
                        RemainingCount = kvp.Value.RemainingCount,
                        WindowStart = kvp.Value.WindowStart
                    }
                );
            }
        }

        /// <summary>
        /// 重置指定键的限流状态
        /// </summary>
        public static void Reset(string key)
        {
            lock (_lock)
            {
                if (_windows.ContainsKey(key))
                {
                    _windows[key].Clear();
                }
                if (_stats.ContainsKey(key))
                {
                    _stats[key] = new RateLimitStats();
                }
                Logger.Log($"RateLimiter: 已重置 [{key}] 的限流状态");
            }
        }

        /// <summary>
        /// 重置所有限流状态
        /// </summary>
        public static void ResetAll()
        {
            lock (_lock)
            {
                _windows.Clear();
                _stats.Clear();
                foreach (var key in _configs.Keys)
                {
                    _stats[key] = new RateLimitStats();
                }
                Logger.Log("RateLimiter: 已重置所有限流状态");
            }
        }

        /// <summary>
        /// 启用指定键的限流
        /// </summary>
        public static void Enable(string key)
        {
            lock (_lock)
            {
                if (_configs.TryGetValue(key, out var config))
                {
                    config.Enabled = true;
                    Logger.Log($"RateLimiter: 已启用 [{key}] 的限流");
                }
            }
        }

        /// <summary>
        /// 禁用指定键的限流
        /// </summary>
        public static void Disable(string key)
        {
            lock (_lock)
            {
                if (_configs.TryGetValue(key, out var config))
                {
                    config.Enabled = false;
                    Logger.Log($"RateLimiter: 已禁用 [{key}] 的限流");
                }
            }
        }

        /// <summary>
        /// 获取所有配置的键
        /// </summary>
        public static List<string> GetAllKeys()
        {
            lock (_lock)
            {
                return _configs.Keys.ToList();
            }
        }
    }
}