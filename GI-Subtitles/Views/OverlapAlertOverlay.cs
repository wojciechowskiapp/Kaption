// ─────────────────────────────────────────────────────────────────────────────
//  OverlapAlertOverlay.cs
//  ---------------------------------------------------------------------------
//  Runtime red-alert overlay shown while the subtitle box bounds intersect a
//  capture region. Replaces the "let OCR run, mask the pixels, show garbled
//  output" behaviour with "hide the translation, tell the user what's wrong,
//  keep the alert visible until the user fixes it."
//
//  Lifecycle:
//   - Show(overlapRect, regionRect, intersectionRect) on the UI thread whenever
//     the runtime guard detects overlap; safe to call every tick — subsequent
//     calls mutate the existing window in place rather than recreating it.
//   - Hide() when the overlap resolves. The window stays allocated but hidden
//     so the next flip-flop doesn't pay the create cost.
//   - Dispose() on app shutdown.
//
//  Positioning:
//   - Top-center of the primary screen if the region is in the bottom half,
//     bottom-center otherwise. That way the alert banner cannot itself cause
//     overlap with the capture region the user is trying to fix.
//
//  Capture exclusion:
//   - SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) so the alert pixels are
//     invisible to screen capture — otherwise the alert could create its own
//     feedback loop when the user has a larger-than-expected region.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GI_Subtitles.Views
{
    internal sealed class OverlapAlertOverlay : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        // Win32 click-through: without WS_EX_TRANSPARENT, a WPF window with
        // `IsHitTestVisible = false` still intercepts OS-level mouse clicks
        // because HWND hit-testing is independent of WPF's logical tree.
        // Result: the red alert window would eat clicks meant for the
        // subtitle box beneath it, and the user couldn't drag to fix.
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;

        private Window _window;
        private Canvas _canvas;
        private Rectangle _regionOutline;
        private Rectangle _intersectionFill;
        private Border _banner;
        private TextBlock _bannerTitle;
        private TextBlock _bannerBody;

        private readonly double _scale;

        // Window origin in WPF DIPs. Captured at EnsureBuilt time and used to
        // map physical-pixel coordinates (the space Region CSV lives in) to
        // Canvas-relative coordinates (the space WPF layout uses). Without
        // this subtraction, a multi-monitor setup where the virtual screen
        // starts at a negative X/Y (secondary monitor extends left/above
        // primary) drew all content off-screen.
        private double _windowOriginLeftDip;
        private double _windowOriginTopDip;

        public OverlapAlertOverlay(double dpiScale)
        {
            _scale = dpiScale > 0 ? dpiScale : 1.0;
        }

        /// <summary>
        /// True when the overlay window is currently displayed. Callers can
        /// use this to avoid recreating state on every tick when the overlap
        /// condition hasn't changed.
        /// </summary>
        public bool IsShown => _window != null && _window.IsVisible;

        /// <summary>
        /// Show or update the runtime red-alert overlay. Coordinates are in
        /// screen physical pixels (the same space the validator produces).
        /// Copy is provided by the caller so localisation stays in Resources.
        /// </summary>
        public void Show(
            Rect regionScreenPx,
            Rect intersectionScreenPx,
            string titleText,
            string bodyText)
        {
            EnsureBuilt();
            UpdateBanner(titleText, bodyText, regionScreenPx);
            UpdateRegionOutline(regionScreenPx);
            UpdateIntersection(intersectionScreenPx);

            if (!_window.IsVisible)
            {
                _window.Show();
                ApplyWin32WindowStyles();
            }
        }

        public void Hide()
        {
            if (_window != null && _window.IsVisible)
                _window.Hide();
        }

        public void Dispose()
        {
            try { _window?.Close(); } catch { /* non-fatal */ }
            _window = null;
        }

        // ── Private ───────────────────────────────────────────────────────

        private void EnsureBuilt()
        {
            if (_window != null) return;

            _canvas = new Canvas();

            // Region outline — always red when shown (this overlay only
            // appears in the overlap state, so the "safe" color branch
            // doesn't apply).
            _regionOutline = new Rectangle
            {
                Stroke = Brushes.Red,
                StrokeThickness = 3,
                Fill = new SolidColorBrush(Color.FromArgb(35, 255, 0, 0)),
                IsHitTestVisible = false,
            };
            _canvas.Children.Add(_regionOutline);

            // Intersection highlight — the exact pixels the overlay was
            // stealing. Dashed stroke matches the Ctrl+Shift+D diagnostic.
            _intersectionFill = new Rectangle
            {
                Stroke = Brushes.Red,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(110, 255, 0, 0)),
                IsHitTestVisible = false,
            };
            _canvas.Children.Add(_intersectionFill);

            // Banner: red card with title + body. Placed on the canvas; its
            // top-left is set in UpdateBanner.
            var bannerStack = new StackPanel { Orientation = Orientation.Vertical };
            _bannerTitle = new TextBlock
            {
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 18,
                TextWrapping = TextWrapping.Wrap,
            };
            _bannerBody = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                Opacity = 0.95,
            };
            bannerStack.Children.Add(_bannerTitle);
            bannerStack.Children.Add(_bannerBody);

            _banner = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 14, 18, 14),
                MaxWidth = 680,
                Child = bannerStack,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 14,
                    ShadowDepth = 2,
                    Opacity = 0.45,
                    Color = Colors.Black,
                },
            };
            _canvas.Children.Add(_banner);

            // SystemParameters.VirtualScreen* are in WPF DIPs and cover the
            // full virtual desktop spanning every monitor. Capture them as
            // the window origin so ToCanvasX/Y can translate physical-pixel
            // rectangles into Canvas-local coordinates regardless of whether
            // the virtual screen starts at (0,0) or at negative values.
            _windowOriginLeftDip = SystemParameters.VirtualScreenLeft;
            _windowOriginTopDip = SystemParameters.VirtualScreenTop;

            _window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
                IsHitTestVisible = false,
                Focusable = false,
                Width = SystemParameters.VirtualScreenWidth,
                Height = SystemParameters.VirtualScreenHeight,
                Left = _windowOriginLeftDip,
                Top = _windowOriginTopDip,
                Content = _canvas,
                Title = "Kaption alert",
            };
        }

        /// <summary>Physical-pixel X → Canvas-relative X (DIPs).</summary>
        private double ToCanvasX(double screenPxX) => screenPxX / _scale - _windowOriginLeftDip;

        /// <summary>Physical-pixel Y → Canvas-relative Y (DIPs).</summary>
        private double ToCanvasY(double screenPxY) => screenPxY / _scale - _windowOriginTopDip;

        private void UpdateRegionOutline(Rect regionScreenPx)
        {
            if (regionScreenPx.IsEmpty) { _regionOutline.Visibility = Visibility.Collapsed; return; }
            _regionOutline.Visibility = Visibility.Visible;
            _regionOutline.Width = regionScreenPx.Width / _scale;
            _regionOutline.Height = regionScreenPx.Height / _scale;
            Canvas.SetLeft(_regionOutline, ToCanvasX(regionScreenPx.X));
            Canvas.SetTop(_regionOutline, ToCanvasY(regionScreenPx.Y));
        }

        private void UpdateIntersection(Rect intersectionScreenPx)
        {
            if (intersectionScreenPx.IsEmpty || intersectionScreenPx.Width <= 0 || intersectionScreenPx.Height <= 0)
            {
                _intersectionFill.Visibility = Visibility.Collapsed;
                return;
            }
            _intersectionFill.Visibility = Visibility.Visible;
            _intersectionFill.Width = intersectionScreenPx.Width / _scale;
            _intersectionFill.Height = intersectionScreenPx.Height / _scale;
            Canvas.SetLeft(_intersectionFill, ToCanvasX(intersectionScreenPx.X));
            Canvas.SetTop(_intersectionFill, ToCanvasY(intersectionScreenPx.Y));
        }

        private void UpdateBanner(string title, string body, Rect regionScreenPx)
        {
            _bannerTitle.Text = title ?? string.Empty;
            _bannerBody.Text = body ?? string.Empty;

            // Measure the banner so we can centre it without a one-frame
            // layout flicker. Safe to call during window construction.
            _banner.Measure(new Size(_banner.MaxWidth, double.PositiveInfinity));
            double bannerWidth = _banner.DesiredSize.Width;
            double bannerHeight = _banner.DesiredSize.Height;

            // Find the physical monitor that contains the region, so the
            // banner lands on THAT monitor instead of somewhere on the
            // virtual-screen bounding box (which is "between monitors" on
            // horizontal multi-monitor setups — a perfect way to have the
            // banner straddle two screens or appear on the wrong one).
            // Falls back to the primary screen for empty regions.
            var screen = ResolveTargetScreen(regionScreenPx);
            double screenLeftDip = screen.Bounds.Left / _scale;
            double screenTopDip = screen.Bounds.Top / _scale;
            double screenWidthDip = screen.Bounds.Width / _scale;
            double screenHeightDip = screen.Bounds.Height / _scale;

            // Top vs bottom: if the region is in the bottom half of ITS
            // monitor, place the banner near the top of that monitor.
            // Otherwise near the bottom. Keeps the banner visually separated
            // from the broken layout the user is trying to fix.
            double regionCenterYDip = regionScreenPx.IsEmpty
                ? screenTopDip + screenHeightDip / 2
                : (regionScreenPx.Y + regionScreenPx.Height / 2) / _scale;
            bool placeAtTop = regionCenterYDip > (screenTopDip + screenHeightDip / 2);

            // Horizontal centre OF THE REGION'S MONITOR, then subtract the
            // window origin so the Canvas coords are window-relative.
            double bannerScreenLeftDip = screenLeftDip + (screenWidthDip - bannerWidth) / 2;
            double bannerScreenTopDip = placeAtTop
                ? screenTopDip + 40
                : screenTopDip + screenHeightDip - bannerHeight - 60;

            Canvas.SetLeft(_banner, bannerScreenLeftDip - _windowOriginLeftDip);
            Canvas.SetTop(_banner, bannerScreenTopDip - _windowOriginTopDip);
        }

        /// <summary>
        /// Pick the monitor that contains the region. Uses
        /// System.Windows.Forms.Screen — its Bounds are in physical pixels,
        /// same as the Region CSV — so no scale fuzziness. If the region
        /// straddles monitors we pick the one containing the top-left; if
        /// the region is empty or off-screen we fall back to PrimaryScreen.
        /// </summary>
        private static System.Windows.Forms.Screen ResolveTargetScreen(Rect regionScreenPx)
        {
            try
            {
                if (!regionScreenPx.IsEmpty)
                {
                    var anchor = new System.Drawing.Point((int)regionScreenPx.X, (int)regionScreenPx.Y);
                    foreach (var s in System.Windows.Forms.Screen.AllScreens)
                    {
                        if (s.Bounds.Contains(anchor)) return s;
                    }
                    // Region off-screen → use the screen whose centre is closest.
                    var regionCenter = new System.Drawing.Point(
                        (int)(regionScreenPx.X + regionScreenPx.Width / 2),
                        (int)(regionScreenPx.Y + regionScreenPx.Height / 2));
                    System.Windows.Forms.Screen nearest = System.Windows.Forms.Screen.PrimaryScreen;
                    double bestDist = double.MaxValue;
                    foreach (var s in System.Windows.Forms.Screen.AllScreens)
                    {
                        double cx = s.Bounds.Left + s.Bounds.Width / 2.0;
                        double cy = s.Bounds.Top + s.Bounds.Height / 2.0;
                        double d = (cx - regionCenter.X) * (cx - regionCenter.X) + (cy - regionCenter.Y) * (cy - regionCenter.Y);
                        if (d < bestDist) { bestDist = d; nearest = s; }
                    }
                    return nearest;
                }
            }
            catch { /* fall through to primary */ }

            return System.Windows.Forms.Screen.PrimaryScreen;
        }

        private void ApplyWin32WindowStyles()
        {
            try
            {
                var handle = new WindowInteropHelper(_window).Handle;
                if (handle == IntPtr.Zero) return;

                // (1) Hide from screen capture so the alert can't enter its
                // own feedback loop.
                SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE);

                // (2) Make the window truly click-through at the HWND level.
                // This is the critical fix: without it, the subtitle box
                // directly underneath the red outline can't be grabbed —
                // clicks land on this alert window instead. With WS_EX_TRANSPARENT
                // + WS_EX_LAYERED the OS skips this HWND during hit-testing
                // and the click falls through to the subtitle box window.
                int ex = GetWindowLong(handle, GWL_EXSTYLE);
                SetWindowLong(handle, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED);
            }
            catch
            {
                // Non-fatal — if the Win32 calls fail the alert still shows;
                // worst case the user falls back to the Ctrl+Shift+R path to
                // redraw the region.
            }
        }
    }
}
