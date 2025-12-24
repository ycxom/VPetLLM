using System;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core;
using VPetLLM.Utils;

namespace VPetLLM.Handlers
{
    public class PluginHandler : IActionHandler
    {
        public ActionType ActionType => ActionType.Plugin;
        public ActionCategory Category => ActionCategory.Unknown;
        public string Keyword => "plugin";
        public string Description => Utils.PromptHelper.Get("Handler_Plugin_Description", VPetLLM.Instance.Settings.PromptLanguage);
        
        // Store the current plugin name for new format commands
        [ThreadStatic]
        private static string _currentPluginName;

        public Task Execute(IMainWindow main)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Set the plugin name for the next Execute call (used by ActionProcessor for new format)
        /// </summary>
        public static void SetPluginName(string pluginName)
        {
            _currentPluginName = pluginName;
        }

        public async Task Execute(string value, IMainWindow main)
        {
            // 若处于单条 AI 回复的执行会话中，则豁免限流（允许该回复内多次插件联合调用）
            var inMessageSession = global::VPetLLM.Utils.ExecutionContext.CurrentMessageId.Value.HasValue;

            // 非用户触发的跨消息限流：使用配置的参数
            if (!inMessageSession)
            {
                var config = RateLimiter.GetConfig("ai-plugin");
                if (!RateLimiter.TryAcquire("ai-plugin", 5, TimeSpan.FromMinutes(2)))
                {
                    var stats = RateLimiter.GetStats("ai-plugin");
                    VPetLLM.Instance.Log($"PluginHandler: 触发熔断 - 插件调用超限（跨消息）");
                    if (VPetLLM.Instance?.Settings?.RateLimiter?.LogRateLimitEvents == true && stats != null)
                    {
                        VPetLLM.Instance.Log($"  当前: {stats.CurrentCount}/{config?.MaxCount}, 已阻止: {stats.BlockedRequests}次");
                    }
                    return;
                }
            }

            VPetLLM.Instance.Log($"PluginHandler: Received value: {value}");
            
            string pluginName = null;
            string arguments = value;
            
            // Check if we have a plugin name from new format (set by ActionProcessor)
            if (!string.IsNullOrEmpty(_currentPluginName))
            {
                pluginName = _currentPluginName;
                _currentPluginName = null; // Clear after use
                VPetLLM.Instance.Log($"PluginHandler: New format - plugin name: {pluginName}, arguments: {arguments}");
            }
            else
            {
                // Try old format: pluginName(arguments)
                var firstParen = value.IndexOf('(');
                var lastParen = value.LastIndexOf(')');

                if (firstParen != -1 && lastParen != -1 && lastParen > firstParen)
                {
                    pluginName = value.Substring(0, firstParen).Trim();
                    arguments = value.Substring(firstParen + 1, lastParen - firstParen - 1);
                    VPetLLM.Instance.Log($"PluginHandler: Old format - plugin name: {pluginName}, arguments: {arguments}");
                }
            }

            if (string.IsNullOrEmpty(pluginName))
            {
                VPetLLM.Instance.Log($"PluginHandler: Unable to determine plugin name from value: {value}");
                return;
            }

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
                    VPetLLM.Instance.Log($"PluginHandler: Plugin function returned: {result}");

                    // 只有当返回值非空时才回灌给 AI
                    if (!string.IsNullOrEmpty(result))
                    {
                        var formattedResult = $"[Plugin Result: {pluginName}] {result}";
                        VPetLLM.Instance.Log($"PluginHandler: Formatted result: {formattedResult}");
                        // 聚合到2秒窗口，统一回灌，避免连续触发LLM
                        ResultAggregator.Enqueue(formattedResult);
                    }
                    else
                    {
                        VPetLLM.Instance.Log($"PluginHandler: Plugin returned empty, skipping feedback to AI");
                    }
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