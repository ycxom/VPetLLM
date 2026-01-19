namespace VPetLLM.Infrastructure.Events
{
    /// <summary>
    /// 配置变更事件（泛型）
    /// </summary>
    /// <typeparam name="T">配置类型</typeparam>
    public class ConfigurationChangedEvent<T> where T : class
    {
        /// <summary>
        /// 旧配置
        /// </summary>
        public T OldConfiguration { get; set; }

        /// <summary>
        /// 新配置
        /// </summary>
        public T NewConfiguration { get; set; }

        /// <summary>
        /// 变更时间
        /// </summary>
        public DateTime ChangedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 变更原因
        /// </summary>
        public string Reason { get; set; }

        /// <summary>
        /// 变更的属性列表
        /// </summary>
        public string[] ChangedProperties { get; set; }
    }

    /// <summary>
    /// 配置加载事件
    /// </summary>
    public class ConfigurationLoadedEvent
    {
        /// <summary>
        /// 配置名称
        /// </summary>
        public string ConfigurationName { get; set; }

        /// <summary>
        /// 加载时间
        /// </summary>
        public DateTime LoadedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 错误消息（如果失败）
        /// </summary>
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// 配置保存事件
    /// </summary>
    public class ConfigurationSavedEvent
    {
        /// <summary>
        /// 配置名称
        /// </summary>
        public string ConfigurationName { get; set; }

        /// <summary>
        /// 保存时间
        /// </summary>
        public DateTime SavedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 错误消息（如果失败）
        /// </summary>
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// 配置验证事件
    /// </summary>
    public class ConfigurationValidatedEvent
    {
        /// <summary>
        /// 配置名称
        /// </summary>
        public string ConfigurationName { get; set; }

        /// <summary>
        /// 验证时间
        /// </summary>
        public DateTime ValidatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 是否有效
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// 验证错误列表
        /// </summary>
        public string[] ValidationErrors { get; set; }
    }
}
