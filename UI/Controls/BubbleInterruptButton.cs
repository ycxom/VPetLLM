using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VPetLLM.Utils.Localization;

namespace VPetLLM.UI.Controls
{
    /// <summary>
    /// 气泡里的中断按钮。
    ///
    /// 侧边栏上的状态灯兼中断按钮只有开了侧边栏的人才看得到；关掉侧边栏的用户
    /// 在桌宠说个不停时无处可点。气泡是这种时候唯一必然在视线里的东西，
    /// 所以把同一个中断动作挂到气泡下方。
    ///
    /// 挂载点是宿主 MessageBar 的 MessageBoxContent（宿主留给消息框内容的公开容器）。
    /// 宿主每次 Show 都会 Clear 掉它，所以这里靠 TText.TextChanged ——
    /// 打字机每吐一个字都会触发 —— 在气泡还活着时把按钮补回去。
    ///
    /// 对 MessageBar 内部的访问一律走反射并允许失败：用户可能装了第三方消息条插件
    /// （IMassageBar 的其它实现），拿不到容器时本功能安静缺席，不影响说话。
    /// </summary>
    public static class BubbleInterruptButton
    {
        private static readonly object _lock = new object();

        private static VPetLLM? _plugin;
        private static Panel? _content;   // MessageBar.MessageBoxContent
        private static TextBox? _text;    // MessageBar.TText
        private static Button? _button;
        private static bool _attached;

        /// <summary>
        /// 挂到宿主消息条上。重复调用安全，宿主结构不支持时安静返回。
        /// </summary>
        public static void Attach(VPetLLM plugin)
        {
            if (plugin is null) return;

            lock (_lock)
            {
                if (_attached) return;

                // 记下插件引用：消息条可能晚于插件初始化才建好，
                // 后续 Refresh 会拿它再试一次，不至于一次失败就永久缺席
                _plugin = plugin;

                try
                {
                    var msgBar = plugin.MW?.Main?.MsgBar;
                    if (msgBar is null)
                    {
                        Logger.Log("BubbleInterruptButton: 消息条尚不可用，稍后重试挂载");
                        return;
                    }

                    var type = msgBar.GetType();
                    _content = GetFieldValue(msgBar, type, "MessageBoxContent") as Panel;
                    _text = GetFieldValue(msgBar, type, "TText") as TextBox;

                    if (_content is null || _text is null)
                    {
                        Logger.Log($"BubbleInterruptButton: 消息条({type.Name})缺少 MessageBoxContent/TText，气泡中断按钮未启用");
                        return;
                    }

                    _text.TextChanged += OnBubbleTextChanged;
                    _attached = true;

                    Logger.Log("BubbleInterruptButton: 已挂载到气泡");
                }
                catch (Exception ex)
                {
                    Logger.Log($"BubbleInterruptButton: 挂载失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 从宿主消息条上摘下来（插件卸载时调用）
        /// </summary>
        public static void Detach()
        {
            lock (_lock)
            {
                try
                {
                    if (_text is not null)
                        _text.TextChanged -= OnBubbleTextChanged;

                    RemoveButton();
                }
                catch (Exception ex)
                {
                    Logger.Log($"BubbleInterruptButton: 卸载失败: {ex.Message}");
                }
                finally
                {
                    _plugin = null;
                    _content = null;
                    _text = null;
                    _button = null;
                    _attached = false;
                }
            }
        }

        /// <summary>
        /// 按当前状态决定按钮的去留。状态变化时调用；线程安全。
        /// </summary>
        public static void Refresh()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;

            // 一律回到 UI 线程再做事：订阅 TextChanged、增删子元素都有线程亲和性，
            // 而状态更新是从后台线程打过来的
            if (dispatcher.CheckAccess())
                RefreshCore();
            else
                dispatcher.BeginInvoke(new Action(RefreshCore), System.Windows.Threading.DispatcherPriority.Background);
        }

        private static void RefreshCore()
        {
            if (!_attached)
            {
                // 首次挂载可能因为消息条还没建好而失败，这里补一次
                var pending = _plugin;
                if (pending is null) return;

                Attach(pending);
                if (!_attached) return;
            }

            Apply();
        }

        /// <summary>
        /// 打字机每吐一个字都会走这里。宿主 Show 时清空过 MessageBoxContent，
        /// 按钮要在这里补回去 —— 频率高，所以判断必须廉价（几个引用比较）。
        /// </summary>
        private static void OnBubbleTextChanged(object sender, TextChangedEventArgs e)
        {
            Apply();
        }

        /// <summary>
        /// 实际增删按钮。必须在 UI 线程调用。
        /// </summary>
        private static void Apply()
        {
            try
            {
                var content = _content;
                if (content is null) return;

                if (ShouldShow())
                {
                    if (_button is null)
                        _button = CreateButton();

                    // Clear() 之后按钮已经没有父级，直接加回去即可
                    if (!content.Children.Contains(_button))
                        content.Children.Add(_button);
                }
                else
                {
                    RemoveButton();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleInterruptButton: 刷新按钮失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 只在"有一轮对话正在进行"且"侧边栏没显示"时出现 ——
        /// 开了侧边栏的用户已经有一个中断入口，气泡里再放一个纯属重复。
        /// </summary>
        private static bool ShouldShow()
        {
            var manager = _plugin?.FloatingSidebarManager;
            if (manager is null) return false;

            return manager.IsBusy && !manager.IsVisible;
        }

        private static void RemoveButton()
        {
            if (_button is null) return;

            if (_content is not null && _content.Children.Contains(_button))
                _content.Children.Remove(_button);
        }

        private static Button CreateButton()
        {
            var label = new TextBlock
            {
                Text = "■  " + Localize("FloatingSidebar.Interrupt", "点击中断"),
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "DARKPrimaryText");

            var button = new Button
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(10, 2, 10, 2),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = Localize("FloatingSidebar.Interrupt", "点击中断")
            };
            button.SetResourceReference(Control.BackgroundProperty, "DARKPrimaryLighter");
            button.SetResourceReference(Control.BorderBrushProperty, "DARKPrimaryDark");

            button.Click += OnClick;
            return button;
        }

        private static void OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var plugin = _plugin;
                if (plugin is null) return;

                Logger.Log("BubbleInterruptButton: 用户从气泡请求中断");
                plugin.InterruptCurrentResponse();

                // 中断后状态会转 Idle 并触发 Refresh，这里先把按钮收掉，
                // 免得用户在状态传导完成前看到它还杵在那
                RemoveButton();
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleInterruptButton: 处理中断点击失败: {ex.Message}");
            }
        }

        private static object? GetFieldValue(object instance, Type type, string name)
        {
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(instance);
        }

        private static string Localize(string key, string fallback)
        {
            try
            {
                var text = LocalizationService.Instance[key];
                return string.IsNullOrWhiteSpace(text) ? fallback : text;
            }
            catch
            {
                return fallback;
            }
        }
    }
}
