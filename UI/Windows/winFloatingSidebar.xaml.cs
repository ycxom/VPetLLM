using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using VPetLLM.UI.Controls;

namespace VPetLLMPlugin.UI.Windows
{
    /// <summary>
    /// winFloatingSidebar.xaml 的交互逻辑
    /// </summary>
    public partial class winFloatingSidebar : Window
    {
        private readonly Dictionary<string, SidebarButton> _buttons = new();
        private bool _isDragging = false;
        private Point _dragStartPoint;
        private bool _isClosing = false;

        public event EventHandler<SidebarButtonClickedEventArgs>? ButtonClicked;
        public event EventHandler? SidebarClosed;

        public winFloatingSidebar()
        {
            try
            {
                InitializeComponent();
                Closed += OnWindowClosed;
                Logger.Log("winFloatingSidebar initialized");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error initializing winFloatingSidebar: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 窗口关闭时清理资源
        /// </summary>
        private void OnWindowClosed(object? sender, EventArgs e)
        {
            try
            {
                _isClosing = true;

                // 清理按钮字典
                _buttons.Clear();

                // 清理事件处理器
                ButtonClicked = null;
                SidebarClosed = null;

                Logger.Log("winFloatingSidebar resources cleaned up");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error cleaning up winFloatingSidebar: {ex.Message}");
            }
        }

        /// <summary>
        /// 添加按钮到侧边栏
        /// </summary>
        public void AddButton(SidebarButton sidebarButton)
        {
            try
            {
                // 检查窗口是否正在关闭
                if (_isClosing)
                {
                    Logger.Log($"Cannot add button {sidebarButton.ButtonId}: window is closing");
                    return;
                }

                if (sidebarButton is null)
                {
                    Logger.Log("Cannot add null button");
                    return;
                }

                if (_buttons.ContainsKey(sidebarButton.ButtonId))
                {
                    Logger.Log($"Button {sidebarButton.ButtonId} already exists, updating");
                    RemoveButton(sidebarButton.ButtonId);
                }

                var buttonStyle = TryFindResource("SidebarButtonStyle") as Style;
                var button = new Button
                {
                    Style = buttonStyle,
                    Content = sidebarButton.GetContent(),
                    ToolTip = sidebarButton.ToolTip,
                    IsEnabled = sidebarButton.IsEnabled,
                    Tag = sidebarButton.ButtonId
                };

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
                        Logger.Log($"Error handling button click for {sidebarButton.ButtonId}: {ex.Message}");
                    }
                };

                ButtonContainer.Children.Add(button);
                _buttons[sidebarButton.ButtonId] = sidebarButton;

                Logger.Log($"Added button: {sidebarButton.ButtonId}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error adding button {sidebarButton?.ButtonId ?? "null"}: {ex.Message}");
            }
        }

        /// <summary>
        /// 移除按钮
        /// </summary>
        public void RemoveButton(string buttonId)
        {
            try
            {
                if (_isClosing || string.IsNullOrEmpty(buttonId))
                {
                    return;
                }

                if (!_buttons.ContainsKey(buttonId))
                {
                    return;
                }

                for (int i = ButtonContainer.Children.Count - 1; i >= 0; i--)
                {
                    if (ButtonContainer.Children[i] is Button button &&
                        button.Tag?.ToString() == buttonId)
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
                Logger.Log($"Error removing button {buttonId}: {ex.Message}");
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
                    if (string.IsNullOrEmpty(buttonId))
                    {
                        continue;
                    }

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
        /// 设置透明度
        /// </summary>
        public void SetTransparency(double opacity)
        {
            try
            {
                if (_isClosing)
                {
                    return;
                }

                // 确保透明度在有效范围内
                double safeOpacity = Math.Max(0.1, Math.Min(1.0, opacity));

                var animation = new DoubleAnimation
                {
                    To = safeOpacity,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };

                SidebarContainer.BeginAnimation(OpacityProperty, animation);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error setting transparency: {ex.Message}");

                // 降级处理：直接设置透明度，不使用动画
                try
                {
                    SidebarContainer.Opacity = Math.Max(0.1, Math.Min(1.0, opacity));
                }
                catch
                {
                    // 忽略降级处理的错误
                }
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

        /// <summary>
        /// 更新按钮状态
        /// </summary>
        public void UpdateButtonState(string buttonId, bool isEnabled)
        {
            try
            {
                if (_isClosing || string.IsNullOrEmpty(buttonId))
                {
                    return;
                }

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
                Logger.Log($"Error updating button state for {buttonId}: {ex.Message}");
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isClosing)
                {
                    return;
                }

                Logger.Log("Sidebar close button clicked");

                try
                {
                    SidebarClosed?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception invokeEx)
                {
                    Logger.Log($"Error invoking SidebarClosed event: {invokeEx.Message}");
                }

                Hide();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling close button click: {ex.Message}");
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.ClickCount == 1)
                {
                    _isDragging = true;
                    _dragStartPoint = e.GetPosition(this);
                    DragMove();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling window drag: {ex.Message}");
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            _isDragging = false;
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);

            if (!_isDragging)
            {
                return;
            }

            try
            {
                Logger.Log($"Sidebar position changed to: {Left}, {Top}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling location change: {ex.Message}");
            }
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