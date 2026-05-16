using System;
using System.Drawing;
using System.Drawing.Imaging;
using GI_Subtitles.Common;

namespace GI_Subtitles.Services.Capture
{
    /// <summary>
    /// GDI-based screen capture using Graphics.CopyFromScreen (BitBlt).
    /// Fast and reliable, but does NOT respect WDA_EXCLUDEFROMCAPTURE —
    /// overlay windows will appear in captured frames.
    /// </summary>
    public sealed class GdiScreenCapture : IScreenCapture
    {
        public bool IsAvailable => true;

        /// <summary>
        /// Allocates a fresh Bitmap and captures into it. Convenience overload — the OCR
        /// hot path should call <see cref="CaptureRegionInto"/> with a pool-rented Bitmap.
        /// </summary>
        public Bitmap CaptureRegion(int x, int y, int width, int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            Bitmap bitmap = null;
            try
            {
                bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                return CaptureRegionInto(x, y, width, height, bitmap);
            }
            catch
            {
                bitmap?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Copies the screen region into <paramref name="destination"/>. CopyFromScreen
        /// writes in the destination Bitmap's native pixel format — we require
        /// <see cref="PixelFormat.Format32bppArgb"/> or <see cref="PixelFormat.Format32bppRgb"/>
        /// so the byte layout matches the pool's default.
        /// </summary>
        public Bitmap CaptureRegionInto(int x, int y, int width, int height, Bitmap destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            if (destination.Width < width || destination.Height < height)
                throw new ArgumentException(
                    $"Destination Bitmap too small: got {destination.Width}x{destination.Height}, need >= {width}x{height}",
                    nameof(destination));

            // CopyFromScreen is tolerant of most 32bpp formats, but the pool's default is
            // Format32bppArgb (BGRA byte order). Anything else is a caller bug.
            if (destination.PixelFormat != PixelFormat.Format32bppArgb
                && destination.PixelFormat != PixelFormat.Format32bppRgb
                && destination.PixelFormat != PixelFormat.Format32bppPArgb)
            {
                throw new ArgumentException(
                    $"Destination Bitmap pixel format {destination.PixelFormat} is not supported by GDI capture; use Format32bppArgb.",
                    nameof(destination));
            }

            try
            {
                using (Graphics g = Graphics.FromImage(destination))
                {
                    g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
                }
                return destination;
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"GDI capture failed: {ex.Message}");
                throw;
            }
        }

        public void Dispose() { }
    }
}
