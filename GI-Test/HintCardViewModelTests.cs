using System.Collections.Generic;
using System.Linq;
using GI_Subtitles.Services.Hints;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// What the hint card draws follows the player's own shortcuts (Views/HintCardViewModel): a changed key shows
    /// the new combination, and a shortcut that is not bound is never named.
    /// </summary>
    [TestClass]
    public class HintCardViewModelTests
    {
        private static string English(string key, string fallback) => fallback;

        private static HintBindings Bound(Dictionary<int, string> keys) => HintBindings.From(id => keys.GetValueOrDefault(id));

        private static Dictionary<int, string> Defaults() => new()
        {
            [HintCatalog.KeyToggleTranslation] = "Ctrl+Shift+S",
            [HintCatalog.KeyHideSubtitles] = "Ctrl+Shift+H",
            [HintCatalog.KeyShowDialogueFrame] = "Ctrl+Shift+D",
            [HintCatalog.KeyQuickTranslate] = "Ctrl+Q",
            [HintCatalog.KeyFixLine] = "Ctrl+Shift+E",
        };

        [TestMethod]
        public void TheSheet_ShowsThePlayersOwnShortcuts()
        {
            var sheet = HintCardViewModel.Sheet(Bound(Defaults()), English, clock: true);
            CollectionAssert.AreEqual(new[] { "Ctrl+Q", "Ctrl+Shift+H", "Ctrl+Shift+E", "Ctrl+Shift+S" }, sheet.Rows.Select(r => r.Keys).ToArray());
            Assert.AreEqual("Translate any text on screen", sheet.Rows[0].Label);
            Assert.AreEqual(new HintLine("", "Ctrl+Shift+D", " shows this again"), sheet.Footer);

            // The player moved "hide subtitles" to Alt+H and quick translate to Ctrl+T: the sheet says so.
            var keys = Defaults();
            keys[HintCatalog.KeyHideSubtitles] = "Alt+H";
            keys[HintCatalog.KeyQuickTranslate] = "Ctrl+T";
            var rebound = HintCardViewModel.Sheet(Bound(keys), English, clock: true);
            CollectionAssert.AreEqual(new[] { "Ctrl+T", "Alt+H", "Ctrl+Shift+E", "Ctrl+Shift+S" }, rebound.Rows.Select(r => r.Keys).ToArray());
        }

        [TestMethod]
        public void AShortcutThatIsNotBound_IsLeftOut_AndTheFooterNamesTheTabInstead()
        {
            var keys = Defaults();
            keys.Remove(HintCatalog.KeyFixLine);          // another app owns the combination
            keys.Remove(HintCatalog.KeyShowDialogueFrame); // no way back by key
            var sheet = HintCardViewModel.Sheet(Bound(keys), English, clock: false);
            CollectionAssert.AreEqual(new[] { "Ctrl+Q", "Ctrl+Shift+H", "Ctrl+Shift+S" }, sheet.Rows.Select(r => r.Keys).ToArray());
            Assert.IsNull(sheet.Footer.Keys);
            Assert.AreEqual(HintCatalog.SheetFooterNoKey.Fallback, sheet.Footer.Before);
            Assert.IsFalse(sheet.HasClock, "a pinned sheet stays");
        }

        [TestMethod]
        public void AnEcho_NamesTheKeyThatUndoesIt_WhereTheSentenceHasIt()
        {
            var keys = Defaults();
            keys[HintCatalog.KeyHideSubtitles] = "Alt+H";
            var echo = HintCardViewModel.Line(HintCatalog.EchoSubtitlesHidden, Bound(keys), English);
            Assert.AreEqual(new HintLine("Subtitles hidden. ", "Alt+H", " brings them back."), echo.Message);
            Assert.IsFalse(echo.HasClock, "an answer to a key has no clock");

            // Polish puts the key first in some sentences; the split follows the text, not a position.
            var polish = HintCardViewModel.Line(HintCatalog.EchoSubtitlesHidden, Bound(keys), (_, _) => "Napisy ukryte. {0} pokaże je z powrotem.");
            Assert.AreEqual(new HintLine("Napisy ukryte. ", "Alt+H", " pokaże je z powrotem."), polish.Message);

            var plain = HintCardViewModel.Line(HintCatalog.EchoSubtitlesBack, Bound(keys), English);
            Assert.AreEqual(new HintLine("Subtitles are back.", null, ""), plain.Message);

            // A tip borrows its first sentence from the Start card and has a clock.
            var tip = HintCardViewModel.Line(HintCatalog.TipNoMatch, Bound(keys), English);
            Assert.AreEqual("Kaption sees text but can't find the line. Check that the game's text is in English.", tip.Message.Before);
            Assert.IsTrue(tip.HasClock);
        }
    }
}
