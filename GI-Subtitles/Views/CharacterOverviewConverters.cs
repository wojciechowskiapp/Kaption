using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// TextBlock text from a resource key picked at runtime ("Char_Kind_" + kind). Resolves through the
    /// element tree like DynamicResource, so it follows a language switch and works in any host.
    /// Unknown keys show a readable tail ("Char_Kind_dash" → "Dash").
    /// </summary>
    public static class Loc
    {
        public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
            "Key", typeof(string), typeof(Loc), new PropertyMetadata(null, OnKeyChanged));

        private static readonly DependencyProperty ResolvedProperty = DependencyProperty.RegisterAttached(
            "Resolved", typeof(object), typeof(Loc), new PropertyMetadata(null, OnResolvedChanged));

        public static string GetKey(DependencyObject d) => (string)d.GetValue(KeyProperty);
        public static void SetKey(DependencyObject d, string value) => d.SetValue(KeyProperty, value);

        private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is TextBlock text)) return;
            string key = e.NewValue as string;
            text.Text = Fallback(key);
            if (string.IsNullOrEmpty(key)) text.ClearValue(ResolvedProperty);
            else text.SetResourceReference(ResolvedProperty, key);
        }

        private static void OnResolvedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBlock text && e.NewValue is string s && s.Length > 0) text.Text = s;
        }

        internal static string Fallback(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            string tail = key.Substring(key.LastIndexOf('_') + 1);
            return tail.Length == 0 ? key : char.ToUpperInvariant(tail[0]) + tail.Substring(1);
        }
    }

    /// <summary>Local icon files as small frozen bitmaps. Never touches the network; any failure gives null.</summary>
    public static class IconImages
    {
        private const int CacheLimit = 512;
        private const long MaxFileBytes = 8L * 1024 * 1024;
        private static readonly ConcurrentDictionary<string, ImageSource> Loaded =
            new ConcurrentDictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        /// <param name="displaySize">Size on screen in DIP; decoded at twice that to stay sharp on high-DPI screens.</param>
        public static ImageSource Load(string path, int displaySize)
        {
            if (string.IsNullOrWhiteSpace(path) || displaySize <= 0) return null;
            try
            {
                // Rooted local paths only: a UNC or URL source could reach the network.
                if (!Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
                var file = new FileInfo(path);
                if (!file.Exists || file.Length == 0 || file.Length > MaxFileBytes) return null;

                string key = path + "|" + displaySize + "|" + file.LastWriteTimeUtc.Ticks;
                if (Loaded.TryGetValue(key, out var cached)) return cached;

                var image = new BitmapImage();
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    image.DecodePixelWidth = displaySize * 2;
                    image.StreamSource = stream;
                    image.EndInit();
                }
                image.Freeze();
                if (Loaded.Count >= CacheLimit) Loaded.Clear();
                Loaded[key] = image;
                return image;
            }
            catch (Exception)
            {
                return null; // corrupt, locked or an unsupported codec: the view shows its fallback
            }
        }

        // Splash art is large: a few decoded copies at most, separate from the icon cache.
        private const int LargeLimit = 6;
        private static readonly System.Collections.Generic.LinkedList<(string Key, ImageSource Image)> Large =
            new System.Collections.Generic.LinkedList<(string, ImageSource)>();

        /// <summary>A local image decoded to <paramref name="decodeHeight"/> pixels high, kept in a small LRU.</summary>
        public static ImageSource LoadLarge(string path, int decodeHeight)
        {
            if (string.IsNullOrWhiteSpace(path) || decodeHeight <= 0) return null;
            var hit = LargeCachedOrNull(path, decodeHeight);
            if (hit != null) return hit;
            try
            {
                if (!Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
                var file = new FileInfo(path);
                if (!file.Exists || file.Length == 0 || file.Length > MaxFileBytes) return null;
                var image = new BitmapImage();
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    image.DecodePixelHeight = decodeHeight;
                    image.StreamSource = stream;
                    image.EndInit();
                }
                image.Freeze();
                lock (Large)
                {
                    Large.AddFirst((LargeKey(file, decodeHeight), image));
                    while (Large.Count > LargeLimit) Large.RemoveLast();
                }
                return image;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static ImageSource LargeCachedOrNull(string path, int decodeHeight)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) return null;
                string key = LargeKey(file, decodeHeight);
                lock (Large)
                    for (var node = Large.First; node != null; node = node.Next)
                        if (node.Value.Key == key)
                        {
                            Large.Remove(node);
                            Large.AddFirst(node);
                            return node.Value.Image;
                        }
            }
            catch (Exception)
            {
                // treat as not cached
            }
            return null;
        }

        private static string LargeKey(FileInfo file, int h) => file.FullName + "|" + h + "|" + file.LastWriteTimeUtc.Ticks;

        /// <summary>The image if <see cref="Load"/> already decoded it at this size; never decodes.</summary>
        public static ImageSource CachedOrNull(string path, int displaySize)
        {
            if (string.IsNullOrWhiteSpace(path) || displaySize <= 0) return null;
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) return null;
                return Loaded.TryGetValue(path + "|" + displaySize + "|" + file.LastWriteTimeUtc.Ticks, out var cached) ? cached : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static int SizeFrom(object parameter) =>
            parameter is int i ? i
            : int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s
            : 32;
    }

    /// <summary>Icon file path → image. ConverterParameter is the display size in DIP.</summary>
    public sealed class IconImageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            IconImages.Load(value as string, IconImages.SizeFrom(parameter));

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    /// <summary>Visible when the icon can't be shown, for the initial-letter fallback underneath it.</summary>
    public sealed class MissingIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            IconImages.Load(value as string, IconImages.SizeFrom(parameter)) == null ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    /// <summary>Product of the bound numbers (a size times a factor).</summary>
    public sealed class ScaleConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            double product = 1;
            foreach (var v in values)
            {
                if (!(v is double d) || double.IsNaN(d)) return DependencyProperty.UnsetValue;
                product *= d;
            }
            return product;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// The part of the ambience image the header shows. The band is wider than the picture, so a strip of it fills the
    /// band at the picture's own proportions, centred on its focal point and kept inside it (a narrower band takes a
    /// column instead). Relative units, for an ImageBrush with Stretch Fill.
    /// </summary>
    public static class HeroArtFrame
    {
        public static Rect Viewbox(double areaWidth, double areaHeight, double imageWidth, double imageHeight, Point focal)
        {
            if (!(areaWidth > 0 && areaHeight > 0 && imageWidth > 0 && imageHeight > 0)) return new Rect(0, 0, 1, 1);
            double area = areaWidth / areaHeight, image = imageWidth / imageHeight;
            double fx = double.IsNaN(focal.X) ? 0.5 : Math.Clamp(focal.X, 0, 1);
            double fy = double.IsNaN(focal.Y) ? 0.5 : Math.Clamp(focal.Y, 0, 1);
            if (area >= image)
            {
                double h = image / area;
                return new Rect(0, Math.Clamp(fy - h / 2, 0, 1 - h), 1, h);
            }
            double w = area / image;
            return new Rect(Math.Clamp(fx - w / 2, 0, 1 - w), 0, w, 1);
        }
    }

    /// <summary>[band width, band height, image, focal point] → the ImageBrush viewbox (<see cref="HeroArtFrame"/>).</summary>
    public sealed class HeroArtFrameConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            double width = values.Length > 0 && values[0] is double w ? w : 0;
            double height = values.Length > 1 && values[1] is double h ? h : 0;
            var image = values.Length > 2 ? values[2] as BitmapSource : null;
            var focal = values.Length > 3 && values[3] is Point p ? p : new Point(0.5, 0.5);
            return HeroArtFrame.Viewbox(width, height, image?.PixelWidth ?? 0, image?.PixelHeight ?? 0, focal);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Collapses empty strings and empty lists.</summary>
    public sealed class EmptyToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool empty = value == null ||
                         (value is string s && s.Length == 0) ||
                         (value is ICollection c && c.Count == 0);
            return empty ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }
}
