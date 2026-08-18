namespace VPetLLM.Handlers.TTS
{
    /// <summary>
    /// TTS请求序列化器，确保TTS请求按顺序处理，避免播放冲突
    /// 解决VPetLLM与VPetTTS协作时的播放顺序问题
    /// </summary>
    public class TTSRequestSerializer
    {
        private readonly object _lockObject = new object();
        private readonly Queue<TTSRequest> _requestQueue = new Queue<TTSRequest>();
        private volatile bool _isProcessing = false;
        private TTSRequest _currentRequest = null;
        private SmartMessageProcessor _smartMessageProcessor = null;
        private readonly TTSOperationTracker _operationTracker;

        public TTSRequestSerializer()
        {
            _operationTracker = new TTSOperationTracker();
            Logger.Log("TTSRequestSerializer: 初始化完成，操作跟踪器已启用");
        }

        /// <summary>
        /// TTS请求信息
        /// </summary>
        public class TTSRequest
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            public string Text { get; set; }
            public string ActionContent { get; set; }
            public DateTime RequestTime { get; set; } = DateTime.Now;
            public TaskCompletionSource<bool> CompletionSource { get; set; } = new TaskCompletionSource<bool>();
        }

        /// <summary>
        /// 处理TTS请求（异步，线程安全）
        /// </summary>
        /// <param name="text">TTS文本内容</param>
        /// <param name="actionContent">动作内容</param>
        /// <returns>处理是否成功</returns>
        public async Task<bool> ProcessTTSRequestAsync(string text, string actionContent)
        {
            var request = new TTSRequest { Text = text, ActionContent = actionContent };

            // 入队和"由我来排空"必须在同一把锁里定下。
            // 分开做的话有一个窗口：排空循环刚看到队列空、还没把 _isProcessing 置回 false 时，
            // 新请求入队并读到 _isProcessing 仍为 true，于是不启动新的排空；紧接着排空循环退出
            // 并置 false —— 这个请求就永远躺在队列里，调用方 await 一个不会完成的任务，
            // 表现为偶发某一句既不出声也不出字。
            bool startDrain;
            lock (_lockObject)
            {
                _requestQueue.Enqueue(request);
                startDrain = !_isProcessing;
                if (startDrain)
                {
                    _isProcessing = true;
                }
                Logger.Log($"TTSRequestSerializer: 请求已入队 {request.Id}, 队列长度: {_requestQueue.Count}");
            }

            if (startDrain)
            {
                _ = Task.Run(ProcessQueueAsync);
            }

            return await request.CompletionSource.Task;
        }

        /// <summary>
        /// 处理队列中的请求（私有方法）。
        ///
        /// 调用方（<see cref="ProcessTTSRequestAsync"/>）已经在入队的同一把锁里把
        /// <c>_isProcessing</c> 置为 true，这里不再自行抢占；退出时只在"看到队列已空"的
        /// 那把锁里置回 false，保证"队列非空 ⟹ 有排空任务在跑或即将跑"这个不变式成立。
        /// </summary>
        private async Task ProcessQueueAsync()
        {
            try
            {
                while (true)
                {
                    TTSRequest request;

                    lock (_lockObject)
                    {
                        // 中断：排队等着说的话不用再说了。必须逐个把 CompletionSource 结掉，
                        // 否则提交这些请求的调用方会一直 await 一个永远不会完成的任务
                        if (InterruptManager.IsInterrupted)
                        {
                            var dropped = _requestQueue.Count;
                            while (_requestQueue.Count > 0)
                            {
                                _requestQueue.Dequeue().CompletionSource.TrySetResult(false);
                            }
                            _currentRequest = null;
                            if (dropped > 0)
                                Logger.Log($"TTSRequestSerializer: 已中断，丢弃 {dropped} 个排队请求");

                            _isProcessing = false;
                            return;
                        }

                        if (_requestQueue.Count == 0)
                        {
                            _isProcessing = false;
                            return;
                        }

                        request = _requestQueue.Dequeue();
                        _currentRequest = request;
                    }

                    Logger.Log($"TTSRequestSerializer: 开始处理请求 {request.Id}");
                    var startTime = DateTime.Now;

                    try
                    {
                        // 执行实际的TTS处理
                        await ProcessSingleRequestAsync(request);

                        var duration = (DateTime.Now - startTime).TotalMilliseconds;
                        Logger.Log($"TTSRequestSerializer: 请求 {request.Id} 处理完成，耗时: {duration}ms");

                        request.CompletionSource.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"TTSRequestSerializer: 请求 {request.Id} 处理失败: {ex.Message}");
                        request.CompletionSource.TrySetResult(false);
                    }
                    finally
                    {
                        _currentRequest = null;
                    }
                }
            }
            catch (Exception ex)
            {
                // 循环体本身出意外（锁外的日志、Task.Run 调度等）也必须交还排空权，
                // 否则 _isProcessing 永远留在 true，后续请求再也没有人来排空
                Logger.Log($"TTSRequestSerializer: 队列排空异常终止: {ex.Message}");

                lock (_lockObject)
                {
                    _currentRequest = null;
                    _isProcessing = false;
                }
            }
        }

        /// <summary>
        /// 处理单个TTS请求
        /// 集成现有的TTS处理逻辑，确保与VPetTTS的正确协调
        /// 支持独占会话中的请求ID提交和等待（通过 VPetTTSIntegrationManager）
        /// </summary>
        /// <param name="request">TTS请求</param>
        private async Task ProcessSingleRequestAsync(TTSRequest request)
        {
            Logger.Log($"TTSRequestSerializer: 处理请求 {request.Id} - 文本: {request.Text}");

            // 开始跟踪操作
            _operationTracker.StartOperation(request.Id, request.Text);

            var plugin = VPetLLM.Instance;
            var vpetTTSIntegration = _smartMessageProcessor?.GetVPetTTSIntegration();
            var useExclusiveSession = vpetTTSIntegration?.IsInExclusiveSession() ?? false;

            string ttsRequestId = null;

            try
            {
                // 如果在独占会话中，提交TTS请求并获取请求ID
                if (useExclusiveSession && vpetTTSIntegration != null)
                {
                    Logger.Log($"TTSRequestSerializer: 在独占会话中提交TTS请求");

                    // 提交本身会先走一遍预加载（命中批量预加载的缓存则立即返回），
                    // 所以这里不再单独预加载
                    ttsRequestId = await vpetTTSIntegration.SubmitTTSRequestAsync(request.Text);
                    Logger.Log($"TTSRequestSerializer: TTS请求已提交，请求ID: {ttsRequestId}");
                }

                // 等语音真正出声，再放气泡 —— 这是气泡/语音同步的关键一步。
                // 提交只是把请求排进 VPetTTS 的队列，音频可能还在合成、或在等前一句播完；
                // 在这里显示气泡，字就会比声音早几百毫秒到几秒。
                var playbackStarted = await WaitForPlaybackStartAsync(vpetTTSIntegration, ttsRequestId);

                // 标记播放开始
                _operationTracker.MarkPlaybackStart(request.Id);

                // 执行动作指令（显示气泡等）
                // 气泡自身的打字速度和停留时长一律沿用宿主默认（按字数计时），不做干预。
                // 起播已经等到了的话，这段路上的"错峰"延迟要全部让开：那些延迟本来是为了
                // 摊平低配机器上的瞬时压力，但放在这里就是纯粹让字晚于声音。
                if (playbackStarted)
                {
                    using (BubbleDelayController.BeginAudioAlignedScope())
                    {
                        await ExecuteActionAsync(request.ActionContent);
                    }
                }
                else
                {
                    await ExecuteActionAsync(request.ActionContent);
                }

                // 等待TTS完成
                if (useExclusiveSession && !string.IsNullOrEmpty(ttsRequestId) && vpetTTSIntegration != null)
                {
                    // 在独占会话中，等待请求完成
                    Logger.Log($"TTSRequestSerializer: 等待TTS请求完成，请求ID: {ttsRequestId}");
                    var timeout = Configuration.TTSCoordinationSettings.Instance.RequestCompleteTimeoutMs / 1000; // 转换为秒
                    var completed = await vpetTTSIntegration.WaitForRequestCompleteAsync(ttsRequestId, timeout);

                    if (completed)
                    {
                        Logger.Log($"TTSRequestSerializer: TTS请求完成，请求ID: {ttsRequestId}");
                    }
                    else
                    {
                        Logger.Log($"TTSRequestSerializer: TTS请求等待超时，请求ID: {ttsRequestId}");
                    }
                }
                else
                {
                    // 非独占会话，使用传统等待方式
                    await WaitForExternalTTSCompleteAsync(request.Text);
                }

                // 标记操作成功完成
                _operationTracker.CompleteOperation(request.Id, true);

                Logger.Log($"TTSRequestSerializer: 请求 {request.Id} 处理完成");
            }
            catch (Exception ex)
            {
                // 标记操作失败
                _operationTracker.CompleteOperation(request.Id, false, ex.Message);

                Logger.Log($"TTSRequestSerializer: 请求 {request.Id} 处理异常: {ex.Message}");
                throw; // 重新抛出异常，让调用方处理
            }
        }

        /// <summary>
        /// 等语音真正出声，之后调用方才把气泡放出来。
        /// 返回是否确实等到了起播信号。
        ///
        /// 等不到不算异常，一律照常显示气泡：不在独占会话、对方是旧版插件、
        /// 合成失败、被中断、或等超时了 —— 没出声也得出字，否则用户面对的是
        /// 一只既不说话也不显示的桌宠。等到与否只影响起点是否对齐，
        /// 气泡自身的节奏始终沿用宿主默认。
        /// </summary>
        private async Task<bool> WaitForPlaybackStartAsync(
            VPetTTSIntegrationManager vpetTTSIntegration, string ttsRequestId)
        {
            if (vpetTTSIntegration is null || string.IsNullOrEmpty(ttsRequestId))
            {
                return false;
            }

            if (!TTSCoordinationSettings.Instance.EnablePlaybackStartSync)
            {
                Logger.Log("TTSRequestSerializer: 起播同步已关闭，沿用旧时序（气泡可能早于语音）");
                return false;
            }

            var timeoutMs = TTSCoordinationSettings.Instance.PlaybackStartTimeoutMs;
            var waitStart = DateTime.Now;

            var durationMs = await vpetTTSIntegration.WaitForPlaybackStartAsync(ttsRequestId, timeoutMs);
            var waited = (int)(DateTime.Now - waitStart).TotalMilliseconds;

            // durationMs 只用于记日志：它能直接告诉你旧时序下气泡早出来了多久
            Logger.Log(durationMs >= 0
                ? $"TTSRequestSerializer: 语音已起播（等待 {waited}ms，音频时长 {durationMs}ms），现在显示气泡"
                : $"TTSRequestSerializer: 等待 {waited}ms 未检测到起播，直接显示气泡");

            return durationMs >= 0;
        }

        /// <summary>
        /// 执行动作指令（集成SmartMessageProcessor的逻辑）
        /// </summary>
        /// <param name="actionContent">动作内容</param>
        private async Task ExecuteActionAsync(string actionContent)
        {
            Logger.Log($"TTSRequestSerializer: 执行动作指令: {actionContent}");

            try
            {
                // 通过SmartMessageProcessor实例执行动作
                if (_smartMessageProcessor is not null)
                {
                    await _smartMessageProcessor.ExecuteActionInternalAsync(actionContent);
                }
                else
                {
                    Logger.Log($"TTSRequestSerializer: SmartMessageProcessor未设置，跳过动作执行");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSRequestSerializer: 动作执行失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 等待外置TTS播放完成（集成SmartMessageProcessor的逻辑）
        /// </summary>
        /// <param name="text">TTS文本</param>
        private async Task WaitForExternalTTSCompleteAsync(string text)
        {
            try
            {
                Logger.Log("TTSRequestSerializer: 开始等待外置TTS播放完成...");

                // 通过SmartMessageProcessor实例等待外置TTS
                // 注意：会话跟踪已在 SmartMessageProcessor.WaitForExternalTTSCompleteAsync 中实现
                if (_smartMessageProcessor is not null)
                {
                    await _smartMessageProcessor.WaitForExternalTTSInternalAsync(text);
                }
                else
                {
                    Logger.Log($"TTSRequestSerializer: SmartMessageProcessor未设置，使用默认等待");
                    await Task.Delay(2000); // 默认等待时间
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSRequestSerializer: 等待外置TTS失败: {ex.Message}");
                // 最后的回退：固定等待时间
                await Task.Delay(2000);
            }
        }

        /// <summary>
        /// 获取当前处理状态
        /// </summary>
        public bool IsProcessing => _isProcessing;

        /// <summary>
        /// 获取队列长度
        /// </summary>
        public int QueueLength
        {
            get
            {
                lock (_lockObject)
                {
                    return _requestQueue.Count;
                }
            }
        }

        /// <summary>
        /// 获取当前处理的请求信息
        /// </summary>
        public TTSRequest CurrentRequest => _currentRequest;

        /// <summary>
        /// 设置SmartMessageProcessor引用，用于执行动作和等待外置TTS
        /// </summary>
        /// <param name="processor">SmartMessageProcessor实例</param>
        public void SetSmartMessageProcessor(SmartMessageProcessor processor)
        {
            _smartMessageProcessor = processor;
            Logger.Log("TTSRequestSerializer: SmartMessageProcessor引用已设置");
        }

        /// <summary>
        /// 获取操作跟踪器
        /// </summary>
        public TTSOperationTracker OperationTracker => _operationTracker;

        /// <summary>
        /// 生成性能报告
        /// </summary>
        /// <returns>TTS性能报告</returns>
        public TTSPerformanceReport GeneratePerformanceReport()
        {
            return _operationTracker.GenerateReport();
        }

        /// <summary>
        /// 清理旧的操作记录
        /// </summary>
        public void CleanupOldRecords()
        {
            var maxAge = TimeSpan.FromHours(TTSCoordinationSettings.Instance.MaxRecordRetentionHours);
            _operationTracker.CleanupOldRecords(maxAge);
        }
    }
}