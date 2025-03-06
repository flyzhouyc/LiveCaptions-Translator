using System.Globalization;
using System.Windows.Data;

namespace LiveCaptionsTranslator.utils
{
    public class MillisecondsToSecondsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int milliseconds)
                return milliseconds / 1000;
            return 5; // 默认值
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double seconds || value is int seconds1)
                return (int)((double)value * 1000);
            return 5000; // 默认值
        }
    }
}