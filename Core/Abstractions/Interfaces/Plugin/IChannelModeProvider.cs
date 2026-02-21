namespace VPetLLM.Core.Abstractions.Interfaces.Plugin
{
    /// <summary>
    /// 预留接口 - 允许插件定义自定义渠道用途模式
    /// </summary>
    public interface IChannelModeProvider : IVPetLLMPlugin
    {
        /// <summary>
        /// 获取此插件定义的自定义渠道模式列表
        /// </summary>
        IReadOnlyList<ChannelModeDefinition> GetCustomModes();
    }

    /// <summary>
    /// 自定义渠道模式定义
    /// </summary>
    public class ChannelModeDefinition
    {
        /// <summary>
        /// 唯一标识，格式 "PluginName:ModeName"
        /// </summary>
        public string ModeId { get; set; } = "";

        /// <summary>
        /// 显示名称
        /// </summary>
        public string DisplayName { get; set; } = "";

        /// <summary>
        /// 描述
        /// </summary>
        public string Description { get; set; } = "";
    }
}
