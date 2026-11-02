using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VPetLLM.UI.Controls
{
    /// <summary>
    /// 滚轮平滑滚动。WPF 默认一格滚轮在一帧里跳 48 像素，列表再快也是一顿一顿的。
    /// 挂在 ScrollViewer 或内部有 ScrollViewer 的列表上；列表要用 ScrollUnit="Pixel"，偏移量才是像素。
    /// </summary>
    public static class SmoothWheelScroll
    {
        // 和 WPF 默认一样：滚轮设置里每行 16 像素
        private const double LineHeight = 16;

        // 越小停得越快，约 3 倍这个时间走完
        private const double TimeConstant = 0.045;

        // Rendering 一帧可能触发很多次，改偏移量又会催出下一次，不限制就是空转，而宠物动画也在这条线程上
        private static readonly TimeSpan MinFrameGap = TimeSpan.FromMilliseconds(12);

        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(SmoothWheelScroll), new PropertyMetadata(false, OnEnabledChanged));

        private static readonly DependencyProperty AnimationProperty = DependencyProperty.RegisterAttached(
            "Animation", typeof(Animation), typeof(SmoothWheelScroll));

        public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

        public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

        /// <summary>按实际经过的时间走，卡顿时一帧走得远些，总时长不变。快到时直接落到目标上。</summary>
        public static double Approach(double position, double target, double seconds)
        {
            var next = target + (position - target) * Math.Exp(-seconds / TimeConstant);
            return Math.Abs(target - next) < 0.5 ? target : next;
        }

        /// <summary>lines 是系统设置里每格滚动的行数，-1 表示“滚动一屏”。</summary>
        public static double WheelStep(int lines, double viewport) => lines < 0 ? viewport : lines * LineHeight;

        private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (element is not UIElement target) return;

            target.PreviewMouseWheel -= OnPreviewMouseWheel;
            if ((bool)e.NewValue) target.PreviewMouseWheel += OnPreviewMouseWheel;
        }

        // 用 Preview：ScrollViewer 自己的处理在冒泡阶段，等它就晚了；下拉框也不会再把滚轮拿去改选中项
        private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (FindViewer((DependencyObject)sender) is not { } viewer) return;
            if (!OwnsWheel(viewer, e.OriginalSource as DependencyObject)) return;

            e.Handled = true;
            var animation = (Animation?)viewer.GetValue(AnimationProperty);
            if (animation is null)
            {
                animation = new Animation(viewer);
                viewer.SetValue(AnimationProperty, animation);
            }

            animation.Add(-e.Delta / 120.0 * WheelStep(SystemParameters.WheelScrollLines, viewer.ViewportHeight));
        }

        private static bool OwnsWheel(ScrollViewer viewer, DependencyObject? source)
        {
            // 下拉列表在单独的弹层里，往上走不到 viewer；展开时光标移到外面，事件则落在下拉框本身
            for (var element = source; element is not null; element = Parent(element))
            {
                if (ReferenceEquals(element, viewer)) return true;
                if (element is ComboBox { IsDropDownOpen: true }) return false;
                if (element is ScrollViewer { ScrollableHeight: > 0 }) return false;
            }

            return false;
        }

        // Run 这类内容元素不是 Visual，VisualTreeHelper 会抛异常
        private static DependencyObject? Parent(DependencyObject element)
            => element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

        private static ScrollViewer? FindViewer(DependencyObject element)
        {
            if (element is ScrollViewer viewer) return viewer;

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            {
                if (FindViewer(VisualTreeHelper.GetChild(element, i)) is { } found) return found;
            }

            return null;
        }

        private sealed class Animation(ScrollViewer viewer)
        {
            private TimeSpan? _lastFrame;
            private bool _running;
            private double _position;
            private double _target;

            public void Add(double distance)
            {
                if (!_running) _position = _target = viewer.VerticalOffset;

                _target = Math.Clamp(_target + distance, 0, viewer.ScrollableHeight);
                if (_running) return;

                _running = true;
                _lastFrame = null;
                CompositionTarget.Rendering += OnFrame;
            }

            private void OnFrame(object? sender, EventArgs e)
            {
                var now = ((RenderingEventArgs)e).RenderingTime;
                if (_lastFrame is { } last)
                {
                    if (now - last < MinFrameGap) return;
                    _position = Approach(_position, _target, (now - last).TotalSeconds);
                }

                _lastFrame = now;
                viewer.ScrollToVerticalOffset(_position);
                if (_position == _target) Stop();
            }

            private void Stop()
            {
                _running = false;
                CompositionTarget.Rendering -= OnFrame;
            }
        }
    }
}
