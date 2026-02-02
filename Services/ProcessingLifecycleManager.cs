using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VPetLLM.Core.Abstractions.Interfaces.Plugin;

namespace VPetLLM.Services
{
    /// <summary>
    /// 处理生命周期管理器
    /// 管理插件在消息处理不同阶段的介入
    /// </summary>
    public class ProcessingLifecycleManager
    {
        private readonly VPetLLM _plugin;
        private readonly Dictionary<IProcessingLifecyclePlugin, object?> _pluginContexts = new();

        public ProcessingLifecycleManager(VPetLLM plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        }

        /// <summary>
        /// 通知所有生命周期插件：处理开始
        /// </summary>
        public async Task NotifyProcessingStartAsync(string userInput)
        {
            _pluginContexts.Clear();

            var lifecyclePlugins = _plugin.Plugins
                .OfType<IProcessingLifecyclePlugin>()
                .Where(p => p.Enabled)
                .ToList();

            if (lifecyclePlugins.Count == 0)
            {
                return;
            }

            _plugin.Log($"ProcessingLifecycleManager: 通知 {lifecyclePlugins.Count} 个插件处理开始");

            foreach (var plugin in lifecyclePlugins)
            {
                try
                {
                    var context = await plugin.OnProcessingStartAsync(userInput);
                    _pluginContexts[plugin] = context;
                    _plugin.Log($"ProcessingLifecycleManager: 插件 {plugin.Name} 处理开始完成");
                }
                catch (Exception ex)
                {
                    _plugin.Log($"ProcessingLifecycleManager: 插件 {plugin.Name} 处理开始失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 通知所有生命周期插件：响应开始
        /// </summary>
        public async Task NotifyResponseStartAsync()
        {
            var lifecyclePlugins = _plugin.Plugins
                .OfType<IProcessingLifecyclePlugin>()
                .Where(p => p.Enabled)
                .ToList();

            if (lifecyclePlugins.Count == 0)
            {
                return;
            }

            _plugin.Log($"ProcessingLifecycleManager: 通知 {lifecyclePlugins.Count} 个插件响应开始");

            foreach (var plugin in lifecyclePlugins)
            {
                try
                {
                    var context = _pluginContexts.GetValueOrDefault(plugin);
                    await plugin.OnResponseStartAsync(context);
                    _plugin.Log($"ProcessingLifecycleManager: 插件 {plugin.Name} 响应开始完成");
                }
                catch (Exception ex)
                {
                    _plugin.Log($"ProcessingLifecycleManager: 插件 {plugin.Name} 响应开始失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 通知所有生命周期插件：处理完成
        /// </summary>
        public async Task NotifyProcessingCompleteAsync()
        {
            var lifecyclePlugins = _plugin.Plugins
                .OfType<IProcessingLifecyclePlugin>()
                .Where(p => p.Enabled)
                .ToList();

            if (lifecyclePlugins.Count == 0)
            {
                _pluginContexts.Clear();
                return;
            }

            _plugin.Log($"ProcessingLifecycleManager: 通知 {lifecyclePlugins.Count} 个插件处理完成");

            foreach (var plugin in lifecyclePlugins)
            {
                try
                {
                    var context = _pluginContexts.GetValueOrDefault(plugin);
                    await plugin.OnProcessingCompleteAsync(context);
                    _plugin.Log($"ProcessingLifecycleManager: 插件 {plugin.Name} 处理完成");
                }
                catch (Exception ex)
                {
                    _plugin.Log($"ProcessingLifecycleManager: 插件 {plugin.Name} 处理完成失败: {ex.Message}");
                }
            }

            _pluginContexts.Clear();
        }

        /// <summary>
        /// 通知所有生命周期插件：处理错误
        /// </summary>
        public async Task NotifyProcessingErrorAsync(Exception exception)
        {
            var lifecyclePlugins = _plugin.Plugins
                .OfType<IProcessingLifecyclePlugin>()
                .Where(p => p.Enabled)
                .ToList();

            if (lifecyclePlugins.Count == 0)
            {
                _pluginContexts.Clear();
                return;
            }

            _plugin.Log($"ProcessingLifecycleManager: 通知 {lifecyclePlugins.Count} 个插件处理错误");

            foreach (var plugin in lifecyclePlugins)
            {
                try
                {
                    var context = _pluginContexts.GetValueOrDefault(plugin);
                    await plugin.OnProcessingErrorAsync(context, exception);
                    _plugin.Log($"ProcessingLifecycleManager: 插件 {plugin.Name} 错误处理完成");
                }
                catch (Exception ex)
                {
                    _plugin.Log($"ProcessingLifecycleManager: 插件 {plugin.Name} 错误处理失败: {ex.Message}");
                }
            }

            _pluginContexts.Clear();
        }
    }
}
