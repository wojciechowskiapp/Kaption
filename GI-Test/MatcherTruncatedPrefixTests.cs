using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Truncated typewriter frames against the real <see cref="OptimizedMatcher"/>.
    /// EN keys and PL values are verbatim from the Genshin 7.1 and Star Rail 4.2 packs;
    /// OCR inputs are verbatim frames from the benchmark recordings.
    /// </summary>
    [TestClass]
    public class MatcherTruncatedPrefixTests
    {
        private const string YesYouDid =
            "Yes, you did! Why are you denying it? It was right before I went on that rant about the moody factory owner! Oh, I remember it so clearly...";
        private const string IAgreeDuels =
            "I agree. It might also help your case if you stopped challenging people to duels all the time.";
        private const string MightIAskBrings = "Might I ask what brings you two here?";
        private const string UghBelieveIt = "Ugh, can you believe it? I am so mad right now... Come in!";
        private const string OfCourseInjured =
            "Of course, even if you get injured by accident, no need to panic. Just leave it to the doctor.";
        private const string DisposingReagents =
            "But disposing of Vache's reagents isn't enough to solve the medical supply shortage. Many of my patients can't be seen promptly, and my stock of medicine is running low.";

        private static readonly Dictionary<string, string> Genshin = new Dictionary<string, string>
        {
            ["Yes, you."] = "Tak, ty.",
            [YesYouDid] = "Tak, zrobiłeś to! Dlaczego się wypierasz? To było tuż przed moją tyradą o ponurym właścicielu fabryki! Och, tak dobrze to pamiętam...",
            ["Yes, you did. There is no hatred here, only eternal peace. No bitter salt water, only an endless spring."] =
                "Tak, zrobiłeś to. Tutaj nie ma nienawiści, jest tylko wieczny spokój. Żadnej gorzkiej słonej wody, jedynie niekończące się źródło.",
            ["Yes, you are right."] = "Tak, masz rację.",
            ["Yes, you're right. We are getting closer to the shore. But why is that..."] = "Tak, masz rację. Zbliżamy się do brzegu. Ale dlaczego to...",
            ["Terho"] = "Terho",
            ["Perhaps if you'd worked on your etiquette, I would have invited you over sooner."] = "Być może zaprosiłabym cię wcześniej, gdybyś popracował nad manierami.",
            ["Perhaps if you had less on your mind, you'd be able to absolve yourself of such troubles."] = "Może gdybyś mniej o tym myślał, byłbyś w stanie oczyścić się z tych kłopotów.",
            ["It was!"] = "Tak było!",
            ["It was... different this time."] = "Tym razem było... inaczej.",
            ["Oh..."] = "Och...",
            ["Oh, it's you two."] = "Och, to wy dwoje.",
            [MightIAskBrings] = "Czy mogę spytać, co was tutaj sprowadza?",
            ["Might I ask what this is?"] = "Czy mogę zapytać, co to jest?",
            ["Might I ask, who..."] = "Czy mogę zapytać, kto...",
            ["I agree."] = "Zgadzam się.",
            [IAgreeDuels] = "Zgadzam się. Twojej sprawie mogłoby też pomóc, gdybyś przestał cały czas wyzywać ludzi na pojedynki.",
            ["I agree. It does, but considering that non-human races in Inazuma are by no means uncommon, spooky events are to be expected... And that's exactly what my book is about — please, take a look."] =
                "Zgadzam się. To prawda, ale biorąc pod uwagę, że rasy nieludzkie w Inazuma wcale nie są rzadkością, można spodziewać się strasznych wydarzeń... I właśnie o tym jest moja książka — proszę, rzuć okiem.",
            ["I wasn't expecting to see you here."] = "Nie spodziewałem się, że cię tu zobaczę.",
            [UghBelieveIt] = "Ugh, możesz w to uwierzyć? Jestem teraz taka wściekła... Wejdź!",
            ["Ugh, can you believe her? She just ran off!"] = "Ugh, możesz w to uwierzyć? Po prostu uciekła!",
            ["Ugh, can you believe that guy? He calls us suckers, and then he just trots off!"] = "Ugh, możesz uwierzyć w tego faceta? Nazywa nas frajerami, a potem zwyczajnie odchodzi!",
        };

        private static readonly Dictionary<string, string> StarRail = new Dictionary<string, string>
        {
            [OfCourseInjured] = "Oczywiście, nawet jeśli przypadkowo zostaniesz zraniony, nie panikuj. Zostaw to lekarzowi.",
            ["Of course, even the genius Herta can't compete with me when it comes to hacking."] = "Oczywiście, nawet geniusz Herta nie może się ze mną równać, jeśli chodzi o hakowanie.",
            ["Of course, after all, she was a Landau!"] = "Oczywiście, w końcu była Landau!",
            [DisposingReagents] = "Ale zutylizowanie odczynników Vache'a to nie wystarczy, by rozwiązać niedobór leków. Wielu moich pacjentów nie może być przyjętych na czas, a moje zapasy leków się kurczą.",
            ["I want to thank you again for recovering the wonder medicine. It meant a lot to me."] = "Jeszcze raz dziękuję za odzyskanie cudownego lekarstwa. To wiele dla mnie znaczyło.",
        };

        private readonly List<string> _tempDirs = new List<string>();

        [TestCleanup]
        public void Cleanup()
        {
            foreach (string dir in _tempDirs)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch { /* best-effort */ }
            }
            _tempDirs.Clear();
        }

        // genshin-vodyanitsa-banter painted "Tak, ty." for this frame, ~400 ms before the real line.
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void AmbiguousTypewriterPrefix_IsDeclined(bool mmap)
        {
            using var matcher = Build(Genshin, mmap);

            AssertDeclined(matcher, "Yes, y");
            AssertDeclined(matcher, "Yes, you did");

            Assert.AreEqual(Genshin[YesYouDid],
                matcher.FindClosestMatch("Yes, you did! Why are yo", out string key, out bool partial),
                "Once the typed text has left every other line behind, the match resolves.");
            Assert.AreEqual(YesYouDid, key);
            Assert.IsTrue(partial);
        }

        // genshin-childe-marionette: Stage 0 accepted "Terho" at one edit while dozens of
        // keys start with the input exactly.
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void AmbiguousPrefix_IsDeclined_EvenWithAShortKeyWithinTwoEdits(bool mmap)
        {
            using var matcher = Build(Genshin, mmap);

            AssertDeclined(matcher, "Perh");
        }

        // genshin-jester-council: "It w" painted "Tak było!" before "It was... different this time."
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void FragmentShorterThanOneNgram_OnlyMatchesExactly(bool mmap)
        {
            using var matcher = Build(Genshin, mmap);

            AssertDeclined(matcher, "It w");
            Assert.AreEqual("Och...", matcher.FindClosestMatch("Oh..", out string key, out bool partial));
            Assert.AreEqual("Oh...", key);
            Assert.IsFalse(partial);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void UniquePrefix_ResolvesAsPartial(bool mmap)
        {
            using var matcher = Build(Genshin, mmap);

            AssertDeclined(matcher, "Might I ask wha");
            Assert.AreEqual("Czy mogę spytać, co was tutaj sprowadza?",
                matcher.FindClosestMatch("Might I ask what brings you two", out string key, out bool partial));
            Assert.AreEqual(MightIAskBrings, key);
            Assert.IsTrue(partial, "A prefix match previews the line but must not confirm it.");
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ExactLine_StaysAuthoritative_WhenItIsAlsoAPrefixOfLongerLines(bool mmap)
        {
            using var matcher = Build(Genshin, mmap);

            Assert.AreEqual("Tak, ty.", matcher.FindClosestMatch("Yes, you.", out string key, out bool partial));
            Assert.AreEqual("Yes, you.", key);
            Assert.IsFalse(partial);
        }

        // genshin-childe-marionette painted "Zgadzam się." for two seconds of this line.
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void PrefixCompletion_OutranksAShorterKeyTheInputStartsWith(bool mmap)
        {
            using var matcher = Build(Genshin, mmap);

            AssertDeclined(matcher, "I agree. It");
            Assert.AreEqual(Genshin[IAgreeDuels],
                matcher.FindClosestMatch("I agree. It might also help yo", out string key, out bool partial));
            Assert.AreEqual(IAgreeDuels, key);
            Assert.IsTrue(partial);
        }

        // starrail-natasha-clinic: the region crops long lines at both ends (DialogueWPct 0.70).
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void StarRailClippedLines_StillMatch(bool mmap)
        {
            using var matcher = Build(StarRail, mmap);

            Assert.AreEqual(StarRail[OfCourseInjured], matcher.FindClosestMatch(
                "Of course, even if you get injured by accident, no need to panic. Just leav", out string key, out _),
                "End-clipped line: a unique prefix.");
            Assert.AreEqual(OfCourseInjured, key);

            Assert.AreEqual(StarRail[DisposingReagents], matcher.FindClosestMatch(
                "disposing of Vache's reagents isn't enough to solve the medical supply shor\nV", out key, out _),
                "Clipped at both ends, plus a stray glyph line.");
            Assert.AreEqual(DisposingReagents, key);

            Assert.AreEqual(StarRail[OfCourseInjured], matcher.FindClosestMatch(
                "Of course, even if you get injured by accident, no need to panic. Just leave it to the doctor.\nV",
                out key, out bool partial), "Complete line plus a stray glyph line.");
            Assert.IsFalse(partial);

            AssertDeclined(matcher, "Of course, eve");
        }

        // The hot cache runs before the matcher, so a dialogue-graph prediction still resolves a
        // prefix that is ambiguous across the whole corpus.
        [TestMethod]
        public void ContextPrediction_ResolvesAPrefixTheMatcherDeclines()
        {
            const string ocr = "Ugh, can you believe";
            using var matcher = Build(Genshin, mmap: false);

            var withoutContext = new SubtitleResolver().Resolve(ocr, "", null, matcher, Genshin);
            Assert.IsFalse(withoutContext.HasContent,
                "Three lines start with this text, so the matcher alone has no evidence for one.");

            var engine = LoadEngine(
                ("I wasn't expecting to see you here.", new long[] { 2 }),
                (UghBelieveIt, Array.Empty<long>()),
                ("Ugh, can you believe her? She just ran off!", Array.Empty<long>()));
            engine.SetCurrentDialog(1, Genshin);

            var withContext = new SubtitleResolver().Resolve(ocr, "", engine, matcher, Genshin);
            Assert.AreEqual(Genshin[UghBelieveIt], withContext.Content);
            Assert.AreEqual(ResolutionSource.HotCache, withContext.Source);
            Assert.IsTrue(withContext.IsPartial);
            Assert.IsFalse(withContext.ChainAdvanced);
        }

        private static void AssertDeclined(OptimizedMatcher matcher, string ocr)
        {
            string value = matcher.FindClosestMatch(ocr, out string key, out bool partial);
            Assert.AreEqual("", value, $"'{ocr}' is a prefix of several lines; matched '{key}'.");
            Assert.AreEqual("", key);
            Assert.IsFalse(partial);
        }

        private static OptimizedMatcher Build(Dictionary<string, string> corpus, bool mmap)
        {
            if (!mmap) return new OptimizedMatcher(corpus, "EN");

            var stream = new MemoryStream();
            MatcherBlobWriter.Write(corpus, null,
                new MatcherBlobSchema.MatcherMeta { Game = "genshin", Language = "pl" }, stream);
            stream.Position = 0;
            return OptimizedMatcher.LoadFromReaderMmap(MatcherBlobReader.LoadFromStream(stream), "EN");
        }

        /// <summary>Node ids are 1-based in argument order; each node's text hash equals its id.</summary>
        private NormalizedDialogueContext LoadEngine(params (string Text, long[] Next)[] nodes)
        {
            string dir = Path.Combine(Path.GetTempPath(), "MatcherTruncatedPrefixTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _tempDirs.Add(dir);

            var graph = new Dictionary<string, object>();
            var textMap = new Dictionary<string, string>();
            for (int i = 0; i < nodes.Length; i++)
            {
                long id = i + 1;
                graph[id.ToString()] = new { h = id, nh = 0, n = nodes[i].Next, rt = "NPC", ri = "npc-a" };
                textMap[id.ToString()] = nodes[i].Text;
            }
            File.WriteAllText(Path.Combine(dir, "DialogGraph.json"), JsonSerializer.Serialize(graph));
            File.WriteAllText(Path.Combine(dir, "TextMapEN.json"), JsonSerializer.Serialize(textMap));

            var engine = new NormalizedDialogueContext();
            engine.Load(dir, Path.Combine(dir, "TextMapEN.json"));
            return engine;
        }
    }
}
