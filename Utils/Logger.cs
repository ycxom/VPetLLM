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
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Logs.Add($"[{System.DateTime.Now:G}] {message}");

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
                    // 使用Dispatcher延迟执行滚动，确保UI已更新
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                    {
                        // 线程安全检查：确保集合仍然有元素
                        if (Logs.Count == 0) return;
                        
                        // 查找当前活动的设置窗口并滚动日志框
                        var windows = System.Windows.Application.Current.Windows;
                        foreach (System.Windows.Window window in windows)
                        {
                            var settingWindow = window as winSettingNew;
                            if (settingWindow != null)
                            {
                                var logBox = (ListBox)settingWindow.FindName("LogBox");
                                if (logBox != null && Logs.Count > 0)
                                {
                                    // 再次检查集合大小，防止在访问时被其他线程修改
                                    var lastIndex = Logs.Count - 1;
                                    if (lastIndex >= 0 && lastIndex < Logs.Count)
                                    {
                                        logBox.ScrollIntoView(Logs[lastIndex]);
                                    }
                                    break;
                                }
                            }
                        }
                    }));
                }
            });
        }

        public static void Clear()
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Logs.Clear();
            });
        }
    }
}