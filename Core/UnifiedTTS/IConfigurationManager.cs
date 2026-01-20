using VPetLLM.Models;

namespace VPetLLM.Core.UnifiedTTS
{
    /// <summary>
    /// 配置管理器接口
    /// 提供 TTS 配置的管理功能
    /// </summary>
    public interface IConfigurationManager : IDisposable
    {
        /// <summary>
        /// 配置变更事件
        /// </summary>
        event EventHandler<ConfigurationChangedEventArgs> ConfigurationChanged;

        /// <summary>
        /// 获取当前配置
        /// </summary>
        /// <returns>当前的 TTS 配置</returns>
        TTSConfiguration GetCurrentConfiguration();

        /// <summary>
        /// 更新配置
        /// </summary>
        /// <param name="config">新的配置对象</param>
        /// <returns>是否更新成功</returns>
        Task<bool> UpdateConfigurationAsync(TTSConfiguration config);

        /// <summary>
        /// 验证配置
        /// </summary>
        /// <param name="config">要验证的配置</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateConfiguration(TTSConfiguration config);

        /// <summary>
        /// 重新加载配置
        /// </summary>
        /// <returns>是否重新加载成功</returns>
        Task<bool> ReloadConfigurationAsync();

        /// <summary>
        /// 保存配置到持久化存储
        /// </summary>
        /// <param name="config">要保存的配置</param>
        /// <returns>是否保存成功</returns>
        Task<bool> SaveConfigurationAsync(TTSConfiguration config);
    }

    /// <summary>
    /// 配置变更事件参数
    /// </summary>
    public class ConfigurationChangedEventArgs : EventArgs
    {
        /// <summary>
        /// 旧配置
        /// </summary>
        public TTSConfiguration OldConfiguration { get; }

        /// <summary>
        /// 新配置
        /// </summary>
        public TTSConfiguration NewConfiguration { get; }

        /// <summary>
        /// 变更原因
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// 变更时间
        /// </summary>
        public DateTime ChangeTime { get; }

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="oldConfiguration">旧配置</param>
        /// <param name="newConfiguration">新配置</param>
        /// <param name="reason">变更原因</param>
        public ConfigurationChangedEventArgs(TTSConfiguration oldConfiguration, TTSConfiguration newConfiguration, string reason)
        {
            OldConfiguration = oldConfiguration;
            NewConfiguration = newConfiguration;
            Reason = reason ?? "Unknown";
            ChangeTime = DateTime.UtcNow;
        }
    }
}