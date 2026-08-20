// ─────────────────────────────────────────────────────────────────────────────
//  DialogueHotCachePrefixTests.cs
//  ---------------------------------------------------------------------------
//  Tier-1 (chain cache) prefix matching in DialogueContextBase.TryHotCacheMatch.
//
//  A mid-typewriter OCR line is a prefix of every branch the dialogue graph can
//  take next. When two or more cached branches share that prefix there is no
//  evidence yet for picking either, so the tier must decline and let the full
//  matcher decide — the same rule Tier 2 (_npcCache) already enforces.
//
//  Synthetic graph, four nodes:
//      1 → root (the line currently on screen)
//      2 / 3 → branches sharing the leading "The wind is picking up"
//      4 → unrelated branch, shares nothing
//  Edge order on node 1 is the knob: it decides _chainCache insertion order,
//  which is what a first-hit-wins loop is really keyed on.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class DialogueHotCachePrefixTests
    {
        private const string RootLine = "Lets head down to the harbor";
        private const string BranchA = "The wind is picking up and the ships are leaving early";
        private const string BranchB = "The wind is picking up but I would rather stay here";
        private const string Unrelated = "Paimon fell asleep at the inn again";

        /// <summary>Normalizes to 16 chars — a strict prefix of both BranchA and BranchB,
        /// and long enough to clear <c>ChainPrefixThresholdMulti</c> (12).</summary>
        private const string SharedPrefixOcr = "The wind is picking";

        /// <summary>Normalizes to 29 chars — long enough to have left BranchB behind.</summary>
        private const string DivergedOcr = "The wind is picking up and the ships";

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

        /// <summary>An unambiguous prefix must still resolve, flagged as partial.</summary>
        [TestMethod]
        public void ChainPrefix_UnambiguousCandidate_StillMatches()
        {
            var engine = LoadEngine(rootNextIds: new long[] { 2, 4 });

            string result = engine.TryHotCacheMatch(
                Normalize(SharedPrefixOcr), out string matchedKey, out bool isPartial);

            Assert.AreEqual("PL_A", result,
                "Exactly one cached branch starts with this prefix — it must still match");
            Assert.AreEqual(BranchA, matchedKey);
            Assert.IsTrue(isPartial, "A prefix hit is a partial match — chain state must not advance");
            Assert.AreEqual(1, engine.HotCacheHits);
        }

        /// <summary>Two cached branches share the prefix: no evidence, so no answer.</summary>
        [TestMethod]
        public void ChainPrefix_AmbiguousCandidates_AreRejected()
        {
            var engine = LoadEngine(rootNextIds: new long[] { 2, 3 });

            string result = engine.TryHotCacheMatch(
                Normalize(SharedPrefixOcr), out string matchedKey, out bool isPartial);

            Assert.IsNull(result,
                "Two branches share this prefix — picking either one is a guess, not a match");
            Assert.AreEqual("", matchedKey);
            Assert.IsFalse(isPartial);
            Assert.AreEqual(0, engine.HotCacheHits);
            Assert.AreEqual(1, engine.HotCacheMisses,
                "Rejection must fall through to the caller's full matcher, counted as a miss");
        }

        /// <summary>
        /// The rejection is a property of the data, not of which branch happened to be
        /// cached first. Both edge orders must behave identically.
        /// </summary>
        [TestMethod]
        public void ChainPrefix_AmbiguousCandidates_RejectedInEitherInsertionOrder()
        {
            var forward = LoadEngine(rootNextIds: new long[] { 2, 3 });
            var reversed = LoadEngine(rootNextIds: new long[] { 3, 2 });

            string forwardResult = forward.TryHotCacheMatch(
                Normalize(SharedPrefixOcr), out string forwardKey, out _);
            string reversedResult = reversed.TryHotCacheMatch(
                Normalize(SharedPrefixOcr), out string reversedKey, out _);

            Assert.IsNull(forwardResult);
            Assert.IsNull(reversedResult);
            Assert.AreEqual(forwardKey, reversedKey);
        }

        /// <summary>The accepting path is order-independent too.</summary>
        [TestMethod]
        public void ChainPrefix_UnambiguousCandidate_MatchesInEitherInsertionOrder()
        {
            var forward = LoadEngine(rootNextIds: new long[] { 2, 4 });
            var reversed = LoadEngine(rootNextIds: new long[] { 4, 2 });

            string forwardResult = forward.TryHotCacheMatch(
                Normalize(SharedPrefixOcr), out _, out _);
            string reversedResult = reversed.TryHotCacheMatch(
                Normalize(SharedPrefixOcr), out _, out _);

            Assert.AreEqual("PL_A", forwardResult);
            Assert.AreEqual("PL_A", reversedResult);
        }

        /// <summary>
        /// Rejection defers the match, it does not lose it: once the typewriter has
        /// typed past the shared opening, the same ambiguous graph resolves.
        /// </summary>
        [TestMethod]
        public void ChainPrefix_AmbiguityResolves_OnceInputDiverges()
        {
            var engine = LoadEngine(rootNextIds: new long[] { 2, 3 });

            Assert.IsNull(engine.TryHotCacheMatch(Normalize(SharedPrefixOcr), out _, out _));

            string result = engine.TryHotCacheMatch(
                Normalize(DivergedOcr), out string matchedKey, out bool isPartial);

            Assert.AreEqual("PL_A", result);
            Assert.AreEqual(BranchA, matchedKey);
            Assert.IsTrue(isPartial);
        }

        private static string Normalize(string ocr) => OptimizedMatcher.NormalizeInput(ocr, true);

        private NormalizedDialogueContext LoadEngine(long[] rootNextIds)
        {
            string dir = WriteSyntheticBundle(rootNextIds);
            var engine = new NormalizedDialogueContext();
            engine.Load(dir, Path.Combine(dir, "TextMapEN.json"));
            engine.SetCurrentDialog(1, new Dictionary<string, string>
            {
                { RootLine, "PL_ROOT" },
                { BranchA, "PL_A" },
                { BranchB, "PL_B" },
                { Unrelated, "PL_UNRELATED" },
            });
            return engine;
        }

        private string WriteSyntheticBundle(long[] rootNextIds)
        {
            string dir = Path.Combine(Path.GetTempPath(),
                "DialogueHotCachePrefixTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _tempDirs.Add(dir);

            var graph = new StringBuilder();
            graph.Append('{');
            graph.Append($"\"1\":{{\"h\":1,\"nh\":0,\"n\":[{string.Join(",", rootNextIds)}],\"rt\":\"NPC\",\"ri\":\"npc-a\"}},");
            graph.Append("\"2\":{\"h\":2,\"nh\":0,\"n\":[],\"rt\":\"NPC\",\"ri\":\"npc-a\"},");
            graph.Append("\"3\":{\"h\":3,\"nh\":0,\"n\":[],\"rt\":\"NPC\",\"ri\":\"npc-a\"},");
            graph.Append("\"4\":{\"h\":4,\"nh\":0,\"n\":[],\"rt\":\"NPC\",\"ri\":\"npc-a\"}");
            graph.Append('}');
            File.WriteAllText(Path.Combine(dir, "DialogGraph.json"), graph.ToString());

            File.WriteAllText(Path.Combine(dir, "TextMapEN.json"),
                $"{{\"1\":\"{RootLine}\",\"2\":\"{BranchA}\",\"3\":\"{BranchB}\",\"4\":\"{Unrelated}\"}}");

            return dir;
        }
    }
}
