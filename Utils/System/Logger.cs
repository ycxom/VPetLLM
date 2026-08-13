using System.Collections.ObjectModel;
using System.Windows.Controls;
using VPetLLM.UI.Windows;
using SystemDateTime = System.DateTime;
using SystemText = System.Text;
using SystemWindows = System.Windows;

namespace VPetLLM.Utils.System
{
    public static class Logger
    {
        public static ObservableCollection<string> Logs { get; } = new ObservableCollection<string>();

        private static WeakReference<ScrollViewer>? _cachedLogScroller;
        private static DateTime _lastScrollUtc = DateTime.MinValue;
        private static readonly TimeSpan ScrollThrottle = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// 角色设定含 VPetLLM_DeBug 时才写热路径明细。
        /// </summary>
        public static bool VerboseEnabled =>
            VPetLLM.Instance?.Settings is not null
            && ErrorMessageHelper.IsDebugMode(VPetLLM.Instance.Settings);

        public static void LogVerbose(string message)
        {
            if (!VerboseEnabled)
                return;
            Log(message);
        }

        public static void Log(string message)
        {
            // 使用 BeginInvoke 异步调度，避免阻塞调用线程
            var app = SystemWindows.Application.Current;
            if (app is null) return; // 应用程序未初始化时跳过

            // 预先格式化消息，避免在 UI 线程中进行
            var formattedMessage = FormatLogMessage(message);

            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                Logs.Add(formattedMessage);

                var settings = VPetLLM.Instance?.Settings;
                // 上限 <= 0 一律回落到默认值：设置页只做 int.TryParse，0 和负数都存得进来，
                // 当成"不裁剪"会让 Logs 无限增长，把设置页的 ListBox 拖死。
                var maxCount = settings?.MaxLogCount ?? 1000;
                if (maxCount <= 0)
                    maxCount = 1000;

                if (Logs.Count > maxCount)
                {
                    var excess = Logs.Count - maxCount;
                    // RemoveAt(0) 每次 O(n)，超限时按批裁，避免一条条挪数组
                    var remove = Math.Min(excess + Math.Max(32, maxCount / 20), Logs.Count - 1);
                    for (var i = 0; i < remove; i++)
                    {
                        Logs.RemoveAt(0);
                    }
                }

                if (settings?.LogAutoScroll == true && Logs.Count > 0)
                    TryScrollLogBox(app);
            }));
        }

        private static void TryScrollLogBox(SystemWindows.Application app)
        {
            var now = DateTime.UtcNow;
            if (now - _lastScrollUtc < ScrollThrottle)
                return;
            _lastScrollUtc = now;

            if (_cachedLogScroller is not null
                && _cachedLogScroller.TryGetTarget(out var cached)
                && cached.IsLoaded)
            {
                ScrollAfterLayout(app, cached);
                return;
            }

            ScrollViewer? scrollViewer = null;
            foreach (SystemWindows.Window window in app.Windows)
            {
                if (window is not winSettingNew settingWindow || !settingWindow.IsLoaded)
                    continue;

                if (settingWindow.FindName("LogBox") is ListBox logBox)
                    scrollViewer = FindScrollViewer(logBox);

                if (scrollViewer is not null)
                    break;
            }

            if (scrollViewer is null)
                return;

            _cachedLogScroller = new WeakReference<ScrollViewer>(scrollViewer);
            ScrollAfterLayout(app, scrollViewer);
        }

        /// <summary>
        /// 必须等布局跑完再滚：此刻刚 Add 进 Logs 的那条还没被 ItemsPanel 实现，
        /// ScrollToEnd 读到的 ExtentHeight 是旧值，直接调会永远停在倒数第二条。
        /// </summary>
        private static void ScrollAfterLayout(SystemWindows.Application app, ScrollViewer scrollViewer)
        {
            app.Dispatcher.BeginInvoke(
                SystemWindows.Threading.DispatcherPriority.Background,
                new Action(() =>
                {
                    if (Logs.Count > 0)
                        scrollViewer.ScrollToEnd();
                }));
        }

        /// <summary>
        /// 格式化日志消息，添加时间戳
        /// </summary>
        /// <param name="message">原始消息</param>
        /// <returns>格式化后的消息</returns>
        public static string FormatLogMessage(string message)
        {
            return $"[{SystemDateTime.Now:G}] {message}";
        }

        /// <summary>
        /// 解析格式化的日志消息，提取时间戳和内容
        /// </summary>
        /// <param name="formattedMessage">格式化的日志消息</param>
        /// <returns>时间戳和消息内容的元组，解析失败时返回默认值</returns>
        public static (DateTime timestamp, string content) ParseLogMessage(string formattedMessage)
        {
            if (string.IsNullOrEmpty(formattedMessage))
                return (DateTime.MinValue, string.Empty);

            // 格式: [timestamp] message
            var match = SystemText.RegularExpressions.Regex.Match(formattedMessage, @"^\[(.+?)\]\s*(.*)$");
            if (match.Success)
            {
                if (DateTime.TryParse(match.Groups[1].Value, out var timestamp))
                {
                    return (timestamp, match.Groups[2].Value);
                }
            }

            // 解析失败，返回原始消息作为内容
            return (DateTime.MinValue, formattedMessage);
        }

        /// <summary>
        /// 查找控件内部的 ScrollViewer
        /// </summary>
        private static ScrollViewer FindScrollViewer(SystemWindows.DependencyObject obj)
        {
            if (obj is null) return null;

            for (int i = 0; i < SystemWindows.Media.VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var child = SystemWindows.Media.VisualTreeHelper.GetChild(obj, i);
                if (child is ScrollViewer scrollViewer)
                {
                    return scrollViewer;
                }

                var result = FindScrollViewer(child);
                if (result is not null)
                {
                    return result;
                }
            }

            return null;
        }

        public static void Clear()
        {
            var app = SystemWindows.Application.Current;
            if (app is null) return;

            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                Logs.Clear();
            }));
        }
    }
}