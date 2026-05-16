// ─────────────────────────────────────────────────────────────────────────────
//  OverlayRegionValidatorTests.cs
//  ---------------------------------------------------------------------------
//  Covers the overlap-detection service that prevents the "overlay inside the
//  capture region → OCR translates its own translation" feedback loop.
//
//  The projection path (ProjectOverlayRect / ProjectOverlayRectUsingConfig)
//  calls into DefaultSubtitleLayoutEngine which measures text via WPF — that
//  path is exercised by integration tests on a Windows agent where a WPF
//  dispatcher is available. The unit tests here cover the pure-math paths
//  (ParseRegion, Check, multi-region priority, DPI invariance).
// ─────────────────────────────────────────────────────────────────────────────

using System.Windows;
using GI_Subtitles.Services.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class OverlayRegionValidatorTests
    {
        // ── ParseRegion ────────────────────────────────────────────────────

        [TestMethod]
        public void ParseRegion_ValidCsv_ReturnsRect()
        {
            var r = OverlayRegionValidator.ParseRegion(new[] { "100", "200", "300", "50" });
            Assert.AreEqual(100, r.X);
            Assert.AreEqual(200, r.Y);
            Assert.AreEqual(300, r.Width);
            Assert.AreEqual(50, r.Height);
        }

        [TestMethod]
        public void ParseRegion_NullOrShort_ReturnsEmpty()
        {
            Assert.IsTrue(OverlayRegionValidator.ParseRegion(null).IsEmpty);
            Assert.IsTrue(OverlayRegionValidator.ParseRegion(new string[] { }).IsEmpty);
            Assert.IsTrue(OverlayRegionValidator.ParseRegion(new[] { "1", "2", "3" }).IsEmpty);
        }

        [TestMethod]
        public void ParseRegion_NonInteger_ReturnsEmpty()
        {
            Assert.IsTrue(OverlayRegionValidator.ParseRegion(new[] { "a", "b", "c", "d" }).IsEmpty);
            Assert.IsTrue(OverlayRegionValidator.ParseRegion(new[] { "100", "200", "", "50" }).IsEmpty);
        }

        [TestMethod]
        public void ParseRegion_ZeroOrNegativeSize_ReturnsEmpty()
        {
            // Zero width/height is "not configured" — Config writes "0,0,0,0"
            // shape on some legacy paths and the splitter hands us "" entries.
            Assert.IsTrue(OverlayRegionValidator.ParseRegion(new[] { "100", "200", "0", "50" }).IsEmpty);
            Assert.IsTrue(OverlayRegionValidator.ParseRegion(new[] { "100", "200", "300", "0" }).IsEmpty);
            Assert.IsTrue(OverlayRegionValidator.ParseRegion(new[] { "100", "200", "-5", "50" }).IsEmpty);
        }

        // ── Check (pure rect math) ─────────────────────────────────────────

        [TestMethod]
        public void Check_NoOverlap_Safe()
        {
            var overlay = new Rect(0, 0, 100, 50);
            var region = new Rect(500, 500, 200, 100);
            var result = OverlayRegionValidator.Check(overlay, region, Rect.Empty, Rect.Empty, false);
            Assert.IsFalse(result.HasOverlap);
            Assert.AreEqual(OverlapRegionKind.None, result.Kind);
        }

        [TestMethod]
        public void Check_OverlayFullyInsideRegion_ReportsDialogue()
        {
            var overlay = new Rect(550, 520, 50, 20);
            var region = new Rect(500, 500, 200, 100);
            var result = OverlayRegionValidator.Check(overlay, region, Rect.Empty, Rect.Empty, false);
            Assert.IsTrue(result.HasOverlap);
            Assert.AreEqual(OverlapRegionKind.Dialogue, result.Kind);
            Assert.AreEqual(overlay, result.IntersectionRect);
        }

        [TestMethod]
        public void Check_PartialEdgeOverlap_IsStrict()
        {
            // Even a 1px sliver is an overlap — OCR masking paints that sliver
            // black, which corrupts the bitmap. No tolerance.
            var overlay = new Rect(490, 500, 20, 50);   // right edge extends 10px into region
            var region = new Rect(500, 500, 200, 100);
            var result = OverlayRegionValidator.Check(overlay, region, Rect.Empty, Rect.Empty, false);
            Assert.IsTrue(result.HasOverlap);
            Assert.AreEqual(10.0, result.IntersectionRect.Width);
        }

        [TestMethod]
        public void Check_BordersTouching_NotAnOverlap()
        {
            // Rects whose edges touch (overlay ends where region starts) have
            // zero area — WPF's Rect.Intersect returns an empty rect for this,
            // so the validator must treat it as safe.
            var overlay = new Rect(400, 500, 100, 100); // ends at x=500
            var region = new Rect(500, 500, 200, 100);   // starts at x=500
            var result = OverlayRegionValidator.Check(overlay, region, Rect.Empty, Rect.Empty, false);
            Assert.IsFalse(result.HasOverlap);
        }

        [TestMethod]
        public void Check_EmptyOverlay_Safe()
        {
            var result = OverlayRegionValidator.Check(
                Rect.Empty, new Rect(0, 0, 100, 50), Rect.Empty, Rect.Empty, false);
            Assert.IsFalse(result.HasOverlap);
        }

        [TestMethod]
        public void Check_AllRegionsEmpty_Safe()
        {
            var overlay = new Rect(100, 100, 200, 50);
            var result = OverlayRegionValidator.Check(
                overlay, Rect.Empty, Rect.Empty, Rect.Empty, false);
            Assert.IsFalse(result.HasOverlap);
        }

        // ── Priority order: Dialogue first, then Secondary, then Answer ────

        [TestMethod]
        public void Check_DialogueAndAnswerBothOverlap_ReportsDialogue()
        {
            var overlay = new Rect(100, 100, 800, 50);
            var dialogue = new Rect(200, 100, 100, 50);
            var answer = new Rect(500, 100, 100, 50);
            var result = OverlayRegionValidator.Check(overlay, dialogue, Rect.Empty, answer, false);
            Assert.IsTrue(result.HasOverlap);
            Assert.AreEqual(OverlapRegionKind.Dialogue, result.Kind);
        }

        [TestMethod]
        public void Check_OnlyAnswerOverlaps_ReportsAnswer()
        {
            var overlay = new Rect(600, 100, 50, 50);
            var dialogue = new Rect(0, 0, 100, 50);
            var answer = new Rect(580, 80, 100, 100);
            var result = OverlayRegionValidator.Check(overlay, dialogue, Rect.Empty, answer, false);
            Assert.IsTrue(result.HasOverlap);
            Assert.AreEqual(OverlapRegionKind.Answer, result.Kind);
        }

        [TestMethod]
        public void Check_OnlySecondaryOverlaps_ReportsSecondary()
        {
            var overlay = new Rect(100, 100, 200, 50);
            var dialogue = Rect.Empty;
            var secondary = new Rect(150, 100, 100, 50);
            var answer = Rect.Empty;
            var result = OverlayRegionValidator.Check(overlay, dialogue, secondary, answer, false);
            Assert.IsTrue(result.HasOverlap);
            Assert.AreEqual(OverlapRegionKind.Secondary, result.Kind);
        }

        // ── CSV overload ───────────────────────────────────────────────────

        [TestMethod]
        public void CheckCsv_MalformedRegionsSkipped()
        {
            var overlay = new Rect(100, 100, 50, 50);
            var result = OverlayRegionValidator.Check(
                overlay,
                dialogueRegionCsv: new[] { "" },
                secondaryRegionCsv: null,
                answerRegionCsv: new[] { "a", "b", "c", "d" },
                overlayIsProjected: false);
            Assert.IsFalse(result.HasOverlap);
        }

        [TestMethod]
        public void CheckCsv_ValidCsvDetectsOverlap()
        {
            var overlay = new Rect(600, 500, 200, 100);
            var result = OverlayRegionValidator.Check(
                overlay,
                dialogueRegionCsv: new[] { "500", "450", "400", "200" }, // contains the overlay
                secondaryRegionCsv: null,
                answerRegionCsv: null,
                overlayIsProjected: true);
            Assert.IsTrue(result.HasOverlap);
            Assert.AreEqual(OverlapRegionKind.Dialogue, result.Kind);
            Assert.IsTrue(result.OverlayWasProjected);
        }

        // ── Projection flag propagation ────────────────────────────────────

        [TestMethod]
        public void Safe_FactoryPreservesProjectionFlag()
        {
            var r1 = OverlapCheckResult.Safe(new Rect(0, 0, 10, 10), projected: true);
            var r2 = OverlapCheckResult.Safe(new Rect(0, 0, 10, 10), projected: false);
            Assert.IsTrue(r1.OverlayWasProjected);
            Assert.IsFalse(r2.OverlayWasProjected);
            Assert.IsFalse(r1.HasOverlap);
            Assert.IsFalse(r2.HasOverlap);
        }

        // ── ProjectOverlayRect (geometry-only, no WPF text measurement) ────
        //
        // The projection is the hot path called from the live-drag handler
        // at ~60 Hz. It must run fast (no FormattedText.Measure) AND match
        // DefaultSubtitleLayoutEngine's geometry — these tests lock in the
        // invariants so a future engine tweak can't silently drift the two
        // apart.

        [TestMethod]
        public void Project_TypicalGenshinRegion_LandsAboveIt_NoOverlap()
        {
            // Region at the bottom of a 1920x1080 screen with negative PadVertical
            // — the real "dialogue box at screen bottom" layout.
            var region = new Rect(200, 800, 1200, 200);
            var screen = new Rect(0, 0, 1920, 1080);
            var projected = OverlayRegionValidator.ProjectOverlayRect(
                captureRegionScreenPx: region,
                screenBoundsScreenPx: screen,
                dpiScale: 1.0,
                maxOverlayHeightPx: 80,
                maxOverlayWidthPx: 900,
                padVertical: -140,
                padHorizontal: 0);

            Assert.IsFalse(projected.IsEmpty);
            // Should NOT overlap the region — overlay bottom < region top.
            var intersection = projected;
            intersection.Intersect(region);
            Assert.IsTrue(intersection.IsEmpty, "Projected overlay must clear the region with default -140 pad.");
        }

        [TestMethod]
        public void Project_ZeroMaxHeight_FallsBackTo25PercentOfScreen()
        {
            var region = new Rect(200, 100, 1200, 200);
            var screen = new Rect(0, 0, 1920, 1080);
            var projected = OverlayRegionValidator.ProjectOverlayRect(
                captureRegionScreenPx: region,
                screenBoundsScreenPx: screen,
                dpiScale: 1.0,
                maxOverlayHeightPx: 0,       // auto
                maxOverlayWidthPx: 900,
                padVertical: 20,
                padHorizontal: 0);

            Assert.AreEqual(270, projected.Height, "25% of 1080 = 270.");
        }

        [TestMethod]
        public void Project_OverlayTrappedInsideTallRegion_Overlaps()
        {
            // Huge region near screen top; overlay can't escape above or below
            // → projection clamps inside the region, which the gate catches.
            var region = new Rect(0, 0, 1920, 900);
            var screen = new Rect(0, 0, 1920, 1080);
            var projected = OverlayRegionValidator.ProjectOverlayRect(
                captureRegionScreenPx: region,
                screenBoundsScreenPx: screen,
                dpiScale: 1.0,
                maxOverlayHeightPx: 100,
                maxOverlayWidthPx: 900,
                padVertical: -140,
                padHorizontal: 0);

            var intersection = projected;
            intersection.Intersect(region);
            Assert.IsFalse(intersection.IsEmpty, "Overlay should have nowhere to go → overlaps.");
        }

        [TestMethod]
        public void Project_EmptyRegion_ReturnsEmpty()
        {
            var projected = OverlayRegionValidator.ProjectOverlayRect(
                captureRegionScreenPx: Rect.Empty,
                screenBoundsScreenPx: new Rect(0, 0, 1920, 1080),
                dpiScale: 1.0,
                maxOverlayHeightPx: 100,
                maxOverlayWidthPx: 900,
                padVertical: -140,
                padHorizontal: 0);
            Assert.IsTrue(projected.IsEmpty);
        }

        [TestMethod]
        public void Project_DpiScale_PreservesScreenPixelOutput()
        {
            // At DPI 1.5, same physical region should project to the same
            // physical rect (the scale factor cancels out in conversion).
            var region = new Rect(300, 1200, 1800, 300);     // physical px on a 2880x1620 screen
            var screen = new Rect(0, 0, 2880, 1620);         // physical px
            var projected150 = OverlayRegionValidator.ProjectOverlayRect(
                region, screen, dpiScale: 1.5,
                maxOverlayHeightPx: 100, maxOverlayWidthPx: 900,
                padVertical: -140, padHorizontal: 0);

            Assert.IsFalse(projected150.IsEmpty);
            // Projected overlay must be contained by the screen (no overflow).
            Assert.IsTrue(projected150.Left >= 0);
            Assert.IsTrue(projected150.Top >= 0);
            Assert.IsTrue(projected150.Right <= screen.Right + 1);   // +1 for rounding
            Assert.IsTrue(projected150.Bottom <= screen.Bottom + 1);
        }
    }
}
