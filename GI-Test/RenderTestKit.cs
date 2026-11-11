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
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GI_Subtitles.Services.Detection;
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

        /// <summary>
        /// The 2.6 review renders: %TEMP%\kaption-w26\renders\&lt;yyyyMMdd-HHmm&gt;\, one dated folder per test run (set
        /// KAPTION_RENDER_STAMP to choose the name), so a review copy is never overwritten by another agent's run.
        /// </summary>
        public static string ReviewDir => ReviewDirValue.Value;
        private static readonly Lazy<string> ReviewDirValue = new(() => Path.Combine(Path.GetTempPath(), "kaption-w26", "renders",
            Environment.GetEnvironmentVariable("KAPTION_RENDER_STAMP") is { Length: > 0 } stamp ? stamp : DateTime.Now.ToString("yyyyMMdd-HHmm")));

        /// <summary>Writes a review render into <see cref="ReviewDir"/> (sub-folder allowed in the name); returns the path.</summary>
        public static string SaveReview(BitmapSource bitmap, string name)
        {
            AssertNotBlank(bitmap, name);
            string path = Path.Combine(ReviewDir, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var fs = File.Create(path)) encoder.Save(fs);
            return path;
        }

        public static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "GI-Subtitles.sln"))) return dir.FullName;
            return null;
        }

        /// <summary>
        /// Runs a WPF test on its own STA thread: one UI test at a time on this machine, whichever test process runs it
        /// (<see cref="MachineUiLock"/>), and no input from the desktop reaches its windows (<see cref="DropDesktopInput"/>).
        /// </summary>
        public static void RunSta(Action action)
        {
            using var uiLock = MachineUiLock.Acquire();
            Exception error = null;
            var thread = new Thread(() =>
            {
                // As on the UI thread: awaits inside views resume on this dispatcher, which DoEvents pumps.
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                Dispatcher.CurrentDispatcher.UnhandledException += (s, e) => Console.WriteLine("Dispatcher: " + e.Exception);
                InputManager.Current.PreProcessInput += DropDesktopInput;
                try { action(); }
                catch (Exception ex) { error = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }

        /// <summary>
        /// UI tests never type or click, so keys, text and mouse clicks from the desktop are dropped. Windows the
        /// product shows take the foreground (the fix window calls Activate and focuses its box), and on a desktop
        /// someone is using, their keystrokes landed there: a "/" or a Backspace in the fix box failed its test.
        /// Focus and activation still go through.
        /// </summary>
        internal static void DropDesktopInput(object sender, PreProcessInputEventArgs e)
        {
            if (e.StagingItem.Input is KeyEventArgs or TextCompositionEventArgs or MouseButtonEventArgs or MouseWheelEventArgs)
                e.Cancel();
        }

        /// <summary>
        /// A machine-wide mutex around each WPF UI test. CI runs two Windows runners in one desktop session (and agents
        /// run GI-Test there too), so without it two test processes show and activate windows at once and take each
        /// other's focus, and write same-named renders (%TEMP%\kaption-render, the review folder) at once. Tests outside
        /// <see cref="RunSta"/> still run side by side. Acquired and released on the calling thread (a mutex is owned
        /// by a thread).
        /// </summary>
        internal sealed class MachineUiLock : IDisposable
        {
            public const string Name = @"Global\KaptionGiTestWpfUi";

            /// <summary>Far longer than any UI test (the slowest render takes ~16 s), under CI's 10-minute hang timeout.</summary>
            public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

            private readonly Mutex _mutex;
            private readonly Thread _owner = Thread.CurrentThread;

            private MachineUiLock(Mutex mutex) => _mutex = mutex;

            public static MachineUiLock Acquire() => Acquire(Name, Timeout);

            internal static MachineUiLock Acquire(string name, TimeSpan timeout)
            {
                Mutex mutex;
                try { mutex = new Mutex(false, name); }
                catch (UnauthorizedAccessException)
                {
                    // Created by another Windows account (its default ACL shuts us out): that account has its own desktop.
                    Console.WriteLine($"WPF UI lock: {name} belongs to another account; running without it.");
                    return new MachineUiLock(null);
                }
                var waited = System.Diagnostics.Stopwatch.StartNew();
                bool busy = false;
                try
                {
                    if (!mutex.WaitOne(0))
                    {
                        busy = true;
                        if (!mutex.WaitOne(timeout))
                        {
                            mutex.Dispose();
                            Assert.Fail($"WPF UI lock: another test process held {name} for over {timeout.TotalSeconds:0} s. " +
                                "A UI test there is hung, or a stuck testhost.exe still holds it.");
                        }
                    }
                }
                catch (AbandonedMutexException)
                {
                    // Ours now: its last holder was killed mid-test (a cancelled run, a hang dump).
                    Console.WriteLine($"WPF UI lock: {name} was abandoned by a test process that exited; taking it over.");
                }
                if (busy) Console.WriteLine($"WPF UI lock: waited {waited.ElapsedMilliseconds} ms for another test process's UI test.");
                return new MachineUiLock(mutex);
            }

            public void Dispose()
            {
                if (_mutex == null) return;
                if (Thread.CurrentThread != _owner)
                    throw new InvalidOperationException("WPF UI lock: released on another thread than the one that took it.");
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
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

        // ── contrast (WCAG ratio of a text against what is drawn behind it) ──

        public static Color Fg(Brush brush) => brush is SolidColorBrush s ? s.Color : Colors.White;

        /// <summary>Where a text's glyphs are: the element's box, narrowed to the text it holds (a stretched line is
        /// as wide as its column, the words are not).</summary>
        public static Rect InkBox(FrameworkElement element, FrameworkElement host)
        {
            FrameworkElement text = element is TextBlock ? element : Descendants<TextBlock>(element).FirstOrDefault() ?? element;
            double width = text is TextBlock tb
                ? Math.Min(tb.ActualWidth, tb.DesiredSize.Width - tb.Margin.Left - tb.Margin.Right)
                : text.ActualWidth;
            return text.TransformToAncestor(host).TransformBounds(new Rect(0, 0, Math.Max(1, width), text.ActualHeight));
        }

        /// <summary>The relative luminance below which <paramref name="fraction"/> of the pixels in a box fall.</summary>
        public static double BrightestLuminance(BitmapSource bitmap, Rect box, double fraction)
        {
            int x0 = Math.Max(0, (int)Math.Floor(box.X)), y0 = Math.Max(0, (int)Math.Floor(box.Y));
            int x1 = Math.Min(bitmap.PixelWidth, (int)Math.Ceiling(box.Right)), y1 = Math.Min(bitmap.PixelHeight, (int)Math.Ceiling(box.Bottom));
            int w = x1 - x0, h = y1 - y0;
            Assert.IsTrue(w > 0 && h > 0, $"the text box {box} lies on the render");
            var pixels = new byte[w * h * 4];
            bitmap.CopyPixels(new Int32Rect(x0, y0, w, h), pixels, w * 4, 0);
            var lum = new double[w * h];
            for (int i = 0; i < lum.Length; i++)
                lum[i] = RelativeLuminance(Color.FromRgb(pixels[i * 4 + 2], pixels[i * 4 + 1], pixels[i * 4]));
            Array.Sort(lum);
            return lum[Math.Min(lum.Length - 1, (int)(lum.Length * fraction))];
        }

        public static double RelativeLuminance(Color c)
        {
            static double Lin(byte v) { double s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }

        public static double ContrastRatio(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

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

        /// <summary>The game pickers the windows' constructors build from the registered games, built the same way.</summary>
        public static void BuildGamePickers(Window window)
        {
            if (window.FindName("GamePills") is Panel pills) SettingsWindow.BuildGamePills(pills, GameRegionProfile.RegisteredProfiles, null);
            if (window.FindName("GameSelector") is ComboBox selector) SettingsWindow.FillGameSelector(selector, GameRegionProfile.RegisteredProfiles);
            if (window.FindName("GameRadios") is Panel radios) OnboardingWindow.BuildGameRadios(radios, GameRegionProfile.RegisteredProfiles, null);
        }

        /// <summary>The picker entry for <paramref name="gameId"/> inside a named panel (GamePills, GameRadios).</summary>
        public static T GamePicker<T>(Func<string, object> find, string panel, string gameId) where T : FrameworkElement =>
            ((Panel)find(panel)).Children.OfType<T>().First(e => string.Equals(e.Tag as string, gameId, StringComparison.Ordinal));

        /// <summary>
        /// Parses GI-Subtitles/&lt;viewPath&gt; without x:Class and event handlers, then moves the content into a
        /// plain host that carries the window's background, its resources and the UI strings. Code-behind never
        /// runs, so text it sets at runtime shows the XAML defaults.
        /// </summary>
        public static Border LoadLooseWindow(string viewPath, string lang, out Window window, string palette = "Light")
        {
            string root = RepoRoot() ?? throw new AssertInconclusiveException("Repository root not found.");
            string xaml = File.ReadAllText(Path.Combine(root, "GI-Subtitles", viewPath.Replace('/', Path.DirectorySeparatorChar)));
            xaml = ClassAttributes.Replace(xaml, "");
            xaml = Regex.Replace(xaml, "<EventSetter\\b[^>]*/>", "");
            xaml = Handler.Replace(xaml, m => EventNames.Value.Contains(m.Groups["name"].Value) ? "" : m.Value);
            xaml = LocalNamespace.Replace(xaml, "$1;assembly=" + AssemblyName + "\"");

            var context = new ParserContext { BaseUri = Pack(viewPath) };
            window = (Window)XamlReader.Parse(xaml, context);
            BuildGamePickers(window);
            var content = (UIElement)window.Content;
            var resources = window.Resources;
            window.Content = null;
            window.Resources = new ResourceDictionary();

            var host = new Border { Child = content, UseLayoutRounding = true, SnapsToDevicePixels = true };
            // The app-level palette slot (ThemeService) comes first; window resources may not redefine its keys.
            host.Resources.MergedDictionaries.Add(Dictionary("Themes/Tokens.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary($"Themes/Palette.{palette}.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary("Themes/Shell.xaml"));
            foreach (var d in Strings(lang)) host.Resources.MergedDictionaries.Add(d);
            host.Resources.MergedDictionaries.Add(resources);
            if (window.Background is SolidColorBrush b && b.Color.A == 0) host.Background = Brushes.Transparent;
            else host.SetResourceReference(Border.BackgroundProperty, "BgBrush");
            host.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            return host;
        }
    }
}
