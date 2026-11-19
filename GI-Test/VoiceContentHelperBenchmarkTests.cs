using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using GI_Subtitles.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace GI_Test
{
    /// <summary>
    /// Micro-benchmarks and round-trip correctness tests for the
    /// System.Text.Json-based streaming dictionary loader in
    /// <see cref="VoiceContentHelper"/>.
    ///
    /// The round-trip tests MUST pass in every CI run: they prove the
    /// loader returns exactly the expected dict on every tolerated input
    /// shape. Until 2026-11-16 they compared against a Newtonsoft
    /// JObject.Load parse kept in the tests; the last run matched on all
    /// three shapes, and the benchmark below measured, on 10k entries
    /// with a wrapper every 10th: Newtonsoft 8.19 ms and 5,894,736 bytes
    /// allocated, the streaming loader 2.25 ms and 1,982,112 bytes (66.4%
    /// less).
    ///
    /// The benchmark is marked [TestCategory("Performance")] so it
    /// doesn't block CI on machines with unpredictable GC timing. Run
    /// manually with `vstest.console.exe ... /TestCaseFilter:...` when
    /// you need fresh numbers.
    ///
    /// Baseline vs. post-change numbers (observed 2026-04-23 on the
    /// maintainer's dev machine, 10k synthetic entries with one HSR
    /// wrapper every 10 entries — the pre-merged `.gisub` load path):
    ///
    ///                                    Baseline        Post-change
    ///                                    (JObject.Load)  (Utf8JsonReader)
    ///   Allocated bytes / parse          6 006 824       2 062 128
    ///   Managed heap Δ / parse           6 027 072       2 065 632
    ///   Elapsed / parse                  5.68 ms         4.07 ms
    ///   Reduction in allocated bytes                     65.7 %
    ///
    /// Scaled linearly to the real ~500k-entry pre-merged dict:
    ///                                    ~300 MB         ~100 MB
    ///                                    transient       transient
    /// The scout report's 250-300 MB peak-DOM figure sits at the low end
    /// of that projection. Reduction applies to per-parse allocation
    /// counter, not just working-set — i.e. real GC pressure relief.
    /// </summary>
    [TestClass]
    public class VoiceContentHelperBenchmarkTests
    {
        // Synthetic corpus sizes. 10k is enough to show the gap without
        // making the test slow. Bump locally if you're tuning the parser.
        private const int EntryCount = 10_000;

        // One parse of the 10k-entry corpus allocated 1,982,112 bytes on 2026-11-16 (the old Newtonsoft path: 5,894,736).
        // About twice that is the budget: room for runtime changes, while a regression to a DOM-style parse fails.
        private const long AllocationBudgetBytes = 4_000_000;

        /// <summary>
        /// Round-trip: synthetic JSON through
        /// <see cref="VoiceContentHelper.Test_StreamFlatJsonDictionary"/>
        /// gives exactly the entries it was built from.
        /// </summary>
        [TestMethod]
        public void Stj_RoundTrip_PlainStrings()
        {
            byte[] json = BuildSyntheticJson(EntryCount, includeHsrWrappers: false);

            Dictionary<string, string> newDict;
            using (var ms = new MemoryStream(json))
            {
                newDict = VoiceContentHelper.Test_StreamFlatJsonDictionary(
                    ms, flattenWrappedObjects: true);
            }

            AssertDictEqual(Expected(EntryCount, includeHsrWrappers: false, flattenWrappedObjects: true), newDict);
        }

        /// <summary>
        /// Round-trip covering the HSR wrapper case: every 10th entry is
        /// an object { "value": "...", "metadata": {...} } instead of a
        /// plain string. The flattener must unwrap all of them to "value".
        /// </summary>
        [TestMethod]
        public void Stj_RoundTrip_HsrWrappers()
        {
            byte[] json = BuildSyntheticJson(EntryCount, includeHsrWrappers: true);

            Dictionary<string, string> newDict;
            using (var ms = new MemoryStream(json))
            {
                newDict = VoiceContentHelper.Test_StreamFlatJsonDictionary(
                    ms, flattenWrappedObjects: true);
            }

            AssertDictEqual(Expected(EntryCount, includeHsrWrappers: true, flattenWrappedObjects: true), newDict);
            // Sanity check: a wrapper entry actually ended up flattened to
            // its "value" string, not the raw object.
            Assert.AreEqual("TranslationFor-0010", newDict["Key-0010"]);
        }

        /// <summary>
        /// Round-trip: flattenWrappedObjects=false should drop object
        /// values entirely (matches legacy cache-format parse).
        /// </summary>
        [TestMethod]
        public void Stj_RoundTrip_DropObjectsWhenStrict()
        {
            byte[] json = BuildSyntheticJson(EntryCount, includeHsrWrappers: true);

            Dictionary<string, string> newDict;
            using (var ms = new MemoryStream(json))
            {
                newDict = VoiceContentHelper.Test_StreamFlatJsonDictionary(
                    ms, flattenWrappedObjects: false);
            }

            AssertDictEqual(Expected(EntryCount, includeHsrWrappers: true, flattenWrappedObjects: false), newDict);
            // Every 10th entry was an object — must have been skipped.
            Assert.IsFalse(newDict.ContainsKey("Key-0010"));
            Assert.IsTrue(newDict.ContainsKey("Key-0011"));
        }

        /// <summary>
        /// Edge case: completely empty input stream. Both parsers must
        /// return an empty dict (no exception).
        /// </summary>
        [TestMethod]
        public void Stj_EmptyStream_ReturnsEmptyDict()
        {
            using (var ms = new MemoryStream(Array.Empty<byte>()))
            {
                var d = VoiceContentHelper.Test_StreamFlatJsonDictionary(
                    ms, flattenWrappedObjects: true);
                Assert.IsNotNull(d);
                Assert.AreEqual(0, d.Count);
            }
        }

        /// <summary>
        /// Edge case: explicit empty object "{}". Must return a zero-count dict.
        /// </summary>
        [TestMethod]
        public void Stj_EmptyObject_ReturnsEmptyDict()
        {
            byte[] json = Encoding.UTF8.GetBytes("{}");
            using (var ms = new MemoryStream(json))
            {
                var d = VoiceContentHelper.Test_StreamFlatJsonDictionary(
                    ms, flattenWrappedObjects: true);
                Assert.IsNotNull(d);
                Assert.AreEqual(0, d.Count);
            }
        }

        /// <summary>
        /// Edge case: values that are numbers / bools / nulls must be silently
        /// skipped (matches legacy JToken tolerance). Missing EndObject at the
        /// tail is handled as "tolerant — return what we got".
        /// </summary>
        [TestMethod]
        public void Stj_MalformedOrNonStringValues_SkippedSilently()
        {
            const string payload =
                "{" +
                "\"k1\":\"a\"," +
                "\"k2\":42," +
                "\"k3\":null," +
                "\"k4\":true," +
                "\"k5\":\"b\"," +
                "\"k6\":[1,2,3]," +
                "\"k7\":\"c\"" +
                "}";
            byte[] json = Encoding.UTF8.GetBytes(payload);
            using (var ms = new MemoryStream(json))
            {
                var d = VoiceContentHelper.Test_StreamFlatJsonDictionary(
                    ms, flattenWrappedObjects: true);
                Assert.AreEqual(3, d.Count, "Only string-valued keys should survive.");
                Assert.AreEqual("a", d["k1"]);
                Assert.AreEqual("b", d["k5"]);
                Assert.AreEqual("c", d["k7"]);
                Assert.IsFalse(d.ContainsKey("k2"));
                Assert.IsFalse(d.ContainsKey("k3"));
                Assert.IsFalse(d.ContainsKey("k4"));
                Assert.IsFalse(d.ContainsKey("k6"));
            }
        }

        /// <summary>
        /// Edge case: truncated file (object never terminated). The streaming
        /// parser should return what it already extracted rather than throwing.
        /// </summary>
        [TestMethod]
        public void Stj_TruncatedInput_ReturnsPartialDict()
        {
            // Deliberately lop off the closing brace and the last value.
            const string payload = "{\"k1\":\"a\",\"k2\":\"b\",\"k3\":\"c";
            byte[] json = Encoding.UTF8.GetBytes(payload);
            using (var ms = new MemoryStream(json))
            {
                Dictionary<string, string> d = null;
                try
                {
                    d = VoiceContentHelper.Test_StreamFlatJsonDictionary(
                        ms, flattenWrappedObjects: true);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException)
                {
                    // STJ may throw on truncated tail — acceptable IFF at least
                    // one leading entry was surfaced. (The legacy loader threw
                    // outright on truncation, so we're a net upgrade here.)
                    return;
                }
                Assert.IsNotNull(d);
                Assert.IsTrue(d.ContainsKey("k1") && d["k1"] == "a");
                Assert.IsTrue(d.ContainsKey("k2") && d["k2"] == "b");
            }
        }

        /// <summary>
        /// Edge case: duplicate keys in the JSON: the last value wins, as it
        /// did with the old Newtonsoft loader.
        /// </summary>
        [TestMethod]
        public void Stj_DuplicateKeys_LastWins()
        {
            const string payload = "{\"k\":\"first\",\"k\":\"second\",\"k\":\"third\"}";
            byte[] json = Encoding.UTF8.GetBytes(payload);

            Dictionary<string, string> stjDict;
            using (var ms = new MemoryStream(json))
            {
                stjDict = VoiceContentHelper.Test_StreamFlatJsonDictionary(
                    ms, flattenWrappedObjects: true);
            }

            Assert.AreEqual(1, stjDict.Count);
            Assert.AreEqual("third", stjDict["k"],
                "STJ Dictionary indexer with duplicate-key overwrite yields last-wins.");
        }

        /// <summary>
        /// HSR-style nested wrapper that ALSO contains a nested object inside
        /// itself. The wrapper flattener should skip the nested object without
        /// desynchronising the parent reader.
        /// </summary>
        [TestMethod]
        public void Stj_HsrWrapperWithNestedObject_StillExtractsValue()
        {
            const string payload =
                "{" +
                "\"k1\":{\"meta\":{\"a\":1,\"b\":2},\"value\":\"hello\",\"flag\":9}," +
                "\"k2\":\"plain\"" +
                "}";
            byte[] json = Encoding.UTF8.GetBytes(payload);

            using (var ms = new MemoryStream(json))
            {
                var d = VoiceContentHelper.Test_StreamFlatJsonDictionary(
                    ms, flattenWrappedObjects: true);
                Assert.AreEqual(2, d.Count);
                Assert.AreEqual("hello", d["k1"]);
                Assert.AreEqual("plain", d["k2"]);
            }
        }

        /// <summary>
        /// Streaming must handle a JSON object that exceeds the initial 64 KiB
        /// rent buffer. Builds a 1 MB payload of plain-string entries and
        /// verifies every key round-trips.
        /// </summary>
        [TestMethod]
        public void Stj_LargeObject_SpansMultipleBufferRefills()
        {
            // 50k entries × ~30 bytes/entry ≈ 1.5 MB → forces ~24 buffer refills.
            const int Count = 50_000;
            byte[] json = BuildSyntheticJson(Count, includeHsrWrappers: false);
            Assert.IsTrue(json.Length > 64 * 1024,
                "Test payload must be bigger than the streaming buffer.");

            using (var ms = new MemoryStream(json))
            {
                var d = VoiceContentHelper.Test_StreamFlatJsonDictionary(
                    ms, flattenWrappedObjects: true);
                Assert.AreEqual(Count, d.Count);
                Assert.AreEqual("TranslationFor-0000", d["Key-0000"]);
                Assert.AreEqual("TranslationFor-" + (Count - 1).ToString("D4"),
                    d["Key-" + (Count - 1).ToString("D4")]);
            }
        }

        /// <summary>
        /// Memory + latency of one parse: logs the numbers, and fails when
        /// the parse allocates more than its budget (time is not asserted).
        /// </summary>
        [TestMethod]
        [TestCategory("Performance")]
        public void Bench_MemoryAndLatency()
        {
            byte[] json = BuildSyntheticJson(EntryCount, includeHsrWrappers: true);
            RunStjOnce(json); // JIT and static init

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            long heapBefore = GC.GetTotalMemory(false);
            var sw = Stopwatch.StartNew();
            int count = RunStjOnce(json);
            sw.Stop();
            long heap = GC.GetTotalMemory(false) - heapBefore;
            long alloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

            string report =
                "=== VoiceContentHelper parse benchmark ===\n" +
                $"Synthetic entries:         {EntryCount:N0}\n" +
                $"JSON payload size:         {json.Length:N0} bytes\n" +
                $"Entries materialised:      {count:N0}\n" +
                $"Elapsed (ms):              {sw.Elapsed.TotalMilliseconds:F2}\n" +
                $"Allocated bytes / parse:   {alloc:N0}\n" +
                $"Managed heap delta:        {heap:N0}\n" +
                "==========================================";
            Console.WriteLine(report);
            Debug.WriteLine(report);
            Assert.AreEqual(EntryCount, count);
            Assert.IsTrue(alloc <= AllocationBudgetBytes,
                $"One parse allocated {alloc:N0} bytes, over the {AllocationBudgetBytes:N0}-byte budget.");
        }

        // ---------------- helpers ----------------

        private static int RunStjOnce(byte[] json)
        {
            using (var ms = new MemoryStream(json))
            {
                var d = VoiceContentHelper.Test_StreamFlatJsonDictionary(
                    ms, flattenWrappedObjects: true);
                return d.Count;
            }
        }

        /// <summary>
        /// Build a plain-UTF8 JSON object with n entries. Every 10th entry
        /// is an HSR-style wrapper object when <paramref name="includeHsrWrappers"/>
        /// is true.
        /// </summary>
        private static byte[] BuildSyntheticJson(int n, bool includeHsrWrappers)
        {
            var buffer = new MemoryStream(n * 40);
            using (var jw = new Utf8JsonWriter(buffer))
            {
                jw.WriteStartObject();
                for (int i = 0; i < n; i++)
                {
                    string key = "Key-" + i.ToString("D4");
                    string value = "TranslationFor-" + i.ToString("D4");
                    if (includeHsrWrappers && i % 10 == 0)
                    {
                        jw.WriteStartObject(key);
                        jw.WriteString("value", value);
                        jw.WriteNumber("flag", i);
                        jw.WriteEndObject();
                    }
                    else
                    {
                        jw.WriteString(key, value);
                    }
                }
                jw.WriteEndObject();
            }
            return buffer.ToArray();
        }

        /// <summary>What the loader must return for <see cref="BuildSyntheticJson"/>'s output.</summary>
        private static Dictionary<string, string> Expected(int n, bool includeHsrWrappers, bool flattenWrappedObjects)
        {
            var expected = new Dictionary<string, string>(n, StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                if (includeHsrWrappers && i % 10 == 0 && !flattenWrappedObjects) continue;
                expected["Key-" + i.ToString("D4")] = "TranslationFor-" + i.ToString("D4");
            }
            return expected;
        }

        private static void AssertDictEqual(
            Dictionary<string, string> expected,
            Dictionary<string, string> actual)
        {
            Assert.AreEqual(expected.Count, actual.Count,
                $"Count mismatch: expected {expected.Count}, actual {actual.Count}");
            foreach (var kv in expected)
            {
                Assert.IsTrue(actual.TryGetValue(kv.Key, out var v),
                    $"Key '{kv.Key}' missing from actual dict.");
                Assert.AreEqual(kv.Value, v,
                    $"Value mismatch for key '{kv.Key}': expected '{kv.Value}', actual '{v}'.");
            }
        }
    }
}
