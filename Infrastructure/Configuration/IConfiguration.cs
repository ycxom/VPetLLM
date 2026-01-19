using VPetLLM.Configuration;

namespace VPetLLM.Infrastructure.Configuration
{
    /// <summary>
    /// 配置接口
    /// </summary>
    public interface IConfiguration : ISettings
    {
        /// <summary>
        /// 配置名称
        /// </summary>
        string ConfigurationName { get; }

        /// <summary>
        /// 配置版本
        /// </summary>
        Version Version { get; }

        /// <summary>
        /// 最后修改时间
        /// </summary>
        DateTime LastModified { get; set; }

        /// <summary>
        /// 配置是否已修改
        /// </summary>
        bool IsModified { get; set; }

        /// <summary>
        /// 克隆配置
        /// </summary>
        IConfiguration Clone();

        /// <summary>
        /// 合并配置
        /// </summary>
        void Merge(IConfiguration other);

        /// <summary>
        /// 重置为默认值
        /// </summary>
        void ResetToDefaults();
    }

    /// <summary>
    /// 配置管理器接口
    /// </summary>
    public interface IConfigurationManager : IDisposable
    {
        /// <summary>
        /// 获取配置
        /// </summary>
        T GetConfiguration<T>() where T : class, IConfiguration, new();

        /// <summary>
        /// 保存配置
        /// </summary>
        Task SaveConfigurationAsync<T>(T configuration) where T : class, IConfiguration;

        /// <summary>
        /// 保存所有配置
        /// </summary>
        Task SaveAllAsync();

        /// <summary>
        /// 重新加载配置
        /// </summary>
        Task ReloadConfigurationAsync<T>() where T : class, IConfiguration, new();

        /// <summary>
        /// 重新加载所有配置
        /// </summary>
        Task ReloadAllConfigurationsAsync();

        /// <summary>
        /// 检查配置是否存在
        /// </summary>
        bool ConfigurationExists<T>() where T : class, IConfiguration;

        /// <summary>
        /// 获取所有配置类型
        /// </summary>
        IEnumerable<Type> GetConfigurationTypes();

        /// <summary>
        /// 启用配置热重载
        /// </summary>
        void EnableHotReload<T>() where T : class, IConfiguration, new();

        /// <summary>
        /// 禁用配置热重载
        /// </summary>
        void DisableHotReload<T>() where T : class, IConfiguration;

        /// <summary>
        /// 配置变更事件
        /// </summary>
        event EventHandler<ConfigurationChangedEventArgs> ConfigurationChanged;

        /// <summary>
        /// 配置加载事件
        /// </summary>
        event EventHandler<ConfigurationLoadedEventArgs> ConfigurationLoaded;

        /// <summary>
        /// 配置保存事件
        /// </summary>
        event EventHandler<ConfigurationSavedEventArgs> ConfigurationSaved;
    }

    /// <summary>
    /// 配置变更事件参数
    /// </summary>
    public class ConfigurationChangedEventArgs : EventArgs
    {
        /// <summary>
        /// 配置类型
        /// </summary>
        public Type ConfigurationType { get; }

        /// <summary>
        /// 配置名称
        /// </summary>
        public string ConfigurationName { get; }

        /// <summary>
        /// 旧配置
        /// </summary>
        public IConfiguration OldConfiguration { get; }

        /// <summary>
        /// 新配置
        /// </summary>
        public IConfiguration NewConfiguration { get; }

        /// <summary>
        /// 变更时间
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// 变更原因
        /// </summary>
        public ConfigurationChangeReason Reason { get; }

        public ConfigurationChangedEventArgs(Type configurationType, string configurationName,
            IConfiguration oldConfiguration, IConfiguration newConfiguration, ConfigurationChangeReason reason)
        {
            ConfigurationType = configurationType;
            ConfigurationName = configurationName;
            OldConfiguration = oldConfiguration;
            NewConfiguration = newConfiguration;
            Timestamp = DateTime.UtcNow;
            Reason = reason;
        }
    }

