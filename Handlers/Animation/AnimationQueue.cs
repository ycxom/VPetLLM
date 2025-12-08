using System;
using System.Collections.Generic;
using System.Linq;
using VPetLLM.Utils;

namespace VPetLLM.Handlers.Animation
{
    /// <summary>
    /// 动画请求队列
    /// 管理动画请求的 FIFO 队列，支持优先级、合并和溢出处理
    /// </summary>
    public class AnimationQueue
    {
        /// <summary>最大队列深度</summary>
        public const int MaxQueueDepth = 5;

        /// <summary>最小请求间隔 (毫秒)</summary>
        public const int MinRequestInterval = 50;

        /// <summary>请求合并窗口 (毫秒)</summary>
        public const int CoalesceWindow = 100;

        private readonly object _lock = new object();
        private readonly LinkedList<AnimationRequest> _queue = new LinkedList<AnimationRequest>();
        private DateTime _lastDequeueTime = DateTime.MinValue;
        private DateTime _lastEnqueueTime = DateTime.MinValue;

        /// <summary>当前队列深度</summary>
        public int Count
        {
            get { lock (_lock) { return _queue.Count; } }
        }

        /// <summary>队列是否为空</summary>
        public bool IsEmpty
        {
            get { lock (_lock) { return _queue.Count == 0; } }
        }

        /// <summary>
        /// 入队请求
        /// </summary>
        /// <param name="request">动画请求</param>
        /// <returns>true 如果请求被入队，false 如果被合并或丢弃</returns>
        public bool Enqueue(AnimationRequest request)
        {
            if (request == null)
            {
                Logger.Log("AnimationQueue: Received null request, ignoring");
                return false;
            }

            lock (_lock)
            {
                Logger.Log($"AnimationQueue: Enqueue request {request}");

                // 尝试合并请求
                if (request.AllowCoalesce && TryCoalesce(request))
                {
                    Logger.Log($"AnimationQueue: Request {request.Id.Substring(0, 8)} coalesced with existing request");
                    return false;
                }

                // 处理优先级抢占
                if (request.Priority >= AnimationPriority.High)
                {
                    InsertByPriority(request);
                }
                else
                {
                    _queue.AddLast(request);
                }

                _lastEnqueueTime = DateTime.Now;

                // 检查队列溢出
                if (_queue.Count > MaxQueueDepth)
                {
                    PruneStaleRequests();
                }

                Logger.Log($"AnimationQueue: Queue depth is now {_queue.Count}");
                return true;
            }
        }

        /// <summary>
        /// 出队请求
        /// </summary>
        /// <returns>下一个要处理的请求，如果队列为空则返回 null</returns>
        public AnimationRequest Dequeue()
        {
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    return null;
                }

                // 检查最小间隔
                var timeSinceLastDequeue = (DateTime.Now - _lastDequeueTime).TotalMilliseconds;
                if (timeSinceLastDequeue < MinRequestInterval && _lastDequeueTime != DateTime.MinValue)
                {
                    Logger.Log($"AnimationQueue: Dequeue blocked, only {timeSinceLastDequeue:F0}ms since last dequeue (min: {MinRequestInterval}ms)");
                    return null;
                }

                var request = _queue.First.Value;
                _queue.RemoveFirst();
                _lastDequeueTime = DateTime.Now;

