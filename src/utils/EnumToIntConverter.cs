using System;
using System.Globalization;
using System.Windows.Data;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator
{
    public class EnumToIntConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is TranslateAPIConfig.ProxyType proxyType)
            {
                return (int)proxyType;
            }
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intValue)
            {
                return (TranslateAPIConfig.ProxyType)intValue;
            }
            return TranslateAPIConfig.ProxyType.Http;
        }
    }
}