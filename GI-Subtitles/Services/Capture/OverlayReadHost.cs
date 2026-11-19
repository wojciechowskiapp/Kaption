using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Rectangle = System.Drawing.Rectangle;

namespace GI_Subtitles.Services.Capture
{
    /// <summary>
    /// The read check's hands and eyes on a real overlay window (<see cref="IOverlayReadHost"/>): it draws the strip in
    /// the window's corner, looks at that spot through the reader the app reads the dialogue with and through a plain
    /// screen copy, and waits a composition between looks. How the window is hidden from the reader and how it is kept
    /// on top are the caller's (the main window's, or the benchmark's). Everything runs on the window's thread, which
    /// is the thread that captures.
    /// </summary>
    public sealed class OverlayReadHost : IOverlayReadHost, IDisposable
    {
        private readonly Window _overlay;
        private readonly Panel _root;
        private readonly Func<IScreenCapture> _reader;
        private readonly Func<bool, bool> _setHidden;
        private readonly Action _keepOnTop;
        private readonly Canvas _strip;
        private readonly GdiScreenCapture _screenCopy = new GdiScreenCapture();
        private readonly Bitmap _look = new Bitmap(StripCode.Width, StripCode.Height, PixelFormat.Format32bppArgb);
        private readonly byte[] _pixels = new byte[StripCode.Width * StripCode.Height * 4];
        private double _stripScale;
        private System.Windows.Point _stripAt;
        private long _copies;

        /// <param name="overlay">The overlay window, already shown.</param>
        /// <param name="root">The window's root panel; the strip is added to it, above everything else.</param>
        /// <param name="reader">The capture backend in use now (it can be replaced while the app runs).</param>
        /// <param name="setHidden">
        /// Keeps the window out of the reader's frames in a way that leaves it in a plain screen copy (true), or lets it
        /// into every screen read again (false). False: Windows refused.
        /// </param>
        /// <param name="keepOnTop">Puts the window back above the others, as the app does on every tick.</param>
        public OverlayReadHost(Window overlay, Panel root, Func<IScreenCapture> reader, Func<bool, bool> setHidden, Action keepOnTop)
        {
            _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _setHidden = setHidden ?? throw new ArgumentNullException(nameof(setHidden));
            _keepOnTop = keepOnTop;
            _strip = new Canvas
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
            };
            RenderOptions.SetEdgeMode(_strip, EdgeMode.Aliased);
            Panel.SetZIndex(_strip, int.MaxValue);
            _strip.Children.Add(new System.Windows.Shapes.Rectangle { Fill = Frozen(255, 0, 255) });
            _strip.Children.Add(new System.Windows.Shapes.Rectangle { Fill = Frozen(0, 255, 255) });
            _root.Children.Add(_strip);
        }

        public long NowMs => Environment.TickCount64;

        public void ShowStrip(bool shown)
        {
            if (shown) PlaceStrip();
            _strip.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        }

        public bool SetHidden(bool hidden) => _setHidden(hidden);

        public Rectangle? StripArea()
        {
            if (!_overlay.IsVisible || PresentationSource.FromVisual(_overlay) == null) return null;
            PlaceStrip();
            // The window must hold the whole strip, or part of the spot is somebody else's picture.
            if (_stripAt.X + _strip.Width > _overlay.ActualWidth || _stripAt.Y + _strip.Height > _overlay.ActualHeight) return null;
            System.Windows.Point at = _overlay.PointToScreen(_stripAt);
            return new Rectangle((int)Math.Round(at.X), (int)Math.Round(at.Y), StripCode.Width, StripCode.Height);
        }

        public StripLook ReadThroughReader(Rectangle area)
        {
            IScreenCapture reader = _reader();
            if (reader == null) return new StripLook(StripSight.NoFrame, 0);
            bool read = TryRead(reader, area);
            // Which picture this look is of, by the reader that made it. Desktop Duplication: the pictures that very
            // reader has taken (a held frame read twice is one picture). A screen copy is a new picture every time.
            long frame = reader is DxgiScreenCapture duplication ? duplication.PictureSerial : read ? ++_copies : _copies;
            return new StripLook(read ? Sight() : StripSight.NoFrame, frame);
        }

        public StripSight ReadThroughGdi(Rectangle area) => TryRead(_screenCopy, area) ? Sight() : StripSight.NoFrame;

        public void Wait()
        {
            _keepOnTop?.Invoke();
            // What was just changed on the window is drawn (WPF renders at this priority), then one composition passes.
            _overlay.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
            DwmFlush();
        }

        public void Dispose()
        {
            _root.Children.Remove(_strip);
            _look.Dispose();
        }

        /// <summary>
        /// Sizes the strip in device pixels and puts it in the window's top-left corner, or, when that corner is off
        /// the monitor (a dialogue area at the screen's edge), at the first spot of the window that is on it.
        /// </summary>
        private void PlaceStrip()
        {
            double scale = VisualTreeHelper.GetDpi(_overlay).DpiScaleX;
            if (scale != _stripScale)
            {
                _stripScale = scale;
                double half = StripCode.Width / 2 / scale, tall = StripCode.Height / scale;
                _strip.Width = 2 * half;
                _strip.Height = tall;
                for (int i = 0; i < 2; i++)
                {
                    var part = (System.Windows.Shapes.Rectangle)_strip.Children[i];
                    part.Width = half;
                    part.Height = tall;
                    Canvas.SetLeft(part, i * half);
                }
            }
            double dx = 0, dy = 0;
            if (PresentationSource.FromVisual(_overlay) is System.Windows.Interop.HwndSource source)
            {
                var monitor = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(MonitorFromWindow(source.Handle, MonitorDefaultToNearest), ref monitor))
                {
                    System.Windows.Point corner = _overlay.PointToScreen(new System.Windows.Point(0, 0));
                    dx = Math.Max(0, monitor.Monitor.Left - corner.X) / scale;
                    dy = Math.Max(0, monitor.Monitor.Top - corner.Y) / scale;
                }
            }
            if (dx != _stripAt.X || dy != _stripAt.Y)
            {
                _stripAt = new System.Windows.Point(dx, dy);
                _strip.Margin = new Thickness(dx, dy, 0, 0);
            }
        }

        /// <summary>One look at the spot into <see cref="_pixels"/>. A reader that cannot give it (no frame, another monitor, the secure desktop) is an answer, not an error.</summary>
        private bool TryRead(IScreenCapture capture, Rectangle area)
        {
            try
            {
                if (capture.CaptureRegionInto(area.X, area.Y, area.Width, area.Height, _look) == null) return false;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is ExternalException
                                       || ex is System.ComponentModel.Win32Exception)
            {
                return false;
            }
            BitmapData bits = _look.LockBits(new Rectangle(0, 0, StripCode.Width, StripCode.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int row = StripCode.Width * 4;
                for (int y = 0; y < StripCode.Height; y++)
                    Marshal.Copy(bits.Scan0 + y * bits.Stride, _pixels, y * row, row);
            }
            finally
            {
                _look.UnlockBits(bits);
            }
            return true;
        }

        private StripSight Sight() =>
            StripCode.Matches(_pixels, StripCode.Width, StripCode.Height) ? StripSight.Seen : StripSight.NotSeen;

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private const uint MonitorDefaultToNearest = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int Size;
            public RECT Monitor, Work;
            public uint Flags;
        }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    }
}
