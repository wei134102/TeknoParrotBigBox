using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace TeknoParrotBigBox
{
    /// <summary>
    /// 将路径字符串或 null 转为 ImageSource，避免 WPF 默认转换器对 null 报错。
    /// 内置静态缓存（按绝对路径）：同一封面图多处绑定时，只从磁盘加载一次，复用 BitmapImage 实例。
    /// 可选的尺寸参数：传入数字时按 DecodePixelWidth 解码，降低内存占用和 BlurEffect 开销。
    /// </summary>
    public class NullToImageSourceConverter : IValueConverter
    {
        /// <summary>按绝对路径缓存已加载的 BitmapImage。BitmapImage.Freeze() 后跨线程可安全使用。</summary>
        private static readonly ConcurrentDictionary<string, BitmapImage> ImageCache =
            new ConcurrentDictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);

        /// <summary>小图缓存（用于背景模糊，256px 宽度解码）。</summary>
        private static readonly ConcurrentDictionary<string, BitmapImage> ThumbCache =
            new ConcurrentDictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);

        private const int BlurThumbWidth = 256;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string path = value as string;
            if (string.IsNullOrWhiteSpace(path)) return null;

            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { return null; }
            if (!File.Exists(fullPath)) return null;

            // 根据 parameter 决定缓存池和目标尺寸
            bool useThumb = parameter is string paramStr && string.Equals(paramStr, "thumb", StringComparison.OrdinalIgnoreCase);
            var cache = useThumb ? ThumbCache : ImageCache;

            if (cache.TryGetValue(fullPath, out var cached))
                return cached;

            // WPF 的 BitmapImage 必须在 UI 线程创建
            if (Application.Current == null) return null;
            if (!Application.Current.Dispatcher.CheckAccess())
                return Application.Current.Dispatcher.Invoke(
                    () => Convert(value, targetType, parameter, culture));

            try
            {
                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = stream;
                    if (useThumb)
                    {
                        // 解码时按目标尺寸缩小，大幅降低内存和后续 BlurEffect 开销
                        img.DecodePixelWidth = BlurThumbWidth;
                        img.DecodePixelHeight = BlurThumbWidth;
                    }
                    img.EndInit();
                    img.Freeze();
                    cache.TryAdd(fullPath, img);
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

        /// <summary>清除所有缓存（设置 MediaPath 或重新扫描媒体后调用）。</summary>
        public static void ClearCache()
        {
            ImageCache.Clear();
            ThumbCache.Clear();
        }
    }
}
