namespace VPetLLM.Handlers.TTS
{
    /// <summary>
    /// TTS操作跟踪器，用于跟踪和分析TTS操作的性能指标
    /// 提供操作生命周期跟踪和性能报告生成功能
    /// </summary>
    public class TTSOperationTracker
    {
        private readonly Dictionary<string, TTSOperationRecord> _operations = new Dictionary<string, TTSOperationRecord>();
        private readonly object _lockObject = new object();

        /// <summary>
        /// TTS操作记录
        /// </summary>
        public class TTSOperationRecord
        {
            public string OperationId { get; set; }
            public string Text { get; set; }
            public DateTime RequestTime { get; set; }
            public DateTime? PlaybackStartTime { get; set; }
            public DateTime? PlaybackEndTime { get; set; }
            public bool IsCompleted { get; set; }
            public bool IsSuccessful { get; set; }
            public string ErrorMessage { get; set; }

            /// <summary>
            /// 总持续时间
            /// </summary>
            public TimeSpan TotalDuration => (PlaybackEndTime ?? DateTime.Now) - RequestTime;

            /// <summary>
            /// 等待时间（从请求到播放开始）
            /// </summary>
            public TimeSpan? WaitTime => PlaybackStartTime.HasValue ? PlaybackStartTime.Value - RequestTime : null;

            /// <summary>
            /// 播放时间（从播放开始到播放结束）
            /// </summary>
            public TimeSpan? PlaybackDuration => PlaybackStartTime.HasValue && PlaybackEndTime.HasValue
                ? PlaybackEndTime.Value - PlaybackStartTime.Value : null;
        }

        /// <summary>
        /// 开始跟踪操作
        /// </summary>
        /// <param name="operationId">操作唯一标识符</param>
        /// <param name="text">TTS文本内容</param>
        public void StartOperation(string operationId, string text)
        {
            lock (_lockObject)
            {
                _operations[operationId] = new TTSOperationRecord
                {
                    OperationId = operationId,
                    Text = text ?? "",
                    RequestTime = DateTime.Now,
                    IsCompleted = false,
                    IsSuccessful = false
                };
            }
            Logger.Log($"TTSOperationTracker: 开始跟踪操作 {operationId}, 文本长度: {text?.Length ?? 0}");
        }

        /// <summary>
        /// 标记播放开始
        /// </summary>
        /// <param name="operationId">操作标识符</param>
        public void MarkPlaybackStart(string operationId)
        {
            lock (_lockObject)
            {
                if (_operations.TryGetValue(operationId, out var record))
                {
                    record.PlaybackStartTime = DateTime.Now;
                    var waitTime = record.WaitTime?.TotalMilliseconds ?? 0;
                    Logger.Log($"TTSOperationTracker: 播放开始 {operationId}, 等待时间: {waitTime:F0}ms");
                }
                else
                {
                    Logger.Log($"TTSOperationTracker: 未找到操作记录 {operationId}");
                }
            }
        }

        /// <summary>
        /// 完成操作
        /// </summary>
        /// <param name="operationId">操作标识符</param>
        /// <param name="success">是否成功</param>
        /// <param name="errorMessage">错误信息（如果失败）</param>
        public void CompleteOperation(string operationId, bool success, string errorMessage = null)
        {
            lock (_lockObject)
            {
                if (_operations.TryGetValue(operationId, out var record))
                {
                    record.PlaybackEndTime = DateTime.Now;
                    record.IsCompleted = true;
                    record.IsSuccessful = success;
                    record.ErrorMessage = errorMessage;

                    var totalTime = record.TotalDuration.TotalMilliseconds;
                    var playbackTime = record.PlaybackDuration?.TotalMilliseconds ?? 0;

                    Logger.Log($"TTSOperationTracker: 操作完成 {operationId}, 成功: {success}, " +
                              $"总时长: {totalTime:F0}ms, 播放时长: {playbackTime:F0}ms");

                    if (!success && !string.IsNullOrEmpty(errorMessage))
                    {
                        Logger.Log($"TTSOperationTracker: 操作失败原因: {errorMessage}");
                    }
                }
                else
                {
                    Logger.Log($"TTSOperationTracker: 未找到操作记录 {operationId}");
                }
            }
        }

