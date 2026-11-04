using System;
using System.Collections.Generic;
using System.Drawing;
using GI_Subtitles.Models;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.OCR;
using GI_Subtitles.Services.Security;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Session 57: what the Genshin overlay painted with no dialogue on screen. OCR texts are verbatim reads from the
    /// benchmark scenes genshin-witch-lodge-map, genshin-moonlit-* and the 7.1 patch-test windows.
    /// </summary>
    [TestClass]
    public class GenshinStrayPaintTests
    {
        private const string VeryWell = "...Very well.";
        private const string VeryWellFull = "Very well. In the interests of progress, it is wise to make the most of what information we have available.";
        private const string Anyway = "Anyway, if you can hear my voice, I assume that means you're currently in Nod-Krai.";

        // ── Matcher: a shorter key the read starts with ──

        [TestMethod]
        public void ShorterKey_MidLine_IsNotTheWholeRead()
        {
            var corpus = new Dictionary<string, string> { [VeryWell] = "...Bardzo dobrze." };
            using var matcher = new OptimizedMatcher(corpus, "EN");

            string value = matcher.FindClosestMatch("Very well. In the interests\nS", out string key);

            Assert.AreEqual("", value, $"painted \"{key}\" for a line that only starts with it");
        }

        [TestMethod]
        public void ShorterKey_EndingAtALineBreak_StillMatches()
        {
            // The dialogue line above a choice list: the options are extra OCR lines.
            var corpus = new Dictionary<string, string> { [Anyway] = "W każdym razie..." };
            using var matcher = new OptimizedMatcher(corpus, "EN");

            string value = matcher.FindClosestMatch(
                "Anyway, if you can hear my voice, I assume that means you're currently in\nNod-Krai.\nWhat things, exactly?\nIs it something to do with st",
                out string key);

            Assert.AreEqual(Anyway, key);
            Assert.AreEqual("W każdym razie...", value);
        }

        [TestMethod]
        public void ShorterKey_WithTheHeaderSplit_KeepsTheLineBreaks()
        {
            var corpus = new Dictionary<string, string> { ["The Jester"] = "Błazen" };
            using var matcher = new OptimizedMatcher(corpus, "EN");

            Assert.AreEqual("", matcher.FindMatchWithHeaderSeparated("The Jester Unless you", out _).Content,
                "one OCR line: the key ends mid-line");
            Assert.AreEqual("Błazen", matcher.FindMatchWithHeaderSeparated("The Jester\nUnless you", out _).Content,
                "the name glued above a body is a line of its own; the dialogue evidence decides on it");
        }

        [TestMethod]
        [DataRow("abc\ndef", 3, true)]
        [DataRow("abc def", 3, false)]
        [DataRow("a.b,c \n d", 3, true)]
        [DataRow("abc", 3, true)]
        [DataRow("ab\ncd", 1, false)]
        public void EndsAtLineBreak_CountsNormalizedCharacters(string input, int length, bool expected)
        {
            Assert.AreEqual(expected, OptimizedMatcher.EndsAtLineBreak(input, length, isEng: true));
        }

        // ── Resolver: the dialogue data has to vouch for a match ──

        [TestMethod]
        [DataRow("Teleport Waypoint")]
        [DataRow("Wing of Keres")]
        [DataRow("Unless otherwise stated, effects that affect Sprints will not affect abilities that are named \"Alternate Sprint.\"")]
        public void UiText_IsNotPainted(string uiKey)
        {
            var corpus = new Dictionary<string, string> { [uiKey] = "PL", [VeryWellFull] = "Bardzo dobrze." };
            using var matcher = new OptimizedMatcher(corpus, "EN");
            var context = new GraphContext(VeryWellFull);
            var resolver = new SubtitleResolver { RequireDialogueEvidence = true };

            var shown = resolver.Resolve(uiKey, "", context, matcher, corpus);

            Assert.IsFalse(shown.HasContent);
            Assert.AreEqual(ResolutionSource.NotDialogue, shown.Source);
            Assert.AreEqual(0, context.Confirmed.Count, "a rejected read must not move the conversation");
        }

        [TestMethod]
        public void GraphLine_IsPainted()
        {
            var corpus = new Dictionary<string, string> { [VeryWellFull] = "Bardzo dobrze." };
            using var matcher = new OptimizedMatcher(corpus, "EN");
            var resolver = new SubtitleResolver { RequireDialogueEvidence = true };

            var shown = resolver.Resolve(VeryWellFull, "", new GraphContext(VeryWellFull), matcher, corpus);

            Assert.AreEqual("Bardzo dobrze.", shown.Content);
        }

        [TestMethod]
        public void OffGraphLine_FromAKnownSpeaker_IsPainted()
        {
            const string line = "What? How did you know we were coming?";
            var corpus = new Dictionary<string, string> { [line] = "Co? Skąd wiedziałaś, że idziemy?" };
            using var matcher = new OptimizedMatcher(corpus, "EN");
            var context = new GraphContext("Something else entirely.") { Speakers = { "Yinger" } };
            var resolver = new SubtitleResolver { RequireDialogueEvidence = true };

            Assert.IsTrue(resolver.Resolve(line, "Yinger", context, matcher, corpus).HasContent);
        }

        [TestMethod]
        [DataRow("Max HP")]
        [DataRow("Talk to Illuga")]
        [DataRow("Wing of Keres")]
        public void UiLabel_UnderAKnownSpeakerName_IsNotPainted(string label)
        {
            // A gold name on a menu screen classifies as the speaker; the label under it is still no line.
            var corpus = new Dictionary<string, string> { [label] = "PL" };
            using var matcher = new OptimizedMatcher(corpus, "EN");
            var context = new GraphContext("Something else entirely.") { Speakers = { "Furina" } };
            var resolver = new SubtitleResolver { RequireDialogueEvidence = true };

            var shown = resolver.Resolve(label, "Furina", context, matcher, corpus);

            Assert.IsFalse(shown.HasContent);
            Assert.AreEqual(ResolutionSource.NotDialogue, shown.Source);
        }

        [TestMethod]
        public void SpeakerName_ReadAsTheBody_IsNotPainted()
        {
            var corpus = new Dictionary<string, string> { ["Nicole"] = "Nicole" };
            using var matcher = new OptimizedMatcher(corpus, "EN");
            var context = new GraphContext("Something else entirely.") { Speakers = { "Nicole" } };
            var resolver = new SubtitleResolver { RequireDialogueEvidence = true };

            Assert.IsFalse(resolver.Resolve("Nicole", "Nicole", context, matcher, corpus).HasContent);
        }

        [TestMethod]
        public void WithoutAGraph_OrWithTheKnobOff_NothingIsFiltered()
        {
            var corpus = new Dictionary<string, string> { ["Teleport Waypoint"] = "Punkt teleportacji" };
            using var matcher = new OptimizedMatcher(corpus, "EN");

            var noGraph = new GraphContext(null);
            Assert.IsTrue(new SubtitleResolver { RequireDialogueEvidence = true }
                .Resolve("Teleport Waypoint", "", noGraph, matcher, corpus).HasContent, "an unloaded graph vouches for nothing, so it blocks nothing");
            Assert.IsTrue(new SubtitleResolver()
                .Resolve("Teleport Waypoint", "", new GraphContext(VeryWellFull), matcher, corpus).HasContent);
        }

        [TestMethod]
        [DataRow("Hey, look over there.", true)]
        [DataRow("Yes, sir!", true)]
        [DataRow("Hmm...", true)]
        [DataRow("Wait—", true)]
        [DataRow("*sigh*", true)]
        [DataRow("I think we should go now", true)]
        [DataRow("Wing of Keres", false)]
        [DataRow("Talk to Illuga", false)]
        [DataRow("Max HP", false)]
        [DataRow("Nicole", false)]
        [DataRow("Dreadshade", false)]
        [DataRow("Xiangling's", false)]
        [DataRow("", false)]
        public void ReadsAsSpeech(string text, bool expected)
        {
            Assert.AreEqual(expected, SubtitleResolver.ReadsAsSpeech(text));
        }

        // ── A line read while its box fades ──

        [TestMethod]
        public void FadedRead_IsRecognisedByItsLitPixels()
        {
            // "*sob* Thank you..." at 32.5 s of genshin-moonlit-dialogue-end: 0 lit pixels over a 9,000 px body box.
            Assert.IsTrue(OcrReadEvidence.IsFaded(0, 9000));
            Assert.IsTrue(OcrReadEvidence.IsFaded(81, 54000), "Still, this isn't exactly the place... (fading, 0.0015)");
            Assert.IsFalse(OcrReadEvidence.IsFaded(277, 5300), "I hav (typing, 0.052)");
            Assert.IsFalse(OcrReadEvidence.IsFaded(10709, 66000), "a settled two-line read (0.16)");
            Assert.IsFalse(OcrReadEvidence.IsFaded(0, 0), "no positioned body: nothing to judge");
        }

        [TestMethod]
        public void BodyArea_SumsTheBoxes()
        {
            var blocks = new List<TextBlockInfo>
            {
                new TextBlockInfo { BoundingRect = new RectangleF(0, 0, 100, 20) },
                new TextBlockInfo { BoundingRect = new RectangleF(0, 30, 50, 20) },
            };
            Assert.AreEqual(3000, OcrReadEvidence.BodyArea(blocks), 0.001);
            Assert.AreEqual(0, OcrReadEvidence.BodyArea(null));
        }

        [TestMethod]
        public void OnlyGenshin_AsksForDialogueEvidence_AndRejectsFadedReads()
        {
            Assert.IsTrue(GameRegionProfile.Get("Genshin").RequiresDialogueEvidence);
            Assert.IsTrue(GameRegionProfile.Get("Genshin").RejectsFadedReads);
            foreach (string game in new[] { "StarRail", "ZZZ", "WuWa" })
            {
                Assert.IsFalse(GameRegionProfile.Get(game).RequiresDialogueEvidence, game);
                Assert.IsFalse(GameRegionProfile.Get(game).RejectsFadedReads, game);
            }
        }

        private sealed class GraphContext : IGameDialogueContext
        {
            private readonly HashSet<string> _lines;
            public readonly HashSet<string> Speakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Confirmed = new List<string>();

            /// <param name="graphLine">One line the graph holds; null = graph not loaded.</param>
            public GraphContext(string graphLine) =>
                _lines = graphLine == null ? null : new HashSet<string> { graphLine };

            public bool IsLoaded => true;
            public bool CanVouchForLines => _lines != null;
            public bool IsDialogueLine(string text) => _lines?.Contains(text) == true;
            public bool IsKnownSpeaker(string npc) => !string.IsNullOrEmpty(npc) && Speakers.Contains(npc);
            public DialoguePrepareFailure LastPrepareFailure => DialoguePrepareFailure.None;
            public int HotCacheHits => 0;
            public int HotCacheMisses => 0;
            public bool HasSingleChainPrediction => false;
            public string TryHotCacheMatch(string input, out string key, out bool partial)
            {
                key = "";
                partial = false;
                return null;
            }
            public void PreloadForNpc(string npc, Dictionary<string, string> dict) { }
            public void OnTextMatched(string key, string npc, Dictionary<string, string> dict) => Confirmed.Add(key);
            public void Load(string dir, string map, IProgress<(int percent, string message)> progress = null,
                FileProtectionHelper protectionHelper = null) => throw new NotSupportedException();
            public long FindNodeByText(string text, string npcFilter = null) => 0;
            public void SetCurrentDialog(long id, Dictionary<string, string> dict) { }
            public void Reset() { }
            public PredictionResult? GetSingleChainPrediction() => null;
            public List<PredictionResult> GetPredictedAnswers(Dictionary<string, string> dict) => new List<PredictionResult>();
            public List<PredictionResult> GetNpcCachePredictions() => new List<PredictionResult>();
            public (string title, string type)? GetCurrentQuestInfo() => null;
            public string GetTranslatedQuestTitle(Dictionary<string, string> dict) => null;
            public string GetStats() => "";
        }
    }
}
