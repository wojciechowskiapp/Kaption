using System;
using System.Collections.Generic;
using GI_Subtitles.Services.Security;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class SubtitleResolverTests
    {
        private const string Line = "The wind is picking up again.";
        private const string OtherLine = "We should return to the harbor.";

        [DataTestMethod]
        [DataRow("The wind is picking", "Paimon")]
        [DataRow("Paimon\nThe wind is picking", "")]
        public void MatcherPrefix_IsDisplayedWithoutAdvancingTheChain(string input, string npc)
        {
            var corpus = new Dictionary<string, string> { [Line] = "one" };
            using var matcher = new OptimizedMatcher(corpus, "EN");
            var context = new TestContext();
            var resolver = new SubtitleResolver();

            var preview = resolver.Resolve(input, npc, context, matcher, corpus);
            Assert.AreEqual("one", preview.Content);
            Assert.IsTrue(preview.IsPartial);
            Assert.IsFalse(preview.ChainAdvanced);
            Assert.AreEqual(0, context.Confirmed.Count);

            var full = resolver.Resolve(Line, npc, context, matcher, corpus);
            Assert.IsFalse(full.IsPartial);
            Assert.IsTrue(full.ChainAdvanced);
            Assert.AreEqual(1, context.Confirmed.Count);
        }

        [TestMethod]
        public void PartialPrediction_IsReevaluatedWhenContextChanges()
        {
            var corpus = new Dictionary<string, string>();
            using var matcher = new OptimizedMatcher(corpus, "EN");
            var context = new TestContext { Partial = true, Translation = "first prediction" };
            var resolver = new SubtitleResolver();

            Assert.IsTrue(resolver.Resolve(Line, "Paimon", context, matcher, corpus).IsPartial);
            context.Translation = "updated prediction";
            var second = resolver.Resolve(Line, "Paimon", context, matcher, corpus);

            Assert.AreEqual("updated prediction", second.Content);
            Assert.IsTrue(second.IsPartial);
            Assert.AreEqual(0, context.Confirmed.Count);
        }

        [TestMethod]
        public void Cache_IsScopedToSpeaker()
        {
            var corpus = new Dictionary<string, string>();
            using var matcher = new OptimizedMatcher(corpus, "EN");
            var context = new TestContext { Translation = "speaker one" };
            var resolver = new SubtitleResolver();
            resolver.Resolve(Line, "Paimon", context, matcher, corpus);
            context.Translation = "speaker two";

            var second = resolver.Resolve(Line, "Amber", context, matcher, corpus);

            Assert.AreEqual("speaker two", second.Content);
            Assert.AreEqual("Amber", context.LastPreloadedNpc);
            Assert.IsTrue(second.ChainAdvanced);
        }

        [TestMethod]
        public void Cache_IsInvalidatedWhenMatcherChanges()
        {
            var firstCorpus = new Dictionary<string, string> { [Line] = "old translation" };
            var secondCorpus = new Dictionary<string, string> { [Line] = "updated translation" };
            using var first = new OptimizedMatcher(firstCorpus, "EN");
            using var second = new OptimizedMatcher(secondCorpus, "EN");
            var resolver = new SubtitleResolver();
            resolver.Resolve(Line, "", null, first, firstCorpus);

            Assert.AreEqual("updated translation",
                resolver.Resolve(Line, "", null, second, secondCorpus).Content);
        }

        [TestMethod]
        public void CachedLine_AdvancesContextAgainAfterAnInterveningLine()
        {
            var corpus = new Dictionary<string, string> { [Line] = "one", [OtherLine] = "two" };
            using var matcher = new OptimizedMatcher(corpus, "EN");
            var context = new TestContext();
            var resolver = new SubtitleResolver();

            Assert.IsTrue(resolver.Resolve(Line, "Paimon", context, matcher, corpus).ChainAdvanced);
            Assert.IsFalse(resolver.Resolve(Line, "Paimon", context, matcher, corpus).ChainAdvanced,
                "A UI refresh of the same line must not repeatedly advance the graph.");
            Assert.IsTrue(resolver.Resolve(OtherLine, "Paimon", context, matcher, corpus).ChainAdvanced);
            Assert.IsTrue(resolver.Resolve(Line, "Paimon", context, matcher, corpus).ChainAdvanced);
            CollectionAssert.AreEqual(new[] { Line, OtherLine, Line }, context.Confirmed);
        }

        [TestMethod]
        public void Miss_IsRetriedWhenContextBecomesAvailable()
        {
            var corpus = new Dictionary<string, string>();
            using var matcher = new OptimizedMatcher(corpus, "EN");
            var context = new TestContext();
            var resolver = new SubtitleResolver();
            Assert.IsFalse(resolver.Resolve(Line, "Paimon", context, matcher, corpus).HasContent);
            var repeatedMiss = resolver.Resolve(Line, "Paimon", context, matcher, corpus);
            Assert.IsFalse(repeatedMiss.HasContent);
            Assert.AreEqual(ResolutionSource.MemoCache, repeatedMiss.Source,
                "Retry the changing hot cache, but do not rerun the same global miss every UI tick.");
            context.Translation = "now available";

            Assert.AreEqual("now available", resolver.Resolve(Line, "Paimon", context, matcher, corpus).Content);
        }

        [TestMethod]
        public void ClearCache_AllowsSameLineToStartANewConversation()
        {
            var corpus = new Dictionary<string, string> { [Line] = "one" };
            using var matcher = new OptimizedMatcher(corpus, "EN");
            var context = new TestContext();
            var resolver = new SubtitleResolver();
            resolver.Resolve(Line, "Paimon", context, matcher, corpus);
            resolver.ClearCache();

            Assert.IsTrue(resolver.Resolve(Line, "Paimon", context, matcher, corpus).ChainAdvanced);
            Assert.AreEqual(2, context.Confirmed.Count);
        }

        private sealed class TestContext : IGameDialogueContext
        {
            public bool Partial;
            public string Translation;
            public string LastPreloadedNpc;
            public readonly List<string> Confirmed = new List<string>();
            public bool IsLoaded => true;
            public int HotCacheHits => 0;
            public int HotCacheMisses => 0;
            public bool HasSingleChainPrediction => false;
            public string TryHotCacheMatch(string input, out string key, out bool partial)
            {
                key = Translation == null ? "" : Line;
                partial = Translation != null && Partial;
                return Translation;
            }
            public void PreloadForNpc(string npc, Dictionary<string, string> dict) => LastPreloadedNpc = npc;
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
