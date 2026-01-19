using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VPetLLM.Utils.System;

namespace VPetLLM.UI.Windows
{
    public class HotkeyCapture : Window
    {
        public string CapturedModifiers { get; private set; } = "";
        public string CapturedKey { get; private set; } = "";
        public bool IsCaptured { get; private set; } = false;

        private TextBlock _hotkeyDisplay;
        private TextBlock _hintText;

        public HotkeyCapture()
        {
            InitializeUI();

            // 确保窗口获得焦点
            this.Loaded += (s, e) =>
            {
                this.Activate();
                this.Focus();
                Logger.Log("HotkeyCapture: Window loaded and focused");
            };

            // 绑定键盘事件
            this.KeyDown += Window_KeyDown;
        }

        private void InitializeUI()
        {
            // 窗口属性
            this.Title = "捕获快捷键";
            this.Height = 240;  // 增加高度以完整显示内容
            this.Width = 450;   // 增加宽度
            this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            this.ResizeMode = ResizeMode.NoResize;
            this.WindowStyle = WindowStyle.ToolWindow;
            this.Topmost = true;
            this.Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));

            // 创建主Grid
            var mainGrid = new Grid { Margin = new Thickness(20) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 标题
            var titleText = new TextBlock
            {
                Text = "请按下您想要设置的快捷键组合",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(51, 51, 51)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 15),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(titleText, 0);
            mainGrid.Children.Add(titleText);

            // 显示区域
            var displayBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(20),
                MinHeight = 80  // 设置最小高度
            };
            Grid.SetRow(displayBorder, 1);

            _hotkeyDisplay = new TextBlock
            {
                Text = "等待按键...",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            };
            displayBorder.Child = _hotkeyDisplay;
            mainGrid.Children.Add(displayBorder);

            // 提示文本
            _hintText = new TextBlock
            {
                Text = "按 ESC 取消",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            };
            Grid.SetRow(_hintText, 2);
            mainGrid.Children.Add(_hintText);

            this.Content = mainGrid;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            Logger.Log($"HotkeyCapture: KeyDown - Key: {e.Key}, SystemKey: {e.SystemKey}, Modifiers: {Keyboard.Modifiers}");

            e.Handled = true;

            // ESC 键取消
            if (e.Key == Key.Escape)
            {
                Logger.Log("HotkeyCapture: ESC pressed, cancelling");
                this.DialogResult = false;
                this.Close();
                return;
            }

            // 忽略单独的修饰键
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
                e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
                e.Key == Key.LeftShift || e.Key == Key.RightShift ||
                e.Key == Key.LWin || e.Key == Key.RWin ||
                e.Key == Key.System)
            {
                // 显示当前按下的修饰键
                var modifiers = new List<string>();
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                    modifiers.Add("Ctrl");
                if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
                    modifiers.Add("Alt");
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                    modifiers.Add("Shift");
                if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0)
                    modifiers.Add("Win");

                if (modifiers.Count > 0)
                {
                    _hotkeyDisplay.Text = string.Join(" + ", modifiers) + " + ?";
                    _hintText.Text = "请继续按下一个普通键";
                }

                Logger.Log($"HotkeyCapture: Modifier key only, waiting for actual key. Current modifiers: {string.Join("+", modifiers)}");
                return;
            }

            // 获取修饰键
            var capturedModifiers = new List<string>();
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                capturedModifiers.Add("Ctrl");
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
                capturedModifiers.Add("Alt");
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                capturedModifiers.Add("Shift");
            if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0)
                capturedModifiers.Add("Win");

            // 获取按键
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var keyString = key.ToString();

            // 保存结果
            CapturedModifiers = string.Join("+", capturedModifiers);
            CapturedKey = keyString;
            IsCaptured = true;

            // 显示捕获的快捷键
            if (string.IsNullOrEmpty(CapturedModifiers))
            {
                _hotkeyDisplay.Text = CapturedKey;
            }
            else
            {
                _hotkeyDisplay.Text = $"{CapturedModifiers} + {CapturedKey}";
            }

            _hintText.Text = "快捷键已捕获，窗口将自动关闭...";

            Logger.Log($"HotkeyCapture: Captured successfully - Modifiers: {CapturedModifiers}, Key: {CapturedKey}");

            // 延迟关闭，让用户看到结果
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                this.DialogResult = true;
                this.Close();
            };
            timer.Start();
        }
    }
}
