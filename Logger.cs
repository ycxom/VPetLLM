using System.Collections.ObjectModel;

namespace VPetLLM
{
    public static class Logger
    {
        public static ObservableCollection<string> Logs { get; } = new ObservableCollection<string>();
        public static int MaxLogCount { get; set; } = 1000; // 默认最大日志数量
        public static bool AutoScroll { get; set; } = true; // 默认启用自动滚动

        public static void Log(string message)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Logs.Add($"[{System.DateTime.Now:G}] {message}");
                
                // 如果超过最大日志数量，移除最早的日志
                if (Logs.Count > MaxLogCount)
                {
                    while (Logs.Count > MaxLogCount)
                    {
                        Logs.RemoveAt(0);
                    }
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

        public static void SetMaxLogCount(int maxCount)
        {
            MaxLogCount = maxCount;
            
            // 立即应用新的最大数量限制
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (Logs.Count > MaxLogCount)
                {
                    while (Logs.Count > MaxLogCount)
                    {
                        Logs.RemoveAt(0);
                    }
                }
            });
        }
    }
}