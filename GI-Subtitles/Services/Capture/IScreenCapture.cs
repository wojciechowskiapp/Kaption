using System;
using System.Drawing;

namespace GI_Subtitles.Services.Capture
{
    /// <summary>
    /// Abstraction for screen region capture. Implementations can use GDI (CopyFromScreen)
    /// or DXGI Desktop Duplication for capture-exclusion support.
    /// </summary>
    public interface IScreenCapture : IDisposable
    {
        /// <summary>
        /// Capture a screen region as a freshly-allocated Bitmap (BGRA32 format).
        /// Returns null only when no frame data is available yet.
        /// </summary>
        /// <remarks>
        /// Convenience overload — always allocates a new <see cref="Bitmap"/>. The OCR hot
        /// path should prefer <see cref="CaptureRegionInto"/> with a pool-rented destination
        /// to avoid per-tick LOH churn.
        /// </remarks>
        Bitmap CaptureRegion(int x, int y, int width, int height);

        /// <summary>
        /// Capture a screen region into an existing Bitmap. The <paramref name="destination"/>
        /// MUST have width/height matching the requested region and a compatible pixel format
        /// (<see cref="System.Drawing.Imaging.PixelFormat.Format32bppArgb"/> is the canonical
        /// choice — it maps 1:1 to the BGRA8 byte order produced by DXGI/GDI on Windows).
        /// Returns the same destination instance for fluent use; returns null when the
        /// backend had no frame data available (e.g. DXGI access transiently revoked).
        /// </summary>
        /// <exception cref="ArgumentNullException">destination is null.</exception>
        /// <exception cref="ArgumentException">destination dimensions smaller than the
        /// requested region, or pixel format is not supported by the backend.</exception>
        /// <exception cref="ArgumentOutOfRangeException">width or height is not positive.</exception>
        Bitmap CaptureRegionInto(int x, int y, int width, int height, Bitmap destination);

        /// <summary>
        /// Whether this capture backend initialized successfully and can produce frames.
        /// </summary>
        bool IsAvailable { get; }
    }
}