        /// <summary>
        /// 获取操作记录
        /// </summary>
        /// <param name="operationId">操作标识符</param>
        /// <returns>操作记录，如果不存在则返回null</returns>
        public TTSOperationRecord GetOperation(string operationId)
        {
            lock (_lockObject)
            {
                return _operations.TryGetValue(operationId, out var record) ? record : null;
            }
        }

        /// <summary>
        /// 获取所有操作记录
        /// </summary>
        /// <returns>操作记录列表</returns>
        public List<TTSOperationRecord> GetAllOperations()
        {
            lock (_lockObject)
            {
                return _operations.Values.ToList();
            }
        }

        /// <summary>
        /// 获取已完成的操作记录
        /// </summary>
        /// <returns>已完成的操作记录列表</returns>
        public List<TTSOperationRecord> GetCompletedOperations()
        {
            lock (_lockObject)
            {
                return _operations.Values.Where(op => op.IsCompleted).ToList();
            }
        }

        /// <summary>
        /// 获取正在进行的操作记录
        /// </summary>
        /// <returns>正在进行的操作记录列表</returns>
        public List<TTSOperationRecord> GetActiveOperations()
        {
            lock (_lockObject)
            {
                return _operations.Values.Where(op => !op.IsCompleted).ToList();
            }
        }

        /// <summary>
        /// 生成性能报告
        /// </summary>
        /// <returns>TTS性能报告</returns>
        public TTSPerformanceReport GenerateReport()
        {
            lock (_lockObject)
            {
                var completedOps = _operations.Values.Where(op => op.IsCompleted).ToList();
                var successfulOps = completedOps.Where(op => op.IsSuccessful).ToList();

                var report = new TTSPerformanceReport
                {
                    TotalOperations = completedOps.Count,
                    SuccessfulOperations = successfulOps.Count,
                    SuccessRate = completedOps.Count > 0 ? (double)successfulOps.Count / completedOps.Count : 0,
                    ErrorCount = completedOps.Count(op => !op.IsSuccessful),
                    GeneratedAt = DateTime.Now
                };

                // 计算平均时间（只包括成功的操作）
                if (successfulOps.Count > 0)
                {
                    var waitTimes = successfulOps.Where(op => op.WaitTime.HasValue).Select(op => op.WaitTime.Value.TotalMilliseconds);
                    var totalTimes = successfulOps.Select(op => op.TotalDuration.TotalMilliseconds);
                    var playbackTimes = successfulOps.Where(op => op.PlaybackDuration.HasValue).Select(op => op.PlaybackDuration.Value.TotalMilliseconds);

                    report.AverageWaitTime = waitTimes.Any() ? waitTimes.Average() : 0;
                    report.AverageTotalTime = totalTimes.Average();
                    report.AveragePlaybackTime = playbackTimes.Any() ? playbackTimes.Average() : 0;

                    // 计算最小和最大时间
                    report.MinTotalTime = totalTimes.Min();
                    report.MaxTotalTime = totalTimes.Max();

                    if (waitTimes.Any())
                    {
                        report.MinWaitTime = waitTimes.Min();
                        report.MaxWaitTime = waitTimes.Max();
                    }
                }

                Logger.Log($"TTSOperationTracker: 生成性能报告 - {report}");
                return report;
            }
        }

