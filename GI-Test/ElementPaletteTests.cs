using System.Windows.Media;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The games' own text can spell an element with a no-break space ("Auric Ink" in Zenless Zone Zero);
    /// the hue tables are keyed with plain spaces, so the lookup must treat both spellings as one element.
    /// </summary>
    [TestClass]
    public class ElementPaletteTests
    {
        [TestMethod]
        public void Zzz_element_with_a_no_break_space_gets_the_same_hue_as_the_plain_spelling()
        {
            Color plain = ElementPalette.For("zzz", "Auric Ink");
            Color spaced = ElementPalette.For("zzz", "Auric Ink");

            Assert.AreEqual(plain, spaced);
            Assert.AreNotEqual(ElementPalette.For("zzz", "Not an element"), spaced, "the no-break space spelling must not fall back to the neutral hue");
        }

        [TestMethod]
        public void Order_ignores_the_no_break_space_too()
        {
            Assert.AreEqual(ElementPalette.Order("Auric Ink"), ElementPalette.Order("Auric Ink"));
        }
    }
}
