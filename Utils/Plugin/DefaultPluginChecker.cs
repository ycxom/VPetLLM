using System.Windows;
using VPetLLM.Utils.Localization;

namespace VPetLLM.Utils.Plugin
{
    /// <summary>
    /// 窗口标题常量
    /// 支持多语言
    /// </summary>
    public static class WindowTitleConstants
    {
        /// <summary>
        /// 获取基础标题（支持多语言）
        /// </summary>
        public static string GetBaseTitle()
        {
            return LocalizationService.Instance["WindowTitle.BaseTitle"] ?? "VPetLLM";
        }

        /// <summary>
        /// 获取设置窗口后缀（支持多语言）
        /// </summary>
        public static string GetSettingsSuffix()
        {
            return LocalizationService.Instance["WindowTitle.SettingsSuffix"] ?? " 设置";
        }

        /// <summary>
        /// 获取非默认插件模式的横幅（支持多语言）
        /// </summary>
        public static string GetNonDefaultPluginBanner()
        {
            return LocalizationService.Instance["WindowTitle.NonDefaultPluginBanner"] ?? "（当前没有将VPetLLM设置为默认聊天接口，功能已停用）";
        }

        /// <summary>
        /// 获取默认模式的完整标题
        /// </summary>
        public static string GetDefaultModeTitle()
        {
            return GetBaseTitle() + GetSettingsSuffix();
        }

        /// <summary>
        /// 获取非默认模式的完整标题
        /// </summary>
        public static string GetNonDefaultModeTitle()
        {
            return GetBaseTitle() + GetSettingsSuffix() + GetNonDefaultPluginBanner();
        }
    }

    /// <summary>
    /// 默认插件状态检查器
    /// 核心功能：检测VPetLLM是否为默认插件，并更新窗口标题
    /// </summary>
    public class DefaultPluginChecker
    {
        private readonly VPetLLM _plugin;
        private bool _isDefaultPlugin = false;
        private DateTime _lastCheckTime = DateTime.MinValue;

        public DefaultPluginChecker(VPetLLM plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        }

        /// <summary>
        /// 检查VPetLLM是否为默认插件
        /// </summary>
        public bool IsVPetLLMDefaultPlugin()
        {
            try
            {
                // 缓存检查结果（10秒内不重复检查）
                if (DateTime.Now - _lastCheckTime < TimeSpan.FromSeconds(10))
                {
                    return _isDefaultPlugin;
                }

                _lastCheckTime = DateTime.Now;

                // 检查DIY模式和API选择
                bool isDIYMode = CheckDIYMode();
                bool isVPetLLMSelected = CheckVPetLLMSelected();

                bool newState = isDIYMode && isVPetLLMSelected;

                // 状态变化时更新标题和日志
                if (newState != _isDefaultPlugin)
                {
                    _isDefaultPlugin = newState;
                    Logger.Log($"默认插件状态变更: {newState}");
                    UpdateWindowTitle();
                }

                return _isDefaultPlugin;
            }
            catch (Exception ex)
            {
                Logger.Log($"默认插件检查出错: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 检查是否启用DIY模式
        /// </summary>
        private bool CheckDIYMode()
        {
            try
            {
                var cGPTConfig = _plugin.MW?.Set?["CGPT"];
                if (cGPTConfig is null) return false;

                var typeValue = cGPTConfig[(LinePutScript.gstr)"type"] as string;
                return typeValue == "DIY";
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 检查是否选择了VPetLLM
        /// </summary>
        private bool CheckVPetLLMSelected()
        {
            try
            {
                // 方法1：直接从配置中读取DIY设置值（最可靠）
                // 这避免了依赖TalkAPIIndex可能未正确更新的问题
                var cGPTConfig = _plugin.MW?.Set?["CGPT"];
                if (cGPTConfig is not null)
                {
                    var diyValue = cGPTConfig[(LinePutScript.gstr)"DIY"] as string;
                    if (diyValue == "VPetLLM")
                    {
                        return true;
                    }
                }

                // 方法2：通过TalkBoxCurr检查（作为备用）
                // TalkBoxCurr依赖于TalkAPIIndex，可能在某些情况下不准确
                var talkBoxCurr = _plugin.MW?.TalkBoxCurr;
                if (talkBoxCurr is not null)
                {
                    var apiNameProperty = talkBoxCurr.GetType().GetProperty("APIName");
                    if (apiNameProperty is not null)
                    {
                        var currentApiName = apiNameProperty.GetValue(talkBoxCurr) as string;
                        return currentApiName == "VPetLLM";
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 更新窗口标题
        /// </summary>
        private void UpdateWindowTitle()
        {
            try
            {
                if (_plugin?.SettingWindow is not null)
                {
                    _plugin.SettingWindow.Dispatcher.Invoke(() =>
                    {
                        var window = _plugin.SettingWindow as Window;
                        if (window is not null && window.IsLoaded)
                        {
                            window.Title = _plugin.SettingWindow.WindowTitle;
                            Logger.Log($"窗口标题已更新: {window.Title}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"更新窗口标题出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 手动刷新窗口标题
        /// </summary>
        public void RefreshWindowTitle()
        {
            UpdateWindowTitle();
        }

        /// <summary>
        /// 强制重新检查
        /// </summary>
        public bool ForceRecheck()
        {
            _lastCheckTime = DateTime.MinValue;
            return IsVPetLLMDefaultPlugin();
        }
    }
}
