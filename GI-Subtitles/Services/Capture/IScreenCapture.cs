using System;
using System.Drawing;

namespace GI_Subtitles.Services.Capture
{
    /// <summary>
    /// Abstraction for screen region capture. Implementations can use GDI (CopyFromScreen)
    /// or DXGI Desktop Duplication for capture-exclusion support.
    /// </summary>
    /// <summary>What <see cref="IScreenCapture.TryCopyFromLastFrame"/> did.</summary>
    public enum FrameCopy
    {
        /// <summary>Every rectangle was copied.</summary>
        Copied,
        /// <summary>The backend holds no frame (none captured yet, the desktop is locked, access was revoked).</summary>
        NoFrame,
        /// <summary>A rectangle is not on the screen this backend captures; nothing was copied.</summary>
        OutsideOutput,
    }

    /// <summary>The layout <see cref="IScreenCapture.TryCopyFromLastFrame"/> writes: rectangles back to back, rows tight.</summary>
    public static class FrameRegions
    {
        public const int BytesPerPixel = 4;

        /// <summary>The bytes one rectangle takes.</summary>
        public static int BytesFor(Rectangle region) => checked(region.Width * region.Height * BytesPerPixel);

        /// <summary>The bytes all of them take, in order.</summary>
        public static int BytesFor(ReadOnlySpan<Rectangle> regions)
        {
            int total = 0;
            foreach (Rectangle r in regions)
            {
                if (r.Width <= 0 || r.Height <= 0) throw new ArgumentException("A rectangle to copy must not be empty.", nameof(regions));
                total = checked(total + BytesFor(r));
            }
            return total;
        }
    }

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
        /// A second look at the frame the last capture took: copies small rectangles (screen pixels) into
        /// <paramref name="destination"/>, one after another, each as tight BGRA rows (<see cref="FrameRegions"/>).
        /// It never asks the desktop for a new frame and never waits for one, so a reader beside the dialogue pays a
        /// copy, not a frame. DXGI: one map and the row copies. GDI holds no frame and pays one screen copy per
        /// rectangle, so its callers ration themselves. All or nothing: unless the answer is
        /// <see cref="FrameCopy.Copied"/> the destination holds nothing to read. A frame it cannot give is an
        /// answer, never an exception. Call on the thread that captures.
        /// </summary>
        /// <exception cref="ArgumentException">A rectangle is empty, or the destination is too small.</exception>
        FrameCopy TryCopyFromLastFrame(ReadOnlySpan<Rectangle> regions, Span<byte> destination);

        /// <summary>
        /// True when <see cref="TryCopyFromLastFrame"/> reads a frame the backend already holds (a memory copy); false
        /// when every rectangle is a fresh screen copy and callers should ration them.
        /// </summary>
        bool HoldsFrame { get; }

        /// <summary>
        /// Whether this capture backend initialized successfully and can produce frames.
        /// </summary>
        bool IsAvailable { get; }
    }
}
