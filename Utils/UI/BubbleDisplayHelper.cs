using VPetLLM.Utils.System;
using SystemWindows = System.Windows;

namespace VPetLLM.Utils.UI
{
    /// <summary>
    /// 简化的气泡显示助手 - 直接调用VPet原生API
    /// 移除了多层抽象，提供更直接和高效的气泡显示
    /// </summary>
    [Obsolete("BubbleDisplayHelper is deprecated. Use DirectBubbleManager instead for new implementations.")]
    public static class BubbleDisplayHelper
    {
        /// <summary>
        /// 显示气泡（简化版 - 直接调用VPet API）
        /// </summary>
        /// <param name="plugin">VPetLLM插件实例</param>
        /// <param name="text">要显示的文本</param>
        /// <param name="animation">动画名称（可选）</param>
        /// <returns>是否成功显示</returns>
        public static async Task<bool> ShowBubbleAsync(VPetLLM plugin, string text, string animation = null)
        {
            if (plugin == null || string.IsNullOrEmpty(text))
            {
                return false;
            }

            try
            {
                // 直接调用VPet原生API，移除多层抽象
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
                        Logger.Log($"BubbleDisplayHelper: 直接API调用失败: {ex.Message}");
                        throw;
                    }
                });

                Logger.Log($"BubbleDisplayHelper: 直接显示气泡成功 - 文本长度: {text.Length}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleDisplayHelper: 显示气泡失败: {ex.Message}");
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
        public static bool ShowBubble(VPetLLM plugin, string text, string animation = null)
        {
            if (plugin == null || string.IsNullOrEmpty(text))
            {
                return false;
            }

            try
            {
                // 直接调用VPet原生API，移除多层抽象
                if (!string.IsNullOrEmpty(animation))
                {
                    plugin.MW.Main.Say(text, animation, true);
                }
                else
                {
                    plugin.MW.Main.Say(text, null, false);
                }

                Logger.Log($"BubbleDisplayHelper: 直接显示气泡成功 - 文本长度: {text.Length}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleDisplayHelper: 显示气泡失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 显示思考气泡（简化版 - 直接使用MessageBarHelper）
        /// </summary>
        /// <param name="plugin">VPetLLM插件实例</param>
        /// <param name="thinkingText">思考文本</param>
        /// <returns>是否成功显示</returns>
        public static bool ShowThinkingBubble(VPetLLM plugin, string thinkingText)
        {
            if (plugin == null || string.IsNullOrEmpty(thinkingText))
            {
                return false;
            }

            try
            {
                // 直接使用MessageBarHelper，移除UnifiedBubbleFacade抽象层
                var msgBar = plugin.MW?.Main?.MsgBar;
                if (msgBar != null)
                {
                    MessageBarHelper.ShowBubbleQuick(msgBar, thinkingText, plugin.MW.Core.Save.Name);
                    Logger.Log($"BubbleDisplayHelper: 直接显示思考气泡成功 - 文本长度: {thinkingText.Length}");
                    return true;
                }

                Logger.Log("BubbleDisplayHelper: MessageBar不可用，无法显示思考气泡");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleDisplayHelper: 显示思考气泡失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 隐藏气泡（简化版 - 直接使用MessageBarHelper）
        /// </summary>
        /// <param name="plugin">VPetLLM插件实例</param>
        /// <returns>是否成功隐藏</returns>
        public static bool HideBubble(VPetLLM plugin)
        {
            if (plugin == null)
            {
                return false;
            }

            try
            {
                // 直接使用MessageBarHelper，移除UnifiedBubbleFacade抽象层
                var msgBar = plugin.MW?.Main?.MsgBar;
                if (msgBar != null)
                {
                    MessageBarHelper.SetVisibility(msgBar, false);
                    Logger.Log("BubbleDisplayHelper: 直接隐藏气泡成功");
                    return true;
                }

                Logger.Log("BubbleDisplayHelper: MessageBar不可用，无法隐藏气泡");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleDisplayHelper: 隐藏气泡失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 清理气泡状态（简化版 - 直接使用MessageBarHelper）
        /// </summary>
        /// <param name="plugin">VPetLLM插件实例</param>
        /// <returns>是否成功清理</returns>
        public static bool ClearBubbleState(VPetLLM plugin)
        {
            if (plugin == null)
            {
                return false;
            }

            try
            {
                // 直接使用MessageBarHelper，移除UnifiedBubbleFacade抽象层
                var msgBar = plugin.MW?.Main?.MsgBar;
                if (msgBar != null)
                {
                    MessageBarHelper.ClearStreamState(msgBar);
                    Logger.Log("BubbleDisplayHelper: 直接清理状态成功");
                    return true;
                }

                Logger.Log("BubbleDisplayHelper: MessageBar不可用，无法清理状态");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleDisplayHelper: 清理状态失败: {ex.Message}");
                return false;
            }
        }
    }
}