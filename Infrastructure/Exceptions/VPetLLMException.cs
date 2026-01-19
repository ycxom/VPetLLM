namespace VPetLLM.Infrastructure.Exceptions
{
    /// <summary>
    /// VPetLLM基础异常类
    /// </summary>
    public abstract class VPetLLMException : Exception
    {
        /// <summary>
        /// 错误代码
        /// </summary>
        public string ErrorCode { get; }

        /// <summary>
        /// 异常上下文信息
        /// </summary>
        public Dictionary<string, object> Context { get; }

        protected VPetLLMException(string errorCode, string message, Exception innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
            Context = new Dictionary<string, object>();
        }

        /// <summary>
        /// 添加上下文信息
        /// </summary>
        public void AddContext(string key, object value)
        {
            Context[key] = value;
        }

        /// <summary>
        /// 获取完整的错误信息
        /// </summary>
        public virtual string GetFullErrorMessage()
        {
            var message = $"[{ErrorCode}] {Message}";

            if (Context.Count > 0)
            {
                message += "\nContext:";
                foreach (var kvp in Context)
                {
                    message += $"\n  {kvp.Key}: {kvp.Value}";
                }
            }

            if (InnerException != null)
            {
                message += $"\nInner Exception: {InnerException.Message}";
            }

            return message;
        }
    }

    /// <summary>
    /// 服务异常
    /// </summary>
    public class ServiceException : VPetLLMException
    {
        public string ServiceName { get; }

        public ServiceException(string serviceName, string message, Exception innerException = null)
            : base("SERVICE_ERROR", message, innerException)
        {
            ServiceName = serviceName;
            Context["ServiceName"] = serviceName;
        }
    }

    /// <summary>
    /// 配置异常
    /// </summary>
    public class ConfigurationException : VPetLLMException
    {
        public string ConfigurationName { get; }

        public ConfigurationException(string configurationName, string message, Exception innerException = null)
            : base("CONFIG_ERROR", message, innerException)
        {
            ConfigurationName = configurationName;
            Context["ConfigurationName"] = configurationName;
        }
    }

    /// <summary>
    /// 模块异常
    /// </summary>
    public class ModuleException : VPetLLMException
    {
        public string ModuleName { get; }

        public ModuleException(string moduleName, string message, Exception innerException = null)
            : base("MODULE_ERROR", message, innerException)
        {
            ModuleName = moduleName;
            Context["ModuleName"] = moduleName;
        }
    }

    /// <summary>
    /// 初始化异常
    /// </summary>
    public class InitializationException : VPetLLMException
    {
        public string ComponentName { get; }

        public InitializationException(string componentName, string message, Exception innerException = null)
            : base("INIT_ERROR", message, innerException)
        {
            ComponentName = componentName;
            Context["ComponentName"] = componentName;
        }
    }

    /// <summary>
    /// 验证异常
    /// </summary>
    public class ValidationException : VPetLLMException
    {
        public List<string> ValidationErrors { get; }

        public ValidationException(List<string> validationErrors)
            : base("VALIDATION_ERROR", $"Validation failed with {validationErrors.Count} errors")
        {
            ValidationErrors = validationErrors;
            Context["ValidationErrors"] = validationErrors;
        }

        public ValidationException(string validationError)
            : this(new List<string> { validationError })
        {
        }
    }
}