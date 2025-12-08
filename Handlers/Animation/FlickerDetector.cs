using System;
using System.Collections.Generic;
using System.Linq;
using VPetLLM.Utils;

namespace VPetLLM.Handlers.Animation
{
    /// <summary>
    /// 闪烁检测器
    /// 检测和预防动画闪烁
    /// </summary>
    public class FlickerDetector
    {
        /// <summary>检测窗口 (毫秒)</summary>
        public const int DetectionWindow = 100;

        /// <summary>闪烁阈值 (窗口内切换次数)</summary>
        public const int FlickerThreshold = 2;

        /// <summary>最大延迟 (毫秒)</summary>
        public const int MaxRecommendedDelay = 200;

        /// <summary>基础延迟 (毫秒)</summary>
        public const int BaseDelay = 50;

        private readonly object _lock = new object();
        private readonly Queue<DateTime> _recentSwitches = new Queue<DateTime>();
        private int _totalSwitchCount = 0;
        private int _flickerWarningCount = 0;

        /// <summary>
        /// 记录动画切换
        /// </summary>
        public void RecordSwitch()
        {
            lock (_lock)
            {
                var now = DateTime.Now;
                _recentSwitches.Enqueue(now);
                _totalSwitchCount++;

                // 清理过期记录
                PruneOldSwitches(now);

                // 检查闪烁
                if (IsFlickerRisk())
                {
                    _flickerWarningCount++;
                    Logger.Log($"FlickerDetector: WARNING - Flicker detected! {_recentSwitches.Count} switches in {DetectionWindow}ms window (total warnings: {_flickerWarningCount})");
                }
            }
        }

        /// <summary>
        /// 检测是否存在闪烁风险
        /// </summary>
        public bool IsFlickerRisk()
        {
            lock (_lock)
            {
                PruneOldSwitches(DateTime.Now);
                return _recentSwitches.Count > FlickerThreshold;
            }
        }

        /// <summary>
        /// 获取当前闪烁风险等级 (0-100)
        /// </summary>
        public int GetFlickerRiskLevel()
        {
            lock (_lock)
            {
                PruneOldSwitches(DateTime.Now);
                var count = _recentSwitches.Count;
                
                if (count <= 1) return 0;
                if (count >= FlickerThreshold * 2) return 100;
                
                // 线性映射到 0-100
                return (int)((count - 1) * 100.0 / (FlickerThreshold * 2 - 1));
            }
        }

        /// <summary>
        /// 获取建议延迟 (毫秒)
        /// </summary>
        public int GetRecommendedDelay()
        {
            lock (_lock)
            {
                PruneOldSwitches(DateTime.Now);
                var count = _recentSwitches.Count;

                if (count <= 1)
                {
                    return 0;
                }

                // 根据最近切换次数计算延迟
                // 切换越频繁，延迟越长
                var delay = BaseDelay * count;
                return Math.Min(delay, MaxRecommendedDelay);
            }
        }

        /// <summary>
        /// 获取最近切换次数
        /// </summary>
        public int GetRecentSwitchCount()
        {
            lock (_lock)
            {
                PruneOldSwitches(DateTime.Now);
                return _recentSwitches.Count;
            }
        }

        /// <summary>
        /// 获取总切换次数
        /// </summary>
        public int GetTotalSwitchCount()
        {
            lock (_lock)
            {
                return _totalSwitchCount;
            }
        }

        /// <summary>
        /// 获取闪烁警告次数
        /// </summary>
        public int GetFlickerWarningCount()
        {
            lock (_lock)
            {
                return _flickerWarningCount;
            }
        }

        /// <summary>
        /// 重置统计
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _recentSwitches.Clear();
                _totalSwitchCount = 0;
                _flickerWarningCount = 0;
                Logger.Log("FlickerDetector: Statistics reset");
            }
        }

        /// <summary>
        /// 获取自上次切换以来的毫秒数
        /// </summary>
        public double GetMillisecondsSinceLastSwitch()
        {
            lock (_lock)
            {
                if (_recentSwitches.Count == 0)
                {
                    return double.MaxValue;
                }

                var lastSwitch = _recentSwitches.Last();
                return (DateTime.Now - lastSwitch).TotalMilliseconds;
            }
        }

        /// <summary>
        /// 检查是否应该延迟执行
        /// </summary>
        public bool ShouldDelay()
        {
            return GetRecommendedDelay() > 0;
        }

        /// <summary>
        /// 获取状态摘要
        /// </summary>
        public string GetStatusSummary()
        {
            lock (_lock)
            {
                PruneOldSwitches(DateTime.Now);
                return $"Recent: {_recentSwitches.Count}, Total: {_totalSwitchCount}, Warnings: {_flickerWarningCount}, Risk: {GetFlickerRiskLevel()}%";
            }
        }

        /// <summary>
        /// 清理过期的切换记录
        /// </summary>
        private void PruneOldSwitches(DateTime now)
        {
            var cutoff = now.AddMilliseconds(-DetectionWindow);
            while (_recentSwitches.Count > 0 && _recentSwitches.Peek() < cutoff)
            {
                _recentSwitches.Dequeue();
            }
        }
    }
}
