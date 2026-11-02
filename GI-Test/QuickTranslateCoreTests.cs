using System;
using System.Windows;
using GI_Subtitles.Services.QuickTranslate;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class QuickTranslateCoreTests
    {
        private static readonly Rect WorkArea = new Rect(0, 0, 1920, 1040);

        private static QuickTranslateSettings Auto() => QuickTranslateSettings.FromValues("Auto", 8, 16);

        [TestMethod]
        public void Auto_ShortText_IsFourSeconds()
        {
            Assert.AreEqual(TimeSpan.FromSeconds(4), ReadingTime.Compute(20, Auto()));
        }

        [TestMethod]
        public void Auto_ScalesWithLength()
        {
            // 2 s + 654 / 15 chars per second — the median Genshin skill text.
            var t = ReadingTime.Compute(654, Auto()).Value.TotalSeconds;
            Assert.AreEqual(2 + 654 / 15.0, t, 0.01);
        }

        [TestMethod]
        public void Auto_LongSkill_CapsAtNinety()
        {
            Assert.AreEqual(TimeSpan.FromSeconds(90), ReadingTime.Compute(2436, Auto()));
        }

        [TestMethod]
        public void Manual_ReturnsNull()
        {
            var s = QuickTranslateSettings.FromValues("Manual", 8, 16);
            Assert.IsNull(ReadingTime.Compute(500, s));
        }

        [TestMethod]
        public void Timed_UsesSeconds()
        {
            var s = QuickTranslateSettings.FromValues("Timed", 25, 16);
            Assert.AreEqual(TimeSpan.FromSeconds(25), ReadingTime.Compute(5, s));
        }

        [TestMethod]
        public void Settings_ClampsAndDefaults()
        {
            var s = QuickTranslateSettings.FromValues("bogus", 999, 1);
            Assert.AreEqual(QuickTranslateCloseMode.Auto, s.CloseMode);
            Assert.AreEqual(120, s.Seconds);
            Assert.AreEqual(12, s.FontSize);

            var low = QuickTranslateSettings.FromValues(null, 0, 99);
            Assert.AreEqual(QuickTranslateCloseMode.Auto, low.CloseMode);
            Assert.AreEqual(2, low.Seconds);
            Assert.AreEqual(28, low.FontSize);
        }

        [TestMethod]
        public void Settings_ModeIsCaseInsensitive()
        {
            Assert.AreEqual(QuickTranslateCloseMode.Manual, QuickTranslateSettings.FromValues("manual", 8, 16).CloseMode);
        }

        [TestMethod]
        public void Placement_FitsBelow()
        {
            var sel = new Rect(800, 200, 400, 100);
            var r = PopupPlacement.Compute(sel, new Size(300, 200), WorkArea);
            Assert.AreEqual(308, r.Top, 0.01);
            Assert.AreEqual(850, r.Left, 0.01); // centred on the selection
        }

        [TestMethod]
        public void Placement_FlipsAboveAtBottomEdge()
        {
            var sel = new Rect(800, 800, 400, 200);
            var r = PopupPlacement.Compute(sel, new Size(300, 300), WorkArea);
            Assert.AreEqual(800 - 8 - 300, r.Top, 0.01);
            AssertInside(r);
        }

        [TestMethod]
        public void Placement_GoesBesideWhenNoVerticalRoom()
        {
            // A tall skill panel on the right: no room above or below.
            var sel = new Rect(1300, 100, 500, 900);
            var r = PopupPlacement.Compute(sel, new Size(500, 600), WorkArea);
            Assert.IsTrue(r.Right <= sel.Left - 8 + 0.01, $"expected left of the selection, got {r}");
            AssertInside(r);
        }

        [TestMethod]
        public void Placement_ClampsHorizontally()
        {
            var sel = new Rect(1850, 100, 60, 40);
            var r = PopupPlacement.Compute(sel, new Size(400, 100), WorkArea);
            Assert.AreEqual(WorkArea.Right - 400, r.Left, 0.01);
            AssertInside(r);
        }

        [TestMethod]
        public void Placement_TallerThanWorkArea_PinsToTop()
        {
            var sel = new Rect(100, 100, 200, 50);
            var r = PopupPlacement.Compute(sel, new Size(300, 2000), WorkArea);
            Assert.AreEqual(WorkArea.Top, r.Top, 0.01);
        }

        [TestMethod]
        public void Placement_RespectsWorkAreaOffset()
        {
            // Secondary monitor to the left of the primary.
            var wa = new Rect(-1920, 0, 1920, 1080);
            var sel = new Rect(-1900, 1000, 300, 60);
            var r = PopupPlacement.Compute(sel, new Size(400, 200), wa);
            Assert.IsTrue(r.Left >= wa.Left && r.Right <= wa.Right && r.Top >= wa.Top && r.Bottom <= wa.Bottom, r.ToString());
        }

        [TestMethod]
        public void Dismissal_ClosesAfterDuration()
        {
            var d = new QuickTranslateDismissal(TimeSpan.FromSeconds(4));
            Assert.IsFalse(d.Tick(TimeSpan.FromSeconds(3)));
            Assert.IsTrue(d.Tick(TimeSpan.FromSeconds(1.1)));
        }

        [TestMethod]
        public void Dismissal_PausedDoesNotClose()
        {
            var d = new QuickTranslateDismissal(TimeSpan.FromSeconds(4));
            d.Pause();
            Assert.IsFalse(d.Tick(TimeSpan.FromSeconds(60)));
        }

        [TestMethod]
        public void Dismissal_PinnedNeverCloses()
        {
            var d = new QuickTranslateDismissal(TimeSpan.FromSeconds(4));
            d.Pin();
            d.Resume();
            Assert.IsTrue(d.IsPinned);
            Assert.IsFalse(d.Tick(TimeSpan.FromSeconds(600)));
        }

        [TestMethod]
        public void Dismissal_ResumeGrantsMinimum()
        {
            var d = new QuickTranslateDismissal(TimeSpan.FromSeconds(4));
            d.Tick(TimeSpan.FromSeconds(3.9));
            d.Pause();
            d.Resume();
            Assert.IsFalse(d.Tick(TimeSpan.FromSeconds(1.4)));
            Assert.IsTrue(d.Tick(TimeSpan.FromSeconds(0.2)));
        }

        [TestMethod]
        public void Dismissal_ManualNeverCloses()
        {
            var d = new QuickTranslateDismissal(null);
            Assert.IsTrue(d.IsPinned);
            Assert.IsFalse(d.Tick(TimeSpan.FromHours(1)));
        }

        [TestMethod]
        public void Dismissal_RemainingFractionShrinks()
        {
            var d = new QuickTranslateDismissal(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1.0, d.RemainingFraction, 0.001);
            d.Tick(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0.5, d.RemainingFraction, 0.001);
        }

        private static void AssertInside(Rect r)
        {
            Assert.IsTrue(r.Left >= WorkArea.Left - 0.01 && r.Top >= WorkArea.Top - 0.01 &&
                          r.Right <= WorkArea.Right + 0.01 && r.Bottom <= WorkArea.Bottom + 0.01,
                $"popup {r} not inside {WorkArea}");
        }
    }
}
