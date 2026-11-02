using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Shared plumbing for the render-to-PNG tests: STA runner, pack URIs, string dictionaries, layout,
    /// snapshots, and a loader that turns a window's XAML into a renderable tree without its code-behind.
    /// Output goes to %TEMP%\kaption-render\.
    /// </summary>
    internal static class RenderTestKit
    {
        public static string OutputDir => Path.Combine(Path.GetTempPath(), "kaption-render");

        public static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "GI-Subtitles.sln"))) return dir.FullName;
            return null;
        }

        public static void RunSta(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                // As on the UI thread: awaits inside views resume on this dispatcher, which DoEvents pumps.
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                Dispatcher.CurrentDispatcher.UnhandledException += (s, e) => Console.WriteLine("Dispatcher: " + e.Exception);
                try { action(); }
                catch (Exception ex) { error = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }

        public static void DoEvents()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new DispatcherOperationCallback(f => { ((DispatcherFrame)f).Continue = false; return null; }), frame);
            Dispatcher.PushFrame(frame);
        }

        public static string AssemblyName => typeof(CharacterOverviewView).Assembly.GetName().Name;

        public static Uri Pack(string path)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
            return new Uri($"pack://application:,,,/{AssemblyName};component/{path}", UriKind.Absolute);
        }

        public static ResourceDictionary Dictionary(string path) => new ResourceDictionary { Source = Pack(path) };

        /// <summary>en-US first, then the chosen language on top: the app's own lookup order.</summary>
        public static IEnumerable<ResourceDictionary> Strings(string lang)
        {
            foreach (string culture in lang == "en-US" ? new[] { "en-US" } : new[] { "en-US", lang })
                yield return Dictionary($"Resources/Strings.{culture}.xaml");
        }

        /// <summary>Measures and arranges at a fixed width; the height is fixed too when given.</summary>
        public static void Layout(FrameworkElement host, double width, double height = double.NaN)
        {
            host.Width = width;
            if (!double.IsNaN(height)) host.Height = height;
            for (int i = 0; i < 3; i++)
            {
                host.Measure(new Size(width, double.IsNaN(height) ? double.PositiveInfinity : height));
                host.Arrange(new Rect(new Point(0, 0), host.DesiredSize));
                host.UpdateLayout();
                DoEvents();
            }
        }

        public static BitmapSource Snapshot(FrameworkElement host, double scale = 1.0)
        {
            int w = (int)Math.Ceiling(host.ActualWidth * scale), h = (int)Math.Ceiling(host.ActualHeight * scale);
            var rtb = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(host);
            rtb.Freeze();
            return rtb;
        }

        public static BitmapSource SideBySide(IReadOnlyList<BitmapSource> shots, int gap = 16)
        {
            int w = shots.Sum(s => s.PixelWidth) + gap * (shots.Count + 1);
            int h = shots.Max(s => s.PixelHeight) + gap * 2;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5A, 0x5F, 0x6B)), null, new Rect(0, 0, w, h));
                double x = gap;
                foreach (var s in shots)
                {
                    dc.DrawImage(s, new Rect(x, gap, s.PixelWidth, s.PixelHeight));
                    x += s.PixelWidth + gap;
                }
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        public static void AssertNotBlank(BitmapSource bitmap, string name)
        {
            Assert.IsTrue(bitmap.PixelWidth > 100 && bitmap.PixelHeight > 100, $"{name}: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
            int stride = bitmap.PixelWidth * 4;
            var px = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(px, stride, 0);
            var distinct = new HashSet<int>();
            for (int i = 0; i < px.Length && distinct.Count < 64; i += 4 * 7)
                distinct.Add(px[i] | px[i + 1] << 8 | px[i + 2] << 16);
            Assert.IsTrue(distinct.Count >= 16, $"{name} looks blank ({distinct.Count} colours).");
        }

        /// <summary>Checks the bitmap is not blank and writes it to the output folder; returns the path.</summary>
        public static string Save(BitmapSource bitmap, string name)
        {
            AssertNotBlank(bitmap, name);
            Directory.CreateDirectory(OutputDir);
            string path = Path.Combine(OutputDir, name);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var fs = File.Create(path)) encoder.Save(fs);
            return path;
        }

        public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) yield return t;
                foreach (var d in Descendants<T>(child)) yield return d;
            }
        }

        // ── window XAML without code-behind ──────────────────────────────

        private static readonly Lazy<HashSet<string>> EventNames = new Lazy<HashSet<string>>(() =>
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var assemblies = new[]
            {
                typeof(UIElement).Assembly, typeof(FrameworkElement).Assembly, typeof(DependencyObject).Assembly,
                typeof(CharacterOverviewView).Assembly,
            };
            foreach (var asm in assemblies)
            foreach (var type in ExportedTypes(asm))
            foreach (var e in type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                names.Add(e.Name);
            foreach (var re in EventManager.GetRoutedEvents()) names.Add(re.Name);
            return names;
        });

        private static IEnumerable<Type> ExportedTypes(Assembly asm)
        {
            try { return asm.GetExportedTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
        }

        // An event attribute: (Owner.)EventName="HandlerName".
        private static readonly Regex Handler = new Regex(
            "\\s(?:[A-Za-z_]\\w*\\.)?(?<name>[A-Za-z_]\\w*)=\"(?<value>[A-Za-z_]\\w*)\"", RegexOptions.CultureInvariant);

        private static readonly Regex ClassAttributes = new Regex(
            "\\sx:(Class|FieldModifier|ClassModifier|Subclass)=\"[^\"]*\"", RegexOptions.CultureInvariant);

        private static readonly Regex LocalNamespace = new Regex(
            "(xmlns:\\w+=\"clr-namespace:[\\w.]+)\"", RegexOptions.CultureInvariant);

        /// <summary>
        /// Parses GI-Subtitles/&lt;viewPath&gt; without x:Class and event handlers, then moves the content into a
        /// plain host that carries the window's background, its resources and the UI strings. Code-behind never
        /// runs, so text it sets at runtime shows the XAML defaults.
        /// </summary>
        public static Border LoadLooseWindow(string viewPath, string lang, out Window window)
        {
            string root = RepoRoot() ?? throw new AssertInconclusiveException("Repository root not found.");
            string xaml = File.ReadAllText(Path.Combine(root, "GI-Subtitles", viewPath.Replace('/', Path.DirectorySeparatorChar)));
            xaml = ClassAttributes.Replace(xaml, "");
            xaml = Regex.Replace(xaml, "<EventSetter\\b[^>]*/>", "");
            xaml = Handler.Replace(xaml, m => EventNames.Value.Contains(m.Groups["name"].Value) ? "" : m.Value);
            xaml = LocalNamespace.Replace(xaml, "$1;assembly=" + AssemblyName + "\"");

            var context = new ParserContext { BaseUri = Pack(viewPath) };
            window = (Window)XamlReader.Parse(xaml, context);
            var content = (UIElement)window.Content;
            var resources = window.Resources;
            window.Content = null;
            window.Resources = new ResourceDictionary();

            var host = new Border { Background = window.Background ?? Brushes.White, Child = content, UseLayoutRounding = true };
            foreach (var d in Strings(lang)) host.Resources.MergedDictionaries.Add(d);
            host.Resources.MergedDictionaries.Add(resources);
            return host;
        }
    }
}
