namespace VPetLLM.Configuration
{
    /// <summary>
    /// 配置管理器接口
    /// </summary>
    public interface ISettingsManager
    {
        /// <summary>
        /// 获取指定类型的配置
        /// </summary>
        /// <typeparam name="T">配置类型</typeparam>
        /// <returns>配置实例</returns>
        T GetSettings<T>() where T : class, ISettings;

        /// <summary>
        /// 保存指定类型的配置
        /// </summary>
        /// <typeparam name="T">配置类型</typeparam>
        /// <param name="settings">配置实例</param>
        void SaveSettings<T>(T settings) where T : class, ISettings;

        /// <summary>
        /// 重新加载所有配置
        /// </summary>
        void ReloadSettings();

        /// <summary>
        /// 验证所有配置
        /// </summary>
        /// <returns>验证结果</returns>
        SettingsValidationResult ValidateAll();

        /// <summary>
        /// 配置变更事件
        /// </summary>
        event EventHandler<SettingsChangedEventArgs>? SettingsChanged;
    }

    /// <summary>
    /// 配置变更事件参数
    /// </summary>
    public class SettingsChangedEventArgs : EventArgs
    {
        /// <summary>
        /// 变更的配置类型
        /// </summary>
        public Type SettingsType { get; }

        /// <summary>
        /// 变更的配置实例
        /// </summary>
        public ISettings Settings { get; }

        public SettingsChangedEventArgs(Type settingsType, ISettings settings)
        {
            SettingsType = settingsType;
            Settings = settings;
        }
    }
}
