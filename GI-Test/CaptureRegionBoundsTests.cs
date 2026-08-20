// ─────────────────────────────────────────────────────────────────────────────
// The region picker used to store whatever rectangle the user dragged, with no
// bounds check. A drag ending one pixel past the left edge produced a negative
// X, DxgiScreenCapture refused it, and the app fell back to GDI — silently, on
// every OCR tick, permanently, until the region was drawn again.
//
// Seen in the field 2026-08-20 on a single-monitor 2560x1440 machine:
//   "Capture region -14,1104 2034x304 is outside DXGI output 0,0 2560x1440"
// ─────────────────────────────────────────────────────────────────────────────

using GI_Subtitles.Services.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class CaptureRegionBoundsTests
    {
        // A single 2560x1440 monitor at the origin — the reported machine.
        private const int L = 0, T = 0, W = 2560, H = 1440;

        [TestMethod]
        public void RegionInsideTheDesktop_IsUnchanged()
        {
            Assert.IsTrue(CaptureRegionBounds.TryClamp(115, 1011, 2040, 420, L, T, W, H,
                out int x, out int y, out int w, out int h));
            Assert.AreEqual(115, x); Assert.AreEqual(1011, y);
            Assert.AreEqual(2040, w); Assert.AreEqual(420, h);
            Assert.IsFalse(CaptureRegionBounds.WasClamped(115, 1011, 2040, 420, x, y, w, h));
        }

        [TestMethod]
        public void TheFieldReport_IsTrimmedBackOntoTheDesktop()
        {
            // -14,1104 2034x304 — exactly what forced the GDI fallback.
            Assert.IsTrue(CaptureRegionBounds.TryClamp(-14, 1104, 2034, 304, L, T, W, H,
                out int x, out int y, out int w, out int h));

            Assert.AreEqual(0, x, "Negative X is what DXGI rejects; it must land on the desktop edge.");
            Assert.AreEqual(1104, y, "Y was already valid and must not move.");
            Assert.AreEqual(2020, w, "The far edge must stay put: -14+2034 = 2020, so width shrinks by the 14 lost.");
            Assert.AreEqual(304, h);
            Assert.IsTrue(CaptureRegionBounds.WasClamped(-14, 1104, 2034, 304, x, y, w, h));
        }

        [TestMethod]
        public void ClippingMovesTheOriginWithoutDraggingTheFarEdge()
        {
            // Clamping origin and size independently would move the right edge
            // from 2600 to 2660. Edge arithmetic keeps it at the desktop bound.
            Assert.IsTrue(CaptureRegionBounds.TryClamp(-60, -40, 2660, 500, L, T, W, H,
                out int x, out int y, out int w, out int h));
            Assert.AreEqual(0, x); Assert.AreEqual(0, y);
            Assert.AreEqual(2560, w, "Right edge was 2600, clipped to the 2560 desktop bound.");
            Assert.AreEqual(460, h, "Bottom edge was 460 and is inside the desktop, so it stays.");
        }

        [TestMethod]
        public void RegionRunningOffTheRightEdge_IsTrimmed()
        {
            Assert.IsTrue(CaptureRegionBounds.TryClamp(2400, 100, 400, 200, L, T, W, H,
                out int x, out int y, out int w, out int h));
            Assert.AreEqual(2400, x);
            Assert.AreEqual(160, w, "2400+400 = 2800, past the 2560 edge.");
            Assert.AreEqual(200, h);
        }

        [TestMethod]
        public void RegionEntirelyOffDesktop_IsRefused()
        {
            Assert.IsFalse(CaptureRegionBounds.TryClamp(-900, 100, 400, 200, L, T, W, H,
                out _, out _, out _, out _),
                "Nothing survives the clip, so there is no region to store.");
        }

        [TestMethod]
        public void SliverTooSmallToRead_IsRefused()
        {
            // Overlapping by 3 px is arithmetically valid and useless to OCR.
            Assert.IsFalse(CaptureRegionBounds.TryClamp(-397, 100, 400, 200, L, T, W, H,
                out _, out _, out _, out _),
                "3 px of surviving width is below MinDimension and must not be stored.");
        }

        [TestMethod]
        public void SecondMonitorLeftOfPrimary_IsAValidPlaceForNegativeCoordinates()
        {
            // Virtual desktop spanning -1920..2560. A negative X is legitimate
            // here, so clamping must respect the desktop origin, not zero.
            Assert.IsTrue(CaptureRegionBounds.TryClamp(-1800, 300, 800, 200, -1920, 0, 4480, 1440,
                out int x, out int y, out int w, out int h));
            Assert.AreEqual(-1800, x, "Negative is correct when the desktop starts at -1920.");
            Assert.AreEqual(800, w);
            Assert.IsFalse(CaptureRegionBounds.WasClamped(-1800, 300, 800, 200, x, y, w, h));
        }

        [TestMethod]
        public void DegenerateInput_IsRefusedRatherThanDividedBy()
        {
            Assert.IsFalse(CaptureRegionBounds.TryClamp(10, 10, 0, 100, L, T, W, H, out _, out _, out _, out _));
            Assert.IsFalse(CaptureRegionBounds.TryClamp(10, 10, 100, -5, L, T, W, H, out _, out _, out _, out _));
            Assert.IsFalse(CaptureRegionBounds.TryClamp(10, 10, 100, 100, L, T, 0, 0, out _, out _, out _, out _));
        }
    }
}
