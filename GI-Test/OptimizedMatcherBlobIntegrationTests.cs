// ─────────────────────────────────────────────────────────────────────────────
//  OptimizedMatcherBlobIntegrationTests.cs
//  ---------------------------------------------------------------------------
//  End-to-end: build OptimizedMatcher in-memory, Save to KMX blob, reload
//  via LoadFromBlob, verify FindClosestMatch returns identical results.
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
    public class OptimizedMatcherBlobIntegrationTests
    {
        private static Dictionary<string, string> BuildCorpus(int n, int seed = 123)
        {
            var rand = new Random(seed);
            var dict = new Dictionary<string, string>(n, StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                int len = 5 + rand.Next(15);
                var sb = new StringBuilder(len);
                for (int j = 0; j < len; j++)
                {
                    // ASCII letters + space so NormalizeInput has something to work with.
                    int r = rand.Next(27);
                    sb.Append(r == 26 ? ' ' : (char)('a' + r));
                }
                // Make first char uppercase, so NormalizeInput's lowercasing
                // actually does work.
                string key = char.ToUpperInvariant(sb[0]) + sb.ToString(1, sb.Length - 1)
                             + " " + i.ToString("D5");
                string value = "Tłumaczenie polskie dla wpisu numer " + i + ".";
                dict[key] = value;
            }
            return dict;
        }

        [TestMethod]
        public void Save_Reload_FindClosestMatch_ReturnsSameValues()
        {
            var corpus = BuildCorpus(500);
            var original = new OptimizedMatcher(corpus, "EN");

            var ms = new MemoryStream();
            var meta = new MatcherBlobSchema.MatcherMeta
            {
                CorpusVersion = "test-integration",
                Game = "genshin",
                Language = "pl",
            };
            original.Save(ms, meta);
            ms.Position = 0;

            var reloaded = OptimizedMatcher.LoadFromBlob(ms, "EN");

            Assert.AreEqual(original.EntryCount, reloaded.EntryCount);

            // Exact-match lookups should agree verbatim.
            foreach (var key in corpus.Keys)
            {
                string origMatch = original.FindClosestMatch(key, out string origKey);
                string newMatch = reloaded.FindClosestMatch(key, out string newKey);

                Assert.AreEqual(origMatch, newMatch,
                    $"Value divergence for exact key '{key}': " +
                    $"original='{origMatch}' reloaded='{newMatch}'");
                Assert.AreEqual(origKey, newKey,
                    $"Picked-key divergence for '{key}'.");
            }
        }

        [TestMethod]
        public void Save_EmptyCorpus_LoadsCleanly()
        {
            var empty = new Dictionary<string, string>();
            var m = new OptimizedMatcher(empty, "EN");
            var ms = new MemoryStream();
            var meta = new MatcherBlobSchema.MatcherMeta
            {
                CorpusVersion = "empty",
                Game = "genshin",
                Language = "pl",
            };
            m.Save(ms, meta);
            ms.Position = 0;

            var reloaded = OptimizedMatcher.LoadFromBlob(ms, "EN");
            Assert.AreEqual(0, reloaded.EntryCount);
            Assert.IsTrue(reloaded.Loaded);

            string result = reloaded.FindClosestMatch("anything", out string key);
            Assert.AreEqual("", result);
            Assert.AreEqual("", key);
        }

        [TestMethod]
        public void Save_WithTrainedDictionary_StillRoundTrips()
        {
            var corpus = BuildCorpus(200);
            var samples = new List<byte[]>();
            foreach (var v in corpus.Values)
                samples.Add(Encoding.UTF8.GetBytes(v));
            byte[] dict = ZstdDictionaryTrainer.Train(samples);

            var m = new OptimizedMatcher(corpus, "EN");
            var ms = new MemoryStream();
            m.Save(ms, new MatcherBlobSchema.MatcherMeta
            {
                CorpusVersion = "test-with-dict",
                Game = "genshin",
                Language = "pl",
            }, dict);
            ms.Position = 0;

            var reloaded = OptimizedMatcher.LoadFromBlob(ms, "EN");

            foreach (var kvp in corpus)
            {
                string v = reloaded.FindClosestMatch(kvp.Key, out _);
                Assert.AreEqual(kvp.Value, v);
            }
        }
    }
}
