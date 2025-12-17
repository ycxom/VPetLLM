using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using VPetLLM.UI.Controls;
using VPetLLM.Utils;

namespace VPetLLMPlugin.UI.Controls
{
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
        /// 定时器触发 - 切换到后层并降低透明度
        /// </summary>
        private void CloseTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    // 再次检查 PlaceAutoBack 设置，因为用户可能在定时器运行期间更改了设置
                    if (!PlaceAutoBack)
                    {
                        return;
                    }
                    
                    Opacity = DefaultOpacity;
                    
                    // 切换到后层 (参考 DemoClock)
                    if (_master?.MW?.Main?.UIGrid?.Children.Contains(this) == true)
                    {
                        _master.MW.Main.UIGrid.Children.Remove(this);
                        _master.MW.Main.UIGrid_Back.Children.Add(this);
                        Logger.Log("FloatingSidebar moved to UIGrid_Back");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error in CloseTimer_Elapsed: {ex.Message}");
                }
            });
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
                
                // 如果控件在后层，移动到前层（无论 PlaceAutoBack 设置如何）
                if (_master?.MW?.Main?.UIGrid_Back?.Children.Contains(this) == true)
                {
                    _master.MW.Main.UIGrid_Back.Children.Remove(this);
                    _master.MW.Main.UIGrid.Children.Insert(0, this);
                    Logger.Log("FloatingSidebar moved to UIGrid (front)");
                }
                // 如果控件不在任何 Grid 中，添加到前层
                else if (_master?.MW?.Main?.UIGrid?.Children.Contains(this) == false && 
                         _master?.MW?.Main?.UIGrid_Back?.Children.Contains(this) == false)
                {
                    _master.MW.Main.UIGrid.Children.Insert(0, this);
                    Logger.Log("FloatingSidebar added to UIGrid (front)");
                }
                
                // 恢复透明度
                Opacity = ActiveOpacity;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error in BringToFront: {ex.Message}");
            }
        }

        /// <summary>
        /// 启动自动切换到后层的定时器
        /// </summary>
        public void StartAutoBackTimer()
        {
            // 只有在启用自动隐藏时才启动定时器
            if (PlaceAutoBack)
            {
                _closeTimer.Interval = AutoBackDelay;
                _closeTimer.Start();
            }
            else
            {
                // 如果禁用自动隐藏，确保定时器停止
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
                if (_isClosing || sidebarButton == null)
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
                if (_isClosing || buttonIds == null || buttonIds.Count == 0)
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
                    if (button != null)
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
                    
                    // 调整关闭按钮位置（横向时在左侧）
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
                    
                    // 调整关闭按钮位置（竖向时在顶部）
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
                _closeTimer?.Stop();
                _closeTimer?.Dispose();

                // 从 UIGrid 移除控件
                if (_master?.MW?.Main?.UIGrid?.Children.Contains(this) == true)
                {
                    _master.MW.Main.UIGrid.Children.Remove(this);
                }
                if (_master?.MW?.Main?.UIGrid_Back?.Children.Contains(this) == true)
                {
                    _master.MW.Main.UIGrid_Back.Children.Remove(this);
                }

                // 取消事件订阅
                if (_master?.MW?.Main != null)
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
                if (child is Button button && button.Tag != null)
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
