using VPetLLMUtils = VPetLLM.Utils.System;

namespace VPetLLM.Utils.Plugin
{
    /// <summary>
    /// 单个插件操作的结果。Silent 表示这次操作不计入批次汇总
    /// （例如用户在校验失败的提示里主动选了"取消"，不该再被当成一条失败去打扰他）。
    /// </summary>
    public sealed class PluginOperationResult
    {
        private PluginOperationResult(bool success, string? reason, bool silent)
        {
            Success = success;
            Reason = reason;
            Silent = silent;
        }

        public bool Success { get; }
        public string? Reason { get; }
        public bool Silent { get; }

        public static PluginOperationResult Ok() => new(true, null, false);
        public static PluginOperationResult Fail(string reason) => new(false, reason, false);

        /// <summary>用户自己中止的操作：不成功也不算失败，不进汇总。</summary>
        public static PluginOperationResult Cancelled() => new(false, null, true);
    }

    /// <summary>批次里的一条失败记录。</summary>
    public sealed class PluginOperationFailure
    {
        public PluginOperationFailure(string name, string? reason)
        {
            Name = name;
            Reason = reason;
        }

        public string Name { get; }
        public string? Reason { get; }
    }

    /// <summary>一次批量插件操作跑完后的汇总。</summary>
    public sealed class PluginBatchReport
    {
        public List<string> Succeeded { get; } = new();
        public List<PluginOperationFailure> Failed { get; } = new();

        public int Total => Succeeded.Count + Failed.Count;
        public bool AllSucceeded => Failed.Count == 0;
    }

    /// <summary>
    /// 插件安装/更新/卸载的串行队列。
    ///
    /// 要解决的问题：原来每个操作按钮都是 async void 直接开跑，用户连点 N 个"更新"就会有
    /// N 条流水线并行——同时改 PluginManager 的静态集合（List/Dictionary 没锁，会真的写坏）、
    /// 同时重建 DataGrid、每条结束时各弹一个模态框。模态框在 UI 线程上开的是嵌套消息循环，
    /// 后面那些续体又在这个循环里接着弹下一个框，于是框摞框，窗口看起来就是卡死。
    ///
    /// 现在：同一时刻只跑一个操作；排队期间入列的操作合并成一个"批次"，
    /// 批次跑完只回调一次，由调用方汇总提示（一个框，不是 N 个）。
    /// </summary>
    public sealed class PluginOperationQueue
    {
        private sealed class QueuedOperation
        {
            public QueuedOperation(string displayName, Func<Task<PluginOperationResult>> operation)
            {
                DisplayName = displayName;
                Operation = operation;
            }

            public string DisplayName { get; }
            public Func<Task<PluginOperationResult>> Operation { get; }

            public TaskCompletionSource<PluginOperationResult> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly object _sync = new();
        private readonly Queue<QueuedOperation> _queue = new();
        private readonly Func<PluginBatchReport, Task> _onBatchCompleted;
        private readonly Action<string, int, int>? _onProgress;

        private bool _draining;
        private PluginBatchReport _report = new();
        private int _batchEnqueued;
        private int _batchDone;

        /// <param name="onBatchCompleted">批次跑完时回调一次，用来做统一的刷新 + 汇总提示。</param>
        /// <param name="onProgress">可选：每条操作开始前回调，参数为 (插件名, 第几个, 共几个)。</param>
        public PluginOperationQueue(Func<PluginBatchReport, Task> onBatchCompleted, Action<string, int, int>? onProgress = null)
        {
            _onBatchCompleted = onBatchCompleted ?? throw new ArgumentNullException(nameof(onBatchCompleted));
            _onProgress = onProgress;
        }

        /// <summary>当前是否有操作在跑或在排队。</summary>
        public bool IsBusy
        {
            get { lock (_sync) { return _draining; } }
        }

        /// <summary>还在排队等待的操作数（不含正在跑的那一个）。</summary>
        public int PendingCount
        {
            get { lock (_sync) { return _queue.Count; } }
        }

        /// <summary>
        /// 入列一个操作。返回的 Task 在"这一条"跑完时完成——调用方可以据此停掉自己那颗按钮的转圈动画，
        /// 而不用等整个批次。
        ///
        /// 必须从 UI 线程调用：内部 await 不切上下文，操作体里的续体会回到 UI 线程，
        /// 这样操作实现里可以直接碰控件。
        /// </summary>
        public Task<PluginOperationResult> EnqueueAsync(string displayName, Func<Task<PluginOperationResult>> operation)
        {
            if (operation is null) throw new ArgumentNullException(nameof(operation));

            var op = new QueuedOperation(string.IsNullOrWhiteSpace(displayName) ? "?" : displayName, operation);

            bool startDrain;
            lock (_sync)
            {
                _queue.Enqueue(op);
                _batchEnqueued++;

                // 入列和"认领排空权"必须在同一把锁里做完，
                // 否则两个线程可能都看到 _draining == false，各自开一条排空循环。
                startDrain = !_draining;
                if (startDrain) _draining = true;
            }

            if (startDrain) _ = DrainAsync();

            return op.Completion.Task;
        }

        private async Task DrainAsync()
        {
            PluginBatchReport finishedBatch;

            while (true)
            {
                QueuedOperation op;
                int index, total;

                lock (_sync)
                {
                    if (_queue.Count == 0)
                    {
                        // 队列空了：结束本批次，交出报告，重置状态。
                        _draining = false;
                        finishedBatch = _report;
                        _report = new PluginBatchReport();
                        _batchEnqueued = 0;
                        _batchDone = 0;
                        break;
                    }

                    op = _queue.Dequeue();
                    index = _batchDone + 1;
                    total = _batchEnqueued;
                }

                try { _onProgress?.Invoke(op.DisplayName, index, total); }
                catch (Exception ex) { VPetLLMUtils.Logger.Log($"插件队列: 进度回调异常: {ex.Message}"); }

                PluginOperationResult result;
                try
                {
                    result = await op.Operation() ?? PluginOperationResult.Ok();
                }
                catch (Exception ex)
                {
                    VPetLLMUtils.Logger.Log($"插件队列: 操作 [{op.DisplayName}] 抛出异常: {ex.Message}");
                    result = PluginOperationResult.Fail(ex.Message);
                }

                lock (_sync)
                {
                    _batchDone++;
                    if (!result.Silent)
                    {
                        if (result.Success) _report.Succeeded.Add(op.DisplayName);
                        else _report.Failed.Add(new PluginOperationFailure(op.DisplayName, result.Reason));
                    }
                }

                // 唤醒等这一条的调用方（停按钮动画）。TCS 建的时候要了
                // RunContinuationsAsynchronously，所以不会在这里内联回调重入排空循环。
                op.Completion.TrySetResult(result);
            }

            if (finishedBatch.Total == 0) return;

            try
            {
                await _onBatchCompleted(finishedBatch);
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"插件队列: 批次完成回调异常: {ex.Message}");
            }
        }
    }
}
