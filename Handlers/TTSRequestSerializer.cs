using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VPetLLM.Utils;
using VPetLLM.Configuration;

namespace VPetLLM.Handlers
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
            
            lock (_lockObject)
            {
                _requestQueue.Enqueue(request);
                Logger.Log($"TTSRequestSerializer: 请求已入队 {request.Id}, 队列长度: {_requestQueue.Count}");
            }
            
            // 如果当前没有在处理，启动处理
            if (!_isProcessing)
            {
                _ = Task.Run(ProcessQueueAsync);
            }
            
            return await request.CompletionSource.Task;
        }
        
        /// <summary>
        /// 处理队列中的请求（私有方法）
        /// </summary>
        private async Task ProcessQueueAsync()
        {
            lock (_lockObject)
            {
                if (_isProcessing) return;
                _isProcessing = true;
            }
            
            try
            {
                while (true)
                {
                    TTSRequest request;
                    lock (_lockObject)
                    {
                        if (_requestQueue.Count == 0) break;
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
                        
                        request.CompletionSource.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"TTSRequestSerializer: 请求 {request.Id} 处理失败: {ex.Message}");
                        request.CompletionSource.SetResult(false);
                    }
                    finally
                    {
                        _currentRequest = null;
                    }
                }
            }
            finally
            {
                lock (_lockObject)
                {
                    _isProcessing = false;
                }
            }
        }
        
        /// <summary>
        /// 处理单个TTS请求
        /// 集成现有的TTS处理逻辑，确保与VPetTTS的正确协调
        /// </summary>
        /// <param name="request">TTS请求</param>
        private async Task ProcessSingleRequestAsync(TTSRequest request)
        {
            Logger.Log($"TTSRequestSerializer: 处理请求 {request.Id} - 文本: {request.Text}");
            
            // 开始跟踪操作
            _operationTracker.StartOperation(request.Id, request.Text);
            
            try
            {
                // 标记播放开始
                _operationTracker.MarkPlaybackStart(request.Id);
                
                // 执行动作指令（显示气泡等）
                await ExecuteActionAsync(request.ActionContent);
                
                // 等待外置TTS播放完成
                await WaitForExternalTTSCompleteAsync(request.Text);
                
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
        /// 执行动作指令（集成SmartMessageProcessor的逻辑）
        /// </summary>
        /// <param name="actionContent">动作内容</param>
        private async Task ExecuteActionAsync(string actionContent)
        {
            Logger.Log($"TTSRequestSerializer: 执行动作指令: {actionContent}");

            try
            {
                // 通过SmartMessageProcessor实例执行动作
                if (_smartMessageProcessor != null)
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
                if (_smartMessageProcessor != null)
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