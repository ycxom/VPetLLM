using PluginManager = VPetLLM.Utils.Plugin.PluginManager;

namespace VPetLLM.Core.Routing
{
    /// <summary>一个可选的渠道用途：宿主内置三种，其余由已启用的插件注册。</summary>
    public sealed class ChannelPurpose
    {
        /// <summary>用途标识，与传给 IChatCore.Summarize 第三参的 purpose 一致。</summary>
        public string Key { get; init; } = "";

        /// <summary>已本地化的显示名（插件用途由插件提供，内置走宿主语言文件）。</summary>
        public string DisplayName { get; init; } = "";

        /// <summary>是否由插件注册 —— 插件停用或未安装后，这类用途不再出现在设置界面。</summary>
        public bool IsFromPlugin { get; init; }
    }

    /// <summary>
    /// 渠道用途注册表：内置 Chat / Compression / Memory，其余来自实现了
    /// <c>IChannelModeProvider</c> 的「已启用」插件（如 DiaryPlugin 的「日记」）。
    /// 插件停用或未安装时，它的用途自动从渠道编辑器和徽标里消失。
    /// </summary>
    public static class ChannelPurposeRegistry
    {
        /// <summary>内置用途的键。插件注册同名键会被忽略。</summary>
        public static readonly string[] BuiltInKeys = { "Chat", "Compression", "Memory" };

        /// <summary>
        /// 当前可用的全部用途。任何插件抛异常都不影响其它用途（设置界面不能被一个坏插件拖垮）。
        /// </summary>
        public static List<ChannelPurpose> GetPurposes(Func<string, string, string> localize)
        {
            var list = new List<ChannelPurpose>
            {
                new() { Key = "Chat", DisplayName = localize("Purpose.Chat", "聊天") },
                new() { Key = "Compression", DisplayName = localize("Purpose.Compression", "总结 / 上下文压缩") },
                new() { Key = "Memory", DisplayName = localize("Purpose.Memory", "记忆整理") }
            };
            var seen = new HashSet<string>(list.Select(p => p.Key));

            foreach (var plugin in PluginManager.Plugins)
            {
                if (!plugin.Enabled || plugin is not IChannelModeProvider provider) continue;
                IReadOnlyList<ChannelModeDefinition>? modes = null;
                try { modes = provider.GetCustomModes(); }
                catch (Exception ex)
                {
                    Logger.Log($"ChannelPurposeRegistry: 插件 {plugin.Name} 的 GetCustomModes 抛异常，忽略其用途: {ex.Message}");
                }
                if (modes is null) continue;
                foreach (var mode in modes)
                {
                    if (mode is null || string.IsNullOrWhiteSpace(mode.ModeId) || !seen.Add(mode.ModeId)) continue;
                    var key = mode.ModeId.Trim();
                    list.Add(new ChannelPurpose
                    {
                        Key = key,
                        DisplayName = string.IsNullOrWhiteSpace(mode.DisplayName) ? key : mode.DisplayName,
                        IsFromPlugin = true
                    });
                }
            }
            return list;
        }

        /// <summary>用途键的显示名：内置走本地化，插件用途问注册表；找不到（插件刚被停用/卸载）就显示原始键。</summary>
        public static string DescribeKey(string key, Func<string, string, string> localize)
        {
            var localized = key switch
            {
                "Chat" => localize("Purpose.Chat", "聊天"),
                "Compression" => localize("Purpose.Compression", "总结 / 上下文压缩"),
                "Memory" => localize("Purpose.Memory", "记忆整理"),
                _ => null
            };
            if (localized is not null) return localized;

            foreach (var plugin in PluginManager.Plugins)
            {
                if (!plugin.Enabled || plugin is not IChannelModeProvider provider) continue;
                try
                {
                    foreach (var mode in provider.GetCustomModes())
                        if (mode is not null && mode.ModeId == key && !string.IsNullOrWhiteSpace(mode.DisplayName))
                            return mode.DisplayName;
                }
                catch { /* 只是展示，取不到就落回原始键 */ }
            }
            return key;
        }
    }
}
