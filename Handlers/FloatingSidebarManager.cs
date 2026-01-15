using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using System.Windows;
using System.Windows.Threading;
using VPetLLM.Configuration;
using VPetLLM.UI.Controls;
using VPetLLM.Utils;
using VPetLLMPlugin.UI.Controls;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// 悬浮侧边栏管理器 - 采用 DemoClock TimeClock 的实现方式
    /// 使用 UserControl 而非独立 Window，通过 UIGrid 插入到 VPet 主界面
    /// </summary>
    public class FloatingSidebarManager : IDisposable
    {
        private readonly VPetLLM _vpetLLM;
        private FloatingSidebar? _sidebar;
        private DispatcherTimer? _settingsCheckTimer;
        private bool _isDisposed = false;
        
        // 活动会话计数器，用于跟踪是否有活动的处理
        private int _activeSessionCount = 0;
        private readonly object _sessionLock = new object();

        public bool IsVisible => _sidebar?.Visibility == Visibility.Visible;
        public FloatingSidebar? Sidebar => _sidebar;

        public FloatingSidebarManager(VPetLLM vpetLLM)
        {
            _vpetLLM = vpetLLM ?? throw new ArgumentNullException(nameof(vpetLLM));
            
            SubscribeToSettingsWindowEvents();
            Logger.Log("FloatingSidebarManager initialized (UserControl mode)");
        }

        /// <summary>
        /// 订阅设置窗口事件
        /// </summary>
        private void SubscribeToSettingsWindowEvents()
        {
            try
            {
                _settingsCheckTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                
                bool wasSettingsWindowOpen = false;
                
                _settingsCheckTimer.Tick += (s, e) =>
                {
                    try
                    {
                        bool isSettingsWindowOpen = _vpetLLM.SettingWindow?.IsVisible == true;
                        
                        if (wasSettingsWindowOpen && !isSettingsWindowOpen)
                        {
                            OnSettingsWindowClosed();
                        }
                        
                        wasSettingsWindowOpen = isSettingsWindowOpen;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error checking settings window state: {ex.Message}");
                    }
                };
                
                _settingsCheckTimer.Start();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error subscribing to settings window events: {ex.Message}");
            }
        }

        /// <summary>
        /// 设置窗口关闭时的处理
        /// </summary>
        private void OnSettingsWindowClosed()
        {
            try
            {
                Logger.Log("Settings window closed, refreshing sidebar configuration");
                
                var settings = GetFloatingSidebarSettings();
                ApplyConfiguration(settings);
                
                if (settings.IsEnabled && !IsVisible)
                {
                    Show();
                }
                else if (!settings.IsEnabled && IsVisible)
                {
                    Hide();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling settings window close: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示侧边栏
        /// </summary>
        public void Show()
        {
            try
            {
                if (_isDisposed)
                {
                    Logger.Log("Cannot show sidebar: manager is disposed");
                    return;
                }

                if (_sidebar == null)
                {
                    CreateSidebar();
                }

                if (_sidebar != null)
                {
                    _sidebar.Show();
                    RefreshButtons();
                    Logger.Log("Floating sidebar shown");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error showing sidebar: {ex.Message}");
            }
        }

        /// <summary>
        /// 隐藏侧边栏
        /// </summary>
        public void Hide()
        {
            try
            {
                _sidebar?.Hide();
                Logger.Log("Floating sidebar hidden");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error hiding sidebar: {ex.Message}");
            }
        }

        /// <summary>
        /// 创建侧边栏 UserControl
        /// </summary>
        private void CreateSidebar()
        {
            try
            {
                if (!Application.Current.Dispatcher.CheckAccess())
                {
                    Application.Current.Dispatcher.Invoke(CreateSidebar);
                    return;
                }

                CleanupSidebar();

                _sidebar = new FloatingSidebar();
                
                // 应用配置
                var settings = GetFloatingSidebarSettings();
                _sidebar.DefaultOpacity = settings.InactiveOpacity;
                _sidebar.ActiveOpacity = settings.DefaultOpacity;
                _sidebar.PlaceAutoBack = settings.AutoHide;
                _sidebar.AutoBackDelay = settings.AutoHideDelay * 1000;
                
                // 设置位置
                ApplyPositionSettings(settings);
                
                // 初始化并插入到 VPet UIGrid
                _sidebar.Initialize(_vpetLLM);
                
                // 订阅事件
                _sidebar.ButtonClicked += OnButtonClicked;
                _sidebar.SidebarClosed += OnSidebarClosed;

                Logger.Log("Sidebar UserControl created and inserted into UIGrid");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error creating sidebar: {ex.Message}");
                _sidebar = null;
            }
        }

        /// <summary>
        /// 应用位置设置
        /// </summary>
        private void ApplyPositionSettings(FloatingSidebarSettings settings)
        {
            if (_sidebar == null) return;

            // 使用 VerticalAlignment.Top 配合 Margin 来定位，避免层级切换时的位置跳动
            // 因为 UIGrid 和 UIGrid_Back 都是 VerticalAlignment="Top" 的 Grid
            switch (settings.Position)
            {
                case SidebarPosition.Left:
                    _sidebar.HorizontalAlignment = HorizontalAlignment.Left;
                    _sidebar.VerticalAlignment = VerticalAlignment.Top;
                    // 使用 Margin.Top 来实现垂直居中效果 (250 是 VPet 主界面高度 500 的一半)
                    _sidebar.Margin = new Thickness(10 + settings.CustomOffsetX, 200 + settings.CustomOffsetY, 0, 0);
                    _sidebar.SetOrientation(System.Windows.Controls.Orientation.Vertical);
                    break;
                case SidebarPosition.Right:
                    _sidebar.HorizontalAlignment = HorizontalAlignment.Right;
                    _sidebar.VerticalAlignment = VerticalAlignment.Top;
                    _sidebar.Margin = new Thickness(0, 200 + settings.CustomOffsetY, 10 + settings.CustomOffsetX, 0);
                    _sidebar.SetOrientation(System.Windows.Controls.Orientation.Vertical);
                    break;
                case SidebarPosition.Top:
                    _sidebar.HorizontalAlignment = HorizontalAlignment.Center;
                    _sidebar.VerticalAlignment = VerticalAlignment.Top;
                    _sidebar.Margin = new Thickness(settings.CustomOffsetX, 10 + settings.CustomOffsetY, 0, 0);
                    _sidebar.SetOrientation(System.Windows.Controls.Orientation.Horizontal);
                    break;
                case SidebarPosition.Bottom:
                    _sidebar.HorizontalAlignment = HorizontalAlignment.Center;
                    _sidebar.VerticalAlignment = VerticalAlignment.Top;
                    // 使用 Margin.Top 来定位到底部 (500 - 控件高度约50 - 10边距)
                    _sidebar.Margin = new Thickness(settings.CustomOffsetX, 440 + settings.CustomOffsetY, 0, 0);
                    _sidebar.SetOrientation(System.Windows.Controls.Orientation.Horizontal);
                    break;
                default:
                    _sidebar.HorizontalAlignment = HorizontalAlignment.Right;
                    _sidebar.VerticalAlignment = VerticalAlignment.Top;
                    _sidebar.Margin = new Thickness(0, 200 + settings.CustomOffsetY, 10, 0);
                    _sidebar.SetOrientation(System.Windows.Controls.Orientation.Vertical);
                    break;
            }
        }

        /// <summary>
        /// 清理侧边栏资源
        /// </summary>
        private void CleanupSidebar()
        {
            try
            {
                if (_sidebar != null)
                {
                    _sidebar.ButtonClicked -= OnButtonClicked;
                    _sidebar.SidebarClosed -= OnSidebarClosed;
                    _sidebar.Dispose();
                    _sidebar = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error cleaning up sidebar: {ex.Message}");
            }
        }

        /// <summary>
        /// 刷新按钮
        /// </summary>
        public void RefreshButtons()
        {
            try
            {
                if (_sidebar == null) return;

                var settings = GetFloatingSidebarSettings();
                var enabledButtons = settings.EnabledButtons ?? SidebarButton.GetDefaultButtons().Select(b => b.ButtonId).ToList();

                // 清除现有按钮
                var currentButtons = _sidebar.GetButtonIds();
                foreach (var buttonId in currentButtons)
                {
                    _sidebar.RemoveButton(buttonId);
                }

                // 添加启用的按钮
                var allButtons = SidebarButton.GetDefaultButtons();
                var buttonsToAdd = allButtons
                    .Where(b => enabledButtons.Contains(b.ButtonId))
                    .OrderBy(b => settings.ButtonOrder?.ContainsKey(b.ButtonId) == true ? settings.ButtonOrder[b.ButtonId] : b.Order)
                    .ToList();

                foreach (var button in buttonsToAdd)
                {
                    if (button.ButtonId == "asr")
                    {
                        bool asrAvailable = IsASRAvailable();
                        button.IsEnabled = asrAvailable;
                        if (!asrAvailable)
                        {
                            button.ToolTip = LocalizationService.Instance["FloatingSidebar.VoiceInputDisabled"] 
                                ?? "语音输入未启用，请在设置中配置ASR";
                        }
                    }
                    else if (button.ButtonId == "screenshot")
                    {
                        bool screenshotAvailable = IsScreenshotAvailable();
                        button.IsEnabled = screenshotAvailable;
                        if (!screenshotAvailable)
                        {
                            button.ToolTip = LocalizationService.Instance["FloatingSidebar.ScreenshotDisabled"] 
                                ?? "截图功能未启用，请在设置中配置截图";
                        }
                    }
                    _sidebar.AddButton(button);
                }

                Logger.Log($"Refreshed sidebar buttons: {string.Join(", ", buttonsToAdd.Select(b => b.ButtonId))}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error refreshing buttons: {ex.Message}");
            }
        }
        /// <summary>
        /// 更新VPetLLM状态灯
        /// </summary>
        /// <param name="status">新状态</param>
        public void UpdateStatus(VPetLLMStatus status)
        {
            try
            {
                if (_sidebar != null && !_isDisposed)
                {
                    _sidebar.UpdateStatusLight(status);
                    Logger.Log($"VPetLLM status updated to: {status}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error updating VPetLLM status: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取当前状态
        /// </summary>
        /// <returns>当前VPetLLM状态</returns>
        public VPetLLMStatus GetCurrentStatus()
        {
            try
            {
                return _sidebar?.GetCurrentStatus() ?? VPetLLMStatus.Idle;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error getting current status: {ex.Message}");
                return VPetLLMStatus.Idle;
            }
        }

        /// <summary>
        /// 示例：设置处理请求状态
        /// 在VPetLLM开始处理用户请求时调用
        /// </summary>
        public void SetProcessingStatus()
        {
            UpdateStatus(VPetLLMStatus.Processing);
        }

        /// <summary>
        /// 示例：设置输出响应状态
        /// 在VPetLLM开始输出响应时调用
        /// </summary>
        public void SetOutputtingStatus()
        {
            UpdateStatus(VPetLLMStatus.Outputting);
        }

        /// <summary>
        /// 示例：设置错误状态
        /// 在VPetLLM遇到错误时调用
        /// </summary>
        public void SetErrorStatus()
        {
            UpdateStatus(VPetLLMStatus.Error);
        }

        /// <summary>
        /// 设置插件执行状态
        /// 在VPetLLM执行插件时调用，显示蓝色状态灯
        /// </summary>
        public void SetPluginExecutingStatus()
        {
            Logger.Log("FloatingSidebarManager: SetPluginExecutingStatus called");
            UpdateStatus(VPetLLMStatus.PluginExecuting);
        }

        /// <summary>
        /// 开始一个活动会话
        /// 在开始处理用户输入或AI回复时调用
        /// </summary>
        /// <param name="source">调用源（可选），用于调试</param>
        public void BeginActiveSession(string source = null)
        {
            lock (_sessionLock)
            {
                _activeSessionCount++;
                var sourceInfo = string.IsNullOrEmpty(source) ? "" : $", source = {source}";
                Logger.Log($"FloatingSidebarManager: BeginActiveSession, count = {_activeSessionCount}{sourceInfo}");
            }
        }

        /// <summary>
        /// 结束一个活动会话
        /// 在处理完成时调用，如果没有其他活动会话则设置Idle状态
        /// </summary>
        /// <param name="source">调用源（可选），用于调试</param>
        public void EndActiveSession(string source = null)
        {
            bool shouldSetIdle = false;
            lock (_sessionLock)
            {
                if (_activeSessionCount > 0)
                {
                    _activeSessionCount--;
                }
                shouldSetIdle = _activeSessionCount == 0;
                var sourceInfo = string.IsNullOrEmpty(source) ? "" : $", source = {source}";
                Logger.Log($"FloatingSidebarManager: EndActiveSession, count = {_activeSessionCount}, shouldSetIdle = {shouldSetIdle}{sourceInfo}");
            }
            
            if (shouldSetIdle)
            {
                UpdateStatus(VPetLLMStatus.Idle);
            }
        }

        /// <summary>
        /// 获取当前活动会话数
        /// </summary>
        public int ActiveSessionCount
        {
            get
            {
                lock (_sessionLock)
                {
                    return _activeSessionCount;
                }
            }
        }

        /// <summary>
        /// 示例：设置待机状态
        /// 在VPetLLM完成操作或空闲时调用
        /// 注意：优先使用 EndActiveSession() 来管理状态，此方法会强制设置Idle
        /// </summary>
        public void SetIdleStatus()
        {
            // 重置活动会话计数器
            lock (_sessionLock)
            {
                _activeSessionCount = 0;
            }
            UpdateStatus(VPetLLMStatus.Idle);
        }

        /// <summary>
        /// 测试状态灯功能 - 循环显示所有状态
        /// </summary>
        public void TestStatusLight()
        {
            try
            {
                Logger.Log("Starting status light test...");
                
                // 使用定时器循环显示不同状态
                var testTimer = new System.Timers.Timer(2000); // 每2秒切换一次
                var statusIndex = 0;
                var statuses = new[] { VPetLLMStatus.Processing, VPetLLMStatus.Outputting, VPetLLMStatus.Error, VPetLLMStatus.Idle };
                
                testTimer.Elapsed += (s, e) =>
                {
                    try
                    {
                        var status = statuses[statusIndex % statuses.Length];
                        UpdateStatus(status);
                        Logger.Log($"Test: Status changed to {status}");
                        statusIndex++;
                        
                        // 测试完成后停止
                        if (statusIndex >= statuses.Length * 2)
                        {
                            testTimer.Stop();
                            testTimer.Dispose();
                            Logger.Log("Status light test completed");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error in status light test: {ex.Message}");
                    }
                };
                
                testTimer.Start();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error starting status light test: {ex.Message}");
            }
        }

        /// <summary>
        /// 应用配置
        /// </summary>
        public void ApplyConfiguration(FloatingSidebarSettings settings)
        {
            try
            {
                if (_sidebar == null) return;

                _sidebar.DefaultOpacity = settings.InactiveOpacity;
                _sidebar.ActiveOpacity = settings.DefaultOpacity;
                _sidebar.PlaceAutoBack = settings.AutoHide;
                _sidebar.AutoBackDelay = settings.AutoHideDelay * 1000;
                
                ApplyPositionSettings(settings);
                RefreshButtons();
                
                // 如果禁用了自动隐藏，确保控件在前层并且可点击
                if (!settings.AutoHide)
                {
                    _sidebar.BringToFront();
                }

                Logger.Log("Applied sidebar configuration");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error applying configuration: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查ASR是否可用
        /// </summary>
        private bool IsASRAvailable()
        {
            try
            {
                return _vpetLLM.Settings?.ASR?.IsEnabled == true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 检查截图功能是否可用
        /// </summary>
        private bool IsScreenshotAvailable()
        {
            try
            {
                return _vpetLLM.Settings?.Screenshot?.IsEnabled == true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取悬浮侧边栏设置
        /// </summary>
        private FloatingSidebarSettings GetFloatingSidebarSettings()
        {
            try
            {
                var settings = _vpetLLM.Settings?.FloatingSidebar;
                
                if (settings == null)
                {
                    Logger.Log("FloatingSidebar settings is null, creating default settings");
                    return new FloatingSidebarSettings();
                }

                if (settings.ValidateAndRepair())
                {
                    Logger.Log("FloatingSidebar settings were repaired");
                    try { _vpetLLM.Settings?.Save(); } catch { }
                }

                return settings;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error getting sidebar settings: {ex.Message}");
                return new FloatingSidebarSettings();
            }
        }

        private void OnButtonClicked(object? sender, SidebarButtonClickedEventArgs e)
        {
            try
            {
                Logger.Log($"Sidebar button clicked: {e.ButtonId}");

                var allButtons = SidebarButton.GetDefaultButtons();
                var button = allButtons.FirstOrDefault(b => b.ButtonId == e.ButtonId);
                
                if (button != null)
                {
                    try
                    {
                        button.ExecuteAction(_vpetLLM);
                    }
                    catch (Exception actionEx)
                    {
                        Logger.Log($"Error executing button action '{e.ButtonId}': {actionEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling button click: {ex.Message}");
            }
        }

        private void OnSidebarClosed(object? sender, EventArgs e)
        {
            try
            {
                var settings = GetFloatingSidebarSettings();
                settings.IsEnabled = false;
                _vpetLLM.Settings.Save();
                Logger.Log("Sidebar closed by user");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling sidebar close: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;

            try
            {
                if (disposing)
                {
                    _settingsCheckTimer?.Stop();
                    _settingsCheckTimer = null;
                    CleanupSidebar();
                }

                _isDisposed = true;
                Logger.Log("FloatingSidebarManager disposed");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error disposing FloatingSidebarManager: {ex.Message}");
                _isDisposed = true;
            }
        }

        ~FloatingSidebarManager()
        {
            Dispose(false);
        }
    }
}