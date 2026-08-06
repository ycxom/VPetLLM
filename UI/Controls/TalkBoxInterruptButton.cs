using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using VPetLLM.Utils.Localization;

namespace VPetLLM.UI.Controls
{
    /// <summary>
    /// 劫持 VPet 输入框发送按钮的中断按钮。
    ///
    /// 侧边栏上的状态灯兼中断按钮只有开了侧边栏的人才看得到；关掉侧边栏的用户
    /// 在桌宠说个不停时无处可点。把 VPet 输入框的"发送"按钮在 LLM 处理期间
    /// 改成"中断"按钮，是一个不依赖气泡、也不依赖侧边栏的中断入口。
    ///
    /// 挂载点是 VPet TalkBox 的 btnSend（公开字段，x:FieldModifier="public"）。
    /// 两条发送途径都要拦，否则处理期间还能重复塞消息进去：
    ///   · 鼠标点按钮 —— PreviewMouseLeftButtonDown，在 Click（Send_Click）之前
    ///   · Ctrl+Enter —— PreviewKeyDown，挂在 TalkBox 根上抢在 tbTalk 之前
    /// 处理期间吃掉事件并转为中断，空闲时一律放行让正常的发送走完。
    ///
    /// btnSend 实例会在 RemoveTalkBox/LoadTalkDIY 时被换掉，Refresh 检测引用变化后重新挂载。
    /// </summary>
    public static class TalkBoxInterruptButton
    {
        private static readonly object _lock = new object();

        private static VPetLLM? _plugin;
        private static Button? _btnSend;
        private static UIElement? _talkBoxRoot;
        private static object? _originalContent;
        private static object? _originalToolTip;
        private static bool _hooked;       // 是否已订阅 btnSend 的事件
        private static bool _isHijacked;   // 当前是否处于劫持状态

        /// <summary>
        /// 挂到 VPet 输入框的发送按钮上。重复调用安全，TalkBox 尚未建好时安静等待。
        /// </summary>
        public static void Attach(VPetLLM plugin)
        {
            if (plugin is null) return;

            lock (_lock)
            {
                // 记下插件引用：TalkBox 可能晚于插件初始化才建好，
                // 后续 Refresh 会拿它再试一次，不至于一次失败就永久缺席
                _plugin = plugin;
            }
            // 不在这里拿 btnSend：TalkBox 可能还没建好。
            // Refresh() 会在状态变化时补上。
            Refresh();
        }

        /// <summary>
        /// 从发送按钮上摘下来（插件卸载时调用）
        /// </summary>
        public static void Detach()
        {
            // UnhookButton 操作 UI 元素，必须在 UI 线程执行
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(new Action(DetachCore));
                return;
            }
            DetachCore();
        }

        private static void DetachCore()
        {
            lock (_lock)
            {
                try { UnhookButton(); }
                catch (Exception ex) { Logger.Log($"TalkBoxInterruptButton: 卸载失败: {ex.Message}"); }
                finally { _plugin = null; }
            }
        }

        /// <summary>
        /// 按当前状态决定按钮的劫持/恢复。状态变化时调用；线程安全。
        /// </summary>
        public static void Refresh()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;