                Logger.Log($"AnimationQueue: Dequeued request {request}");
                return request;
            }
        }

        /// <summary>
        /// 查看队首请求但不移除
        /// </summary>
        public AnimationRequest Peek()
        {
            lock (_lock)
            {
                return _queue.Count > 0 ? _queue.First.Value : null;
            }
        }

        /// <summary>
        /// 清空队列
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                var count = _queue.Count;
                _queue.Clear();
                Logger.Log($"AnimationQueue: Cleared {count} requests");
            }
        }

        /// <summary>
        /// 取消指定来源的所有请求
        /// </summary>
        public int CancelBySource(string source)
        {
            lock (_lock)
            {
                var toRemove = _queue.Where(r => r.Source == source).ToList();
                foreach (var request in toRemove)
                {
                    _queue.Remove(request);
                }
                Logger.Log($"AnimationQueue: Cancelled {toRemove.Count} requests from source '{source}'");
                return toRemove.Count;
            }
        }

        /// <summary>
        /// 获取待处理请求的来源列表
        /// </summary>
        public List<string> GetPendingSources()
        {
            lock (_lock)
            {
                return _queue.Select(r => r.Source).Distinct().ToList();
            }
        }

        /// <summary>
        /// 尝试合并相似请求
        /// </summary>
        private bool TryCoalesce(AnimationRequest newRequest)
        {
            // 检查时间窗口
            var timeSinceLastEnqueue = (DateTime.Now - _lastEnqueueTime).TotalMilliseconds;
            if (timeSinceLastEnqueue > CoalesceWindow)
            {
                return false;
            }

            // 查找可合并的请求
            var existingRequest = _queue.LastOrDefault(r =>
                r.AllowCoalesce &&
                r.Source == newRequest.Source &&
                r.Type == newRequest.Type &&
                r.Priority == newRequest.Priority);

            if (existingRequest != null)
            {
                // 更新现有请求的参数为新请求的参数
                existingRequest.AnimationName = newRequest.AnimationName;
                existingRequest.AnimatType = newRequest.AnimatType;
                existingRequest.TargetGraphType = newRequest.TargetGraphType;
                existingRequest.EndAction = newRequest.EndAction;
                existingRequest.TargetState = newRequest.TargetState;

                Logger.Log($"AnimationQueue: Coalesced request from {newRequest.Source}, window: {timeSinceLastEnqueue:F0}ms");
                return true;
            }

            return false;
        }

        /// <summary>
        /// 按优先级插入请求
        /// </summary>
        private void InsertByPriority(AnimationRequest request)
        {
            var node = _queue.First;
            while (node != null)
            {
                if (node.Value.Priority < request.Priority)
                {
                    _queue.AddBefore(node, request);
                    Logger.Log($"AnimationQueue: Inserted high priority request before {node.Value.Id.Substring(0, 8)}");
                    return;
                }
                node = node.Next;
            }
            _queue.AddLast(request);
        }

        /// <summary>
        /// 清理过期请求以防止队列溢出
        /// </summary>
        private void PruneStaleRequests()
        {
            var prunedCount = 0;

            // 保留 Critical 优先级请求
            var criticalRequests = _queue.Where(r => r.Priority == AnimationPriority.Critical).ToList();
            var otherRequests = _queue.Where(r => r.Priority != AnimationPriority.Critical)
                                      .OrderByDescending(r => r.Timestamp)
                                      .Take(MaxQueueDepth - criticalRequests.Count)
                                      .ToList();

            var keepRequests = new HashSet<string>(
                criticalRequests.Concat(otherRequests).Select(r => r.Id));

            var toRemove = _queue.Where(r => !keepRequests.Contains(r.Id)).ToList();
            foreach (var request in toRemove)
            {
                _queue.Remove(request);
                prunedCount++;
            }

            if (prunedCount > 0)
            {
                Logger.Log($"AnimationQueue: Pruned {prunedCount} stale requests, queue depth now {_queue.Count}");
            }
        }

        /// <summary>
        /// 检查是否可以立即出队 (考虑最小间隔)
        /// </summary>
        public bool CanDequeueNow()
        {
            lock (_lock)
            {
                if (_queue.Count == 0) return false;
                if (_lastDequeueTime == DateTime.MinValue) return true;

                var timeSinceLastDequeue = (DateTime.Now - _lastDequeueTime).TotalMilliseconds;
                return timeSinceLastDequeue >= MinRequestInterval;
            }
        }

        /// <summary>
        /// 获取距离下次可出队的剩余毫秒数
        /// </summary>
        public int GetMillisecondsUntilNextDequeue()
        {
            lock (_lock)
            {
                if (_queue.Count == 0) return -1;
                if (_lastDequeueTime == DateTime.MinValue) return 0;

                var timeSinceLastDequeue = (DateTime.Now - _lastDequeueTime).TotalMilliseconds;
                var remaining = MinRequestInterval - timeSinceLastDequeue;
                return remaining > 0 ? (int)remaining : 0;
            }
        }
    }
}
