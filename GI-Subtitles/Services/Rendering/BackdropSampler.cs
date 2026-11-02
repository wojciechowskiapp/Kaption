using System;
using System.Runtime.InteropServices;

namespace GI_Subtitles.Services.Rendering
{
    /// <summary>
    /// How bright the game is behind the subtitle panel, so the panel can darken over snow, sky and white UI. One GDI
    /// read of the panel's screen rectangle, shrunk to a few dozen pixels (HALFTONE averages as it shrinks). A plain
    /// SRCCOPY from the screen leaves out layered windows, and the overlay is one, so the panel never measures itself.
    /// Not on the capture path: the overlay asks at most about once a second, off the UI thread.
    /// </summary>
    public static class BackdropSampler
    {
        private const int SampleW = 48, SampleH = 12;

        /// <summary>Relative luminance (0-1) of the screen rectangle in physical pixels, or null when it cannot be read.</summary>
        public static double? Sample(int x, int y, int width, int height)
        {
            if (width <= 0 || height <= 0) return null;
            IntPtr screen = IntPtr.Zero, mem = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                screen = GetDC(IntPtr.Zero);
                if (screen == IntPtr.Zero) return null;
                mem = CreateCompatibleDC(screen);
                var info = new BITMAPINFO
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = SampleW,
                    biHeight = -SampleH, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                };
                dib = CreateDIBSection(mem, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
                old = SelectObject(mem, dib);
                SetStretchBltMode(mem, HALFTONE);
                SetBrushOrgEx(mem, 0, 0, IntPtr.Zero);
                if (!StretchBlt(mem, 0, 0, SampleW, SampleH, screen, x, y, width, height, SRCCOPY)) return null;
                GdiFlush();
                var pixels = new byte[SampleW * SampleH * 4];
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                return Luminance(pixels);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            }
        }

        /// <summary>
        /// Brightness of BGRA pixels for readability: mostly the mean, pulled towards the brightest tenth, because a
        /// bright patch behind a few letters hurts as much as a bright panel.
        /// </summary>
        public static double Luminance(byte[] bgra)
        {
            if (bgra == null || bgra.Length < 4) return 0;
            int n = bgra.Length / 4;
            var values = new double[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                double l = 0.2126 * Linear(bgra[i * 4 + 2]) + 0.7152 * Linear(bgra[i * 4 + 1]) + 0.0722 * Linear(bgra[i * 4]);
                values[i] = l;
                sum += l;
            }
            Array.Sort(values);
            double p90 = values[Math.Min(n - 1, (int)(n * 0.9))];
            // Perceived lightness (L*/100) reads closer to "is this bright" than linear luminance does.
            return Lightness(0.6 * (sum / n) + 0.4 * p90);
        }

        private static double Linear(byte c)
        {
            double s = c / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        private static double Lightness(double y) =>
            (y <= 216.0 / 24389 ? y * 24389 / 27 : 116 * Math.Cbrt(y) - 16) / 100;

        // ── GDI ───────────────────────────────────────────────────────────────
        private const int SRCCOPY = 0x00CC0020;
        private const int HALFTONE = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
            public int bmiColors;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr previous);
        [DllImport("gdi32.dll")] private static extern bool GdiFlush();
        [DllImport("gdi32.dll")]
        private static extern bool StretchBlt(IntPtr dest, int xDest, int yDest, int wDest, int hDest,
            IntPtr src, int xSrc, int ySrc, int wSrc, int hSrc, int rop);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    }
}
