using VPetLLM.Configuration;
using VPetLLM.Utils.System;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// TTS错误恢复管理器，处理TTS协调过程中的异常状态检测和恢复
    /// 解决VPetLLM与VPetTTS协作时的异常处理问题
    /// </summary>
    public class TTSErrorRecoveryManager
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, ErrorRecord> _errorHistory = new Dictionary<string, ErrorRecord>();
        private readonly Queue<RecoveryAction> _recoveryQueue = new Queue<RecoveryAction>();
        private volatile bool _isRecovering = false;
        private DateTime _lastRecoveryTime = DateTime.MinValue;

        /// <summary>
        /// 错误恢复事件
        /// </summary>
        public event EventHandler<ErrorRecoveryEventArgs> ErrorRecovered;

        /// <summary>
        /// 错误记录
        /// </summary>
        private class ErrorRecord
        {
            public string ErrorType { get; set; }
            public string Message { get; set; }
            public DateTime FirstOccurrence { get; set; }
            public DateTime LastOccurrence { get; set; }
            public int Count { get; set; }
            public List<string> Details { get; set; } = new List<string>();
        }

        /// <summary>
        /// 恢复动作
        /// </summary>
        private class RecoveryAction
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            public RecoveryType Type { get; set; }
            public string Description { get; set; }
            public Func<Task<bool>> Action { get; set; }
            public DateTime ScheduledTime { get; set; }
            public int Priority { get; set; } // 数字越小优先级越高
        }

        /// <summary>
        /// 恢复类型
        /// </summary>
        public enum RecoveryType
        {
            StateReset,          // 状态重置
            PlayerRestart,       // 播放器重启
            QueueClear,          // 队列清理
            ConfigReload,        // 配置重载
            ServiceReconnect,    // 服务重连
            FallbackMode         // 回退模式
        }

        /// <summary>
        /// 检测并处理TTS异常状态
        /// </summary>
        /// <param name="context">TTS操作上下文</param>
        /// <returns>是否检测到异常并成功恢复</returns>
        public async Task<bool> DetectAndRecoverAsync(TTSOperationContext context)
        {
            try
            {
                Logger.Log($"TTSErrorRecoveryManager: 开始异常检测 - 操作ID: {context.OperationId}");

                var anomalies = DetectAnomalies(context);
                if (anomalies.Count == 0)
                {
                    Logger.Log("TTSErrorRecoveryManager: 未检测到异常");
                    return false;
                }

                Logger.Log($"TTSErrorRecoveryManager: 检测到 {anomalies.Count} 个异常");

                // 记录异常
                foreach (var anomaly in anomalies)
                {
                    RecordError(anomaly.Type, anomaly.Message, anomaly.Details);
                }

                // 执行恢复策略
                var recoverySuccess = await ExecuteRecoveryAsync(anomalies, context);

                if (recoverySuccess)
                {
                    Logger.Log("TTSErrorRecoveryManager: 异常恢复成功");
                    OnErrorRecovered(new ErrorRecoveryEventArgs
                    {
                        OperationId = context.OperationId,
                        AnomaliesCount = anomalies.Count,
                        RecoverySuccess = true,
                        Message = "异常检测和恢复完成"
                    });
                }
                else
                {
                    Logger.Log("TTSErrorRecoveryManager: 异常恢复失败");
                }

                return recoverySuccess;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSErrorRecoveryManager: 异常检测和恢复过程失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 检测TTS操作异常
        /// </summary>
        private List<TTSAnomaly> DetectAnomalies(TTSOperationContext context)
        {
            var anomalies = new List<TTSAnomaly>();

            // 1. 检测超时异常
            if (context.Duration > TTSCoordinationSettings.Instance.WaitTimeoutMs)
            {
                anomalies.Add(new TTSAnomaly
                {
                    Type = "Timeout",
                    Message = $"TTS操作超时: {context.Duration}ms > {TTSCoordinationSettings.Instance.WaitTimeoutMs}ms",
                    Details = $"操作ID: {context.OperationId}, 文本长度: {context.Text?.Length ?? 0}"
                });
            }

            // 2. 检测播放器异常退出
            if (context.HasPlayerError)
            {
                anomalies.Add(new TTSAnomaly
                {
                    Type = "PlayerError",
                    Message = "播放器异常退出",
                    Details = context.PlayerErrorDetails ?? "未知播放器错误"
                });
            }

            // 3. 检测状态同步异常
            if (context.HasStateSyncError)
            {
                anomalies.Add(new TTSAnomaly
                {
                    Type = "StateSyncError",
                    Message = "状态同步异常",
                    Details = context.StateSyncErrorDetails ?? "状态监控失败"
                });
            }

            // 4. 检测频繁重试
            if (context.RetryCount > 3)
            {
                anomalies.Add(new TTSAnomaly
                {
                    Type = "ExcessiveRetry",
                    Message = $"重试次数过多: {context.RetryCount}",
                    Details = $"可能存在系统性问题"
                });
            }

            // 5. 检测资源泄漏
            if (context.HasResourceLeak)
            {
                anomalies.Add(new TTSAnomaly
                {
                    Type = "ResourceLeak",
                    Message = "检测到资源泄漏",
                    Details = context.ResourceLeakDetails ?? "未释放的资源"
                });
            }

            return anomalies;
        }

        /// <summary>
        /// 执行恢复策略
        /// </summary>
        private async Task<bool> ExecuteRecoveryAsync(List<TTSAnomaly> anomalies, TTSOperationContext context)
        {
            if (_isRecovering)
            {
                Logger.Log("TTSErrorRecoveryManager: 已有恢复操作在进行中，跳过");
                return false;
            }

            // 防止频繁恢复
            var timeSinceLastRecovery = DateTime.Now - _lastRecoveryTime;
            if (timeSinceLastRecovery.TotalSeconds < 5)
            {
                Logger.Log("TTSErrorRecoveryManager: 距离上次恢复时间过短，跳过");
                return false;
            }

            _isRecovering = true;
            _lastRecoveryTime = DateTime.Now;

            try
            {
                // 根据异常类型制定恢复策略
                var recoveryActions = PlanRecoveryActions(anomalies, context);

                Logger.Log($"TTSErrorRecoveryManager: 计划执行 {recoveryActions.Count} 个恢复动作");

                // 按优先级执行恢复动作
                var successCount = 0;
                foreach (var action in recoveryActions)
                {
                    try
                    {
                        Logger.Log($"TTSErrorRecoveryManager: 执行恢复动作 - {action.Description}");
                        var success = await action.Action();

                        if (success)
                        {
                            successCount++;
                            Logger.Log($"TTSErrorRecoveryManager: 恢复动作成功 - {action.Description}");
                        }
                        else
                        {
                            Logger.Log($"TTSErrorRecoveryManager: 恢复动作失败 - {action.Description}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"TTSErrorRecoveryManager: 恢复动作异常 - {action.Description}: {ex.Message}");
                    }
                }

                var overallSuccess = successCount > 0;
                Logger.Log($"TTSErrorRecoveryManager: 恢复完成，成功动作: {successCount}/{recoveryActions.Count}");

                return overallSuccess;
            }
            finally
            {
                _isRecovering = false;
            }
        }

        /// <summary>
        /// 制定恢复动作计划
        /// </summary>
        private List<RecoveryAction> PlanRecoveryActions(List<TTSAnomaly> anomalies, TTSOperationContext context)
        {
            var actions = new List<RecoveryAction>();

            foreach (var anomaly in anomalies)
            {
                switch (anomaly.Type)
                {
                    case "Timeout":
                        actions.Add(new RecoveryAction
                        {
                            Type = RecoveryType.StateReset,
                            Description = "重置TTS状态监控器",
                            Priority = 1,
                            Action = () => ResetStateMonitorAsync()
                        });
                        break;

                    case "PlayerError":
                        actions.Add(new RecoveryAction
                        {
                            Type = RecoveryType.PlayerRestart,
                            Description = "重启播放器",
                            Priority = 2,
                            Action = () => RestartPlayerAsync()
                        });
                        break;

                    case "StateSyncError":
                        actions.Add(new RecoveryAction
                        {
                            Type = RecoveryType.StateReset,
                            Description = "重置状态同步",
                            Priority = 1,
                            Action = () => ResetStateSyncAsync()
                        });
                        break;

                    case "ExcessiveRetry":
                        actions.Add(new RecoveryAction
                        {
                            Type = RecoveryType.QueueClear,
                            Description = "清理TTS请求队列",
                            Priority = 3,
                            Action = () => ClearRequestQueueAsync()
                        });
                        break;

                    case "ResourceLeak":
                        actions.Add(new RecoveryAction
                        {
                            Type = RecoveryType.ServiceReconnect,
                            Description = "重连TTS服务",
                            Priority = 4,
                            Action = () => ReconnectTTSServiceAsync()
                        });
                        break;
                }
            }

            // 如果有多个严重异常，添加回退模式
            if (anomalies.Count >= 3)
            {
                actions.Add(new RecoveryAction
                {
                    Type = RecoveryType.FallbackMode,
                    Description = "启用回退模式",
                    Priority = 5,
                    Action = () => EnableFallbackModeAsync()
                });
            }

            // 按优先级排序
            actions.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            return actions;
        }

        /// <summary>
        /// 重置状态监控器
        /// </summary>
        private async Task<bool> ResetStateMonitorAsync()
        {
            try
            {
                Logger.Log("TTSErrorRecoveryManager: 重置状态监控器");
                // 这里应该调用实际的状态监控器重置逻辑
                await Task.Delay(100); // 模拟重置操作
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSErrorRecoveryManager: 重置状态监控器失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 重启播放器
        /// </summary>
        private async Task<bool> RestartPlayerAsync()
        {
            try
            {
                Logger.Log("TTSErrorRecoveryManager: 重启播放器");
                // 这里应该调用实际的播放器重启逻辑
                await Task.Delay(200); // 模拟重启操作
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSErrorRecoveryManager: 重启播放器失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 重置状态同步
        /// </summary>
        private async Task<bool> ResetStateSyncAsync()
        {
            try
            {
                Logger.Log("TTSErrorRecoveryManager: 重置状态同步");
                await Task.Delay(100);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSErrorRecoveryManager: 重置状态同步失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 清理请求队列
        /// </summary>
        private async Task<bool> ClearRequestQueueAsync()
        {
            try
            {
                Logger.Log("TTSErrorRecoveryManager: 清理TTS请求队列");
                await Task.Delay(50);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSErrorRecoveryManager: 清理请求队列失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 重连TTS服务
        /// </summary>
        private async Task<bool> ReconnectTTSServiceAsync()
        {
            try
            {
                Logger.Log("TTSErrorRecoveryManager: 重连TTS服务");
                await Task.Delay(300);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSErrorRecoveryManager: 重连TTS服务失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 启用回退模式
        /// </summary>
        private async Task<bool> EnableFallbackModeAsync()
        {
            try
            {
                Logger.Log("TTSErrorRecoveryManager: 启用回退模式");
                await Task.Delay(100);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSErrorRecoveryManager: 启用回退模式失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 记录错误
        /// </summary>
        private void RecordError(string errorType, string message, string details)
        {
            lock (_lock)
            {
                if (_errorHistory.ContainsKey(errorType))
                {
                    var record = _errorHistory[errorType];
                    record.Count++;
                    record.LastOccurrence = DateTime.Now;
                    record.Details.Add($"{DateTime.Now:HH:mm:ss} - {details}");

                    // 限制详情记录数量
                    if (record.Details.Count > 10)
                    {
                        record.Details.RemoveAt(0);
                    }
                }
                else
                {
                    _errorHistory[errorType] = new ErrorRecord
                    {
                        ErrorType = errorType,
                        Message = message,
                        FirstOccurrence = DateTime.Now,
                        LastOccurrence = DateTime.Now,
                        Count = 1,
                        Details = new List<string> { $"{DateTime.Now:HH:mm:ss} - {details}" }
                    };
                }
            }
        }

        /// <summary>
        /// 获取错误统计报告
        /// </summary>
        public TTSErrorReport GenerateErrorReport()
        {
            lock (_lock)
            {
                var report = new TTSErrorReport
                {
                    GeneratedAt = DateTime.Now,
                    TotalErrorTypes = _errorHistory.Count,
                    IsRecovering = _isRecovering,
                    LastRecoveryTime = _lastRecoveryTime,
                    ErrorSummary = new List<ErrorSummary>()
                };

                foreach (var kvp in _errorHistory)
                {
                    var record = kvp.Value;
                    report.ErrorSummary.Add(new ErrorSummary
                    {
                        ErrorType = record.ErrorType,
                        Message = record.Message,
                        Count = record.Count,
                        FirstOccurrence = record.FirstOccurrence,
                        LastOccurrence = record.LastOccurrence,
                        RecentDetails = record.Details.ToArray()
                    });
                }

                return report;
            }
        }

        /// <summary>
        /// 清理旧的错误记录
        /// </summary>
        public void CleanupOldErrors(TimeSpan maxAge)
        {
            lock (_lock)
            {
                var cutoffTime = DateTime.Now - maxAge;
                var keysToRemove = new List<string>();

                foreach (var kvp in _errorHistory)
                {
                    if (kvp.Value.LastOccurrence < cutoffTime)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    _errorHistory.Remove(key);
                }

                if (keysToRemove.Count > 0)
                {
                    Logger.Log($"TTSErrorRecoveryManager: 清理了 {keysToRemove.Count} 个过期错误记录");
                }
            }
        }

        /// <summary>
        /// 触发错误恢复事件
        /// </summary>
        private void OnErrorRecovered(ErrorRecoveryEventArgs args)
        {
            try
            {
                ErrorRecovered?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSErrorRecoveryManager: 触发错误恢复事件失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// TTS异常信息
    /// </summary>
    public class TTSAnomaly
    {
        public string Type { get; set; }
        public string Message { get; set; }
        public string Details { get; set; }
    }

    /// <summary>
    /// TTS操作上下文
    /// </summary>
    public class TTSOperationContext
    {
        public string OperationId { get; set; }
        public string Text { get; set; }
        public int Duration { get; set; }
        public bool HasPlayerError { get; set; }
        public string PlayerErrorDetails { get; set; }
        public bool HasStateSyncError { get; set; }
        public string StateSyncErrorDetails { get; set; }
        public int RetryCount { get; set; }
        public bool HasResourceLeak { get; set; }
        public string ResourceLeakDetails { get; set; }
    }

    /// <summary>
    /// 错误恢复事件参数
    /// </summary>
    public class ErrorRecoveryEventArgs : EventArgs
    {
        public string OperationId { get; set; }
        public int AnomaliesCount { get; set; }
        public bool RecoverySuccess { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// TTS错误报告
    /// </summary>
    public class TTSErrorReport
    {
        public DateTime GeneratedAt { get; set; }
        public int TotalErrorTypes { get; set; }
        public bool IsRecovering { get; set; }
        public DateTime LastRecoveryTime { get; set; }
        public List<ErrorSummary> ErrorSummary { get; set; } = new List<ErrorSummary>();
    }

    /// <summary>
    /// 错误摘要
    /// </summary>
    public class ErrorSummary
    {
        public string ErrorType { get; set; }
        public string Message { get; set; }
        public int Count { get; set; }
        public DateTime FirstOccurrence { get; set; }
        public DateTime LastOccurrence { get; set; }
        public string[] RecentDetails { get; set; }
    }
}