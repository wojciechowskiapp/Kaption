using System;

namespace GI_Subtitles.Services.Validation
{
    /// <summary>
    /// Keeps a capture region inside the virtual desktop.
    ///
    /// <para><b>Why this exists.</b> The region picker returned whatever
    /// rectangle the user dragged, in absolute virtual-desktop pixels, and
    /// <c>ChooseRegion</c> wrote it straight to Config with no bounds check. Drag
    /// even one pixel past the left or top edge and the stored region gets a
    /// negative coordinate. <c>DxgiScreenCapture</c> then refuses it — correctly,
    /// because Desktop Duplication surfaces are output-local and a region outside
    /// the output would otherwise capture stale pixels — and the app drops to the
    /// GDI fallback.</para>
    ///
    /// <para>That fallback is silent and permanent: every OCR tick from then on
    /// pays the slower path, and nothing in the UI says why. Observed in the
    /// field on 2026-08-20 as <c>Capture region -14,1104 2034x304 is outside DXGI
    /// output 0,0 2560x1440</c>, on a single-monitor machine, from a region drawn
    /// 14 px off the left edge.</para>
    ///
    /// <para>Pure and side-effect free so it can be tested without a desktop.
    /// All coordinates are PHYSICAL pixels, matching what the picker returns and
    /// what <see cref="System.Windows.Forms.SystemInformation.VirtualScreen"/>
    /// reports — not WPF DIPs.</para>
    /// </summary>
    public static class CaptureRegionBounds
    {
        /// <summary>Smallest region worth capturing. Below this OCR has nothing
        /// to read, and a degenerate rect would divide by zero downstream.</summary>
        public const int MinDimension = 8;

        /// <summary>
        /// Clips <paramref name="x"/>/<paramref name="y"/>/<paramref name="width"/>/<paramref name="height"/>
        /// to the desktop rectangle.
        /// </summary>
        /// <returns>
        /// <c>false</c> when nothing usable survives the clip — the region lies
        /// entirely off-desktop, or what remains is under
        /// <see cref="MinDimension"/>. Callers must refuse to store it rather
        /// than persisting a rectangle that cannot be captured.
        /// </returns>
        public static bool TryClamp(
            int x, int y, int width, int height,
            int desktopLeft, int desktopTop, int desktopWidth, int desktopHeight,
            out int clampedX, out int clampedY, out int clampedWidth, out int clampedHeight)
        {
            clampedX = x;
            clampedY = y;
            clampedWidth = width;
            clampedHeight = height;

            if (width <= 0 || height <= 0) return false;
            if (desktopWidth <= 0 || desktopHeight <= 0) return false;

            int desktopRight = desktopLeft + desktopWidth;
            int desktopBottom = desktopTop + desktopHeight;

            // Work in edges rather than origin+size: clipping the origin and the
            // size independently silently moves the far edge.
            long left = Math.Max(x, desktopLeft);
            long top = Math.Max(y, desktopTop);
            long right = Math.Min((long)x + width, desktopRight);
            long bottom = Math.Min((long)y + height, desktopBottom);

            if (right - left < MinDimension) return false;
            if (bottom - top < MinDimension) return false;

            clampedX = (int)left;
            clampedY = (int)top;
            clampedWidth = (int)(right - left);
            clampedHeight = (int)(bottom - top);
            return true;
        }

        /// <summary>True when clipping actually moved something, so the caller
        /// can say so instead of adjusting the user's selection silently.</summary>
        public static bool WasClamped(
            int x, int y, int width, int height,
            int clampedX, int clampedY, int clampedWidth, int clampedHeight)
            => x != clampedX || y != clampedY || width != clampedWidth || height != clampedHeight;
    }
}
