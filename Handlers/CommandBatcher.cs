using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VPetLLM.Utils;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// 命令批处理器 - 将短时间内到达的多个命令合并为批次处理
    /// 用于优化流式传输模式下的命令处理性能
    /// </summary>
    public class CommandBatcher : IDisposable
    {
        private readonly int _batchWindowMs;
        private readonly Action<List<string>> _onBatchReady;
        private readonly List<string> _pendingCommands = new List<string>();
        private readonly object _lock = new object();
        private Timer _batchTimer;
        private bool _isDisposed = false;
        private DateTime _batchStartTime;
        private bool _timerScheduled = false;
        
        /// <summary>
        /// 创建命令批处理器
        /// </summary>
        /// <param name="batchWindowMs">批处理窗口时间（毫秒），默认100ms</param>
        /// <param name="onBatchReady">批次准备好时的回调</param>
        public CommandBatcher(int batchWindowMs, Action<List<string>> onBatchReady)
        {
            if (batchWindowMs <= 0)
                throw new ArgumentException("批处理窗口时间必须大于0", nameof(batchWindowMs));
            if (onBatchReady == null)
                throw new ArgumentNullException(nameof(onBatchReady));
            
            _batchWindowMs = batchWindowMs;
            _onBatchReady = onBatchReady;
            
            Logger.Log($"CommandBatcher: 初始化完成，批处理窗口: {_batchWindowMs}ms");
        }
        
        /// <summary>
        /// 添加命令到批处理队列
        /// </summary>
        /// <param name="command">要添加的命令</param>
        public void AddCommand(string command)
        {
            if (string.IsNullOrEmpty(command) || _isDisposed)
                return;
            
            lock (_lock)
            {
                // 如果是第一个命令，记录批次开始时间
                if (_pendingCommands.Count == 0)
                {
                    _batchStartTime = DateTime.Now;
                }
                
                _pendingCommands.Add(command);
                
                // 如果定时器未启动，启动定时器
                if (!_timerScheduled)
                {
                    ScheduleBatchTimer();
                }
            }
        }

        /// <summary>
        /// 调度批处理定时器
        /// </summary>
        private void ScheduleBatchTimer()
        {
            _timerScheduled = true;
            
            // 使用一次性定时器
            _batchTimer?.Dispose();
            _batchTimer = new Timer(OnBatchTimerElapsed, null, _batchWindowMs, Timeout.Infinite);
        }
        
        /// <summary>
        /// 批处理定时器触发
        /// </summary>
        private void OnBatchTimerElapsed(object state)
        {
            ProcessBatch();
        }
        
        /// <summary>
        /// 处理当前批次
        /// </summary>
        private void ProcessBatch()
        {
            List<string> commandsToProcess = null;
            
            lock (_lock)
            {
                if (_pendingCommands.Count > 0)
                {
                    // 复制命令列表
                    commandsToProcess = new List<string>(_pendingCommands);
                    _pendingCommands.Clear();
                    
                    var batchDuration = (DateTime.Now - _batchStartTime).TotalMilliseconds;
                    Logger.Log($"CommandBatcher: 处理批次，命令数: {commandsToProcess.Count}, 批次时长: {batchDuration:F1}ms");
                }
                
                _timerScheduled = false;
            }
            
            // 在锁外执行回调，避免死锁
            if (commandsToProcess != null && commandsToProcess.Count > 0)
            {
                try
                {
                    _onBatchReady(commandsToProcess);
                }
                catch (Exception ex)
                {
                    Logger.Log($"CommandBatcher: 批处理回调异常: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// 强制刷新当前批次（立即处理所有待处理命令）
        /// </summary>
        public void Flush()
        {
            if (_isDisposed) return;
            
            // 停止定时器
            _batchTimer?.Dispose();
            _batchTimer = null;
            
            // 处理当前批次
            ProcessBatch();
        }
        
        /// <summary>
        /// 获取当前待处理命令数量
        /// </summary>
        public int PendingCount
        {
            get
            {
                lock (_lock)
                {
                    return _pendingCommands.Count;
                }
            }
        }
        
        /// <summary>
        /// 检查是否有待处理的命令
        /// </summary>
        public bool HasPendingCommands
        {
            get
            {
                lock (_lock)
                {
                    return _pendingCommands.Count > 0;
                }
            }
        }
        
        /// <summary>
        /// 清空待处理命令（不触发回调）
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _pendingCommands.Clear();
                _timerScheduled = false;
            }
            
            _batchTimer?.Dispose();
            _batchTimer = null;
        }
        
        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;
            
            _isDisposed = true;
            
            // 处理剩余命令
            Flush();
            
            _batchTimer?.Dispose();
            _batchTimer = null;
            
            lock (_lock)
            {
                _pendingCommands.Clear();
            }
            
            Logger.Log("CommandBatcher: 已释放");
        }
    }
}
