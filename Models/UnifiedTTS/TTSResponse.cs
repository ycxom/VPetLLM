namespace VPetLLM.Models
{
    /// <summary>
    /// TTS 响应对象
    /// 包含处理结果和相关信息
    /// </summary>
    public class TTSResponse
    {
        /// <summary>
        /// 是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 音频数据
        /// </summary>
        public byte[] AudioData { get; set; }

        /// <summary>
        /// 错误消息
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// 请求标识符
        /// </summary>
        public string RequestId { get; set; }

        /// <summary>
        /// 处理时间
        /// </summary>
        public TimeSpan ProcessingTime { get; set; }

        /// <summary>
        /// 音频文件路径（如果保存到文件）
        /// </summary>
        public string AudioFilePath { get; set; }

        /// <summary>
        /// 音频格式
        /// </summary>
        public string AudioFormat { get; set; }

        /// <summary>
        /// 音频时长（毫秒）
        /// </summary>
        public int AudioDurationMs { get; set; }

        /// <summary>
        /// 处理的适配器类型
        /// </summary>
        public string ProcessedByAdapter { get; set; }

        /// <summary>
        /// 响应时间戳
        /// </summary>
        public DateTime ResponseTimestamp { get; set; }

        /// <summary>
        /// 错误代码
        /// </summary>
        public string ErrorCode { get; set; }

        /// <summary>
        /// 详细错误信息
        /// </summary>
        public string ErrorDetails { get; set; }

        public TTSResponse()
        {
            ResponseTimestamp = DateTime.UtcNow;
        }

        public TTSResponse(string requestId) : this()
        {
            RequestId = requestId;
        }

        /// <summary>
        /// 创建成功响应
        /// </summary>
        /// <param name="requestId">请求ID</param>
        /// <param name="audioData">音频数据</param>
        /// <param name="processingTime">处理时间</param>
        /// <param name="adapterType">适配器类型</param>
        /// <returns>成功响应</returns>
        public static TTSResponse CreateSuccess(string requestId, byte[] audioData, TimeSpan processingTime, string adapterType)
        {
            return new TTSResponse(requestId)
            {
                Success = true,
                AudioData = audioData,
                ProcessingTime = processingTime,
                ProcessedByAdapter = adapterType
            };
        }

        /// <summary>
        /// 创建错误响应
        /// </summary>
        /// <param name="requestId">请求ID</param>
        /// <param name="errorCode">错误代码</param>
        /// <param name="errorMessage">错误消息</param>
        /// <param name="errorDetails">详细错误信息</param>
        /// <returns>错误响应</returns>
        public static TTSResponse CreateError(string requestId, string errorCode, string errorMessage, string errorDetails = null)
        {
            return new TTSResponse(requestId)
            {
                Success = false,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                ErrorDetails = errorDetails
            };
        }

        /// <summary>
        /// 创建超时响应
        /// </summary>
        /// <param name="requestId">请求ID</param>
        /// <param name="timeoutMs">超时时间</param>
        /// <returns>超时响应</returns>
        public static TTSResponse CreateTimeout(string requestId, int timeoutMs)
        {
            return new TTSResponse(requestId)
            {
                Success = false,
                ErrorCode = TTSErrorCodes.Timeout,
                ErrorMessage = $"Request timed out after {timeoutMs} milliseconds"
            };
        }

        /// <summary>
        /// 检查响应是否包含有效的音频数据
        /// </summary>
        /// <returns>是否有有效音频数据</returns>
        public bool HasValidAudioData()
        {
            return Success && (AudioData?.Length > 0 || !string.IsNullOrEmpty(AudioFilePath));
        }

        /// <summary>
        /// 获取音频数据大小（字节）
        /// </summary>
        /// <returns>音频数据大小</returns>
        public int GetAudioDataSize()
        {
            return AudioData?.Length ?? 0;
        }

        /// <summary>
        /// 获取处理时间（毫秒）
        /// </summary>
        /// <returns>处理时间毫秒数</returns>
        public double GetProcessingTimeMs()
        {
            return ProcessingTime.TotalMilliseconds;
        }
    }
}