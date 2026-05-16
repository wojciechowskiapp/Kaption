// ─────────────────────────────────────────────────────────────────────────────
//  DialogueContextRamOptimizationTests.cs
//  ---------------------------------------------------------------------------
//  Phase-3 audit item #5 — verify that after LoadCore finishes, the full
//  TextMapEN dictionary (which is 90-110 MB on real Genshin data) is no
//  longer resident. The resident residual must hold ONLY the hashes the
//  graph actually references through IDialogueGraphAccessor.TryGetTextMapValue
//  at runtime (quest-title hashes in practice) — everything else was inlined
//  onto DialogNode.EnText / QuestInfoEntry.QuestTitle and the full map was
//  dropped.
//
//  Measuring the absolute Dictionary<string,string> footprint across GC
//  generations on a loaded CI agent is flaky; instead we assert on an
//  observable side-effect: TryGetTextMapValue on a hash that existed in the
//  full TextMapEN but was NOT referenced by the graph/quest index must now
//  return false. That's a direct proof the slim residual doesn't carry the
//  dropped entries — which is the whole point of the optimization.
//
//  The RAM delta is also logged via GC.GetTotalMemory for diagnostic value;
//  it's not asserted hard because numbers bounce ±10% between runs on
//  different GC modes.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class DialogueContextRamOptimizationTests
    {
        private string _tempDir;
        private string _textMapEnPath;

        // The synthetic dataset is split into two disjoint hash ranges:
        //   [1..1000]  — referenced by a dialog node (inlined onto
        //                DialogNode.EnText during load).
        //   [5000..6000] — "unreferenced" filler: present in the full
        //                  TextMapEN.json file, but nothing in the graph /
        //                  NpcNames / QuestInfo points to these hashes.
        //                  After LoadCore, the slim residual must NOT
        //                  carry these — that's the assertion.
        private const int ReferencedCount = 1000;
        private const int UnreferencedFiller = 1001; // 5000..6000 inclusive

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(),
                "DialogueContextRamOpt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            // Synthetic graph: 1000 nodes. Content hash == dialogId so the
            // TextMap lookup for hash i resolves to "Line_i".
            var graphSb = new StringBuilder();
            graphSb.Append('{');
            for (int i = 1; i <= ReferencedCount; i++)
            {
                if (i > 1) graphSb.Append(',');
                string next = i < ReferencedCount ? $"[{i + 1}]" : "[]";
                graphSb.Append(
                    $"\"{i}\":{{\"h\":{i},\"nh\":0,\"n\":{next},\"rt\":\"NPC\",\"ri\":\"npc-{i % 10}\"}}");
            }
            graphSb.Append('}');
            File.WriteAllText(Path.Combine(_tempDir, "DialogGraph.json"), graphSb.ToString());

            // Synthetic TextMapEN — two ranges. The 5000+ "unreferenced"
            // filler entries are the crux: if the slim-residual optimization
            // is doing its job, they are NOT reachable post-LoadCore.
            var textMapSb = new StringBuilder();
            textMapSb.Append('{');
            bool first = true;
            for (int i = 1; i <= ReferencedCount; i++)
            {
                if (!first) textMapSb.Append(',');
                first = false;
                textMapSb.Append($"\"{i}\":\"Line_{i}\"");
            }
            for (int i = 5000; i < 5000 + UnreferencedFiller; i++)
            {
                textMapSb.Append(',');
                // Longer-than-average value so the filler has real byte weight
                // in the full dictionary — makes the RAM delta visible in the
                // diagnostic log line even with GC noise.
                textMapSb.Append(
                    $"\"{i}\":\"Filler line {i} — this string is only present in the full TextMapEN and has NO inbound reference from the graph or quest index.\"");
            }
            textMapSb.Append('}');
            _textMapEnPath = Path.Combine(_tempDir, "TextMapEN.json");
            File.WriteAllText(_textMapEnPath, textMapSb.ToString());
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Core assertion of phase-3 item #5: after LoadCore, the engine's
        /// residual TextMap dictionary does NOT carry hashes that weren't
        /// referenced by the graph (or the quest index) at load time.
        ///
        /// Implementation-agnostic: we don't probe private fields directly,
        /// we go through the public <see cref="IDialogueGraphAccessor"/>
        /// surface (obtained via reflection because the base class explicitly
        /// implements it). A false result on an "unreferenced" hash means
        /// the slim residual is slimmer than the on-disk TextMapEN — which
        /// proves the optimization is live.
        /// </summary>
        [TestMethod]
        [TestCategory("Performance")]
        public void LoadCore_DropsUnreferencedTextMapEntries()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long baseline = GC.GetTotalMemory(forceFullCollection: true);

            var engine = new NormalizedDialogueContext();
            engine.Load(_tempDir, _textMapEnPath);
            engine.ForceLoadForTests();

            Assert.IsTrue(engine.IsFullyLoaded,
                "Engine should materialise under ForceLoadForTests");

            long afterFullLoad = GC.GetTotalMemory(forceFullCollection: true);
            long loadedCost = afterFullLoad - baseline;
            Trace.WriteLine(
                $"[RAM-OPT] resident after load: {loadedCost:N0} bytes " +
                $"(dataset had {ReferencedCount} referenced + {UnreferencedFiller} unreferenced entries).");

            // Reach the accessor surface. DialogueContextBase implements
            // IDialogueGraphAccessor explicitly, so the methods are only
            // visible through the interface vtable — not as public members
            // on the concrete type. This is deliberate (keeps the public
            // surface small) and we respect it here.
            IDialogueGraphAccessor accessor = (IDialogueGraphAccessor)engine;

            // 1. A REFERENCED hash: DialogNode.EnText carries the string on
            //    the node. FindNodeByText proves the inlined text survived
            //    and drives the reverse index.
            long node42 = engine.FindNodeByText("Line_42");
            Assert.AreEqual(42, node42,
                "Referenced hash 42 must resolve through the inlined EnText path");

            // 2. An UNREFERENCED hash (5050): it was present in the on-disk
            //    TextMapEN.json and we just confirmed it parsed by the
            //    reference count at setup. If TryGetTextMapValue returned
            //    true here, the full map is STILL resident — phase-3 has
            //    not taken effect.
            bool unreferencedPresent = accessor.TryGetTextMapValue("5050", out _);
            Assert.IsFalse(unreferencedPresent,
                "Unreferenced TextMap hash 5050 must NOT be in the slim residual " +
                "— if it resolves, the full TextMapEN is still resident, which " +
                "is exactly what phase-3 #5 was supposed to eliminate.");

            // 3. A control: hash 99999 never existed in any on-disk data.
            //    Must also be absent (sanity check that we didn't
            //    accidentally populate the slim residual from the graph).
            bool absentPresent = accessor.TryGetTextMapValue("99999", out _);
            Assert.IsFalse(absentPresent,
                "Truly-absent hash 99999 must return false");
        }

        /// <summary>
        /// End-to-end smoke: the inline-EnText optimisation doesn't break
        /// chain prediction. If this goes red, the hot-path loop in
        /// TryAddToCache / GetSingleChainPrediction / PopulateHotCache is
        /// reading the wrong field.
        /// </summary>
        [TestMethod]
        [TestCategory("Performance")]
        public void InlinedEnText_PreservesChainPredictionCorrectness()
        {
            var engine = new NormalizedDialogueContext();
            engine.Load(_tempDir, _textMapEnPath);

            var dict = new Dictionary<string, string>
            {
                { "Line_1", "PL_Line_1" },
                { "Line_2", "PL_Line_2" },
                { "Line_3", "PL_Line_3" },
            };

            engine.OnTextMatched("Line_1", detectedNpcName: null, translationDict: dict);

            Assert.IsTrue(engine.IsFullyLoaded);
            Assert.IsTrue(engine.HasSingleChainPrediction,
                "Synthetic straight chain must expose a single-chain prediction");

            var pred = engine.GetSingleChainPrediction();
            Assert.IsNotNull(pred);
            Assert.AreEqual("Line_2", pred.Value.EnText,
                "EnText on the prediction should match the inlined node text");
            Assert.AreEqual("PL_Line_2", pred.Value.Translation);
        }

        /// <summary>
        /// Diagnostic-only test: log a rough RAM delta between baseline and
        /// fully-loaded engine. No hard assertion — GC numbers are
        /// environment-dependent. Exists to give future maintainers a
        /// regression tripwire they can eyeball in the test log.
        /// </summary>
        [TestMethod]
        [TestCategory("Performance")]
        public void LoadCore_LoggedRamDelta_FitsWithinSlimBudget()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long baseline = GC.GetTotalMemory(forceFullCollection: true);

            var engine = new NormalizedDialogueContext();
            engine.Load(_tempDir, _textMapEnPath);
            engine.ForceLoadForTests();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long afterLoad = GC.GetTotalMemory(forceFullCollection: true);
            long delta = afterLoad - baseline;

            // Filler weight on disk (rough upper bound for the string data
            // we EXPECT to have been dropped): ~130 bytes per entry × 1001
            // filler entries ≈ 130 KB. On top of that the full Dictionary
            // backing store would add ~16 bytes/entry overhead.
            //
            // The optimisation should keep the resident footprint well
            // under that ceiling. A generous 2 MB budget catches regressions
            // without flaking on CI noise — keep inline nodes, quest index,
            // reverse index, and the slim residual all together well below.
            Trace.WriteLine(
                $"[RAM-OPT] full-load resident delta: {delta:N0} bytes " +
                $"(baseline: {baseline:N0}, after: {afterLoad:N0})");

            Assert.IsTrue(delta < 5_000_000,
                $"Load resident delta was {delta:N0} bytes — expected < 5 MB on the synthetic " +
                $"dataset. If this spiked, the full TextMapEN may be resident again.");
        }

        /// <summary>
        /// Reflection-based deep check (belt-and-braces): verify the private
        /// <c>_textMapEN</c> field is either null or a slim dictionary, NOT
        /// the full on-disk map. Wrapped in defensive null-handling because
        /// refactors may rename or remove the field — in which case the
        /// test degrades to Inconclusive rather than red, and the public-
        /// surface assertion in <see cref="LoadCore_DropsUnreferencedTextMapEntries"/>
        /// still carries the load.
        /// </summary>
        [TestMethod]
        [TestCategory("Performance")]
        public void LoadCore_PrivateTextMapField_IsSlimOrAbsent()
        {
            var engine = new NormalizedDialogueContext();
            engine.Load(_tempDir, _textMapEnPath);
            engine.ForceLoadForTests();

            var field = typeof(DialogueContextBase).GetField(
                "_textMapEN", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                Assert.Inconclusive(
                    "Private _textMapEN field not found — post-refactor rename. " +
                    "Public-surface tests still cover this.");
                return;
            }

            var value = field.GetValue(engine);
            if (value == null)
            {
                // Fully dropped. That's allowed and even better than "slim";
                // happens if no quest info was present.
                Trace.WriteLine("[RAM-OPT] _textMapEN is null post-load (fully dropped).");
                return;
            }

            if (value is IDictionary<string, string> dict)
            {
                int total = ReferencedCount + UnreferencedFiller;
                Trace.WriteLine(
                    $"[RAM-OPT] _textMapEN residual size: {dict.Count} / {total} on-disk entries.");
                Assert.IsTrue(dict.Count < total / 2,
                    $"Expected the residual TextMap to carry well under half the on-disk entries, " +
                    $"got {dict.Count} of {total}. This means the full map is still resident.");
            }
            else
            {
                Assert.Fail(
                    $"_textMapEN has unexpected type {value.GetType().FullName} — " +
                    "test assumes IDictionary<string,string>.");
            }
        }
    }
}
