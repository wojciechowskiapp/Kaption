using System.IO;
using System.Linq;
using System.Text.Json;
using GI_Subtitles.Services.Capture;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The tripwire behind "subtitles over the game's text": if the app ever reads its own Polish line again, it must
    /// notice within two reads, and it must never take the game's English for that. The pairs are what the text engine
    /// really returned for the app's own panel (Fixtures/overlay-own-line-reads.json, from the benchmark's spike).
    /// </summary>
    [TestClass]
    public class SelfReadTests
    {
        private sealed record Pair(string Shown, string Read);

        private static (string English, Pair[] Own) Load()
        {
            string path = Path.Combine(RenderTestKit.RepoRoot(), "GI-Test", "Fixtures", "overlay-own-line-reads.json");
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            Pair[] own = doc.RootElement.GetProperty("ownLines").EnumerateArray()
                .Select(p => new Pair(p.GetProperty("shown").GetString(), p.GetProperty("read").GetString())).ToArray();
            return (doc.RootElement.GetProperty("englishRead").GetString(), own);
        }

        private static int Letters(string s) => s.Count(char.IsLetterOrDigit);

        [TestMethod]
        public void TheAppsOwnLineReadBack_LooksLikeTheLineOnScreen()
        {
            var (english, own) = Load();
            Pair[] longEnough = own.Where(p => Letters(p.Shown) >= SelfRead.MinLetters).ToArray();
            Assert.IsTrue(longEnough.Length >= 6, "the fixture has lines long enough to judge");
            foreach (Pair p in longEnough)
                Assert.IsTrue(SelfRead.Looks(p.Read, p.Shown, english), $"read back: \"{p.Read}\" for \"{p.Shown}\"");
        }

        [TestMethod]
        public void TheGamesEnglish_NeverLooksLikeThePolishLine()
        {
            var (english, own) = Load();
            foreach (Pair p in own)
                Assert.IsFalse(SelfRead.Looks(english, p.Shown, english), $"the English read against \"{p.Shown}\"");
        }

        [TestMethod]
        public void AShortLine_IsNeverEvidence()
        {
            var (english, own) = Load();
            Pair brief = own.Single(p => Letters(p.Shown) < SelfRead.MinLetters);
            Assert.IsFalse(SelfRead.Looks(brief.Read, brief.Shown, english), "a few letters match too much by chance");
        }

        [TestMethod]
        public void ALineThatIsLikeItsSource_IsNeverEvidence()
        {
            // A line the pack left in English, or a name: reading the game gives the same text, and that is the game.
            const string untranslated = "Adventurers' Guild, Mondstadt branch.";
            Assert.IsFalse(SelfRead.Looks(untranslated, untranslated, untranslated));
            Assert.IsFalse(SelfRead.Looks("Adventurers Guild, Mondstadt branch", untranslated, "Adventurers' Guild, Mondstadt branch"));
        }

        [TestMethod]
        public void WithoutTheSourceRead_NothingIsEvidence()
        {
            var (_, own) = Load();
            Pair p = own[0];
            Assert.IsFalse(SelfRead.Looks(p.Read, p.Shown, null), "a predicted line has no read it came from");
            Assert.IsFalse(SelfRead.Looks(p.Read, p.Shown, ""));
            Assert.IsFalse(SelfRead.Looks("", p.Shown, "anything"));
            Assert.IsFalse(SelfRead.Looks(p.Read, "", "anything"));
        }

        [TestMethod]
        public void AnOwnLineWithMoreReadAfterIt_IsStillThatLine()
        {
            var (english, own) = Load();
            Pair p = own[0];
            Assert.IsTrue(SelfRead.Looks(p.Shown + " 1. Tak, chodźmy. 2. Nie, zostańmy tutaj jeszcze chwilę.", p.Shown, english), "the answers read after the line");
            Assert.IsTrue(SelfRead.Looks(p.Read + " 1. Tak, chodźmy. 2. Nie, zostańmy tutaj jeszcze chwilę.", p.Shown, english), "the name before it and the answers after it");
        }

        /// <summary>A tripwire whose line on screen came from a read of the game's English.</summary>
        private static SelfReadTripwire Watching(string sourceRead)
        {
            var tripwire = new SelfReadTripwire();
            tripwire.LineDecided(sourceRead, readGaveTheLine: true, lineChanged: true);
            return tripwire;
        }

        [TestMethod]
        public void OneOwnLineDoesNotTrip_TwoInARowDo()
        {
            var (english, own) = Load();
            SelfReadTripwire tripwire = Watching(english);
            Assert.IsFalse(tripwire.Observe(own[0].Read, own[0].Shown), "one read can be a coincidence");
            Assert.IsTrue(tripwire.Observe(own[0].Read, own[0].Shown), "the second in a row is the app reading itself");
        }

        [TestMethod]
        public void AWrongLineMatchedFromAnOwnRead_IsCaughtAtTheNextRead()
        {
            // The first own read is matched to some other line, which then goes on screen with that read as its source.
            var (english, own) = Load();
            SelfReadTripwire tripwire = Watching(english);
            Assert.IsFalse(tripwire.Observe(own[0].Read, own[0].Shown));
            tripwire.LineDecided(own[0].Read, readGaveTheLine: true, lineChanged: true);
            Assert.IsTrue(tripwire.Observe(own[2].Read, own[2].Shown), "the new line read back is the second own line in a row");
        }

        [TestMethod]
        public void AnOwnReadMatchedToTheSameLineAgain_DoesNotBecomeItsSource()
        {
            // The own line is read back and the matcher gives the line that is already on screen. Were that read taken
            // as the line's source, the line would be "like its source" from then on and nothing would count.
            var (english, own) = Load();
            SelfReadTripwire tripwire = Watching(english);
            Assert.IsFalse(tripwire.Observe(own[0].Read, own[0].Shown));
            tripwire.LineDecided(own[0].Read, readGaveTheLine: true, lineChanged: false);
            Assert.IsTrue(tripwire.Observe(own[0].Read, own[0].Shown), "the second own line in a row still trips");
        }

        [TestMethod]
        public void AReadOfTheGameBetweenTwoOwnLines_StartsTheCountAgain()
        {
            var (english, own) = Load();
            SelfReadTripwire tripwire = Watching(english);
            Assert.IsFalse(tripwire.Observe(own[0].Read, own[0].Shown));
            Assert.IsFalse(tripwire.Observe(english, own[0].Shown), "the game's own line");
            Assert.IsFalse(tripwire.Observe(own[0].Read, own[0].Shown), "one again, not two");
        }

        [TestMethod]
        public void AnEmptyRead_CountsNeitherWay_AndKeepsTheLinesSource()
        {
            var (english, own) = Load();
            SelfReadTripwire tripwire = Watching(english);
            Assert.IsFalse(tripwire.Observe(own[0].Read, own[0].Shown));
            Assert.IsFalse(tripwire.Observe("", own[0].Shown), "nothing was read");
            tripwire.LineDecided("", readGaveTheLine: false, lineChanged: false);
            Assert.IsTrue(tripwire.Observe(own[0].Read, own[0].Shown), "still the second own line in a row, and still the same source");
        }

        [TestMethod]
        public void APredictedLine_IsWatchedOnceAReadConfirmsIt()
        {
            var (english, own) = Load();
            var tripwire = new SelfReadTripwire();
            // Shown before anything was read: no source, so nothing can be evidence yet.
            tripwire.LineDecided("", readGaveTheLine: false, lineChanged: true);
            Assert.IsFalse(tripwire.Observe(own[0].Read, own[0].Shown));
            Assert.IsFalse(tripwire.Observe(own[0].Read, own[0].Shown));
            // The game's line is read and gives the very same translation: the line does not change, its source is known now.
            tripwire.LineDecided(english, readGaveTheLine: true, lineChanged: false);
            tripwire.Reset();
            Assert.IsFalse(tripwire.Observe(own[0].Read, own[0].Shown));
            Assert.IsTrue(tripwire.Observe(own[0].Read, own[0].Shown), "two own lines in a row after the confirmation");
        }
    }
}
