using System;
using System.Globalization;
using System.Windows.Data;

namespace TeknoParrotBigBox
{
    /// <summary>
    /// 布尔值取反转换器：true -> false, false -> true。
    /// </summary>
    public class BooleanInverterConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
            {
                return !b;
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
            {
                return !b;
            }
            return value;
        }
    }
}

