using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>Lead's rule for the Characters page: percent stats use the game's short names, which need no gloss.</summary>
    [TestClass]
    public class StatShortNameTests
    {
        [TestMethod]
        [DataRow("ATK Percentage", "ATK%")]
        [DataRow("DEF Percentage", "DEF%")]
        [DataRow("HP Percentage", "HP%")]
        [DataRow("Elemental Mastery", "Elemental Mastery")]
        [DataRow("CRIT Rate", "CRIT Rate")]
        [DataRow("CRIT DMG", "CRIT DMG")]
        [DataRow("Energy Recharge", "Energy Recharge")]
        [DataRow("Pyro DMG Bonus", "Pyro DMG Bonus")]
        public void PercentStats_UseTheShortName(string english, string shown)
        {
            Assert.AreEqual(shown, CharacterOverviewViewModel.ShortStatName(english));
        }

        [TestMethod]
        public void AShortName_HasNoGlossLine()
        {
            Assert.AreEqual("", CharacterOverviewViewModel.GlossOf(new[] { "ATK%" }, new[] { "ATK%" }, ""),
                "the gloss input for a short name is the short name itself");
            Assert.AreEqual("ATK% / Doładowanie energii",
                CharacterOverviewViewModel.GlossOf(new[] { "ATK%", "Energy Recharge" }, new[] { "ATK%", "Doładowanie energii" }, " / "),
                "a row with another stat that translates keeps its gloss");
        }
    }
}
