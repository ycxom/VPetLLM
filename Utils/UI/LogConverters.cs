using System.Globalization;
using System.Windows.Data;
using VPetLLM.Utils.System;

namespace VPetLLM.Utils.UI
{
    public class LogTimestampConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string logMessage)
            {
                var (timestamp, _) = Logger.ParseLogMessage(logMessage);
                return timestamp != DateTime.MinValue ? $"[{timestamp:G}]" : "";
            }
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class LogMessageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string logMessage)
            {
                var (_, content) = Logger.ParseLogMessage(logMessage);
                return content;
            }
            return value?.ToString() ?? "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
