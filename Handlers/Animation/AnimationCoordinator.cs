using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers.Animation
{
    /// <summary>
    /// 动画协调器
    /// 协调所有动画请求的中央控制器，消除闪烁问题
    /// </summary>
    public class AnimationCoordinator : IDisposable
    {
        private static readonly Lazy<AnimationCoordinator> _instance =
            new Lazy<AnimationCoordinator>(() => new AnimationCoordinator());

        /// <summary>单例实例</summary>
        public static AnimationCoordinator Instance => _instance.Value;

        private readonly AnimationQueue _queue;
        private readonly AnimationSynchronizer _synchronizer;
        private readonly FlickerDetector _flickerDetector;
        private readonly TransitionController _transitionController;

        // Initialize / Shutdown 会被不同线程调用（插件加载在 UI 线程，卸载可能不在），
        // 而它们要一起改 _initialized / _mainWindow / _processingCts 三个字段。
        private readonly object _lifecycleLock = new object();

        private IMainWindow _mainWindow;
        private CancellationTokenSource _processingCts;
        private Task _processingTask;
        // GetState() 会从别的线程读它，用 volatile 保证读到的不是寄存器里的旧值。
        private volatile bool _isProcessing = false;
        private bool _initialized = false;

        private AnimationCoordinator()
        {
            _queue = new AnimationQueue();
            _synchronizer = new AnimationSynchronizer();
            _flickerDetector = new FlickerDetector();
            _transitionController = new TransitionController(_synchronizer);

            Logger.Log("AnimationCoordinator: Instance created");
        }

        /// <summary>
        /// 初始化协调器
        /// </summary>
        public void Initialize(IMainWindow mainWindow)
        {
            if (mainWindow is null) throw new ArgumentNullException(nameof(mainWindow));

            lock (_lifecycleLock)
            {
                if (_initialized)
                {
                    // 插件重载时主窗口可能换了一个。以前这里直接 return，
                    // 于是协调器一直攥着上一次那个已经死掉的窗口，动画再也发不出去。
                    if (ReferenceEquals(_mainWindow, mainWindow))
                    {
                        Logger.Log("AnimationCoordinator: Already initialized");
                        return;
                    }

                    Logger.Log("AnimationCoordinator: 主窗口已更换，先关掉旧的处理循环再重新初始化");
                    ShutdownCore();
                }

                _mainWindow = mainWindow;
                _initialized = true;

                // 启动队列处理
                StartProcessing();
            }

            Logger.Log("AnimationCoordinator: Initialized successfully");
        }

        /// <summary>
        /// 提交动画请求
        /// </summary>
        public async Task<bool> RequestAnimationAsync(AnimationRequest request)
        {
            if (!_initialized)
            {
                Logger.Log("AnimationCoordinator: Not initialized, rejecting request");
                return false;
            }

            if (request is null)
            {
                Logger.Log("AnimationCoordinator: Null request received");
                return false;
            }

            Logger.Log($"AnimationCoordinator: Received request {request}");

            // 检查闪烁风险
            if (_flickerDetector.IsFlickerRisk() && !request.Force)
            {
                var delay = _flickerDetector.GetRecommendedDelay();
                Logger.Log($"AnimationCoordinator: Flicker risk detected, delaying {delay}ms");
                await Task.Delay(delay);
            }

            // 检查是否可以执行
            if (!request.Force && !_synchronizer.CanExecuteAnimation(_mainWindow))
            {
                var reason = _synchronizer.GetBlockingReason(_mainWindow);
                Logger.Log($"AnimationCoordinator: Request blocked - {reason}");

                // 对于高优先级请求，仍然入队等待
                if (request.Priority >= AnimationPriority.High)
                {
                    _queue.Enqueue(request);
                    return true;
                }
                return false;
            }

            // 入队请求
            var enqueued = _queue.Enqueue(request);
            if (!enqueued)
            {
                Logger.Log("AnimationCoordinator: Request was coalesced or dropped");
            }

            return enqueued;
        }

        /// <summary>
        /// 取消指定来源的待处理请求
        /// </summary>
        public void CancelPendingRequests(string source)
        {
            var cancelled = _queue.CancelBySource(source);
            Logger.Log($"AnimationCoordinator: Cancelled {cancelled} requests from {source}");
        }

        /// <summary>
        /// 获取当前状态
        /// </summary>
        public AnimationCoordinatorState GetState()
        {
            return new AnimationCoordinatorState
            {
                QueueDepth = _queue.Count,
                IsProcessing = _isProcessing,
                CurrentAnimation = _synchronizer.CurrentState,
                FlickerRiskLevel = _flickerDetector.GetFlickerRiskLevel(),
                PendingRequestSources = _queue.GetPendingSources(),
                RecentRequestCount = _flickerDetector.GetRecentSwitchCount(),
                IsInitialized = _initialized
            };
        }

        /// <summary>
        /// 设置用户交互状态
        /// </summary>
        public void SetUserInteracting(bool isInteracting)
        {
            _synchronizer.SetUserInteracting(isInteracting);

            if (isInteracting)
            {
                // 用户开始交互时，清空低优先级请求
                Logger.Log("AnimationCoordinator: User interaction started, yielding control");
            }
        }

        /// <summary>
        /// 启动队列处理
        /// </summary>
        private void StartProcessing()
        {
            if (_processingTask is not null && !_processingTask.IsCompleted)
            {
                return;
            }

            // 令牌源整个交给循环自己持有并收尾。
            // 以前是 Shutdown 里 Cancel 完立刻 Dispose，而循环可能正卡在
            // Task.Delay(50, token) 上 —— 令牌源被抽走就是 ObjectDisposedException。
            var cts = new CancellationTokenSource();
            _processingCts = cts;
            _processingTask = Task.Run(() => ProcessQueueAsync(cts));
            Logger.Log("AnimationCoordinator: Queue processing started");
        }

        /// <summary>
        /// 队列处理循环
        /// </summary>
        private async Task ProcessQueueAsync(CancellationTokenSource ownCts)
        {
            var cancellationToken = ownCts.Token;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // 检查是否可以出队
                        if (!_queue.CanDequeueNow())
                        {
                            var waitTime = _queue.GetMillisecondsUntilNextDequeue();
                            if (waitTime > 0)
                            {
                                await Task.Delay(Math.Min(waitTime, 50), cancellationToken);
                            }
                            else
                            {
                                await Task.Delay(50, cancellationToken);
                            }
                            continue;
                        }

                        // A request may have been queued before VPet entered a host-controlled
                        // animation. Re-check the queue head so delayed high-priority requests
                        // cannot interrupt native movement or dragging.
                        var nextRequest = _queue.Peek();
                        var currentDisplay = _mainWindow?.Main?.DisplayType;
                        if (nextRequest is not null && !nextRequest.Force
                            && currentDisplay is not null
                            && global::VPetLLM.Core.Services.VPetMovementPolicy.IsAnimationProtected(currentDisplay.Type))
                        {
                            await Task.Delay(100, cancellationToken);
                            continue;
                        }

                        // 检查用户交互
                        if (_synchronizer.CurrentState.IsUserInteracting)
                        {
                            await Task.Delay(100, cancellationToken);
                            continue;
                        }

                        // 出队并处理
                        var request = _queue.Dequeue();
                        if (request is not null)
                        {
                            // 必须 try/finally：以前这里出任何岔子 _isProcessing 就永远卡在 true，
                            // GetState() 从此一直报「正在处理」。
                            _isProcessing = true;
                            try { await ProcessRequestAsync(request); }
                            finally { _isProcessing = false; }
                        }
                        else
                        {
                            await Task.Delay(50, cancellationToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"AnimationCoordinator: Error in processing loop: {ex.Message}");
                        try { await Task.Delay(100, cancellationToken); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }
            finally
            {
                // 循环自己收尾令牌源：Shutdown 那边只负责 Cancel，不碰它。
                _isProcessing = false;
                ownCts.Dispose();
                Logger.Log("AnimationCoordinator: 处理循环已退出");
            }
        }

        /// <summary>
        /// 处理单个请求
        /// </summary>
        private async Task ProcessRequestAsync(AnimationRequest request)
        {
            Logger.Log($"AnimationCoordinator: Processing request {request}");

            try
            {
                // 记录动画切换
                _flickerDetector.RecordSwitch();

                // 检查动画是否就绪
                if (!await WaitForAnimationReadyAsync(request))
                {
                    Logger.Log($"AnimationCoordinator: Animation not ready, falling back to default");
                    await ExecuteFallbackAsync();
                    return;
                }

                bool success;
                switch (request.Type)
                {
                    case AnimationRequestType.StateChange:
                        success = await _transitionController.ExecuteStateChangeAsync(_mainWindow, request);
                        break;

                    case AnimationRequestType.Stop:
                        success = await ExecuteStopAsync(request);
                        break;

                    case AnimationRequestType.Display:
                    case AnimationRequestType.Transition:
                    default:
                        success = await _transitionController.ExecuteTransitionAsync(_mainWindow, request);
                        break;
                }

                if (success)
                {
                    Logger.Log($"AnimationCoordinator: Request {request.Id.Substring(0, 8)} completed successfully");
                    return;
                }

                Logger.Log($"AnimationCoordinator: Request {request.Id.Substring(0, 8)} failed");

                // 失败/超时兜底。原来这里只打一行日志就走人，宠物可能就那么僵在半路。
                //
                // 只对 Single 动画兜底：B_Loop 本来就是要一直循环的，5 秒默认超时对它是常态，
                // 这时候去 DisplayToNomal 等于把用户要的循环动画掐掉。
                // A_Start 同理 —— 它后面还接着 B_Loop，不该被当成"卡住了"。
                //
                // 另外这只是第二道保险：动画收尾器里的回收动作在超时后仍然armed，
                // VPet 只要还会回调就会自己把宠物带回待机。这里管的是"回调永远不来"那种。
                if (request.Type != AnimationRequestType.Stop
                    && request.AnimatType == VPet_Simulator.Core.GraphInfo.AnimatType.Single)
                {
                    Logger.Log($"AnimationCoordinator: Single 动画未能收尾，执行兜底回收");
                    await ExecuteFallbackAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"AnimationCoordinator: Error processing request: {ex.Message}");
                await ExecuteFallbackAsync();
            }
        }

        /// <summary>
        /// 等待动画就绪
        /// </summary>
        private async Task<bool> WaitForAnimationReadyAsync(AnimationRequest request)
        {
            // 如果没有指定动画名称，直接返回 true
            if (string.IsNullOrEmpty(request.AnimationName) && !request.TargetGraphType.HasValue)
            {
                return true;
            }

            // 尝试获取动画并检查就绪状态
            var graph = _mainWindow?.Main?.Core?.Graph;
            if (graph is null) return false;

            var mode = _mainWindow.Main.Core.Save.Mode;
            IGraph targetGraph = null;

            if (!string.IsNullOrEmpty(request.AnimationName))
            {
                targetGraph = graph.FindGraph(request.AnimationName, request.AnimatType, mode);
            }
            else if (request.TargetGraphType.HasValue)
            {
                var name = graph.FindName(request.TargetGraphType.Value);
                targetGraph = graph.FindGraph(name, request.AnimatType, mode);
            }

            if (targetGraph is null)
            {
                Logger.Log($"AnimationCoordinator: Target animation not found");
                return false;
            }

            // 等待动画就绪。
            // 用 Stopwatch 而不是 DateTime.Now：墙钟会被 NTP 校时、夏令时、用户手动改表拨动，
            // 拨过去一次就可能让这里当场判超时，或者反过来永远等不到超时。
            var sw = global::System.Diagnostics.Stopwatch.StartNew();
            while (!targetGraph.IsReady)
            {
                if (targetGraph.IsFail)
                {
                    Logger.Log($"AnimationCoordinator: Animation failed to load: {targetGraph.FailMessage}");
                    return false;
                }

                if (sw.ElapsedMilliseconds > request.TimeoutMs)
                {
                    Logger.Log($"AnimationCoordinator: Timeout waiting for animation to be ready");
                    return false;
                }

                await Task.Delay(50);
            }

            return true;
        }

        /// <summary>
        /// 执行停止请求
        /// </summary>
        private async Task<bool> ExecuteStopAsync(AnimationRequest request)
        {
            var tcs = new TaskCompletionSource<bool>();

            // 推进代际：这一下就接管了显示权，之前那些动画迟到的回收动作从此自动失效。
            _synchronizer.BeginDisplay();

            _synchronizer.ExecuteOnUIThread(() =>
            {
                try
                {
                    _mainWindow.Main.DisplayToNomal();
                    _synchronizer.MarkAnimationCompleted();
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    Logger.Log($"AnimationCoordinator: Error executing stop: {ex.Message}");
                    tcs.TrySetResult(false);
                }
            });

            return await tcs.Task;
        }

        /// <summary>
        /// 执行回退动画（仅在非触摸/提起动画状态下才调用，避免打断用户交互和扰乱随机移动调度）
        /// </summary>
        private async Task ExecuteFallbackAsync()
        {
            var tcs = new TaskCompletionSource<bool>();

            _synchronizer.ExecuteOnUIThread(() =>
            {
                try
                {
                    // 这里原来是一份手写的保护清单，只认 Touch_Head / Touch_Body /
                    // Raised_Dynamic / Raised_Static / Move 五种，漏掉了全部 Switch_*、
                    // StartUP / Shutdown，也不管 Say+语音 和宿主瞬时动画。
                    // 于是动画一出错回退，就可能在 VPet 自己的过渡/启动/关机动画中间
                    // 强行 DisplayToNomal() 把它掐掉。现在统一问策略。
                    var blockReason = global::VPetLLM.Core.Services.VPetMovementPolicy
                        .GetAnimationOverrideBlockReason(_mainWindow);

                    if (blockReason is null)
                    {
                        // 同样要推进代际：我们正在接管显示权。
                        _synchronizer.BeginDisplay();
                        _mainWindow?.Main?.DisplayToNomal();
                    }
                    else
                    {
                        Logger.Log($"AnimationCoordinator: Fallback skipped - {blockReason}");
                    }
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    Logger.Log($"AnimationCoordinator: Error executing fallback: {ex.Message}");
                    tcs.TrySetResult(false);
                }
            });

            await tcs.Task;
        }

        /// <summary>
        /// 关停协调器：停处理循环、清空待处理请求、松开对主窗口的引用。
        /// 关停之后还能再 <see cref="Initialize"/> 起来（插件重载要用），
        /// 所以这里不会去 Dispose 掉 _synchronizer 里的信号量 —— 那是一次性的，
        /// 销毁了下次就再也拿不到锁。
        /// </summary>
        public void Shutdown()
        {
            lock (_lifecycleLock)
            {
                ShutdownCore();
            }
        }

        /// <summary>关停的实际执行体，调用方必须已经持有 _lifecycleLock。</summary>
        private void ShutdownCore()
        {
            if (!_initialized && _processingTask is null) return;

            _initialized = false;

            // 只 Cancel，不 Dispose —— 令牌源由处理循环在自己的 finally 里收尾。
            try { _processingCts?.Cancel(); }
            catch (ObjectDisposedException) { }
            _processingCts = null;
            _processingTask = null;

            // 待处理请求全部丢弃：它们捕获的 EndAction 可能指向已经卸载的插件代码。
            _queue.Clear();
            _flickerDetector.Reset();
            _synchronizer.ResetTransientState();

            _mainWindow = null;
            _isProcessing = false;

            Logger.Log("AnimationCoordinator: Shutdown 完成");
        }

        public void Dispose() => Shutdown();
    }
}
