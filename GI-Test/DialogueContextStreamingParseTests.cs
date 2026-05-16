// ─────────────────────────────────────────────────────────────────────────────
//  DialogueContextStreamingParseTests.cs
//  ---------------------------------------------------------------------------
//  Perf + correctness tests for the round-1 streaming TextMapEN parse landed
//  in DialogueContextBase.LoadCore. The old path round-tripped ~60 MB of JSON
//  through reader.ReadToEnd() + JsonConvert.DeserializeObject, which peaked
//  at hundreds of MB of transient heap. The new path streams Utf8JsonReader
//  straight into a Dictionary<string,string>.
//
//  What we assert:
//    * Correctness: the streaming parse produces byte-identical
//      Dictionary contents vs. Newtonsoft.
//    * Memory: GC.GetAllocatedBytesForCurrentThread() delta for the
//      streaming path is materially smaller than the Newtonsoft path
//      on a ~10k-entry synthetic TextMap. Threshold <50% per the
//      perf/round1 brief.
//    * Engine: DialogueContextBase ends up with the same dialog-graph
//      population under the streaming parse as before.
//
//  These tests use fully synthetic JSON — they don't depend on AppData
//  state and run clean on CI.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class DialogueContextStreamingParseTests
    {
        private const int SyntheticTextMapEntries = 10_000;

        /// <summary>
        /// Build a synthetic ~10k-entry TextMap JSON payload. Strings are
        /// plausibly representative of HoYo TextMap content (ascii + a few
        /// unicode punctuation + escape chars) so the parse cost mirrors
        /// the real file's shape per-entry.
        /// </summary>
        private static byte[] BuildSyntheticTextMap(int entryCount)
        {
            var sb = new StringBuilder(entryCount * 64);
            sb.Append('{');
            for (int i = 0; i < entryCount; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(i).Append("\":\"Line_").Append(i)
                  .Append(" — quoted \\\"value\\\" and curly \\u201cquote\\u201d, length ")
                  .Append((i * 7) % 127).Append("\"");
            }
            sb.Append('}');
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// <summary>
        /// Round-trip the same payload through both the Newtonsoft JObject
        /// path and the new streaming STJ path. Dictionaries must be byte-
        /// identical.
        /// </summary>
        [TestMethod]
        public void StreamingParse_Matches_Newtonsoft_OnSyntheticTextMap()
        {
            byte[] json = BuildSyntheticTextMap(SyntheticTextMapEntries);

            Dictionary<string, string> oldDict;
            using (var ms = new MemoryStream(json, writable: false))
            {
                oldDict = VoiceContentHelper.Test_LoadFlatJsonDictionary_Newtonsoft(
                    ms, flattenWrappedObjects: false);
            }

            Dictionary<string, string> newDict;
            using (var ms = new MemoryStream(json, writable: false))
            {
                newDict = VoiceContentHelper.StreamFlatJsonDictionary(
                    ms, flattenWrappedObjects: false, entryCapacityHint: oldDict.Count);
            }

            Assert.AreEqual(oldDict.Count, newDict.Count,
                "Entry count mismatch between Newtonsoft and streaming parse");

            foreach (var kv in oldDict)
            {
                Assert.IsTrue(newDict.TryGetValue(kv.Key, out var v),
                    $"Streaming parse missing key {kv.Key}");
                Assert.AreEqual(kv.Value, v,
                    $"Value mismatch at key {kv.Key}");
            }
        }

        /// <summary>
        /// Measure the managed-allocation delta for each parse path. The
        /// streaming path should allocate materially less (we assert
        /// &lt;50% of the Newtonsoft path on this synthetic corpus).
        ///
        /// We warm the JITs once before measuring so the numbers reflect
        /// steady-state parse cost rather than first-call codegen.
        /// </summary>
        [TestMethod]
        public void StreamingParse_Allocates_LessThanNewtonsoft()
        {
            byte[] json = BuildSyntheticTextMap(SyntheticTextMapEntries);

            // JIT warmup — discard results, just get the codegen on both
            // paths hot so measurement isn't skewed by first-call costs.
            using (var ms = new MemoryStream(json, writable: false))
                _ = VoiceContentHelper.Test_LoadFlatJsonDictionary_Newtonsoft(ms, false);
            using (var ms = new MemoryStream(json, writable: false))
                _ = VoiceContentHelper.StreamFlatJsonDictionary(ms, false, 0);

            // Settle background allocations before sampling thread-local
            // allocation counters. The counter is monotonic, reset-proof.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            Dictionary<string, string> oldDict;
            using (var ms = new MemoryStream(json, writable: false))
            {
                oldDict = VoiceContentHelper.Test_LoadFlatJsonDictionary_Newtonsoft(
                    ms, flattenWrappedObjects: false);
            }
            long oldAlloc = GC.GetAllocatedBytesForCurrentThread() - before;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            before = GC.GetAllocatedBytesForCurrentThread();
            Dictionary<string, string> newDict;
            using (var ms = new MemoryStream(json, writable: false))
            {
                newDict = VoiceContentHelper.StreamFlatJsonDictionary(
                    ms, flattenWrappedObjects: false, entryCapacityHint: 0);
            }
            long newAlloc = GC.GetAllocatedBytesForCurrentThread() - before;

            // Sanity: both paths produced the same dict size.
            Assert.AreEqual(oldDict.Count, newDict.Count);

            // The streaming path owns every `key` + `value` string because
            // we can't intern them from a JToken DOM — so it allocates
            // `count * (keyLen + valueLen) * 2` bytes just for the strings
            // themselves. The Newtonsoft path additionally allocates a
            // JObject + JProperty + JValue trio per entry AND a massive
            // UTF-16 string for the whole payload when using ReadToEnd
            // (MemoryStream path skips the ReadToEnd but still builds the
            // full DOM). Threshold is intentionally loose (50%) since we
            // use JObject.Load (not ReadToEnd) in the test harness — real
            // win vs the old LoadCore path is larger.
            TestContext.WriteLine(
                $"Newtonsoft alloc: {oldAlloc:N0} bytes, streaming alloc: {newAlloc:N0} bytes " +
                $"(ratio: {(double)newAlloc / oldAlloc:P1})");

            Assert.IsTrue(newAlloc < oldAlloc / 2,
                $"Streaming parse allocated {newAlloc:N0} bytes vs Newtonsoft {oldAlloc:N0} bytes " +
                "— expected at least 50% reduction.");
        }

        /// <summary>
        /// Integration: a synthetic DialogGraph + TextMapEN fed through
        /// LoadCore via NormalizedDialogueContext. Confirms the streaming
        /// parse populates the graph + EnText on nodes the same way the
        /// pre-round-1 path did (tested indirectly — text-matching lookup
        /// that relies on inline EnText must succeed).
        /// </summary>
        [TestMethod]
        public void LoadCore_StreamingParse_PopulatesInlineEnText()
        {
            string tempDir = Path.Combine(Path.GetTempPath(),
                "StreamingParseTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // 50 synthetic nodes — just enough to exercise every loader
                // path (graph + npcNames + talkIndex + questInfo absent → fall
                // through branches).
                var graph = new StringBuilder();
                graph.Append('{');
                for (int i = 1; i <= 50; i++)
                {
                    if (i > 1) graph.Append(',');
                    string next = i < 50 ? $"[{i + 1}]" : "[]";
                    graph.Append($"\"{i}\":{{\"h\":{i},\"nh\":0,\"n\":{next},\"rt\":\"NPC\",\"ri\":\"npc-{i % 5}\"}}");
                }
                graph.Append('}');
                File.WriteAllText(Path.Combine(tempDir, "DialogGraph.json"), graph.ToString());

                var textMap = new StringBuilder();
                textMap.Append('{');
                for (int i = 1; i <= 50; i++)
                {
                    if (i > 1) textMap.Append(',');
                    textMap.Append($"\"{i}\":\"Line_{i}\"");
                }
                textMap.Append('}');
                string textMapPath = Path.Combine(tempDir, "TextMapEN.json");
                File.WriteAllText(textMapPath, textMap.ToString());

                var engine = new NormalizedDialogueContext();
                engine.Load(tempDir, textMapPath);
                engine.ForceLoadForTests();

                Assert.IsTrue(engine.IsFullyLoaded,
                    "LoadCore must complete under the streaming TextMapEN path");

                // FindNodeByText round-trips the inline EnText via the
                // text-to-dialog-id reverse index — verifies both the
                // TextMap parse and the node-text inlining.
                long nodeId = engine.FindNodeByText("Line_17");
                Assert.AreEqual(17L, nodeId,
                    "Streaming parse should inline EnText so FindNodeByText resolves");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Malformed-tail tolerance: a truncated TextMapEN file should
        /// produce whatever entries successfully parsed, matching the
        /// Newtonsoft path's "throw → catch → empty dict" behaviour from
        /// LoadCore's outer try/catch. We test the helper directly; the
        /// streaming helper returns what it parsed rather than throwing
        /// on a truncated tail, which is strictly better than Newtonsoft.
        /// </summary>
        [TestMethod]
        public void StreamingParse_Tolerates_TruncatedInput()
        {
            // Valid prefix with 3 entries, then cut mid-value.
            byte[] json = Encoding.UTF8.GetBytes(
                "{\"1\":\"alpha\",\"2\":\"beta\",\"3\":\"gam");

            Dictionary<string, string> dict;
            using (var ms = new MemoryStream(json, writable: false))
            {
                dict = VoiceContentHelper.StreamFlatJsonDictionary(
                    ms, flattenWrappedObjects: false, entryCapacityHint: 0);
            }

            // Exactly the entries that fully finished before the cut.
            Assert.IsTrue(dict.ContainsKey("1"));
            Assert.AreEqual("alpha", dict["1"]);
            Assert.IsTrue(dict.ContainsKey("2"));
            Assert.AreEqual("beta", dict["2"]);
            Assert.IsFalse(dict.ContainsKey("3"),
                "Truncated value must not materialize a partial string");
        }

        /// <summary>
        /// Unicode + escape coverage. TextMap entries routinely contain
        /// non-ASCII characters and JSON escape sequences; the streaming
        /// parse must decode them to the same chars Newtonsoft does.
        /// </summary>
        [TestMethod]
        public void StreamingParse_HandlesUnicodeAndEscapes()
        {
            byte[] json = Encoding.UTF8.GetBytes(
                "{\"k1\":\"tab\\there\",\"k2\":\"newline\\nhere\",\"k3\":\"quote\\\"inside\"," +
                "\"k4\":\"emoji\\uD83D\\uDE00done\",\"k5\":\"kanji-日本語\"}");

            Dictionary<string, string> dict;
            using (var ms = new MemoryStream(json, writable: false))
            {
                dict = VoiceContentHelper.StreamFlatJsonDictionary(
                    ms, flattenWrappedObjects: false, entryCapacityHint: 0);
            }

            Assert.AreEqual("tab\there", dict["k1"]);
            Assert.AreEqual("newline\nhere", dict["k2"]);
            Assert.AreEqual("quote\"inside", dict["k3"]);
            Assert.AreEqual("emoji😀done", dict["k4"]);
            Assert.AreEqual("kanji-日本語", dict["k5"]);
        }

        /// <summary>
        /// Empty object: the helper must return an empty dict, not throw.
        /// Matches the guard in LoadCore when TextMapEN.json is present
        /// but zero-sized.
        /// </summary>
        [TestMethod]
        public void StreamingParse_HandlesEmptyObject()
        {
            byte[] json = Encoding.UTF8.GetBytes("{}");

            Dictionary<string, string> dict;
            using (var ms = new MemoryStream(json, writable: false))
            {
                dict = VoiceContentHelper.StreamFlatJsonDictionary(
                    ms, flattenWrappedObjects: false, entryCapacityHint: 0);
            }
            Assert.IsNotNull(dict);
            Assert.AreEqual(0, dict.Count);
        }

        public TestContext TestContext { get; set; }
    }
}