            // 一律回到 UI 线程再做事：订阅事件、修改按钮属性都有线程亲和性，
            // 而状态更新是从后台线程打过来的
            if (dispatcher.CheckAccess())
                RefreshCore();
            else
                dispatcher.BeginInvoke(new Action(RefreshCore), System.Windows.Threading.DispatcherPriority.Background);
        }

        private static void RefreshCore()
        {
            lock (_lock)
            {
                try
                {
                    var plugin = _plugin;
                    if (plugin is null) return;

                    // 检测 btnSend 是否变化（SwapTalkBox 会创建新 TalkBox）
                    var currentBtn = plugin.TalkBox?.btnSend;
                    if (currentBtn is null)
                        return; // TalkBox 还没建好，下次 Refresh 再试

                    if (!ReferenceEquals(_btnSend, currentBtn))
                    {
                        // btnSend 变了，先摘掉旧的
                        UnhookButton();
                        _btnSend = currentBtn;

                        // 订阅 PreviewMouseLeftButtonDown（隧道事件，在 Click 之前）
                        _btnSend.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;

                        // 宿主在 Send_Click 里会把整个工具栏收起来，输入框跟着一起没了。
                        // 用户在回复期间再点开工具栏时不会有任何状态变化事件，
                        // 不在这里补一次刷新的话，按钮会保持"发送"的样子。
                        _btnSend.IsVisibleChanged += OnSendButtonVisibleChanged;

                        // Ctrl+Enter 也要劫持，否则处理期间还能从键盘塞新消息进去。
                        // 宿主把 PreviewKeyDown 处理器挂在 tbTalk 自己身上，同元素上的
                        // 处理器按注册顺序走，我们再挂一个只会排在它后面、抢不到。
                        // 但 PreviewKeyDown 是隧道事件：从根往下传，挂在 TalkBox 这个
                        // UserControl（tbTalk 的祖先）上就一定先于 tbTalk 触发，
                        // 置 Handled 即可让事件到不了宿主的处理器。
                        _talkBoxRoot = plugin.TalkBox;
                        if (_talkBoxRoot is not null)
                            _talkBoxRoot.PreviewKeyDown += OnTalkBoxPreviewKeyDown;

                        _hooked = true;

                        // 保存原始外观用于恢复。
                        // 存的是"本地值"而不是 Content 读出来的字符串：宿主那两个属性是
                        // {ll:Str} 挂上去的绑定，直接把解析后的字符串写回去会把绑定顶掉，
                        // 之后用户切语言，发送按钮就永远停在旧语言上了。
                        _originalContent = CaptureLocalValue(_btnSend, ContentControl.ContentProperty);
                        _originalToolTip = CaptureLocalValue(_btnSend, FrameworkElement.ToolTipProperty);

                        Logger.Log("TalkBoxInterruptButton: 已挂载到输入框发送按钮");
                    }

                    Apply();
                }
                catch (Exception ex)
                {
                    Logger.Log($"TalkBoxInterruptButton: 刷新失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 实际劫持/恢复按钮。必须在 UI 线程调用。
        /// </summary>
        private static void Apply()
        {
            var btn = _btnSend;
            if (btn is null || !_hooked) return;

            try
            {
                if (ShouldShow())
                {
                    if (!_isHijacked)
                    {
                        // 只放文字不加图标：按钮所在列是 1* 的固定比例（约 100px）而字号是 30，
                        // 多一个字符就会被裁掉
                        btn.Content = Localize("TalkBox.Interrupt", "中断");
                        btn.ToolTip = Localize("FloatingSidebar.Interrupt", "点击中断");
                        _isHijacked = true;
                    }
                }
                else
                {
                    if (_isHijacked)
                    {
                        RestoreOriginalAppearance(btn);
                        _isHijacked = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TalkBoxInterruptButton: 刷新按钮失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 有一轮对话正在进行时就劫持，与侧边栏开没开无关 ——
        /// 输入框是用户主动打开的界面，按钮显示的应当是当前真实状态；
        /// 同时这也堵住了处理期间重复发送。
        /// </summary>
        private static bool ShouldShow()
        {
            return _plugin?.FloatingSidebarManager?.IsBusy == true;
        }

        /// <summary>
        /// 输入框重新出现时按当前状态重刷一次外观
        /// </summary>
        private static void OnSendButtonVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is true)
            {
                lock (_lock)
                {
                    Apply();
                }
            }
        }

        /// <summary>
        /// 输入框的 Ctrl+Enter（宿主的发送快捷键）：处理期间同样转成中断，
        /// 免得桌宠还在说话就又被塞进一条新消息。
        /// </summary>
        private static void OnTalkBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isHijacked) return;

            if (e.Key != Key.Enter || !e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control))
                return;

            // 拦在隧道阶段，宿主的 tbTalk_KeyDown 就收不到了
            e.Handled = true;
            TriggerInterrupt("键盘");
        }

        /// <summary>
        /// PreviewMouseLeftButtonDown 隧道事件：在 Click（Send_Click）之前拦截。
        /// 处于劫持状态时吃掉事件并调用中断，否则放行让正常的发送走完。
        /// </summary>
        private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isHijacked) return;

            // 吃掉事件，阻止后续的 Click → Send_Click
            e.Handled = true;
            TriggerInterrupt("发送按钮");
        }

        /// <summary>
        /// 执行中断并立刻把按钮恢复原样
        /// </summary>
        private static void TriggerInterrupt(string source)
        {
            try
            {
                var plugin = _plugin;
                if (plugin is null) return;

                Logger.Log($"TalkBoxInterruptButton: 用户从输入框请求中断（{source}）");
                plugin.InterruptCurrentResponse();

                // 立即恢复按钮外观，状态传导完成前先收掉
                var btn = _btnSend;
                if (btn is not null)
                {
                    RestoreOriginalAppearance(btn);
                    _isHijacked = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TalkBoxInterruptButton: 处理中断请求失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 摘掉当前 btnSend：恢复外观、取消事件订阅、清空引用。
        /// 必须在 UI 线程调用。
        /// </summary>
        private static void UnhookButton()
        {
            // 先恢复外观
            if (_isHijacked && _btnSend is not null)
            {
                try { RestoreOriginalAppearance(_btnSend); }
                catch { }
            }

            if (_hooked && _btnSend is not null)
            {
                try { _btnSend.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown; }
                catch { }

                try { _btnSend.IsVisibleChanged -= OnSendButtonVisibleChanged; }
                catch { }
            }

            if (_talkBoxRoot is not null)
            {
                try { _talkBoxRoot.PreviewKeyDown -= OnTalkBoxPreviewKeyDown; }
                catch { }
                _talkBoxRoot = null;
            }

            _hooked = false;
            _isHijacked = false;
            _btnSend = null;
            _originalContent = null;
            _originalToolTip = null;
        }

        /// <summary>
        /// 还原发送按钮被劫持前的外观
        /// </summary>
        private static void RestoreOriginalAppearance(Button btn)
        {
            RestoreLocalValue(btn, ContentControl.ContentProperty, _originalContent);
            RestoreLocalValue(btn, FrameworkElement.ToolTipProperty, _originalToolTip);
        }

        /// <summary>
        /// 取属性的本地值：有绑定时拿到的是 BindingExpression，可以原样还回去。
        /// </summary>
        private static object CaptureLocalValue(DependencyObject target, DependencyProperty property)
        {
            return target.ReadLocalValue(property);
        }

        /// <summary>
        /// 把 <see cref="CaptureLocalValue"/> 取到的值还原回去，绑定按绑定还原。
        /// </summary>
        private static void RestoreLocalValue(DependencyObject target, DependencyProperty property, object? saved)
        {
            if (saved is null || saved == DependencyProperty.UnsetValue)
            {
                target.ClearValue(property);
                return;
            }

            if (saved is BindingExpressionBase expression && expression.ParentBindingBase is not null)
            {
                BindingOperations.SetBinding(target, property, expression.ParentBindingBase);
                return;
            }

            target.SetValue(property, saved);
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
