using SystemWindows = System.Windows;

namespace VPetLLM.Utils.UI
{
    /// <summary>
    /// 直接气泡管理器
    /// 提供更直接和高效的气泡显示功能，直接调用VPet原生API
    /// 支持延迟控制以减少瞬时性能压力
    /// </summary>
    public static class DirectBubbleManager
    {
        private static bool _delayControlEnabled = false;
        
        /// <summary>
        /// 启用延迟控制（推荐在低性能设备上启用）
        /// </summary>
        /// <param name="enable">是否启用延迟控制</param>
        public static async Task EnableDelayControlAsync(bool enable = true)
        {
            _delayControlEnabled = enable;
            if (enable)
            {
                await BubbleDelayController.InitializeAsync();
                Logger.Log("DirectBubbleManager: 已启用延迟控制（支持设备识别和缓存）");
            }
            else
            {
                Logger.Log("DirectBubbleManager: 已禁用延迟控制");
            }
        }
        
        /// <summary>
        /// 启用延迟控制（同步版本，兼容旧代码）
        /// </summary>
        /// <param name="enable">是否启用延迟控制</param>
        public static void EnableDelayControl(bool enable = true)
        {
            _delayControlEnabled = enable;
            if (enable)
            {
                // 在后台异步初始化
                _ = Task.Run(async () => await BubbleDelayController.InitializeAsync());
                Logger.Log("DirectBubbleManager: 已启用延迟控制（后台初始化中）");
            }
            else
            {
                Logger.Log("DirectBubbleManager: 已禁用延迟控制");
            }
        }
        
        /// <summary>
        /// 显示气泡（异步版本 - 直接调用VPet API）
        /// </summary>
        /// <param name="plugin">VPetLLM插件实例</param>
        /// <param name="text">要显示的文本</param>
        /// <param name="animation">动画名称（可选）</param>
        /// <returns>是否成功显示</returns>
        public static async Task<bool> ShowBubbleAsync(VPetLLM plugin, string text, string animation = null)
        {
            if (plugin is null || string.IsNullOrEmpty(text))
            {
                return false;
            }

            try
            {
                // 如果启用了延迟控制，使用带延迟的显示方法
                if (_delayControlEnabled)
                {
                    return await BubbleDelayController.ShowBubbleWithDelay(plugin, text, animation);
                }
                
                // 原有的直接显示逻辑
                await SystemWindows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(animation))
                        {
                            plugin.MW.Main.Say(text, animation, true);
                        }
                        else
                        {
                            plugin.MW.Main.Say(text, null, false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"DirectBubbleManager: 直接API调用失败: {ex.Message}");
                        throw;
                    }
                });

