namespace VPetLLM.Handlers.Actions
{
    /// <summary>
    /// Handler 执行结果的标准化类型
    /// </summary>
    public class HandlerResult
    {
        /// <summary>
        /// 执行是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 结果消息
        /// </summary>
        public string? Message { get; set; }

        /// <summary>
        /// 附加数据
        /// </summary>
        public object? Data { get; set; }

        /// <summary>
        /// 执行过程中的异常（如果有）
        /// </summary>
        public Exception? Error { get; set; }

        /// <summary>
        /// 创建成功结果
        /// </summary>
        /// <param name="message">可选的成功消息</param>
        /// <param name="data">可选的附加数据</param>
        /// <returns>成功的 HandlerResult</returns>
        public static HandlerResult Ok(string? message = null, object? data = null)
        {
            return new HandlerResult
            {
                Success = true,
                Message = message,
                Data = data,
                Error = null
            };
        }

        /// <summary>
        /// 创建失败结果
        /// </summary>
        /// <param name="message">错误消息</param>
        /// <param name="error">可选的异常对象</param>
        /// <returns>失败的 HandlerResult</returns>
        public static HandlerResult Fail(string message, Exception? error = null)
        {
            return new HandlerResult
            {
                Success = false,
                Message = message,
                Data = null,
                Error = error
            };
        }

        /// <summary>
        /// 从异常创建失败结果
        /// </summary>
        /// <param name="error">异常对象</param>
        /// <returns>失败的 HandlerResult</returns>
        public static HandlerResult FromException(Exception error)
        {
            return new HandlerResult
            {
                Success = false,
                Message = error.Message,
                Data = null,
                Error = error
            };
        }

        /// <summary>
        /// 隐式转换为 bool，方便条件判断
        /// </summary>
        public static implicit operator bool(HandlerResult result) => result.Success;

        /// <summary>
        /// 获取结果的字符串表示
        /// </summary>
        public override string ToString()
        {
            if (Success)
            {
                return $"Success: {Message ?? "OK"}";
            }
            return $"Failed: {Message ?? Error?.Message ?? "Unknown error"}";
        }
    }

    /// <summary>
    /// 带有强类型数据的 Handler 执行结果
    /// </summary>
    /// <typeparam name="T">数据类型</typeparam>
    public class HandlerResult<T> : HandlerResult
    {
        /// <summary>
        /// 强类型的结果数据
        /// </summary>
        public new T? Data
        {
            get => (T?)base.Data;
            set => base.Data = value;
        }

        /// <summary>
        /// 创建成功结果
        /// </summary>
        /// <param name="data">结果数据</param>
        /// <param name="message">可选的成功消息</param>
        /// <returns>成功的 HandlerResult</returns>
        public static HandlerResult<T> Ok(T data, string? message = null)
        {
            return new HandlerResult<T>
            {
                Success = true,
                Message = message,
                Data = data,
                Error = null
            };
        }

        /// <summary>
        /// 创建失败结果
        /// </summary>
        /// <param name="message">错误消息</param>
        /// <param name="error">可选的异常对象</param>
        /// <returns>失败的 HandlerResult</returns>
        public new static HandlerResult<T> Fail(string message, Exception? error = null)
        {
            return new HandlerResult<T>
            {
                Success = false,
                Message = message,
                Data = default,
                Error = error
            };
        }
    }
}
