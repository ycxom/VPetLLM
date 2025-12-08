using System;
using System.Threading;
using System.Threading.Tasks;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;

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

        private IMainWindow _mainWindow;
        private CancellationTokenSource _processingCts;
        private Task _processingTask;
        private bool _isProcessing = false;
        private bool _disposed = false;
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
            if (_initialized)
            {
                Logger.Log("AnimationCoordinator: Already initialized");
                return;
            }

            _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
            _initialized = true;

            // 启动队列处理
            StartProcessing();

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

            if (request == null)
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
            if (_processingTask != null && !_processingTask.IsCompleted)
            {
                return;
            }

            _processingCts = new CancellationTokenSource();
            _processingTask = Task.Run(() => ProcessQueueAsync(_processingCts.Token));
            Logger.Log("AnimationCoordinator: Queue processing started");
        }

        /// <summary>
        /// 停止队列处理
        /// </summary>
        private void StopProcessing()
        {
            _processingCts?.Cancel();
            Logger.Log("AnimationCoordinator: Queue processing stopped");
        }

        /// <summary>
        /// 队列处理循环
        /// </summary>
        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
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

                    // 检查用户交互
                    if (_synchronizer.CurrentState.IsUserInteracting)
                    {
                        await Task.Delay(100, cancellationToken);
                        continue;
                    }

                    // 出队并处理
                    var request = _queue.Dequeue();
                    if (request != null)
                    {
                        _isProcessing = true;
                        await ProcessRequestAsync(request);
                        _isProcessing = false;
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
                    await Task.Delay(100, cancellationToken);
                }
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
                }
                else
                {
                    Logger.Log($"AnimationCoordinator: Request {request.Id.Substring(0, 8)} failed");
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
            if (graph == null) return false;

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

            if (targetGraph == null)
            {
                Logger.Log($"AnimationCoordinator: Target animation not found");
                return false;
            }

            // 等待动画就绪
            var startTime = DateTime.Now;
            while (!targetGraph.IsReady)
            {
                if (targetGraph.IsFail)
                {
                    Logger.Log($"AnimationCoordinator: Animation failed to load: {targetGraph.FailMessage}");
                    return false;
                }

                if ((DateTime.Now - startTime).TotalMilliseconds > request.TimeoutMs)
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
        /// 执行回退动画
        /// </summary>
        private async Task ExecuteFallbackAsync()
        {
            var tcs = new TaskCompletionSource<bool>();

            _synchronizer.ExecuteOnUIThread(() =>
            {
                try
                {
                    _mainWindow?.Main?.DisplayToNomal();
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

        public void Dispose()
        {
            if (!_disposed)
            {
                StopProcessing();
                _processingCts?.Dispose();
                _synchronizer?.Dispose();
                _disposed = true;
                Logger.Log("AnimationCoordinator: Disposed");
            }
        }
    }
}
