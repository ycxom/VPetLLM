using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VPetLLM.UI.Controls;

namespace VPetLLMPlugin.UI.Controls
{
    /// <summary>
    /// VPetLLM状态枚举
    /// </summary>
    public enum VPetLLMStatus
    {
        /// <summary>
        /// 待机状态 - 透明
        /// </summary>
        Idle,
        /// <summary>
        /// 处理请求 - 黄色
        /// </summary>
        Processing,
        /// <summary>
        /// 输出响应 - 绿色
        /// </summary>
        Outputting,
        /// <summary>
        /// 插件执行中 - 蓝色
        /// </summary>
        PluginExecuting,
        /// <summary>
        /// 错误状态 - 红色
        /// </summary>
        Error
    }

    /// <summary>
    /// 悬浮侧边栏 UserControl - 采用 DemoClock TimeClock 的实现方式
    /// 通过 MW.Main.UIGrid.Children.Insert() 插入到 VPet 主界面
    /// </summary>
    public partial class FloatingSidebar : UserControl
    {
        private global::VPetLLM.VPetLLM _master;
        private readonly Dictionary<string, SidebarButton> _buttons = new();
        private System.Timers.Timer _closeTimer;
        private bool _isClosing = false;

        // 添加层级切换状态管理
        private bool _isInFrontLayer = true;
        private bool _isLayerSwitching = false;
        private readonly object _layerSwitchLock = new object();

        // 状态灯相关
        private VPetLLMStatus _currentStatus = VPetLLMStatus.Idle;
        private System.Timers.Timer _errorResetTimer;
        private readonly object _statusLock = new object();

        public event EventHandler<SidebarButtonClickedEventArgs>? ButtonClicked;
        public event EventHandler? SidebarClosed;

        /// <summary>
        /// 默认透明度
        /// </summary>
        public double DefaultOpacity { get; set; } = 0.7;

        /// <summary>
        /// 活动状态透明度
        /// </summary>
        public double ActiveOpacity { get; set; } = 0.95;

        /// <summary>
        /// 是否自动切换到后层
        /// </summary>
        public bool PlaceAutoBack { get; set; } = true;

        /// <summary>
        /// 自动切换延迟时间（毫秒）
        /// </summary>
        public int AutoBackDelay { get; set; } = 4000;

        /// <summary>
        /// 当前布局方向
        /// </summary>
        private Orientation _currentOrientation = Orientation.Vertical;

        public FloatingSidebar()
        {
            InitializeComponent();
            Resources = Application.Current.Resources;

            // 初始化关闭定时器 (参考 DemoClock 的 CloseTimer)
            _closeTimer = new System.Timers.Timer()
            {
                Interval = AutoBackDelay,
                AutoReset = false,
                Enabled = false
            };
            _closeTimer.Elapsed += CloseTimer_Elapsed;

            // 初始化错误重置定时器
            _errorResetTimer = new System.Timers.Timer()
            {
                Interval = 5000, // 5秒
                AutoReset = false,
                Enabled = false
            };
            _errorResetTimer.Elapsed += ErrorResetTimer_Elapsed;

            Opacity = DefaultOpacity;
            Logger.Log("FloatingSidebar UserControl initialized");
        }