        /// <summary>
        /// 分析请求频率
        /// </summary>
        /// <param name="timeWindowMinutes">时间窗口（分钟）</param>
        /// <returns>频率分析结果</returns>
        public FrequencyAnalysis AnalyzeRequestFrequency(int timeWindowMinutes = 5)
        {
            lock (_lockObject)
            {
                var cutoffTime = DateTime.Now.AddMinutes(-timeWindowMinutes);
                var recentOps = _operations.Values.Where(op => op.RequestTime >= cutoffTime).OrderBy(op => op.RequestTime).ToList();

                var analysis = new FrequencyAnalysis
                {
                    TimeWindowMinutes = timeWindowMinutes,
                    TotalRequests = recentOps.Count,
                    RequestsPerMinute = recentOps.Count / (double)timeWindowMinutes
                };

                // 计算请求间隔
                if (recentOps.Count > 1)
                {
                    var intervals = new List<double>();
                    for (int i = 1; i < recentOps.Count; i++)
                    {
                        var interval = (recentOps[i].RequestTime - recentOps[i - 1].RequestTime).TotalMilliseconds;
                        intervals.Add(interval);
                    }

                    analysis.AverageIntervalMs = intervals.Average();
                    analysis.MinIntervalMs = intervals.Min();
                    analysis.MaxIntervalMs = intervals.Max();

                    // 检测过于频繁的请求（间隔小于1.5秒）
                    analysis.FrequentRequestCount = intervals.Count(interval => interval < 1500);
                }

                Logger.Log($"TTSOperationTracker: 频率分析 - 时间窗口: {timeWindowMinutes}分钟, " +
                          $"请求数: {analysis.TotalRequests}, 平均间隔: {analysis.AverageIntervalMs:F0}ms");

                return analysis;
            }
        }

        /// <summary>
        /// 清理旧的操作记录
        /// </summary>
        /// <param name="maxAge">最大保留时间</param>
        /// <returns>清理的记录数量</returns>
        public int CleanupOldRecords(TimeSpan maxAge)
        {
            lock (_lockObject)
            {
                var cutoffTime = DateTime.Now - maxAge;
                var toRemove = _operations.Where(kvp => kvp.Value.RequestTime < cutoffTime).Select(kvp => kvp.Key).ToList();

                foreach (var key in toRemove)
                {
                    _operations.Remove(key);
                }

                Logger.Log($"TTSOperationTracker: 清理了 {toRemove.Count} 条旧记录");
                return toRemove.Count;
            }
        }

        /// <summary>
        /// 获取统计摘要
        /// </summary>
        /// <returns>统计摘要字符串</returns>
        public string GetSummary()
        {
            lock (_lockObject)
            {
                var total = _operations.Count;
                var completed = _operations.Values.Count(op => op.IsCompleted);
                var successful = _operations.Values.Count(op => op.IsCompleted && op.IsSuccessful);
                var active = total - completed;

                return $"TTS操作跟踪 - 总计: {total}, 已完成: {completed}, 成功: {successful}, 进行中: {active}";
            }
        }
    }

    /// <summary>
    /// TTS性能报告
    /// </summary>
    public class TTSPerformanceReport
    {
        public int TotalOperations { get; set; }
        public int SuccessfulOperations { get; set; }
        public double SuccessRate { get; set; }
        public double AverageWaitTime { get; set; }
        public double AverageTotalTime { get; set; }
        public double AveragePlaybackTime { get; set; }
        public double MinWaitTime { get; set; }
        public double MaxWaitTime { get; set; }
        public double MinTotalTime { get; set; }
        public double MaxTotalTime { get; set; }
        public int ErrorCount { get; set; }
        public DateTime GeneratedAt { get; set; }

        public override string ToString()
        {
            return $"TTS性能报告 - 总操作: {TotalOperations}, 成功率: {SuccessRate:P2}, " +
                   $"平均等待: {AverageWaitTime:F0}ms, 平均总时长: {AverageTotalTime:F0}ms, 错误: {ErrorCount}";
        }
    }

    /// <summary>
    /// 频率分析结果
    /// </summary>
    public class FrequencyAnalysis
    {
        public int TimeWindowMinutes { get; set; }
        public int TotalRequests { get; set; }
        public double RequestsPerMinute { get; set; }
        public double AverageIntervalMs { get; set; }
        public double MinIntervalMs { get; set; }
        public double MaxIntervalMs { get; set; }
        public int FrequentRequestCount { get; set; }

        public override string ToString()
        {
            return $"频率分析 - {TimeWindowMinutes}分钟内: {TotalRequests}个请求, " +
                   $"平均间隔: {AverageIntervalMs:F0}ms, 频繁请求: {FrequentRequestCount}个";
        }
    }
}