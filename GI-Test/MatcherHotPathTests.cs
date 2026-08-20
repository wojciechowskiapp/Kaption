// ─────────────────────────────────────────────────────────────────────────────
//  MatcherHotPathTests.cs
//  ---------------------------------------------------------------------------
//  Regression cover for the OCR-tick matching path in OptimizedMatcher +
//  SymSpellIndex: header separation, the Stage 3 acceptance threshold, the
//  n-gram fallback budget, per-thread candidate scratch, and Stage 0 distance
//  reporting.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class MatcherHotPathTests
    {
        private static Dictionary<string, string> Corpus(int n, string prefix, int seed = 7)
        {
            var rand = new Random(seed);
            var dict = new Dictionary<string, string>(n, StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                // Alphabet capped at a..m so tests can build a query out of x/y/z/w
                // that provably shares no n-gram with the corpus.
                var tail = new char[6];
                for (int j = 0; j < tail.Length; j++) tail[j] = (char)('a' + rand.Next(13));
                string key = prefix + " " + new string(tail) + " " + i.ToString("D5");
                dict[key] = "PL:" + i;
            }
            return dict;
        }

        // ── Claim 1: the header half of FindMatchWithHeaderSeparated ────────────

        [TestMethod]
        public void HeaderSeparated_LeavesHeaderEmpty_AndStillTranslatesBody()
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Paimon"] = "Paimon",
                ["Let's head back to Mondstadt before it gets dark."] =
                    "Wracajmy do Mondstadt, zanim się ściemni.",
            };

            using (var matcher = new OptimizedMatcher(dict, "EN"))
            {
                var result = matcher.FindMatchWithHeaderSeparated(
                    "Paimon\nLet's head back to Mondstadt before it gets dark.", out string key);

                Assert.AreEqual("", result.Header ?? "",
                    "Header must stay empty — every caller discards it, and producing one " +
                    "forces a full-corpus exact-lookup index to be built mid-session.");
                Assert.AreEqual("Wracajmy do Mondstadt, zanim się ściemni.", result.Content);
                Assert.AreEqual("Let's head back to Mondstadt before it gets dark.", key);
            }
        }

        [TestMethod]
        public void HeaderSeparated_SingleLine_MatchesWholeLine()
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["The wind is picking up again."] = "Wiatr znowu się wzmaga.",
            };

            using (var matcher = new OptimizedMatcher(dict, "EN"))
            {
                var result = matcher.FindMatchWithHeaderSeparated(
                    "The wind is picking up again.", out string key);

                Assert.AreEqual("", result.Header ?? "");
                Assert.AreEqual("Wiatr znowu się wzmaga.", result.Content);
                Assert.AreEqual("The wind is picking up again.", key);
            }
        }

        // ── Claim 2: the weighted acceptance gate was a subset of the shipped one ─

        [TestMethod]
        public void WeightedAcceptanceGate_WasSubsumedByTheShippedThreshold()
        {
            // globalBestDistance is Ceiling(globalBestWeightedDist) for the candidate
            // that sets the weighted minimum, so the removed gate could only fire on
            // inputs the surviving gate accepts anyway.
            for (int len = 0; len <= 4096; len++)
            {
                double removedGate = Math.Max(4, len * 0.35);
                double shippedGate = Math.Max(5, len * 0.4);

                Assert.IsTrue(Math.Ceiling(removedGate) <= shippedGate,
                    $"Length {len}: Ceiling({removedGate}) = {Math.Ceiling(removedGate)} " +
                    $"exceeds the shipped threshold {shippedGate}. The removed 0.35 gate " +
                    "would no longer be redundant and dropping it would have changed matching.");
            }
        }

        // ── Claim 3: the !foundRareGram fallback ────────────────────────────────

        [TestMethod]
        public void Fallback_AllGramsOverCap_UnionUnderBudget_StillMatches()
        {
            // One n-gram, 2100 keys behind it: over the 2000 per-gram cap, so the
            // primary pass contributes nothing and the fallback runs. The union fits
            // the budget, so every candidate is admitted exactly as before.
            var dict = new Dictionary<string, string>(2100, StringComparer.Ordinal);
            for (int i = 0; i < 2100; i++) dict["abcd" + i.ToString("D5")] = "PL:" + i;

            using (var matcher = new OptimizedMatcher(dict, "EN"))
            {
                string value = matcher.FindClosestMatch("abcd", out string key);

                Assert.IsFalse(string.IsNullOrEmpty(value),
                    "The fallback must still produce a candidate when every gram is over cap.");
                StringAssert.StartsWith(key, "abcd");
            }
        }

        [TestMethod]
        public void Fallback_AllGramsOverCap_UnionOverBudget_StillMatches()
        {
            // 32 distinct grams x 2100 keys is far over the fallback budget, so the
            // rarest-first selection kicks in. The correct entry carries every gram in
            // the query, so it survives whichever buckets the budget admits.
            const string phrase = "the quick brown fox jumps over the lazy dog";
            var dict = new Dictionary<string, string>(2100, StringComparer.Ordinal);
            for (int i = 0; i < 2100; i++) dict[phrase + " " + i.ToString("D5")] = "PL:" + i;

            using (var matcher = new OptimizedMatcher(dict, "EN"))
            {
                string value = matcher.FindClosestMatch(phrase, out string key);

                Assert.IsFalse(string.IsNullOrEmpty(value),
                    "Budget-capped fallback dropped the whole candidate set.");
                StringAssert.StartsWith(key, phrase);
            }
        }

        [TestMethod]
        public void Fallback_DoesNotStarveWhenASingleBucketExceedsTheBudget()
        {
            // A lone bucket wider than the entire budget must still be admitted;
            // returning nothing would be worse than doing the work.
            var dict = new Dictionary<string, string>(9000, StringComparer.Ordinal);
            for (int i = 0; i < 9000; i++) dict["wxyz" + i.ToString("D5")] = "PL:" + i;

            using (var matcher = new OptimizedMatcher(dict, "EN"))
            {
                string value = matcher.FindClosestMatch("wxyz", out string key);

                Assert.IsFalse(string.IsNullOrEmpty(value));
                StringAssert.StartsWith(key, "wxyz");
            }
        }

        // ── Claim 4: the reused per-thread candidate scratch ────────────────────

        [TestMethod]
        public void CandidateScratch_IsClearedBetweenCalls()
        {
            var dict = Corpus(400, "Blind held man ran along");

            using (var matcher = new OptimizedMatcher(dict, "EN"))
            {
                string firstKey = null;
                foreach (var k in dict.Keys) { firstKey = k; break; }

                string hit = matcher.FindClosestMatch(firstKey, out _);
                Assert.IsFalse(string.IsNullOrEmpty(hit), "Exact key should match.");

                // Every gram here contains x and z, neither of which occurs anywhere
                // in the corpus, so the candidate set must come back empty. It only
                // can if the previous call's candidates were cleared.
                string miss = matcher.FindClosestMatch("xyzwxyzwxyzwxyzwxyzwxyzw", out string missKey);

                Assert.AreEqual("", miss ?? "", "Stale candidates leaked into the next call.");
                Assert.AreEqual("", missKey ?? "");
            }
        }

        [TestMethod]
        public void CandidateScratch_ConcurrentCallsAgreeWithSingleThreadedResults()
        {
            var dict = Corpus(600, "A traveler walks the road to Sumeru", seed: 41);
            var keys = new List<string>(dict.Keys);

            using (var matcher = new OptimizedMatcher(dict, "EN"))
            {
                var expected = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var k in keys) expected[k] = matcher.FindClosestMatch(k, out _);

                var failures = new ConcurrentBag<string>();
                Parallel.For(0, 16, t =>
                {
                    for (int i = 0; i < 150; i++)
                    {
                        string k = keys[(i * 7 + t * 13) % keys.Count];
                        string got = matcher.FindClosestMatch(k, out _);
                        if (!string.Equals(got, expected[k], StringComparison.Ordinal))
                        {
                            failures.Add($"thread {t}: '{k}' → '{got}' (expected '{expected[k]}')");
                        }
                    }
                });

                Assert.AreEqual(0, failures.Count,
                    "Per-thread candidate scratch raced: " + string.Join(" | ", failures));
            }
        }

        [TestMethod]
        public void CandidateScratch_SurvivesAnOversizedCallOnTheSameThread()
        {
            // A pathological input grows the scratch past its retention limit; the
            // next call on the same thread must still be correct.
            var dict = new Dictionary<string, string>(20000, StringComparer.Ordinal);
            for (int i = 0; i < 20000; i++) dict["mnop" + i.ToString("D6")] = "PL:" + i;
            dict["A rare and specific line of dialogue."] = "Rzadka i konkretna kwestia.";

            using (var matcher = new OptimizedMatcher(dict, "EN"))
            {
                matcher.FindClosestMatch("mnop", out _);

                string value = matcher.FindClosestMatch("A rare and specific line of dialogue.", out string key);
                Assert.AreEqual("Rzadka i konkretna kwestia.", value);
                Assert.AreEqual("A rare and specific line of dialogue.", key);
            }
        }

        // ── Claim 5: Stage 0 distance reporting ────────────────────────────────

        private static SymSpellIndex TinyIndex()
        {
            var norm = new[] { "abcdefh", "zzzzzzz", "qqqqqqq" };
            var orig = new[] { "abcdefh", "zzzzzzz", "qqqqqqq" };
            var vals = new[] { "PL-abcdefh", "PL-z", "PL-q" };
            return new SymSpellIndex(norm, orig, vals);
        }

        [TestMethod]
        public void SymSpell_LongerInput_ChargesTheTrailingCharacters()
        {
            var index = TinyIndex();

            // "abcdefh" → "abcdefgx" is one substitution plus one insertion. Truncating
            // the input to the candidate's length hid the insertion and reported 1.
            Assert.IsTrue(index.TryFindMatch("abcdefgx", out int idx, out int dist));
            Assert.AreEqual(0, idx);
            Assert.AreEqual(2, dist,
                "Stage 0 must charge the input's trailing character, not drop it.");
        }

        [TestMethod]
        public void SymSpell_RejectsWhenTheUntruncatedDistanceExceedsTheLimit()
        {
            var index = TinyIndex();

            // True distance from "abcdefh" is 3 (sub h→g, insert x, insert y). The old
            // truncation reported 1, which FindClosestMatch accepted as a near-exact
            // hit and returned immediately, skipping Stages 1-2 entirely.
            Assert.IsFalse(index.TryFindMatch("abcdefgxy", out _, out _),
                "A distance-3 candidate must not be reported as a distance-1 match.");
        }

        [TestMethod]
        public void SymSpell_ShorterInput_KeepsPrefixSemantics()
        {
            var index = TinyIndex();

            // Typewriter capture: a partial line is a legitimate prefix of the full
            // key and the unseen tail must stay uncharged.
            Assert.IsTrue(index.TryFindMatch("abcde", out int idx, out int dist));
            Assert.AreEqual(0, idx);
            Assert.AreEqual(0, dist);
        }

        [TestMethod]
        public void SymSpell_ExactInput_IsDistanceZero()
        {
            var index = TinyIndex();

            Assert.IsTrue(index.TryFindMatch("abcdefh", out int idx, out int dist));
            Assert.AreEqual(0, idx);
            Assert.AreEqual(0, dist);
        }
    }
}
