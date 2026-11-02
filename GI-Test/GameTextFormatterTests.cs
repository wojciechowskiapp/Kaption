using GI_Subtitles.Services.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// HSR text keeps `#1[i]%`-style tokens; merge-textmap strips the '#', so packs carry "1[i]%".
    /// These tests pin how the numbers come back.
    /// </summary>
    [TestClass]
    public class GameTextFormatterTests
    {
        [TestMethod]
        public void Params_PercentScaled()
        {
            Assert.AreEqual("ATK +12%.", GameTextFormatter.FillFromParams("ATK +1[i]%.", "ATK +1[i]%.", new[] { 0.12 }));
        }

        [TestMethod]
        public void Params_PlainValueNotScaled()
        {
            Assert.AreEqual("na 2 tury", GameTextFormatter.FillFromParams("for 1[i] turn(s)", "na 1[i] tury", new[] { 2.0 }));
        }

        [TestMethod]
        public void Params_DecimalFormat()
        {
            Assert.AreEqual("SPD +2.5", GameTextFormatter.FillFromParams("SPD +1[f1]", "SPD +1[f1]", new[] { 2.5 }));
            Assert.AreEqual("DMG +12.5%", GameTextFormatter.FillFromParams("DMG +1[f1]%", "DMG +1[f1]%", new[] { 0.125 }));
        }

        [TestMethod]
        public void Params_TargetMissingPercent_Appends()
        {
            Assert.AreEqual("ATK wzrasta o 12%",
                GameTextFormatter.FillFromParams("ATK increases by 1[i]%", "ATK wzrasta o 1[i]", new[] { 0.12 }));
        }

        [TestMethod]
        public void Params_ReorderedIndices()
        {
            Assert.AreEqual("przez 3 tury zadaje 50%",
                GameTextFormatter.FillFromParams("deals 1[i]% for 2[i] turns", "przez 2[i] tury zadaje 1[i]%", new[] { 0.5, 3.0 }));
        }

        [TestMethod]
        public void Params_MissingParam_Question()
        {
            Assert.AreEqual("ATK +?%", GameTextFormatter.FillFromParams("ATK +2[i]%", "ATK +2[i]%", new[] { 0.1 }));
        }

        [TestMethod]
        public void Params_IntegerRoundsAwayFromZero()
        {
            Assert.AreEqual("3", GameTextFormatter.FillFromParams("1[i]", "1[i]", new[] { 2.5 }));
        }

        [TestMethod]
        public void Source_MapsNumbersPositionally()
        {
            Assert.AreEqual("Zadaje obrażenia równe 150% ATK przez 2 tury",
                GameTextFormatter.FillFromSource(
                    "Deals DMG equal to 1[i]% of ATK for 2[i] turn(s)",
                    "Zadaje obrażenia równe 1[i]% ATK przez 2[i] tury",
                    "Deals DMG equal to 150% of ATK for 2 turn(s)"));
        }

        [TestMethod]
        public void Source_LiteralNumbersSkipped()
        {
            Assert.AreEqual("2 elementy: ATK +12%",
                GameTextFormatter.FillFromSource("2-Pc: ATK +1[i]%", "2 elementy: ATK +1[i]%", "2-Pc: ATK +12%"));
        }

        [TestMethod]
        public void Source_CountMismatch_Question()
        {
            Assert.AreEqual("ATK +?%", GameTextFormatter.FillFromSource("ATK +1[i]%", "ATK +1[i]%", "ATK"));
        }

        [TestMethod]
        public void Source_LiteralMismatch_Question()
        {
            // Misaligned numbers: never show a value from the wrong slot.
            Assert.AreEqual("4 elementy: ?%",
                GameTextFormatter.FillFromSource("4-Pc: 1[i]%", "4 elementy: 1[i]%", "2-Pc: 15%"));
        }

        [TestMethod]
        public void Source_DecimalSourceKept()
        {
            Assert.AreEqual("Szansa 12.5%", GameTextFormatter.FillFromSource("Chance 1[f1]%", "Szansa 1[f1]%", "Chance 12.5%"));
        }

        [TestMethod]
        public void NoTokens_ReturnsTargetUnchanged()
        {
            Assert.AreEqual("Zwykły tekst 50%", GameTextFormatter.FillFromParams("Plain text 50%", "Zwykły tekst 50%", new[] { 1.0 }));
            Assert.AreEqual("Zwykły tekst", GameTextFormatter.FillFromSource("Plain text", "Zwykły tekst", "Plain text 12"));
        }

        [TestMethod]
        public void HasTokens_Detects()
        {
            Assert.IsTrue(GameTextFormatter.HasTokens("ATK +1[i]%"));
            Assert.IsTrue(GameTextFormatter.HasTokens("x 2[f2] y"));
            Assert.IsFalse(GameTextFormatter.HasTokens("Lv. 10 [i]"));
            Assert.IsFalse(GameTextFormatter.HasTokens(null));
        }

        [TestMethod]
        public void StripMarkup_RemovesGameTagsOnly()
        {
            Assert.AreEqual("50% base chance to Freeze",
                GameTextFormatter.StripMarkup("50% <u>base chance</u> to <color=#f29e38ff>Freeze</color>"));
            Assert.AreEqual("Fischl's raven", GameTextFormatter.StripMarkup("<i>Fischl's</i> <b>raven</b>"));
            Assert.AreEqual("a < b and c > d", GameTextFormatter.StripMarkup("a < b and c > d"));
            Assert.AreEqual("", GameTextFormatter.StripMarkup(null));
        }

        [TestMethod]
        public void StripMarkup_HandlesSpritesLinksAndBrokenTags()
        {
            // Seen in HSR catalog text and Polish pack values.
            Assert.AreEqual("Gain Fire DMG.", GameTextFormatter.StripMarkup("Gain <icon SpriteName=\"IconFire\" /> Fire DMG."));
            Assert.AreEqual("Zwiększa obrażenia.", GameTextFormatter.StripMarkup("Zwiększa </color obrażenia."));
            Assert.AreEqual("Zadaje Wzmocnienie.", GameTextFormatter.StripMarkup("Zadaje {LINKP#123>Wzmocnienie{/LINK}."));
            Assert.AreEqual("Patrz Słabość tutaj.", GameTextFormatter.StripMarkup("Patrz <LINKN#55>Słabość</LINK> tutaj."));
            Assert.AreEqual("Big text", GameTextFormatter.StripMarkup("<size=40>Big</size> <sprite=\"x\">text"));
            Assert.AreEqual("a < b and c > d", GameTextFormatter.StripMarkup("a < b and c > d"));
            Assert.AreEqual("Line one\nLine two", GameTextFormatter.StripMarkup("Line one  <icon X>\nLine two"));
        }

        [TestMethod]
        public void NullInputs_DoNotThrow()
        {
            Assert.AreEqual("", GameTextFormatter.FillFromParams(null, null, null));
            Assert.AreEqual("abc", GameTextFormatter.FillFromSource(null, "abc", null));
        }
    }
}
