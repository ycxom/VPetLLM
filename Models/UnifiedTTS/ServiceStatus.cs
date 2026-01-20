namespace VPetLLM.Models
{
    /// <summary>
    /// 服务状态信息
    /// </summary>
    public class ServiceStatus
    {
        /// <summary>
        /// 服务是否可用
        /// </summary>
        public bool IsAvailable { get; set; }

        /// <summary>
        /// 当前使用的 TTS 类型
        /// </summary>
        public TTSType CurrentType { get; set; }

        /// <summary>
        /// 当前活跃的请求数量
        /// </summary>
        public int ActiveRequests { get; set; }

        /// <summary>
        /// 最后健康检查时间
        /// </summary>
        public DateTime LastHealthCheck { get; set; }

        /// <summary>
        /// 服务启动时间
        /// </summary>
        public DateTime ServiceStartTime { get; set; }

        /// <summary>
        /// 总处理请求数
        /// </summary>
        public long TotalProcessedRequests { get; set; }

        /// <summary>
        /// 成功处理请求数
        /// </summary>
        public long SuccessfulRequests { get; set; }

        /// <summary>
        /// 失败请求数
        /// </summary>
        public long FailedRequests { get; set; }

        /// <summary>
        /// 平均响应时间（毫秒）
        /// </summary>
        public double AverageResponseTimeMs { get; set; }

        /// <summary>
        /// 当前状态消息
        /// </summary>
        public string StatusMessage { get; set; }

        /// <summary>
        /// 错误消息（如果有）
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// 服务版本
        /// </summary>
        public string Version { get; set; }

        /// <summary>
        /// 配置版本
        /// </summary>
        public string ConfigurationVersion { get; set; }

        public ServiceStatus()
        {
            LastHealthCheck = DateTime.UtcNow;
            ServiceStartTime = DateTime.UtcNow;
            StatusMessage = "Service initialized";
        }

        /// <summary>
        /// 获取成功率百分比
        /// </summary>
        /// <returns>成功率（0-100）</returns>
        public double GetSuccessRate()
        {
            if (TotalProcessedRequests == 0)
                return 0.0;

            return (double)SuccessfulRequests / TotalProcessedRequests * 100.0;
        }

        /// <summary>
        /// 获取服务运行时间
        /// </summary>
        /// <returns>运行时间</returns>
        public TimeSpan GetUptime()
        {
            return DateTime.UtcNow - ServiceStartTime;
        }

        /// <summary>
        /// 检查服务是否健康
        /// </summary>
        /// <returns>是否健康</returns>
        public bool IsHealthy()
        {
            return IsAvailable &&
                   string.IsNullOrEmpty(ErrorMessage) &&
                   (DateTime.UtcNow - LastHealthCheck).TotalMinutes < 5; // 5分钟内有健康检查
        }

        /// <summary>
        /// 更新健康检查时间
        /// </summary>
        public void UpdateHealthCheck()
        {
            LastHealthCheck = DateTime.UtcNow;
        }

        /// <summary>
        /// 设置错误状态
        /// </summary>
        /// <param name="errorMessage">错误消息</param>
        public void SetError(string errorMessage)
        {
            IsAvailable = false;
            ErrorMessage = errorMessage;
            StatusMessage = "Service error";
            UpdateHealthCheck();
        }

        /// <summary>
        /// 清除错误状态
        /// </summary>
        public void ClearError()
        {
            IsAvailable = true;
            ErrorMessage = null;
            StatusMessage = "Service running";
            UpdateHealthCheck();
        }

        /// <summary>
        /// 增加处理请求计数
        /// </summary>
        /// <param name="success">是否成功</param>
        /// <param name="responseTimeMs">响应时间</param>
        public void IncrementRequestCount(bool success, double responseTimeMs)
        {
            TotalProcessedRequests++;

            if (success)
            {
                SuccessfulRequests++;
            }
            else
            {
                FailedRequests++;
            }

            // 更新平均响应时间（简单移动平均）
            if (TotalProcessedRequests == 1)
            {
                AverageResponseTimeMs = responseTimeMs;
            }
            else
            {
                AverageResponseTimeMs = (AverageResponseTimeMs * (TotalProcessedRequests - 1) + responseTimeMs) / TotalProcessedRequests;
            }
        }

        /// <summary>
        /// 创建状态的快照
        /// </summary>
        /// <returns>状态快照</returns>
        public ServiceStatus CreateSnapshot()
        {
            return new ServiceStatus
            {
                IsAvailable = IsAvailable,
                CurrentType = CurrentType,
                ActiveRequests = ActiveRequests,
                LastHealthCheck = LastHealthCheck,
                ServiceStartTime = ServiceStartTime,
                TotalProcessedRequests = TotalProcessedRequests,
                SuccessfulRequests = SuccessfulRequests,
                FailedRequests = FailedRequests,
                AverageResponseTimeMs = AverageResponseTimeMs,
                StatusMessage = StatusMessage,
                ErrorMessage = ErrorMessage,
                Version = Version,
                ConfigurationVersion = ConfigurationVersion
            };
        }
    }
}