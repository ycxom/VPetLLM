using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VPetLLM.Utils
{
    /// <summary>
    /// 熔断器管理工具，提供便捷的查询和管理接口
    /// </summary>
    public static class RateLimiterManager
    {
        /// <summary>
        /// 获取所有限流器的状态报告
        /// </summary>
        public static string GetStatusReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 熔断器状态报告 ===");
            sb.AppendLine();

            var allStats = RateLimiter.GetAllStats();
            var allKeys = RateLimiter.GetAllKeys();

            if (allKeys.Count == 0)
            {
                sb.AppendLine("未配置任何限流器");
                return sb.ToString();
            }

            foreach (var key in allKeys)
            {
                var config = RateLimiter.GetConfig(key);
                var stats = allStats.ContainsKey(key) ? allStats[key] : null;

                sb.AppendLine($"[{key}] - {config?.Description ?? "无描述"}");
                sb.AppendLine($"  状态: {(config?.Enabled == true ? "启用" : "禁用")}");
                
                if (config != null)
                {
                    sb.AppendLine($"  配置: {config.MaxCount}次 / {config.Window.TotalMinutes}分钟");
                }

                if (stats != null)
                {
                    sb.AppendLine($"  统计:");
                    sb.AppendLine($"    总请求: {stats.TotalRequests}");
                    sb.AppendLine($"    被阻止: {stats.BlockedRequests}");
                    sb.AppendLine($"    当前计数: {stats.CurrentCount}");
                    sb.AppendLine($"    剩余配额: {stats.RemainingCount}");
                    
                    if (stats.LastRequest.HasValue)
                    {
                        sb.AppendLine($"    最后请求: {stats.LastRequest.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                    }
                    
                    if (stats.LastBlocked.HasValue)
                    {
                        sb.AppendLine($"    最后阻止: {stats.LastBlocked.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                    }
                    
                    if (stats.WindowStart.HasValue)
                    {
                        var windowRemaining = config != null 
                            ? config.Window - (DateTime.UtcNow - stats.WindowStart.Value)
                            : TimeSpan.Zero;
                        if (windowRemaining > TimeSpan.Zero)
                        {
                            sb.AppendLine($"    窗口剩余: {windowRemaining.TotalSeconds:F0}秒");
                        }
                    }
                }
                else
                {
                    sb.AppendLine($"  统计: 无数据");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// 获取简短的状态摘要
        /// </summary>
        public static string GetStatusSummary()
        {
            var allStats = RateLimiter.GetAllStats();
            var allKeys = RateLimiter.GetAllKeys();

            if (allKeys.Count == 0)
            {
                return "熔断器: 未配置";
            }

            var summaries = new List<string>();
            foreach (var key in allKeys)
            {
                var config = RateLimiter.GetConfig(key);
                var stats = allStats.ContainsKey(key) ? allStats[key] : null;
                
                if (config != null && stats != null)
                {
                    var status = config.Enabled ? "启用" : "禁用";
                    summaries.Add($"{key}({status}): {stats.CurrentCount}/{config.MaxCount}");
                }
            }

            return $"熔断器: {string.Join(", ", summaries)}";
        }

        /// <summary>
        /// 检查指定键是否接近限流阈值
        /// </summary>
        public static bool IsNearLimit(string key, double threshold = 0.8)
        {
            var config = RateLimiter.GetConfig(key);
            var stats = RateLimiter.GetStats(key);

            if (config == null || stats == null || !config.Enabled)
            {
                return false;
            }

            return stats.CurrentCount >= (config.MaxCount * threshold);
        }

        /// <summary>
        /// 获取指定键的剩余配额百分比
        /// </summary>
        public static double GetRemainingPercentage(string key)
        {
            var config = RateLimiter.GetConfig(key);
            var stats = RateLimiter.GetStats(key);

            if (config == null || stats == null || config.MaxCount == 0)
            {
                return 100.0;
            }

            return (double)stats.RemainingCount / config.MaxCount * 100.0;
        }

        /// <summary>
        /// 重新加载配置（从Setting应用到RateLimiter）
        /// </summary>
        public static void ReloadConfig(Setting setting)
        {
            if (setting?.RateLimiter == null)
            {
                Logger.Log("RateLimiterManager: Setting或RateLimiter配置为null");
                return;
            }

            // 更新Tool限流配置
            RateLimiter.SetConfig("tool", new RateLimiter.RateLimitConfig
            {
                MaxCount = setting.RateLimiter.ToolMaxCount,
                Window = TimeSpan.FromMinutes(setting.RateLimiter.ToolWindowMinutes),
                Enabled = setting.RateLimiter.EnableToolRateLimit,
                Description = "Tool调用限流"
            });

            // 更新Plugin限流配置
            RateLimiter.SetConfig("ai-plugin", new RateLimiter.RateLimitConfig
            {
                MaxCount = setting.RateLimiter.PluginMaxCount,
                Window = TimeSpan.FromMinutes(setting.RateLimiter.PluginWindowMinutes),
                Enabled = setting.RateLimiter.EnablePluginRateLimit,
                Description = "Plugin调用限流"
            });

            Logger.Log("RateLimiterManager: 配置已重新加载");
        }

        /// <summary>
        /// 导出配置和统计信息为JSON格式
        /// </summary>
        public static string ExportToJson()
        {
            var data = new
            {
                timestamp = DateTime.Now,
                configs = RateLimiter.GetAllKeys().Select(key => new
                {
                    key = key,
                    config = RateLimiter.GetConfig(key)
                }),
                stats = RateLimiter.GetAllStats()
            };

            return Newtonsoft.Json.JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented);
        }
    }
}
