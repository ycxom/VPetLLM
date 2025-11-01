using System;
using VPetLLM.Core;

namespace VPetLLM.Utils
{
    /// <summary>
    /// 全局监听器管理器
    /// 负责根据插件状态启用或禁用全局监听功能
    /// </summary>
    public class GlobalListenerManager
    {
        private readonly VPetLLM _plugin;
        private bool _isEnabled = false;

        public GlobalListenerManager(VPetLLM plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        }

        /// <summary>
        /// 启用全局监听功能
        /// </summary>
        public void Enable()
        {
            if (_isEnabled) return;

            try
            {
                Logger.Log("启用全局监听功能");

                // 启用购买监听
                EnableBuyListener();

                // 启用触摸交互监听
                EnableTouchInteractionListener();

                _isEnabled = true;
            }
            catch (Exception ex)
            {
                Logger.Log($"启用全局监听时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 禁用全局监听功能
        /// </summary>
        public void Disable()
        {
            if (!_isEnabled) return;

            try
            {
                Logger.Log("禁用全局监听功能");

                // 禁用购买监听
                DisableBuyListener();

                // 禁用触摸交互监听
                DisableTouchInteractionListener();

                _isEnabled = false;
            }
            catch (Exception ex)
            {
                Logger.Log($"禁用全局监听时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 启用购买监听功能
        /// </summary>
        private void EnableBuyListener()
        {
            try
            {
                // TODO: 实现购买监听启用逻辑
                // 根据实际的VPetLLM架构来启用购买监听
                Logger.Log("已启用购买监听功能");
            }
            catch (Exception ex)
            {
                Logger.Log($"启用购买监听时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 禁用购买监听功能
        /// </summary>
        private void DisableBuyListener()
        {
            try
            {
                // TODO: 实现购买监听禁用逻辑
                // 根据实际的VPetLLM架构来禁用购买监听
                Logger.Log("已禁用购买监听功能");
            }
            catch (Exception ex)
            {
                Logger.Log($"禁用购买监听时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 启用触摸交互监听功能
        /// </summary>
        private void EnableTouchInteractionListener()
        {
            try
            {
                // 恢复设置中的触摸反馈
                if (_plugin?.Settings?.TouchFeedback != null)
                {
                    _plugin.Settings.TouchFeedback.EnableTouchFeedback = true;
                    Logger.Log("已启用触摸反馈设置");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"启用触摸交互监听时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 禁用触摸交互监听功能
        /// </summary>
        private void DisableTouchInteractionListener()
        {
            try
            {
                // 禁用设置中的触摸反馈
                if (_plugin?.Settings?.TouchFeedback != null)
                {
                    _plugin.Settings.TouchFeedback.EnableTouchFeedback = false;
                    Logger.Log("已禁用触摸反馈设置");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"禁用触摸交互监听时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取当前状态
        /// </summary>
        public bool IsEnabled => _isEnabled;
    }
}
