using System.Collections.ObjectModel;

namespace VPetLLM
{
    public static class Logger
    {
        public static ObservableCollection<string> Logs { get; } = new ObservableCollection<string>();

        public static void Log(string message)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Logs.Add($"[{System.DateTime.Now:G}] {message}");
            });
        }
    }
}