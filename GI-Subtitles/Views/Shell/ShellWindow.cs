using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// Kaption window chrome as an attached behaviour: a WindowChrome with our own title bar (the TitleBar control in the
    /// window's content), the DWM shadow kept, content inset while maximized, and Windows "Make text bigger" honoured by
    /// scaling the content uniformly (text never outgrows a fixed-height container that way).
    /// </summary>
    public static class ShellWindow
    {
        public const double CaptionHeight = 40;

        public static readonly DependencyProperty UseChromeProperty = DependencyProperty.RegisterAttached(
            "UseChrome", typeof(bool), typeof(ShellWindow), new PropertyMetadata(false, OnUseChromeChanged));

        public static bool GetUseChrome(DependencyObject d) => (bool)d.GetValue(UseChromeProperty);
        public static void SetUseChrome(DependencyObject d, bool value) => d.SetValue(UseChromeProperty, value);

        private static void OnUseChromeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Window w || !(bool)e.NewValue) return;
            WindowChrome.SetWindowChrome(w, new WindowChrome
            {
                CaptionHeight = CaptionHeight,
                ResizeBorderThickness = new Thickness(6),
                // A one-pixel glass edge is what keeps the DWM drop shadow; the content paints over it.
                GlassFrameThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
                NonClientFrameEdges = NonClientFrameEdges.None,
            });
            w.StateChanged += (s, _) => ApplyMaximizedInset((Window)s);
            w.SourceInitialized += (s, _) =>
            {
                var win = (Window)s;
                FitToWorkArea(win);
                ApplyMaximizedInset(win);
                ApplyTextScale(win);
                SystemEvents.UserPreferenceChanged += Handler;
                win.Closed += (_, __) => SystemEvents.UserPreferenceChanged -= Handler;
                void Handler(object o, UserPreferenceChangedEventArgs a)
                {
                    if (a.Category is UserPreferenceCategory.Accessibility or UserPreferenceCategory.General or UserPreferenceCategory.Window)
                        win.Dispatcher.BeginInvoke(new Action(() => ApplyTextScale(win)));
                }
            };
        }

        /// <summary>A 1366×768 laptop at 125 % has about 1090×610 to offer: never open (or require) more than that.</summary>
        private static void FitToWorkArea(Window w)
        {
            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            var area = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
            DpiScale dpi = VisualTreeHelper.GetDpi(w);
            double maxW = area.Width / dpi.DpiScaleX, maxH = area.Height / dpi.DpiScaleY;
            if (w.MinWidth > maxW) w.MinWidth = maxW;
            if (w.MinHeight > maxH) w.MinHeight = maxH;
            if (w.SizeToContent != SizeToContent.Manual) return;
            if (!double.IsNaN(w.Width) && w.Width > maxW) w.Width = maxW;
            if (!double.IsNaN(w.Height) && w.Height > maxH) w.Height = maxH;
        }

        private static readonly DependencyProperty RestBorderProperty = DependencyProperty.RegisterAttached(
            "RestBorder", typeof(Thickness?), typeof(ShellWindow), new PropertyMetadata(null));

        // Maximized windows hang their resize frame off-screen; inset by that frame so nothing is cut.
        private static void ApplyMaximizedInset(Window w)
        {
            if (w.WindowState == WindowState.Maximized)
            {
                if (w.GetValue(RestBorderProperty) == null) w.SetValue(RestBorderProperty, (Thickness?)w.BorderThickness);
                double dpi = VisualTreeHelper.GetDpi(w).DpiScaleX;
                double padded = GetSystemMetrics(SM_CXPADDEDBORDER) / (dpi <= 0 ? 1 : dpi);
                Thickness frame = SystemParameters.WindowResizeBorderThickness;
                w.BorderThickness = new Thickness(frame.Left + padded, frame.Top + padded, frame.Right + padded, frame.Bottom + padded);
            }
            else if (w.GetValue(RestBorderProperty) is Thickness rest)
            {
                w.BorderThickness = rest;
            }
        }

        /// <summary>Windows text size (Settings › Accessibility › Text size) as a factor; 1 when unset.</summary>
        public static double ReadTextScaleFactor()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
                return key?.GetValue("TextScaleFactor") is int pct ? Clamp(pct / 100.0) : 1.0;
            }
            catch
            {
                return 1.0;
            }
        }

        public static double Clamp(double factor) => double.IsNaN(factor) ? 1 : Math.Max(1, Math.Min(2.25, factor));

        private static readonly DependencyProperty AppliedScaleProperty = DependencyProperty.RegisterAttached(
            "AppliedScale", typeof(double), typeof(ShellWindow), new PropertyMetadata(1.0));

        private static void ApplyTextScale(Window w)
        {
            if (w.Content is not FrameworkElement root) return;
            double next = ReadTextScaleFactor();
            double prev = (double)w.GetValue(AppliedScaleProperty);
            if (Math.Abs(next - prev) < 0.01) return;
            w.SetValue(AppliedScaleProperty, next);
            root.LayoutTransform = next > 1.0 ? new ScaleTransform(next, next) : Transform.Identity;
            // Grow the window and its minimum with the content, within the work area.
            double ratio = next / prev;
            Rect area = SystemParameters.WorkArea;
            if (!double.IsNaN(w.MinWidth) && w.MinWidth > 0) w.MinWidth = Math.Min(area.Width, w.MinWidth * ratio);
            if (!double.IsNaN(w.MinHeight) && w.MinHeight > 0) w.MinHeight = Math.Min(area.Height, w.MinHeight * ratio);
            if (w.SizeToContent == SizeToContent.Manual && w.WindowState == WindowState.Normal)
            {
                if (!double.IsNaN(w.Width)) w.Width = Math.Min(area.Width, w.Width * ratio);
                if (!double.IsNaN(w.Height)) w.Height = Math.Min(area.Height, w.Height * ratio);
            }
        }

        private const int SM_CXPADDEDBORDER = 92;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);
    }
}
