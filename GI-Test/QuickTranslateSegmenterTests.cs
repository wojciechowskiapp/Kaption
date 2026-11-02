using System;
using System.Collections.Generic;
using System.Linq;
using GI_Subtitles.Services.QuickTranslate;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class QuickTranslateSegmenterTests
    {
        private const string Title = "Kamisato Art: Hyouka";
        private const string Desc =
            "Summons blooming ice to launch nearby opponents, dealing AoE Cryo DMG. " +
            "The ice lingers for a while, then shatters into frost that slows opponents down considerably.";
        private const string Desc2 =
            "Unleashes a cutting storm of frost that follows the character, continuously dealing Cryo DMG " +
            "to all opponents caught within its range for the whole duration.";
        private const string NaKey = "Normal Attack";
        private const string NaDesc =
            "Normal Attack\nPerform up to 5 rapid strikes using a polearm that hits harder with every swing.\n" +
            "Charged Attack\nConsumes a certain amount of Stamina to lunge forward.";
        private const string HsrKey = "Deals Ice DMG equal to 1[i]% of March 7th's ATK to a single target enemy.";

        private static readonly Dictionary<string, string> Dict = new Dictionary<string, string>
        {
            [Title] = "Sztuka Kamisato: Hyouka",
            [Desc] = "OPIS-HYOUKA",
            [Desc2] = "OPIS-BURZA",
            [NaKey] = "Atak podstawowy",
            [NaDesc] = "OPIS-ATAK",
            [HsrKey] = "Zadaje obrażenia Lodowe równe 1[i]% ATK March 7th jednemu wrogowi.",
            ["Elemental Skill"] = "Umiejętność żywiołu",
            ["Summons"] = "Przyzywa",
            ["Cryo"] = "Kriogeniczny",
        };

        private static OptimizedMatcher _matcher;

        [ClassInitialize]
        public static void Init(TestContext _) => _matcher = new OptimizedMatcher(Dict, "EN");

        private static MatchProbe Match(string text) => QuickTranslateSegmenter.ProbeWith(_matcher)(text);

        private static IReadOnlyList<QuickTranslateSegment> Run(params string[] lines) =>
            QuickTranslateSegmenter.Segment(lines, Match);

        [TestMethod]
        public void TitlePlusDescription_YieldsTwoSegments()
        {
            var segs = Run(Title,
                "Summons blooming ice to launch nearby opponents, dealing",
                "AoE Cryo DMG. The ice lingers for a while, then shatters",
                "into frost that slows opponents down considerably.");

            Assert.AreEqual(2, segs.Count, Dump(segs));
            Assert.AreEqual("Sztuka Kamisato: Hyouka", segs[0].Translation);
            Assert.AreEqual("OPIS-HYOUKA", segs[1].Translation);
            Assert.IsTrue(segs.All(s => s.IsMatched));
        }

        [TestMethod]
        public void DescriptionWrappedAcrossLines_OneSegment()
        {
            var segs = Run(
                "Summons blooming ice to launch nearby opponents, dealing",
                "AoE Cryo DMG. The ice lingers for a while, then shatters",
                "into frost that slows opponents down considerably.");

            Assert.AreEqual(1, segs.Count, Dump(segs));
            Assert.AreEqual(Desc, segs[0].EnglishKey);
        }

        [TestMethod]
        public void NextKeyNotSwallowed()
        {
            var segs = Run(
                "Summons blooming ice to launch nearby opponents, dealing",
                "AoE Cryo DMG. The ice lingers for a while, then shatters",
                "into frost that slows opponents down considerably.",
                "Unleashes a cutting storm of frost that follows the character,",
                "continuously dealing Cryo DMG to all opponents caught within",
                "its range for the whole duration.");

            Assert.AreEqual(2, segs.Count, Dump(segs));
            Assert.AreEqual("OPIS-HYOUKA", segs[0].Translation);
            Assert.AreEqual("OPIS-BURZA", segs[1].Translation);
        }

        [TestMethod]
        public void TitleThenLongerKeyStartingWithIt_PrefersLongerKey()
        {
            var segs = Run("Normal Attack",
                "Perform up to 5 rapid strikes using a polearm that hits harder with every swing.",
                "Charged Attack",
                "Consumes a certain amount of Stamina to lunge forward.");

            Assert.AreEqual(1, segs.Count, Dump(segs));
            Assert.AreEqual("OPIS-ATAK", segs[0].Translation);
        }

        // Genshin Normal Attack panels: every description starts with the bare header
        // (itself a key) and many share the first sentence, so the key is only unique later.
        private const string NaA = "Normal Attack\nPerforms up to 5 rapid strikes.\nCharged Attack\nConsumes Stamina to fire a piercing arrow.\nPlunging Attack\nPlunges from mid-air to strike the ground.";
        private const string NaB = "Normal Attack\nPerforms up to 5 rapid strikes.\nCharged Attack\nConsumes Stamina to unleash a flurry of icy slashes.\nPlunging Attack\nPlunges from mid-air to strike the ground.";
        private const string NaC = "Normal Attack\nPerforms up to 5 rapid strikes.\nCharged Attack\nConsumes Stamina to dash forward and cut through enemies.\nPlunging Attack\nPlunges from mid-air to strike the ground.";

        [TestMethod]
        public void SharedHeaderAndFirstSentence_ResolvesToTheRightDescription()
        {
            var dict = new Dictionary<string, string>
            {
                ["Normal Attack"] = "Atak podstawowy",
                [NaA] = "OPIS-A",
                [NaB] = "OPIS-B",
                [NaC] = "OPIS-C",
            };
            var m = new OptimizedMatcher(dict, "EN");
            var segs = QuickTranslateSegmenter.Segment(NaB.Split('\n'),
                QuickTranslateSegmenter.ProbeWith(m));

            Assert.AreEqual(1, segs.Count, Dump(segs));
            Assert.AreEqual("OPIS-B", segs[0].Translation);
        }

        [TestMethod]
        public void AmbiguousFuzzyPrefix_IsNotGuessed()
        {
            // Two keys share a long opening; OCR misread one letter and the selection stops
            // inside the shared part, so either key could be meant. Showing one would be a guess.
            var dict = new Dictionary<string, string>
            {
                ["Increases the DMG dealt by allies by a large amount for several turns. Afterwards, heals all allies."] = "LECZY",
                ["Increases the DMG dealt by allies by a large amount for several turns. Afterwards, shields all allies."] = "OSLANIA",
            };
            var m = new OptimizedMatcher(dict, "EN");
            var segs = QuickTranslateSegmenter.Segment(
                new[] { "Increases the DMG dea1t by allies by a large amount for several turns." },
                QuickTranslateSegmenter.ProbeWith(m));

            Assert.AreEqual(1, segs.Count, Dump(segs));
            Assert.IsFalse(segs[0].IsMatched, Dump(segs));
        }

        [TestMethod]
        public void MarkupStrippedFromTranslation()
        {
            var dict = new Dictionary<string, string> { ["Has a 50% base chance to Freeze the target."] = "Ma 50% <u>bazowej szansy</u> na <i>zamrożenie</i> celu." };
            var m = new OptimizedMatcher(dict, "EN");
            var segs = QuickTranslateSegmenter.Segment(new[] { "Has a 50% base chance to Freeze the target." },
                QuickTranslateSegmenter.ProbeWith(m));

            Assert.AreEqual("Ma 50% bazowej szansy na zamrożenie celu.", segs[0].Translation);
        }

        [TestMethod]
        public void JunkLinesBecomeUnmatched()
        {
            var segs = Run("Lv. 10", Title, "CD: 10s");

            Assert.AreEqual(3, segs.Count, Dump(segs));
            Assert.IsFalse(segs[0].IsMatched);
            Assert.AreEqual("Sztuka Kamisato: Hyouka", segs[1].Translation);
            Assert.IsFalse(segs[2].IsMatched);
            Assert.AreEqual("CD: 10s", segs[2].Source);
        }

        [TestMethod]
        public void ConsecutiveUnmatchedLinesMerge()
        {
            var segs = Run("Lv. 10", "Upgrade", Title);

            Assert.AreEqual(2, segs.Count, Dump(segs));
            Assert.AreEqual("Lv. 10\nUpgrade", segs[0].Source);
        }

        [TestMethod]
        public void NothingMatches_AllUnmatched()
        {
            var segs = Run("zzqx vbnm", "lorem ipsum dolor sit amet consectetur");

            Assert.AreEqual(1, segs.Count);
            Assert.IsFalse(segs[0].IsMatched);
        }

        [TestMethod]
        public void SymbolOnlyLinesDropped()
        {
            var segs = Run("•", Title, "—");

            Assert.AreEqual(1, segs.Count, Dump(segs));
            Assert.IsTrue(segs[0].IsMatched);
        }

        [TestMethod]
        public void HsrPlaceholder_FilledFromSource()
        {
            var segs = Run("Deals Ice DMG equal to 50% of March 7th's ATK to a single target enemy.");

            Assert.AreEqual(1, segs.Count, Dump(segs));
            Assert.AreEqual("Zadaje obrażenia Lodowe równe 50% ATK March 7th jednemu wrogowi.", segs[0].Translation);
        }

        [TestMethod]
        public void EscapedNewlinesBecomeRealNewlines()
        {
            var dict = new Dictionary<string, string> { ["Kamisato Art: Soumetsu"] = "Linia jeden\\nLinia dwa" };
            var m = new OptimizedMatcher(dict, "EN");
            var segs = QuickTranslateSegmenter.Segment(new[] { "Kamisato Art: Soumetsu" },
                QuickTranslateSegmenter.ProbeWith(m));

            Assert.AreEqual("Linia jeden\nLinia dwa", segs[0].Translation);
        }

        [TestMethod]
        public void OrderLines_SortsRowsThenColumns()
        {
            var lines = QuickTranslateSegmenter.OrderLines(new[]
            {
                ("second row", 52f, 10f, 20f),
                ("right", 11f, 300f, 20f),
                ("left", 10f, 10f, 20f),
            });

            CollectionAssert.AreEqual(new[] { "left right", "second row" }, lines.ToArray());
        }

        [TestMethod]
        public void OrderLines_SkipsEmptyText()
        {
            var lines = QuickTranslateSegmenter.OrderLines(new[] { ("", 1f, 1f, 10f), ("ok", 30f, 1f, 10f) });
            CollectionAssert.AreEqual(new[] { "ok" }, lines.ToArray());
        }

        [TestMethod]
        public void CapsRespected()
        {
            var many = Enumerable.Range(0, 300).Select(i => "unmatched line number " + i).ToArray();
            var segs = Run(many);

            int consumed = segs.Sum(s => s.Source.Split('\n').Length);
            Assert.IsTrue(consumed <= QuickTranslateSegmenter.MaxLines, $"consumed {consumed}");
        }

        [TestMethod]
        public void MatcherThrows_LineBecomesUnmatched()
        {
            var segs = QuickTranslateSegmenter.Segment(new[] { "anything" },
                new Func<string, MatchProbe>(_ => throw new InvalidOperationException("boom")));

            Assert.AreEqual(1, segs.Count);
            Assert.IsFalse(segs[0].IsMatched);
        }

        [TestMethod]
        public void EmptyInput_EmptyResult()
        {
            Assert.AreEqual(0, QuickTranslateSegmenter.Segment(Array.Empty<string>(), Match).Count);
            Assert.AreEqual(0, QuickTranslateSegmenter.Segment(null, Match).Count);
        }

        private static string Dump(IReadOnlyList<QuickTranslateSegment> segs) =>
            string.Join(" | ", segs.Select(s => $"[{(s.IsMatched ? "M" : "U")}] {s.Source.Replace("\n", "⏎")} => {s.Translation}"));
    }
}
