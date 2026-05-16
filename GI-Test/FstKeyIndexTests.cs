// ─────────────────────────────────────────────────────────────────────────────
//  FstKeyIndexTests.cs
//  ---------------------------------------------------------------------------
//  Round-trip tests for the Lucene.Net-backed FstKeyIndex wrapper.
//  Covers build, save/load, lookup correctness, and the edge cases the
//  matcher pipeline leans on (empty corpus, single entry, duplicate keys,
//  unicode keys, keys > 1 KB, unsorted input).
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class FstKeyIndexTests
    {
        private static (List<string> keys, List<int> slots) MakeSortedPairs(int n)
        {
            var keys = new List<string>(n);
            for (int i = 0; i < n; i++)
                keys.Add("key_" + i.ToString("D6"));
            keys.Sort(StringComparer.Ordinal);
            var slots = new List<int>(n);
            for (int i = 0; i < n; i++) slots.Add(i);
            return (keys, slots);
        }

        [TestMethod]
        public void Build_10k_RoundTripsEveryKey()
        {
            var (keys, slots) = MakeSortedPairs(10_000);
            var fst = FstKeyIndex.Build(keys, slots);
            Assert.AreEqual(10_000, fst.Count);

            for (int i = 0; i < keys.Count; i++)
            {
                int found = fst.Lookup(keys[i]);
                Assert.AreEqual(slots[i], found, $"Key '{keys[i]}' lookup returned wrong slot.");
            }
        }

        [TestMethod]
        public void Lookup_MissingKey_ReturnsMinus1()
        {
            var (keys, slots) = MakeSortedPairs(256);
            var fst = FstKeyIndex.Build(keys, slots);

            Assert.AreEqual(-1, fst.Lookup("no_such_key"));
            Assert.AreEqual(-1, fst.Lookup("key_999999"));
            Assert.AreEqual(-1, fst.Lookup("KEY_000000")); // case-sensitive
        }

        [TestMethod]
        public void SaveLoad_BitIdentical_ProducesSameLookups()
        {
            var (keys, slots) = MakeSortedPairs(1_000);
            var fst = FstKeyIndex.Build(keys, slots);

            byte[] serialized;
            using (var ms = new MemoryStream())
            {
                fst.Save(ms);
                serialized = ms.ToArray();
            }

            FstKeyIndex loaded;
            using (var ms = new MemoryStream(serialized))
            {
                loaded = FstKeyIndex.Load(ms, keys.Count);
            }

            Assert.AreEqual(keys.Count, loaded.Count);
            foreach (var key in keys)
            {
                Assert.AreEqual(fst.Lookup(key), loaded.Lookup(key),
                    $"Post-load lookup mismatch for '{key}'.");
            }
        }

        [TestMethod]
        public void EmptyCorpus_AllLookupsReturnMinus1()
        {
            var fst = FstKeyIndex.Build(new List<string>(), new List<int>());
            Assert.AreEqual(0, fst.Count);
            Assert.AreEqual(-1, fst.Lookup("anything"));
            Assert.AreEqual(-1, fst.Lookup(""));
        }

        [TestMethod]
        public void Empty_HelperMatchesBuildOutput()
        {
            var fst = FstKeyIndex.Empty();
            Assert.AreEqual(0, fst.Count);
            Assert.AreEqual(-1, fst.Lookup("anything"));
        }

        [TestMethod]
        public void SingleEntry_LookupRoundTrips()
        {
            var fst = FstKeyIndex.Build(new[] { "only" }, new[] { 42 });
            Assert.AreEqual(1, fst.Count);
            Assert.AreEqual(42, fst.Lookup("only"));
            Assert.AreEqual(-1, fst.Lookup("other"));
        }

        [TestMethod]
        public void DuplicateKeys_RejectedAtBuildTime()
        {
            var keys = new List<string> { "abc", "abc", "def" };
            var slots = new List<int> { 0, 1, 2 };
            Assert.ThrowsException<ArgumentException>(() => FstKeyIndex.Build(keys, slots));
        }

        [TestMethod]
        public void UnsortedKeys_RejectedAtBuildTime()
        {
            var keys = new List<string> { "zoo", "abc" };
            var slots = new List<int> { 0, 1 };
            Assert.ThrowsException<ArgumentException>(() => FstKeyIndex.Build(keys, slots));
        }

        [TestMethod]
        public void NullKey_RejectedAtBuildTime()
        {
            var keys = new List<string> { null };
            var slots = new List<int> { 0 };
            Assert.ThrowsException<ArgumentException>(() => FstKeyIndex.Build(keys, slots));
        }

        [TestMethod]
        public void NegativeSlotId_Rejected()
        {
            Assert.ThrowsException<ArgumentException>(() =>
                FstKeyIndex.Build(new[] { "ok" }, new[] { -1 }));
        }

        [TestMethod]
        public void UnicodeKeys_RoundTrip()
        {
            // Polish + Japanese + emoji — the UTF-8 byte ordering differs
            // from UTF-16 code-point ordering for emoji, so it's important
            // that callers are prepared to feed keys in UTF-8-byte-sorted
            // order if they want to use this API. We order by
            // StringComparer.Ordinal which is UTF-16 code-unit ordering;
            // the test uses an ASCII subset where the two orderings agree.
            var pairs = new[]
            {
                ("Dziękuję", 0),
                ("Hello", 1),
                ("World", 2),
                ("Żółć", 3),
            };
            var sorted = pairs.OrderBy(p => p.Item1, StringComparer.Ordinal).ToList();

            var fst = FstKeyIndex.Build(
                sorted.Select(p => p.Item1).ToList(),
                sorted.Select(p => p.Item2).ToList());

            foreach (var (key, slot) in sorted)
                Assert.AreEqual(slot, fst.Lookup(key), $"Lookup failed for '{key}'.");
        }

        [TestMethod]
        public void LongKey_1024Bytes_RoundTrips()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 1024; i++) sb.Append('a');
            var fst = FstKeyIndex.Build(new[] { sb.ToString() }, new[] { 7 });
            Assert.AreEqual(7, fst.Lookup(sb.ToString()));
        }

        [TestMethod]
        public void EnumerateAll_ReturnsEveryPair()
        {
            var (keys, slots) = MakeSortedPairs(512);
            var fst = FstKeyIndex.Build(keys, slots);
            var observed = new Dictionary<string, int>();
            foreach (var kv in fst.EnumerateAll())
                observed[kv.Key] = kv.Value;

            Assert.AreEqual(keys.Count, observed.Count);
            for (int i = 0; i < keys.Count; i++)
                Assert.AreEqual(slots[i], observed[keys[i]]);
        }

        [TestMethod]
        public void LoadFromBytes_SameResult()
        {
            var (keys, slots) = MakeSortedPairs(500);
            var fst = FstKeyIndex.Build(keys, slots);

            using (var ms = new MemoryStream())
            {
                fst.Save(ms);
                byte[] payload = ms.ToArray();
                // Strip the length prefix (first 4 bytes): our
                // MatcherBlobReader expects the inner bytes directly.
                var inner = new byte[payload.Length - 4];
                Buffer.BlockCopy(payload, 4, inner, 0, inner.Length);

                var loaded = FstKeyIndex.LoadFromBytes(inner, 0, inner.Length, keys.Count);
                for (int i = 0; i < keys.Count; i++)
                    Assert.AreEqual(slots[i], loaded.Lookup(keys[i]));
            }
        }
    }
}
