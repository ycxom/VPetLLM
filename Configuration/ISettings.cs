namespace VPetLLM.Configuration
{
    /// <summary>
    /// 配置接口，所有配置模块都应实现此接口
    /// </summary>
    public interface ISettings
    {
        /// <summary>
        /// 验证配置是否有效
        /// </summary>
        /// <returns>验证结果</returns>
        SettingsValidationResult Validate();
    }

    /// <summary>
    /// 配置验证结果
    /// </summary>
    public class SettingsValidationResult
    {
        /// <summary>
        /// 验证是否通过
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// 验证错误列表
        /// </summary>
        public List<string> Errors { get; set; } = new();

        /// <summary>
        /// 验证警告列表
        /// </summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>
        /// 创建成功的验证结果
        /// </summary>
        public static SettingsValidationResult Success()
        {
            return new SettingsValidationResult { IsValid = true };
        }

        /// <summary>
        /// 创建失败的验证结果
        /// </summary>
        /// <param name="errors">错误列表</param>
        public static SettingsValidationResult Failure(params string[] errors)
        {
            return new SettingsValidationResult
            {
                IsValid = false,
                Errors = errors.ToList()
            };
        }

        /// <summary>
        /// 添加错误
        /// </summary>
        public void AddError(string error)
        {
            Errors.Add(error);
            IsValid = false;
        }

        /// <summary>
        /// 添加警告
        /// </summary>
        public void AddWarning(string warning)
        {
            Warnings.Add(warning);
        }
    }

    /// <summary>
    /// 配置验证异常
    /// </summary>
    public class SettingsValidationException : Exception
    {
        /// <summary>
        /// 验证错误列表
        /// </summary>
        public List<string> ValidationErrors { get; }

        public SettingsValidationException(List<string> errors)
            : base($"Settings validation failed: {string.Join(", ", errors)}")
        {
            ValidationErrors = errors;
        }

        public SettingsValidationException(string error)
            : base($"Settings validation failed: {error}")
        {
            ValidationErrors = new List<string> { error };
        }
    }
}
