using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace TeknoParrotBigBox
{
    /// <summary>
    /// 将路径字符串或 null 转为 ImageSource，避免 WPF 默认转换器对 null 报错。
    /// 当值为 null 或空字符串时返回 null；否则从路径加载图片，加载失败也返回 null。
    /// BitmapImage 必须在 UI 线程创建，故在非 UI 线程时通过 Dispatcher 切回 UI 线程再创建。
    /// </summary>
    public class NullToImageSourceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string path = value as string;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            // WPF 的 BitmapImage 只能在 UI 线程创建；无 Application 时直接返回 null 避免异常
            if (Application.Current == null)
                return null;
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                return Application.Current.Dispatcher.Invoke(
                    () => Convert(value, targetType, parameter, culture));
            }

            try
            {
                string fullPath = Path.GetFullPath(path);
                // 用 Stream 加载，避免 Uri 方式在部分场景下触发 PresentationFramework 的 NotSupportedException
                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = stream;
                    img.EndInit();
                    img.Freeze();
                    return img;
                }
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }
}
