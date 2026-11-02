using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GI_Subtitles.Services.Detection;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// A thumbnail of the dialogue area with some screen around it, the area outlined in the game's accent, and the
    /// monitor it sits on ("Monitor 2 · 2560×1440"). The calling window is excluded from the capture while it is
    /// taken, so it never photographs itself. For page shows and region changes only, never a timer.
    /// </summary>
    public static class RegionPreview
    {
        public sealed class Shot
        {
            public ImageSource Image { get; init; }
            public int MonitorNumber { get; init; }
            public int MonitorWidth { get; init; }
            public int MonitorHeight { get; init; }
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        private const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;

        /// <summary>Null when the region string is not a valid rectangle.</summary>
        public static Shot Monitor(string region)
        {
            if (!CaptureRegionValidator.TryParse(region, out int x, out int y, out int w, out int h) || w <= 0 || h <= 0) return null;
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x + w / 2, y + h / 2));
            string digits = new string((screen.DeviceName ?? "").Where(char.IsDigit).ToArray());
            return new Shot
            {
                MonitorNumber = int.TryParse(digits, out int n) ? n : 1,
                MonitorWidth = screen.Bounds.Width,
                MonitorHeight = screen.Bounds.Height,
            };
        }

        public static async Task<Shot> CaptureAsync(Window owner, string region, Brush accent)
        {
            var info = Monitor(region);
            if (info == null) return null;
            CaptureRegionValidator.TryParse(region, out int x, out int y, out int w, out int h);
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x + w / 2, y + h / 2));
            var area = SettingsWindow.PreviewArea(new System.Drawing.Rectangle(x, y, w, h), screen.Bounds);
            if (area.Width <= 0 || area.Height <= 0) return info;

            IntPtr hwnd = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
            try
            {
                if (hwnd != IntPtr.Zero) SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
                await Task.Delay(80).ConfigureAwait(true); // one composed frame without the window
                BitmapSource raw = Screenshot.Screenshot.CaptureRegion(new Rect(area.X, area.Y, area.Width, area.Height));
                if (raw == null) return info;
                return new Shot
                {
                    Image = SettingsWindow.ComposeRegionPreview(raw, new Rect(x - area.X, y - area.Y, w, h), accent ?? Brushes.White),
                    MonitorNumber = info.MonitorNumber,
                    MonitorWidth = info.MonitorWidth,
                    MonitorHeight = info.MonitorHeight,
                };
            }
            catch (Exception ex)
            {
                Logger.Log.Info($"Region preview skipped: {ex.Message}");
                return info;
            }
            finally
            {
                if (hwnd != IntPtr.Zero) SetWindowDisplayAffinity(hwnd, WDA_NONE);
            }
        }
    }
}
