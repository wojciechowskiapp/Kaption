using System.Windows;
using GI_Subtitles.Services.QuickTranslate;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Rectangle = System.Drawing.Rectangle;

namespace GI_Test
{
    [TestClass]
    public class QuickTranslateAreaTests
    {
        [TestMethod]
        public void FromPicker_Cancel_IsRejected()
        {
            Assert.IsFalse(QuickTranslateArea.TryFromPicker(Rect.Empty, out var selection));
            Assert.AreEqual(Rectangle.Empty, selection);
        }

        [TestMethod]
        public void FromPicker_ClickJitter_IsRejected()
        {
            // GlitchTip 8730524: a click without a drag picked 3x0.
            Assert.IsFalse(QuickTranslateArea.TryFromPicker(new Rect(812, 640, 3, 0), out _));
        }

        [TestMethod]
        public void FromPicker_SubPixelHeight_IsRejected()
        {
            // Positive before rounding, zero after it.
            Assert.IsFalse(QuickTranslateArea.TryFromPicker(new Rect(812, 640, 300, 0.4), out var selection));
            Assert.AreEqual(Rectangle.Empty, selection);
        }

        [TestMethod]
        [DataRow(7.0, 7.0)]
        [DataRow(7.0, 300.0)]
        [DataRow(300.0, 7.0)]
        [DataRow(300.0, 7.4)]
        public void FromPicker_BelowMinSide_IsRejected(double width, double height)
        {
            Assert.IsFalse(QuickTranslateArea.TryFromPicker(new Rect(100, 100, width, height), out var selection));
            Assert.AreEqual(Rectangle.Empty, selection);
        }

        [TestMethod]
        public void FromPicker_MinSide_IsAccepted()
        {
            Assert.IsTrue(QuickTranslateArea.TryFromPicker(new Rect(100, 200, 8, 8), out var exact));
            Assert.AreEqual(new Rectangle(100, 200, 8, 8), exact);

            // The minimum applies to the rounded sides: 7.6 counts as 8.
            Assert.IsTrue(QuickTranslateArea.TryFromPicker(new Rect(10.4, 20.6, 7.6, 8.4), out var rounded));
            Assert.AreEqual(new Rectangle(10, 21, 8, 8), rounded);
        }

        [TestMethod]
        public void FromPicker_NormalPick_RoundsEachValue()
        {
            Assert.IsTrue(QuickTranslateArea.TryFromPicker(new Rect(1520.4, 880.6, 300.6, 48.4), out var selection));
            Assert.AreEqual(new Rectangle(1520, 881, 301, 48), selection);
        }

        [TestMethod]
        public void FromPicker_NegativeOrigin_IsAccepted()
        {
            // A monitor left of or above the primary has negative coordinates.
            Assert.IsTrue(QuickTranslateArea.TryFromPicker(new Rect(-1900.4, -200.6, 400, 120), out var selection));
            Assert.AreEqual(new Rectangle(-1900, -201, 400, 120), selection);
        }

        // WPF Rect throws on a negative width or height; Rect.Empty (-inf sizes) is the only negative case.
        [TestMethod]
        [DataRow(double.NaN, 100.0, 300.0, 50.0)]
        [DataRow(100.0, double.NaN, 300.0, 50.0)]
        [DataRow(100.0, 100.0, double.NaN, 50.0)]
        [DataRow(100.0, 100.0, 300.0, double.NaN)]
        [DataRow(double.PositiveInfinity, 100.0, 300.0, 50.0)]
        [DataRow(double.NegativeInfinity, 100.0, 300.0, 50.0)]
        [DataRow(100.0, double.PositiveInfinity, 300.0, 50.0)]
        [DataRow(100.0, double.NegativeInfinity, 300.0, 50.0)]
        [DataRow(100.0, 100.0, double.PositiveInfinity, 50.0)]
        [DataRow(100.0, 100.0, 300.0, double.PositiveInfinity)]
        public void FromPicker_NonFinite_IsRejected(double x, double y, double width, double height)
        {
            Assert.IsFalse(QuickTranslateArea.TryFromPicker(new Rect(x, y, width, height), out var selection));
            Assert.AreEqual(Rectangle.Empty, selection);
        }
    }
}