                Logger.Log($"DirectBubbleManager: 直接显示气泡成功 - 文本长度: {text.Length}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"DirectBubbleManager: 显示气泡失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 显示气泡（同步版本 - 直接调用VPet API）
        /// </summary>
        /// <param name="plugin">VPetLLM插件实例</param>
        /// <param name="text">要显示的文本</param>
        /// <param name="animation">动画名称（可选）</param>
        /// <returns>是否成功显示</returns>
        /// <summary>
        /// 显示气泡（同步版本 - 直接调用VPet API）
        /// </summary>
        /// <param name="plugin">VPetLLM插件实例</param>
        /// <param name="text">要显示的文本</param>
        /// <param name="animation">动画名称（可选）</param>
        /// <returns>是否成功显示</returns>
        public static bool ShowBubble(VPetLLM plugin, string text, string animation = null)
        {
            if (plugin is null || string.IsNullOrEmpty(text))
            {
                return false;
            }

            try
            {
                // 添加UI操作延迟，减少瞬时性能压力
                if (_delayControlEnabled)
                {
                    BubbleDelayController.ApplyUIDelay();
                }
                
                // 直接调用VPet原生API
                if (!string.IsNullOrEmpty(animation))
                {
                    plugin.MW.Main.Say(text, animation, true);
                }
                else
                {
                    plugin.MW.Main.Say(text, null, false);
                }

                Logger.Log($"DirectBubbleManager: 直接显示气泡成功 - 文本长度: {text.Length}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"DirectBubbleManager: 显示气泡失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 显示思考气泡（直接使用MessageBarHelper）
        /// </summary>
        /// <param name="plugin">VPetLLM插件实例</param>
        /// <param name="thinkingText">思考文本</param>
        /// <returns>是否成功显示</returns>
        public static bool ShowThinkingBubble(VPetLLM plugin, string thinkingText)
        {
            if (plugin is null || string.IsNullOrEmpty(thinkingText))
            {
                return false;
            }

            try
            {
                // 如果启用了延迟控制，使用异步方法
                if (_delayControlEnabled)
                {
                    // 对于同步调用，我们使用 Task.Run 来避免阻塞
                    _ = Task.Run(async () => await BubbleDelayController.ShowThinkingBubbleWithDelay(plugin, thinkingText));
                    return true;
                }
                
                // 原有的直接显示逻辑
                var result = false;
                SystemWindows.Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        // 直接使用MessageBarHelper，移除UnifiedBubbleFacade抽象层
                        var msgBar = plugin.MW?.Main?.MsgBar;
                        if (msgBar is not null)
                        {
                            MessageBarHelper.ShowBubbleQuick(msgBar, thinkingText, plugin.MW.Core.Save.Name);
                            Logger.Log($"DirectBubbleManager: 直接显示思考气泡成功 - 文本长度: {thinkingText.Length}");
                            result = true;
                        }
                        else
                        {
                            Logger.Log("DirectBubbleManager: MessageBar不可用，无法显示思考气泡");
                            result = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"DirectBubbleManager: UI线程中显示思考气泡失败: {ex.Message}");
                        result = false;
                    }
                });

                return result;
            }
            catch (Exception ex)
            {
                Logger.Log($"DirectBubbleManager: 显示思考气泡失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 隐藏气泡（直接使用MessageBarHelper）
        /// </summary>
        /// <param name="plugin">VPetLLM插件实例</param>
        /// <returns>是否成功隐藏</returns>
        public static bool HideBubble(VPetLLM plugin)
        {
            if (plugin is null)
            {
                return false;
            }

            try
            {
                // 确保在UI线程中执行MessageBarHelper操作
                var result = false;
                SystemWindows.Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        // 直接使用MessageBarHelper，移除UnifiedBubbleFacade抽象层
                        var msgBar = plugin.MW?.Main?.MsgBar;
                        if (msgBar is not null)
                        {
                            MessageBarHelper.SetVisibility(msgBar, false);
                            Logger.Log("DirectBubbleManager: 直接隐藏气泡成功");
                            result = true;
                        }
                        else
                        {
                            Logger.Log("DirectBubbleManager: MessageBar不可用，无法隐藏气泡");
                            result = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"DirectBubbleManager: UI线程中隐藏气泡失败: {ex.Message}");
                        result = false;
                    }
                });

                return result;
            }
            catch (Exception ex)
            {
                Logger.Log($"DirectBubbleManager: 隐藏气泡失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 清理气泡状态（直接使用MessageBarHelper）
        /// </summary>
        /// <param name="plugin">VPetLLM插件实例</param>
        /// <returns>是否成功清理</returns>
        public static bool ClearBubbleState(VPetLLM plugin)
        {
            if (plugin is null)
            {
                return false;
            }

            try
            {
                // 确保在UI线程中执行MessageBarHelper操作
                var result = false;
                SystemWindows.Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        // 直接使用MessageBarHelper，移除UnifiedBubbleFacade抽象层
                        var msgBar = plugin.MW?.Main?.MsgBar;
                        if (msgBar is not null)
                        {
                            MessageBarHelper.ClearStreamState(msgBar);
                            Logger.Log("DirectBubbleManager: 直接清理状态成功");
                            result = true;
                        }
                        else
                        {
                            Logger.Log("DirectBubbleManager: MessageBar不可用，无法清理状态");
                            result = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"DirectBubbleManager: UI线程中清理状态失败: {ex.Message}");
                        result = false;
                    }
                });

                return result;
            }
            catch (Exception ex)
            {
                Logger.Log($"DirectBubbleManager: 清理状态失败: {ex.Message}");
                return false;
            }
        }
    }
}