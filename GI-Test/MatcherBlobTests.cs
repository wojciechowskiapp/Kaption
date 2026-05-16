// ─────────────────────────────────────────────────────────────────────────────
//  MatcherBlobTests.cs
//  ---------------------------------------------------------------------------
//  End-to-end round-trip tests for MatcherBlobWriter + MatcherBlobReader.
//  Builds 10k synthetic entries, writes to a MemoryStream, loads, verifies
//  every key decodes correctly, and spot-checks the allocation profile of
//  the hot lookup path.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class MatcherBlobTests
    {
        private static Dictionary<string, string> SyntheticCorpus(int n, int seed = 42)
        {
            var rand = new Random(seed);
            var dict = new Dictionary<string, string>(n, StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                // Vary key length so n-gram flags see some variety.
                int len = 6 + rand.Next(20);
                var sb = new StringBuilder(len);
                for (int j = 0; j < len; j++)
                    sb.Append((char)('a' + rand.Next(26)));
                string key = sb.ToString() + "_" + i.ToString("D6");
                // PL value bakes in some repetition so zstd has something to compress.
                string value = "Witaj Podróżniku, to jest linia " + i + " z " + n + ".";
                dict[key] = value;
            }
            return dict;
        }

        private static MatcherBlobSchema.MatcherMeta Meta(int n) => new MatcherBlobSchema.MatcherMeta
        {
            CorpusVersion = "test-1",
            Game = "genshin",
            Language = "pl",
            CreatedUtcTicks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
            EntryCount = (uint)n,
        };

        [TestMethod]
        public void WriteRead_10k_RoundTrip()
        {
            var corpus = SyntheticCorpus(10_000);
            var ms = new MemoryStream();
            MatcherBlobWriter.Write(corpus, trainedDictionary: null, meta: Meta(corpus.Count), output: ms);
            ms.Position = 0;

            using (var reader = MatcherBlobReader.LoadFromStream(ms))
            {
                Assert.AreEqual(corpus.Count, reader.EntryCount);
                foreach (var kvp in corpus)
                {
                    int slot = reader.Lookup(kvp.Key);
                    Assert.IsTrue(slot >= 0, $"Key '{kvp.Key}' not found.");

                    Assert.IsTrue(reader.TryGetValue(kvp.Key, out string value));
                    Assert.AreEqual(kvp.Value, value,
                        $"Value mismatch for key '{kvp.Key}'.");
                }
            }
        }

        [TestMethod]
        public void WriteRead_EmptyCorpus_LoadsCleanly()
        {
            var empty = new Dictionary<string, string>();
            var ms = new MemoryStream();
            MatcherBlobWriter.Write(empty, null, Meta(0), ms);
            ms.Position = 0;

            using (var reader = MatcherBlobReader.LoadFromStream(ms))
            {
                Assert.AreEqual(0, reader.EntryCount);
                Assert.AreEqual(-1, reader.Lookup("anything"));
                Assert.IsFalse(reader.TryGetValue("anything", out _));
            }
        }

        [TestMethod]
        public void WriteRead_SingleEntry_Works()
        {
            var corpus = new Dictionary<string, string> { ["only"] = "jedyny" };
            var ms = new MemoryStream();
            MatcherBlobWriter.Write(corpus, null, Meta(1), ms);
            ms.Position = 0;

            using (var reader = MatcherBlobReader.LoadFromStream(ms))
            {
                Assert.AreEqual(1, reader.EntryCount);
                Assert.IsTrue(reader.TryGetValue("only", out string v));
                Assert.AreEqual("jedyny", v);
            }
        }

        [TestMethod]
        public void WriteRead_DuplicateKeys_RejectedAtWrite()
        {
            // IReadOnlyDictionary can't actually carry duplicates, but we
            // exercise the null-value / null-key rejection paths here.
            var corpus = new Dictionary<string, string> { ["a"] = null };
            var ms = new MemoryStream();
            Assert.ThrowsException<ArgumentException>(() =>
                MatcherBlobWriter.Write(corpus, null, Meta(1), ms));
        }

        [TestMethod]
        public void WriteRead_WithTrainedDictionary_StillRoundTrips()
        {
            var corpus = SyntheticCorpus(1_000);
            var samples = new List<byte[]>();
            foreach (var v in corpus.Values)
                samples.Add(Encoding.UTF8.GetBytes(v));
            byte[] dict = ZstdDictionaryTrainer.Train(samples);

            var ms = new MemoryStream();
            MatcherBlobWriter.Write(corpus, dict, Meta(corpus.Count), ms);
            ms.Position = 0;

            using (var reader = MatcherBlobReader.LoadFromStream(ms))
            {
                Assert.AreEqual(dict.Length, (int)reader.Header.ZstdDictLength,
                    "Blob must carry the trained dict verbatim.");
                foreach (var kvp in corpus)
                {
                    Assert.IsTrue(reader.TryGetValue(kvp.Key, out string v));
                    Assert.AreEqual(kvp.Value, v);
                }
            }
        }

        [TestMethod]
        public void CorruptedMagic_LoadThrows()
        {
            var corpus = SyntheticCorpus(100);
            var ms = new MemoryStream();
            MatcherBlobWriter.Write(corpus, null, Meta(corpus.Count), ms);
            byte[] bytes = ms.ToArray();
            bytes[0] = (byte)'X'; // corrupt magic

            Assert.ThrowsException<InvalidDataException>(() =>
                MatcherBlobReader.LoadFromStream(new MemoryStream(bytes)));
        }

        [TestMethod]
        public void CorruptedHeaderCrc_LoadThrows()
        {
            var corpus = SyntheticCorpus(100);
            var ms = new MemoryStream();
            MatcherBlobWriter.Write(corpus, null, Meta(corpus.Count), ms);
            byte[] bytes = ms.ToArray();
            // Flip a bit in an offset/length field (not the reserved/crc bytes).
            bytes[16] ^= 0x01;

            Assert.ThrowsException<InvalidDataException>(() =>
                MatcherBlobReader.LoadFromStream(new MemoryStream(bytes)));
        }

        [TestMethod]
        public void TruncatedBlob_LoadThrows()
        {
            var corpus = SyntheticCorpus(100);
            var ms = new MemoryStream();
            MatcherBlobWriter.Write(corpus, null, Meta(corpus.Count), ms);
            byte[] bytes = ms.ToArray();
            // Chop off the last 8 bytes (inside the metadata section).
            var truncated = new byte[bytes.Length - 8];
            Buffer.BlockCopy(bytes, 0, truncated, 0, truncated.Length);

            Assert.ThrowsException<InvalidDataException>(() =>
                MatcherBlobReader.LoadFromStream(new MemoryStream(truncated)));
        }

        [TestMethod]
        public void HotLookup_Alloc_UnderBudget()
        {
            // 1000 lookups against a warm reader should allocate very little
            // beyond the returned strings. We assert a loose upper bound
            // rather than a hard number so GC tuning differences don't
            // flake the test.
            var corpus = SyntheticCorpus(2_000);
            var ms = new MemoryStream();
            MatcherBlobWriter.Write(corpus, null, Meta(corpus.Count), ms);
            ms.Position = 0;

            using (var reader = MatcherBlobReader.LoadFromStream(ms))
            {
                var keys = new List<string>(corpus.Keys);

                // Warm.
                for (int i = 0; i < 50; i++)
                    reader.Lookup(keys[i % keys.Count]);

                // Benchmark — use GC.GetAllocatedBytesForCurrentThread so
                // inter-thread noise doesn't count.
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++)
                    reader.Lookup(keys[i % keys.Count]);
                long delta = GC.GetAllocatedBytesForCurrentThread() - before;

                // 1000 key lookups → should be well under 1 MB.
                // (Lookup does no UTF-8 decode of the value; that's in TryGetValue.)
                Trace.WriteLine($"Lookup-only 1000x allocated {delta} bytes.");
                Assert.IsTrue(delta < 1_048_576, $"Lookup allocation budget exceeded: {delta} bytes > 1 MB.");
            }
        }

        [TestMethod]
        public void Metadata_RoundTrips()
        {
            var corpus = SyntheticCorpus(10);
            var meta = Meta(corpus.Count);
            meta.CorpusVersion = "6.5-pl-delta3";
            meta.Game = "genshin";
            meta.Language = "pl";

            var ms = new MemoryStream();
            MatcherBlobWriter.Write(corpus, null, meta, ms);
            ms.Position = 0;

            using (var reader = MatcherBlobReader.LoadFromStream(ms))
            {
                Assert.AreEqual("6.5-pl-delta3", reader.Metadata.CorpusVersion);
                Assert.AreEqual("genshin", reader.Metadata.Game);
                Assert.AreEqual("pl", reader.Metadata.Language);
                Assert.AreEqual((uint)corpus.Count, reader.Metadata.EntryCount);
                Assert.AreEqual(MatcherBlobSchema.FormatVersion, reader.Metadata.FormatVersion);
            }
        }
    }
}
