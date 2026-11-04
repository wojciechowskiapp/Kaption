using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using GI_Subtitles.Services.Security;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// MainWindow warms the dialogue graph on a pool thread when OCR starts, so the first matched line
    /// no longer loads it inside a UI tick (1.9-2.5 s on Genshin). Same synthetic bundle format as
    /// DialogueEvidencePackKeyTests, sized so a cold load is measurable.
    /// </summary>
    [TestClass]
    public class DialogueWarmUpTests
    {
        private const int Nodes = 40_000;
        private static readonly TimeSpan WarmUpBudget = TimeSpan.FromSeconds(60);
        private readonly List<string> _tempDirs = new List<string>();

        [TestCleanup]
        public void Cleanup()
        {
            foreach (string dir in _tempDirs)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch { /* best-effort */ }
            }
        }

        [TestMethod]
        public void WarmUp_LoadsTheGraph_SoTheFirstHotPathCallIsFast()
        {
            var engine = LoadEngine(out string dir);
            Assert.IsFalse(engine.IsFullyLoaded, "Load alone must stay lazy");

            // Through the interface, as MainWindow calls it: must reach the class, not the default no-op.
            var warm = Stopwatch.StartNew();
            Task warmUp = ((IGameDialogueContext)engine).WarmUpAsync();
            Assert.IsTrue(warmUp.Wait(WarmUpBudget), "warm-up did not finish");
            warm.Stop();
            Assert.IsTrue(engine.IsFullyLoaded, "the warm-up must leave the graph resident");

            var first = Stopwatch.StartNew();
            engine.TryHotCacheMatch("line 1", out _, out _);
            bool vouches = engine.CanVouchForLines;
            first.Stop();

            Assert.IsTrue(vouches);
            Assert.IsTrue(engine.IsDialogueLine("Line 2 of the synthetic chain."));
            Assert.IsTrue(first.ElapsedMilliseconds < 50,
                $"first hot-path call took {first.ElapsedMilliseconds} ms after warm-up");

            // Same graph, no warm-up: what the first matched line used to pay on the UI thread.
            var cold = new NormalizedDialogueContext();
            cold.Load(dir, Path.Combine(dir, "TextMapEN.json"));
            var coldSw = Stopwatch.StartNew();
            cold.TryHotCacheMatch("line 1", out _, out _);
            coldSw.Stop();
            Console.WriteLine($"{Nodes:N0} nodes: warm-up {warm.ElapsedMilliseconds} ms on the pool, " +
                              $"first call after it {first.Elapsed.TotalMilliseconds:F2} ms, " +
                              $"first call without it {coldSw.ElapsedMilliseconds} ms.");
        }

        [TestMethod]
        public void WarmUp_RunsOncePerLoad_AndAnEarlyCallerWaitsForIt()
        {
            var engine = LoadEngine(out _);

            Task first = engine.WarmUpAsync();
            Task second = engine.WarmUpAsync();
            Assert.AreSame(first, second, "a second call must not start a second load");

            // A line that arrives mid warm-up blocks for the remainder and sees the full graph.
            Assert.IsTrue(engine.CanVouchForLines);
            Assert.IsTrue(first.Wait(WarmUpBudget));
            Assert.IsFalse(first.IsFaulted);
            Assert.IsTrue(engine.WarmUpAsync().IsCompleted, "nothing left to load");
        }

        [TestMethod]
        public void WarmUp_RunsAgainAfterAReLoad()
        {
            var engine = LoadEngine(out _);
            Task first = engine.WarmUpAsync();
            Assert.IsTrue(first.Wait(WarmUpBudget));
            Assert.IsTrue(engine.IsFullyLoaded);

            // Load resets the slot along with the lazy state, so the next warm-up loads the new bundle.
            string secondDir = WriteBundle();
            engine.Load(secondDir, Path.Combine(secondDir, "TextMapEN.json"));
            Assert.IsFalse(engine.IsFullyLoaded, "a re-Load must drop the resident graph");

            Task second = engine.WarmUpAsync();
            Assert.AreNotSame(first, second, "a re-Load must start a new warm-up");
            Assert.IsTrue(second.Wait(WarmUpBudget), "second warm-up did not finish");
            Assert.IsTrue(second.IsCompletedSuccessfully);
            Assert.IsTrue(engine.IsFullyLoaded, "the second warm-up must load the new graph");
        }

        [TestMethod]
        public void WarmUp_IsACompletedNoOp_WhenThereIsNothingToLoad()
        {
            var neverLoaded = new NormalizedDialogueContext();
            Task none = neverLoaded.WarmUpAsync();
            Assert.IsTrue(none.IsCompleted);
            Assert.IsFalse(neverLoaded.IsFullyLoaded);

            // Contexts without a graph of their own (test fakes, future strategies) inherit the interface default.
            IGameDialogueContext graphless = new GraphlessContext();
            Task fromDefault = graphless.WarmUpAsync();
            Assert.IsTrue(fromDefault.IsCompletedSuccessfully);
        }

        private NormalizedDialogueContext LoadEngine(out string dir)
        {
            dir = WriteBundle();
            var engine = new NormalizedDialogueContext();
            engine.Load(dir, Path.Combine(dir, "TextMapEN.json"));
            return engine;
        }

        private string WriteBundle()
        {
            string dir = Path.Combine(Path.GetTempPath(), "DialogueWarmUpTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _tempDirs.Add(dir);

            var graph = new StringBuilder("{");
            var textMap = new StringBuilder("{");
            for (int i = 1; i <= Nodes; i++)
            {
                if (i > 1) { graph.Append(','); textMap.Append(','); }
                string next = i < Nodes ? $"[{i + 1}]" : "[]";
                graph.Append($"\"{i}\":{{\"h\":{i},\"nh\":0,\"n\":{next},\"rt\":\"TALK_ROLE_NPC\",\"ri\":\"npc-{i % 50}\"}}");
                textMap.Append($"\"{i}\":\"Line {i} of the synthetic chain.\"");
            }
            File.WriteAllText(Path.Combine(dir, "DialogGraph.json"), graph.Append('}').ToString());
            File.WriteAllText(Path.Combine(dir, "TextMapEN.json"), textMap.Append('}').ToString());
            return dir;
        }

        private sealed class GraphlessContext : IGameDialogueContext
        {
            public bool IsLoaded => false;
            public DialoguePrepareFailure LastPrepareFailure => DialoguePrepareFailure.None;
            public int HotCacheHits => 0;
            public int HotCacheMisses => 0;
            public bool HasSingleChainPrediction => false;
            public void Load(string dir, string map, IProgress<(int percent, string message)> progress = null,
                FileProtectionHelper protectionHelper = null) { }
            public string TryHotCacheMatch(string input, out string key, out bool partial)
            {
                key = "";
                partial = false;
                return null;
            }
            public long FindNodeByText(string text, string npcFilter = null) => 0;
            public bool CanVouchForLines => false;
            public bool IsDialogueLine(string text) => false;
            public bool IsKnownSpeaker(string npc) => false;
            public void SetCurrentDialog(long id, Dictionary<string, string> dict) { }
            public void PreloadForNpc(string npc, Dictionary<string, string> dict) { }
            public void OnTextMatched(string key, string npc, Dictionary<string, string> dict) { }
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
