using System.Collections.ObjectModel;
using System.Windows.Controls;
using VPetLLM.UI.Windows;

namespace VPetLLM.Utils
{
    public static class Logger
    {
        public static ObservableCollection<string> Logs { get; } = new ObservableCollection<string>();

        public static void Log(string message)
        {
            // 使用 BeginInvoke 异步调度，避免阻塞调用线程
            var app = System.Windows.Application.Current;
            if (app == null) return; // 应用程序未初始化时跳过
            
            // 预先格式化消息，避免在 UI 线程中进行
            var formattedMessage = FormatLogMessage(message);
            
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                Logs.Add(formattedMessage);

                // 如果超过最大日志数量，移除最早的日志
                if (VPetLLM.Instance != null && VPetLLM.Instance.Settings != null && Logs.Count > VPetLLM.Instance.Settings.MaxLogCount)
                {
                    while (Logs.Count > VPetLLM.Instance.Settings.MaxLogCount)
                    {
                        Logs.RemoveAt(0);
                    }
                }

                // 如果启用自动滚动，滚动到最新条目
                if (VPetLLM.Instance != null && VPetLLM.Instance.Settings != null && VPetLLM.Instance.Settings.LogAutoScroll && Logs.Count > 0)
                {
                    // 滚动操作已经在 UI 线程中，使用 BeginInvoke 延迟执行确保 UI 已更新
                    app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                    {
                        // 线程安全检查：确保集合仍然有元素
                        if (Logs.Count == 0) return;
                        
                        // 查找当前活动的设置窗口并滚动日志框
                        var windows = app.Windows;
                        foreach (System.Windows.Window window in windows)
                        {
                            var settingWindow = window as winSettingNew;
                            if (settingWindow != null)
                            {
                                var logBox = (ListBox)settingWindow.FindName("LogBox");
                                if (logBox != null)
                                {
                                    // 使用 ScrollViewer 进行平滑滚动，而不是按条目跳转
                                    var scrollViewer = FindScrollViewer(logBox);
                                    if (scrollViewer != null)
                                    {
                                        scrollViewer.ScrollToEnd();
                                    }
                                    break;
                                }
                            }
                        }
                    }));
                }
            }));
        }

        /// <summary>
        /// 格式化日志消息，添加时间戳
        /// </summary>
        /// <param name="message">原始消息</param>
        /// <returns>格式化后的消息</returns>
        public static string FormatLogMessage(string message)
        {
            return $"[{System.DateTime.Now:G}] {message}";
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
            var match = System.Text.RegularExpressions.Regex.Match(formattedMessage, @"^\[(.+?)\]\s*(.*)$");
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
        private static ScrollViewer FindScrollViewer(System.Windows.DependencyObject obj)
        {
            if (obj == null) return null;

            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(obj, i);
                if (child is ScrollViewer scrollViewer)
                {
                    return scrollViewer;
                }

                var result = FindScrollViewer(child);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        public static void Clear()
        {
            var app = System.Windows.Application.Current;
            if (app == null) return;
            
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                Logs.Clear();
            }));
        }
    }
}