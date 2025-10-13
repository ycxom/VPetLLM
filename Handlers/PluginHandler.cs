using System;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core;
using VPetLLM.Utils;

namespace VPetLLM.Handlers
{
    public class PluginHandler : IActionHandler
    {
        public ActionType ActionType => ActionType.Plugin;
        public string Keyword => "plugin";
        public string Description => Utils.PromptHelper.Get("Handler_Plugin_Description", VPetLLM.Instance.Settings.PromptLanguage);

        public Task Execute(IMainWindow main)
        {
            return Task.CompletedTask;
        }

        public async Task Execute(string value, IMainWindow main)
        {
            // 若处于单条 AI 回复的执行会话中，则豁免限流（允许该回复内多次插件联合调用）
            var inMessageSession = global::VPetLLM.Utils.ExecutionContext.CurrentMessageId.Value.HasValue;

            // 非用户触发的跨消息限流：2分钟内最多5次，超限直接丢弃
            if (!inMessageSession && !RateLimiter.TryAcquire("ai-plugin", 5, TimeSpan.FromMinutes(2)))
            {
                VPetLLM.Instance.Log("PluginHandler: 插件触发频率超限（跨消息），丢弃此次调用。");
                return;
            }

            VPetLLM.Instance.Log($"PluginHandler: Received value: {value}");
            var firstParen = value.IndexOf('(');
            var lastParen = value.LastIndexOf(')');

            if (firstParen != -1 && lastParen != -1 && lastParen > firstParen)
            {
                var pluginName = value.Substring(0, firstParen).Trim();
                var arguments = value.Substring(firstParen + 1, lastParen - firstParen - 1);
                VPetLLM.Instance.Log($"PluginHandler: Parsed plugin name: {pluginName}, arguments: {arguments}");
                var plugin = VPetLLM.Instance.Plugins.Find(p => p.Name.Replace(" ", "_").ToLower() == pluginName.ToLower());
                if (plugin != null)
                {
                    if (!plugin.Enabled)
                    {
                        VPetLLM.Instance.Log($"PluginHandler: Plugin '{plugin.Name}' is disabled.");
                        return;
                    }
                    VPetLLM.Instance.Log($"PluginHandler: Found plugin: {plugin.Name}");
                    if (plugin is IActionPlugin actionPlugin)
                    {
                        var result = await actionPlugin.Function(arguments);
                        var formattedResult = $"[Plugin Result: {pluginName}] {result}";
                        VPetLLM.Instance.Log($"PluginHandler: Plugin function returned: {result}, formatted: {formattedResult}");

                        // 聚合到2秒窗口，统一回灌，避免连续触发LLM
                        ResultAggregator.Enqueue(formattedResult);
                    }
                }
                else
                {
                    VPetLLM.Instance.Log($"PluginHandler: Plugin not found: {pluginName}");
                    var availablePlugins = string.Join(", ", VPetLLM.Instance.Plugins.Where(p => p.Enabled).Select(p => p.Name));
                    var errorMessage = $"[SYSTEM] Error: Plugin '{pluginName}' not found. Available plugins are: {availablePlugins}";

                    // 错误信息也进入聚合，避免多次触发LLM
                    ResultAggregator.Enqueue(errorMessage);
                }
            }
            else
            {
                VPetLLM.Instance.Log($"PluginHandler: Parentheses not found or mismatched for value: {value}");
            }
        }

        public Task Execute(int value, IMainWindow main)
        {
            return Task.CompletedTask;
        }
        public int GetAnimationDuration(string animationName) => 0;

        /// <summary>
        /// 供插件直接调用的方法，确保插件消息经过正确的格式化
        /// </summary>
        public static void SendPluginMessage(string pluginName, string message)
        {
            if (string.IsNullOrEmpty(pluginName) || string.IsNullOrEmpty(message))
            {
                VPetLLM.Instance.Log("Plugin message skipped: plugin name or message is empty");
                return;
            }

            var formattedResult = $"[Plugin Result: {pluginName}] {message}";
            VPetLLM.Instance.Log($"Plugin message formatted: {formattedResult}");

            // 使用聚合器确保消息正确处理
            ResultAggregator.Enqueue(formattedResult);
        }
    }
}