        /// <summary>
        /// 初始化侧边栏并插入到 VPet 主界面
        /// </summary>
        public void Initialize(global::VPetLLM.VPetLLM master)
        {
            try
            {
                _master = master;

                // 插入控件到 VPet 主界面 (参考 DemoClock/TimeClock.xaml.cs)
                _master.MW.Main.UIGrid.Children.Insert(0, this);
                _isInFrontLayer = true; // 初始状态在前层

                // 监听主界面的鼠标事件
                _master.MW.Main.MouseEnter += Main_MouseEnter;
                _master.MW.Main.MouseLeave += Main_MouseLeave;

                Logger.Log("FloatingSidebar inserted into VPet UIGrid");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error initializing FloatingSidebar: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 错误重置定时器触发 - 5秒后自动切换回待机状态
        /// </summary>
        private void ErrorResetTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            // 使用 BeginInvoke 避免阻塞
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_currentStatus == VPetLLMStatus.Error)
                    {
                        UpdateStatusLight(VPetLLMStatus.Idle);
                        Logger.Log("Status light auto-reset from Error to Idle after 5 seconds");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error in ErrorResetTimer_Elapsed: {ex.Message}");
                }
            }));
        }

        /// <summary>
        /// 更新状态灯 - 完全异步，不阻塞任何线程
        /// 优化：避免重复设置相同状态，减少UI更新频率
        /// </summary>
        /// <param name="status">新状态</param>
        public void UpdateStatusLight(VPetLLMStatus status)
        {
            try
            {
                if (_isClosing)
                {
                    return;
                }

                // 检查状态是否真的改变了，避免重复设置
                if (_currentStatus == status)
                {
                    Logger.Log($"UpdateStatusLight: Status already {status}, skipping update");
                    return;
                }

                Logger.Log($"UpdateStatusLight: Updating from {_currentStatus} to {status}");

                // 更新状态
                var oldStatus = _currentStatus;
                _currentStatus = status;

                // 计算颜色 - 只改变填充色和发光效果，边框始终保持白色可见
                Color lightColor;
                Color glowColor;

                switch (status)
                {
                    case VPetLLMStatus.Idle:
                        lightColor = Colors.Transparent;
                        glowColor = Colors.Transparent;
                        break;
                    case VPetLLMStatus.Processing:
                        lightColor = Colors.Orange;
                        glowColor = Colors.Yellow;
                        break;
                    case VPetLLMStatus.Outputting:
                        lightColor = Colors.Lime;
                        glowColor = Colors.LimeGreen;
                        break;
                    case VPetLLMStatus.PluginExecuting:
                        lightColor = Colors.DodgerBlue;
                        glowColor = Colors.DeepSkyBlue;
                        break;
                    case VPetLLMStatus.Error:
                        lightColor = Colors.Red;
                        glowColor = Colors.OrangeRed;
                        // 启动5秒自动重置定时器
                        _errorResetTimer.Start();
                        break;
                    default:
                        lightColor = Colors.Transparent;
                        glowColor = Colors.Transparent;
                        break;
                }

                // 使用异步调度更新UI，完全不阻塞
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_isClosing) return;

                        // 再次检查状态是否被其他线程改变
                        // 如果状态已经不是我们要设置的状态，说明有更新的状态请求，跳过本次更新
                        if (_currentStatus != status)
                        {
                            Logger.Log($"UpdateStatusLight: Status changed to {_currentStatus} before UI update, skipping");
                            return;
                        }

                        // 只更新填充色和发光效果，不改变 Opacity，保持白色边框始终可见
                        StatusLight.Fill = new SolidColorBrush(lightColor);
                        StatusLightGlow.Color = glowColor;

                        Logger.Log($"Status light updated: {oldStatus} -> {status}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error setting status light colors: {ex.Message}");
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error updating status light: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取当前状态 - 无锁读取
        /// </summary>
        public VPetLLMStatus GetCurrentStatus()
        {
            return _currentStatus;
        }

        /// <summary>
        /// 定时器触发 - 切换到后层并降低透明度
        /// 使用 Invoke 确保操作在同一帧内完成，避免位置跳动
        /// </summary>
        private void CloseTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            // 使用 Invoke 确保操作同步完成，避免位置跳动
            Dispatcher.Invoke(new Action(() =>
            {
                try
                {
                    // 再次检查 PlaceAutoBack 设置，因为用户可能在定时器运行期间更改了设置
                    if (!PlaceAutoBack)
                    {
                        return;
                    }

                    // 使用锁防止并发操作
                    lock (_layerSwitchLock)
                    {
                        // 如果已经在后层或正在切换，跳过操作
                        if (!_isInFrontLayer || _isLayerSwitching)
                        {
                            return;
                        }

                        _isLayerSwitching = true;

                        try
                        {
                            // 降低透明度
                            Opacity = DefaultOpacity;

                            // 切换到后层 (参考 DemoClock)
                            if (_master?.MW?.Main?.UIGrid?.Children.Contains(this) == true)
                            {
                                _master.MW.Main.UIGrid.Children.Remove(this);
                                _master.MW.Main.UIGrid_Back.Children.Add(this);
                                _isInFrontLayer = false;
                                Logger.Log("FloatingSidebar moved to UIGrid_Back");
                            }
                        }
                        finally
                        {
                            _isLayerSwitching = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error in CloseTimer_Elapsed: {ex.Message}");
                    // 确保在异常情况下重置状态
                    _isLayerSwitching = false;
                }
            }));
        }

        /// <summary>
        /// 主界面鼠标进入事件
        /// </summary>
        private void Main_MouseEnter(object sender, MouseEventArgs e)
        {
            BringToFront();
        }

        /// <summary>
        /// 主界面鼠标离开事件
        /// </summary>
        private void Main_MouseLeave(object sender, MouseEventArgs e)
        {
            StartAutoBackTimer();
        }

        /// <summary>
        /// 控件鼠标进入事件
        /// </summary>
        private void UserControl_MouseEnter(object sender, MouseEventArgs e)
        {
            BringToFront();
        }

        /// <summary>
        /// 控件鼠标离开事件
        /// </summary>
        private void UserControl_MouseLeave(object sender, MouseEventArgs e)
        {
            StartAutoBackTimer();
        }

        /// <summary>
        /// 将控件切换到前层
        /// </summary>
        public void BringToFront()
        {
            try
            {
                // 停止定时器
                _closeTimer.Enabled = false;

                // 使用锁防止并发操作
                lock (_layerSwitchLock)
                {
                    // 如果已经在前层或正在切换，跳过操作
                    if (_isInFrontLayer || _isLayerSwitching)
                    {
                        // 即使已经在前层，也要恢复透明度
                        if (_isInFrontLayer)
                        {
                            Opacity = ActiveOpacity;
                        }
                        return;
                    }

                    _isLayerSwitching = true;

                    try
                    {
                        // 如果控件在后层，移动到前层
                        if (_master?.MW?.Main?.UIGrid_Back?.Children.Contains(this) == true)
                        {
                            _master.MW.Main.UIGrid_Back.Children.Remove(this);
                            _master.MW.Main.UIGrid.Children.Insert(0, this);
                            _isInFrontLayer = true;
                            Logger.Log("FloatingSidebar moved to UIGrid (front)");
                        }
                        // 如果控件不在任何 Grid 中，添加到前层
                        else if (_master?.MW?.Main?.UIGrid?.Children.Contains(this) == false &&
                                 _master?.MW?.Main?.UIGrid_Back?.Children.Contains(this) == false)
                        {
                            _master.MW.Main.UIGrid.Children.Insert(0, this);
                            _isInFrontLayer = true;
                            Logger.Log("FloatingSidebar added to UIGrid (front)");
                        }

                        // 恢复透明度
                        Opacity = ActiveOpacity;
                    }
                    finally
                    {
                        _isLayerSwitching = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error in BringToFront: {ex.Message}");
                // 确保在异常情况下重置状态
                _isLayerSwitching = false;
            }
        }

        /// <summary>
        /// 启动自动切换到后层的定时器
        /// </summary>
        public void StartAutoBackTimer()
        {
            if (PlaceAutoBack)
            {
                lock (_layerSwitchLock)
                {
                    if (_isLayerSwitching)
                    {
                        return;
                    }
                    _closeTimer.Interval = AutoBackDelay;
                    _closeTimer.Start();
                }
            }
            else
            {
                _closeTimer.Stop();
            }
        }

        /// <summary>
        /// 添加按钮到侧边栏
        /// </summary>
        public void AddButton(SidebarButton sidebarButton)
        {
            try
            {
                if (_isClosing || sidebarButton is null)
                {
                    return;
                }

                if (_buttons.ContainsKey(sidebarButton.ButtonId))
                {
                    RemoveButton(sidebarButton.ButtonId);
                }

                var button = new Button
                {
                    ToolTip = sidebarButton.ToolTip,
                    IsEnabled = sidebarButton.IsEnabled,
                    Tag = sidebarButton.ButtonId,
                    Width = 36,
                    Height = 36,
                    // 根据当前方向设置边距
                    Margin = _currentOrientation == Orientation.Horizontal
                        ? new Thickness(2, 0, 2, 0)
                        : new Thickness(0, 2, 0, 2),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(0)
                };

                // 使用 DynamicResource 适应 VPet 主题颜色（与 DemoClock 一致）
                button.SetResourceReference(BackgroundProperty, "DARKPrimaryLighter");
                button.SetResourceReference(BorderBrushProperty, "DARKPrimaryDark");
                button.BorderThickness = new Thickness(1);

                // 创建按钮内容，使用主题文本颜色
                var content = sidebarButton.GetContent();
                if (content is TextBlock textBlock)
                {
                    textBlock.SetResourceReference(TextBlock.ForegroundProperty, "DARKPrimaryText");
                    textBlock.FontSize = 14;
                }
                button.Content = content;

                button.Click += (sender, e) =>
                {
                    try
                    {
                        if (!_isClosing)
                        {
                            ButtonClicked?.Invoke(this, new SidebarButtonClickedEventArgs(sidebarButton.ButtonId));
                            Logger.Log($"Button clicked: {sidebarButton.ButtonId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error handling button click: {ex.Message}");
                    }
                };

                ButtonContainer.Children.Add(button);
                _buttons[sidebarButton.ButtonId] = sidebarButton;
                Logger.Log($"Added button: {sidebarButton.ButtonId}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error adding button: {ex.Message}");
            }
        }

        /// <summary>
        /// 移除按钮
        /// </summary>
        public void RemoveButton(string buttonId)
        {
            try
            {
                if (_isClosing || string.IsNullOrEmpty(buttonId) || !_buttons.ContainsKey(buttonId))
                {
                    return;
                }

                for (int i = ButtonContainer.Children.Count - 1; i >= 0; i--)
                {
                    if (ButtonContainer.Children[i] is Button button && button.Tag?.ToString() == buttonId)
                    {
                        ButtonContainer.Children.RemoveAt(i);
                        break;
                    }
                }

                _buttons.Remove(buttonId);
                Logger.Log($"Removed button: {buttonId}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error removing button: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新按钮顺序
        /// </summary>
        public void UpdateButtonOrder(List<string> buttonIds)
        {
            try
            {
                if (_isClosing || buttonIds is null || buttonIds.Count == 0)
                {
                    return;
                }

                var buttons = new List<Button>();
                foreach (UIElement child in ButtonContainer.Children)
                {
                    if (child is Button button)
                    {
                        buttons.Add(button);
                    }
                }

                ButtonContainer.Children.Clear();

                foreach (string buttonId in buttonIds)
                {
                    var button = buttons.Find(b => b.Tag?.ToString() == buttonId);
                    if (button is not null)
                    {
                        ButtonContainer.Children.Add(button);
                    }
                }

                Logger.Log($"Updated button order: {string.Join(", ", buttonIds)}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error updating button order: {ex.Message}");
            }
        }

        /// <summary>
        /// 设置透明度（带动画）
        /// </summary>
        public void SetTransparency(double opacity)
        {
            try
            {
                if (_isClosing) return;

                double safeOpacity = Math.Max(0.1, Math.Min(1.0, opacity));
                var animation = new DoubleAnimation
                {
                    To = safeOpacity,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                BeginAnimation(OpacityProperty, animation);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error setting transparency: {ex.Message}");
                Opacity = Math.Max(0.1, Math.Min(1.0, opacity));
            }
        }

        /// <summary>
        /// 更新按钮状态
        /// </summary>
        public void UpdateButtonState(string buttonId, bool isEnabled)
        {
            try
            {
                if (_isClosing || string.IsNullOrEmpty(buttonId)) return;

                foreach (UIElement child in ButtonContainer.Children)
                {
                    if (child is Button button && button.Tag?.ToString() == buttonId)
                    {
                        button.IsEnabled = isEnabled;
                        break;
                    }
                }

                if (_buttons.ContainsKey(buttonId))
                {
                    _buttons[buttonId].IsEnabled = isEnabled;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error updating button state: {ex.Message}");
            }
        }

        /// <summary>
        /// 设置布局方向（上下位置时使用横向布局，左右位置时使用竖向布局）
        /// </summary>
        public void SetOrientation(Orientation orientation)
        {
            try
            {
                _currentOrientation = orientation;
                ButtonContainer.Orientation = orientation;
                VerticalLayoutPanel.Orientation = orientation;

                // 根据方向调整控件尺寸和按钮边距
                if (orientation == Orientation.Horizontal)
                {
                    // 横向布局：宽度自动，高度固定
                    Width = double.NaN; // Auto
                    Height = double.NaN; // Auto
                    MinWidth = 0;
                    MinHeight = 50;

                    // 调整状态灯位置（横向时在左侧）
                    StatusLight.Margin = new Thickness(2, 0, 4, 0);

                    // 调整关闭按钮位置（横向时在状态灯右侧）
                    CloseButton.Margin = new Thickness(0, 0, 4, 0);

                    // 调整按钮边距为横向
                    foreach (UIElement child in ButtonContainer.Children)
                    {
                        if (child is Button button)
                        {
                            button.Margin = new Thickness(2, 0, 2, 0);
                        }
                    }
                }
                else
                {
                    // 竖向布局：宽度固定，高度自动
                    Width = 50;
                    Height = double.NaN; // Auto
                    MinWidth = 50;
                    MinHeight = 0;

                    // 调整状态灯位置（竖向时在顶部）
                    StatusLight.Margin = new Thickness(0, 2, 0, 4);

                    // 调整关闭按钮位置（竖向时在状态灯下方）
                    CloseButton.Margin = new Thickness(0, 0, 0, 2);

                    // 调整按钮边距为竖向
                    foreach (UIElement child in ButtonContainer.Children)
                    {
                        if (child is Button button)
                        {
                            button.Margin = new Thickness(0, 2, 0, 2);
                        }
                    }
                }

                Logger.Log($"Sidebar orientation set to: {orientation}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error setting orientation: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示侧边栏
        /// </summary>
        public void Show()
        {
            try
            {
                Visibility = Visibility.Visible;

                // 确保状态正确初始化
                lock (_layerSwitchLock)
                {
                    // 检查当前实际位置并更新状态
                    if (_master?.MW?.Main?.UIGrid?.Children.Contains(this) == true)
                    {
                        _isInFrontLayer = true;
                    }
                    else if (_master?.MW?.Main?.UIGrid_Back?.Children.Contains(this) == true)
                    {
                        _isInFrontLayer = false;
                    }
                }

                BringToFront();
                Logger.Log("FloatingSidebar shown");
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
                Visibility = Visibility.Collapsed;
                _closeTimer.Enabled = false;
                SidebarClosed?.Invoke(this, EventArgs.Empty);
                Logger.Log("FloatingSidebar hidden");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error hiding sidebar: {ex.Message}");
            }
        }

        /// <summary>
        /// 关闭按钮点击
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        /// <summary>
        /// 设置菜单项点击
        /// </summary>
        private void SettingMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _master?.Setting();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error opening settings: {ex.Message}");
            }
        }

        /// <summary>
        /// 隐藏菜单项点击
        /// </summary>
        private void HideMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        /// <summary>
        /// 清理资源
        /// </summary>
        public void Dispose()
        {
            try
            {
                _isClosing = true;

                // 停止定时器并清理
                _closeTimer?.Stop();
                _closeTimer?.Dispose();

                _errorResetTimer?.Stop();
                _errorResetTimer?.Dispose();

                // 使用锁确保清理过程的线程安全
                lock (_layerSwitchLock)
                {
                    // 从 UIGrid 移除控件
                    if (_master?.MW?.Main?.UIGrid?.Children.Contains(this) == true)
                    {
                        _master.MW.Main.UIGrid.Children.Remove(this);
                    }
                    if (_master?.MW?.Main?.UIGrid_Back?.Children.Contains(this) == true)
                    {
                        _master.MW.Main.UIGrid_Back.Children.Remove(this);
                    }

                    // 重置状态
                    _isInFrontLayer = false;
                    _isLayerSwitching = false;
                }

                // 取消事件订阅
                if (_master?.MW?.Main is not null)
                {
                    _master.MW.Main.MouseEnter -= Main_MouseEnter;
                    _master.MW.Main.MouseLeave -= Main_MouseLeave;
                }

                _buttons.Clear();
                ButtonClicked = null;
                SidebarClosed = null;

                Logger.Log("FloatingSidebar disposed");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error disposing FloatingSidebar: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取当前按钮列表
        /// </summary>
        public List<string> GetButtonIds()
        {
            var buttonIds = new List<string>();
            foreach (UIElement child in ButtonContainer.Children)
            {
                if (child is Button button && button.Tag is not null)
                {
                    buttonIds.Add(button.Tag.ToString()!);
                }
            }
            return buttonIds;
        }
    }

    /// <summary>
    /// 侧边栏按钮点击事件参数
    /// </summary>
    public class SidebarButtonClickedEventArgs : EventArgs
    {
        public string ButtonId { get; }

        public SidebarButtonClickedEventArgs(string buttonId)
        {
            ButtonId = buttonId;
        }
    }
}