    /// <summary>
    /// 配置加载事件参数
    /// </summary>
    public class ConfigurationLoadedEventArgs : EventArgs
    {
        /// <summary>
        /// 配置类型
        /// </summary>
        public Type ConfigurationType { get; }

        /// <summary>
        /// 配置名称
        /// </summary>
        public string ConfigurationName { get; }

        /// <summary>
        /// 配置实例
        /// </summary>
        public IConfiguration Configuration { get; }

        /// <summary>
        /// 加载时间
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// 是否从文件加载
        /// </summary>
        public bool LoadedFromFile { get; }

        public ConfigurationLoadedEventArgs(Type configurationType, string configurationName,
            IConfiguration configuration, bool loadedFromFile)
        {
            ConfigurationType = configurationType;
            ConfigurationName = configurationName;
            Configuration = configuration;
            Timestamp = DateTime.UtcNow;
            LoadedFromFile = loadedFromFile;
        }
    }

    /// <summary>
    /// 配置保存事件参数
    /// </summary>
    public class ConfigurationSavedEventArgs : EventArgs
    {
        /// <summary>
        /// 配置类型
        /// </summary>
        public Type ConfigurationType { get; }

        /// <summary>
        /// 配置名称
        /// </summary>
        public string ConfigurationName { get; }

        /// <summary>
        /// 配置实例
        /// </summary>
        public IConfiguration Configuration { get; }

        /// <summary>
        /// 保存时间
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// 文件路径
        /// </summary>
        public string FilePath { get; }

        public ConfigurationSavedEventArgs(Type configurationType, string configurationName,
            IConfiguration configuration, string filePath)
        {
            ConfigurationType = configurationType;
            ConfigurationName = configurationName;
            Configuration = configuration;
            Timestamp = DateTime.UtcNow;
            FilePath = filePath;
        }
    }

    /// <summary>
    /// 配置变更原因
    /// </summary>
    public enum ConfigurationChangeReason
    {
        /// <summary>
        /// 手动更新
        /// </summary>
        ManualUpdate,

        /// <summary>
        /// 文件变更
        /// </summary>
        FileChanged,

        /// <summary>
        /// 热重载
        /// </summary>
        HotReload,

        /// <summary>
        /// 系统重置
        /// </summary>
        SystemReset,

        /// <summary>
        /// 迁移
        /// </summary>
        Migration
    }

    /// <summary>
    /// 配置基础抽象类
    /// </summary>
    public abstract class ConfigurationBase : IConfiguration
    {
        private bool _isModified = false;

        /// <summary>
        /// 配置名称
        /// </summary>
        public abstract string ConfigurationName { get; }

        /// <summary>
        /// 配置版本
        /// </summary>
        public virtual Version Version => new Version(1, 0, 0);

        /// <summary>
        /// 最后修改时间
        /// </summary>
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 配置是否已修改
        /// </summary>
        public bool IsModified
        {
            get => _isModified;
            set
            {
                _isModified = value;
                if (value)
                {
                    LastModified = DateTime.UtcNow;
                }
            }
        }

        /// <summary>
        /// 验证配置
        /// </summary>
        public abstract SettingsValidationResult Validate();

        /// <summary>
        /// 克隆配置
        /// </summary>
        public abstract IConfiguration Clone();

        /// <summary>
        /// 合并配置
        /// </summary>
        public abstract void Merge(IConfiguration other);

        /// <summary>
        /// 重置为默认值
        /// </summary>
        public abstract void ResetToDefaults();

        /// <summary>
        /// 标记配置已修改
        /// </summary>
        protected void MarkAsModified()
        {
            IsModified = true;
        }

        /// <summary>
        /// 标记配置未修改
        /// </summary>
        protected void MarkAsUnmodified()
        {
            IsModified = false;
        }
    }
}