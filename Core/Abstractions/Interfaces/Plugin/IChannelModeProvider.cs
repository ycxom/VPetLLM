namespace VPetLLM.Core.Abstractions.Interfaces.Plugin
{
    /// <summary>
    /// 标准「渠道用途」接口 —— 插件实现它来向宿主注册自己的渠道用途（purpose）。
    /// 宿主的渠道编辑器会把注册的用途列为可选范围；插件停用或未安装时，其用途自动从界面消失。
    /// </summary>
    public interface IChannelModeProvider : IVPetLLMPlugin
    {
        /// <summary>
        /// 获取此插件定义的渠道用途列表。宿主在打开渠道设置时调用（仅对已启用的插件）。
        /// </summary>
        IReadOnlyList<ChannelModeDefinition> GetCustomModes();
    }

    /// <summary>
    /// 渠道用途定义
    /// </summary>
    public class ChannelModeDefinition
    {
        /// <summary>
        /// 用途标识，须与传给 <c>IChatCore.Summarize</c> 第三参的 purpose 字符串一致（内置的 Chat / Compression / Memory 不可占用）。
        /// </summary>
        public string ModeId { get; set; } = "";

        /// <summary>
        /// 显示名称（插件按 <c>Settings.Language</c> 自行本地化）
        /// </summary>
        public string DisplayName { get; set; } = "";

        /// <summary>
        /// 描述
        /// </summary>
        public string Description { get; set; } = "";
    }
}
