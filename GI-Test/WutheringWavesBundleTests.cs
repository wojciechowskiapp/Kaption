// ─────────────────────────────────────────────────────────────────────────────
//  WutheringWavesBundleTests.cs
//  ---------------------------------------------------------------------------
//  The Wuthering Waves gamedata bundle (tools/build-gamedata-wuwa.cjs) through
//  the real install split and the passthrough dialogue context. The bundle is
//  tools/fixtures/wuwa/expected-bundle.json: the builder's output for states cut
//  from the 3.7.0 dump, kept in step by tools/test-build-gamedata-wuwa.cjs. Ids
//  are looked up in that file by text, so regenerating it never edits this test.
//
//  GameDataPaths.Root cannot be redirected, so the bundle installs under a
//  GUID-suffixed game name (its extension.game rewritten to match) and the
//  folder is deleted in cleanup, as in ZenlessBundleTextMapTests.
//
//  The public source mirror (scripts/publish-source.sh) ships GI-Test without
//  tools/, so there the fixture is absent and every test is inconclusive.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GI_Subtitles.Services.Data;
using GI_Subtitles.Services.Security;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GI_Test
{
    [TestClass]
    public class WutheringWavesBundleTests
    {
        private const string DediLine = "Did you see a roaming soldier on your way here?";
        private const string DediNext = "I just caught an alert that signals abnormal energy fluctuations at the embankment. Tacet Discords might appear anytime.";
        private const string DediAsks = "Can you do me a favor and take his place for now?";
        private const string Mr = "You're back so soon, Mr. Rover! I am in awe!";
        private const string Ms = "You're back so soon, Ms. Rover! I am in awe!";
        private const string Caption = "This one has heard your decision.";
        private const string CaptionNext = "The transfer has been carried out as you instructed.";

        private string _game;
        private string _gameDir;
        private string _bundlePath;
        private FileProtectionHelper _protector;
        private JObject _bundle;

        [TestInitialize]
        public void Setup()
        {
            var fixture = Path.Combine(RepoRoot(), "tools", "fixtures", "wuwa", "expected-bundle.json");
            if (!File.Exists(fixture))
                Assert.Inconclusive("tools/fixtures/wuwa is not in this checkout (public source mirror).");
            _bundle = JObject.Parse(File.ReadAllText(fixture));
            _game = "WuWaBundleTest_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _gameDir = GameDataPaths.GameDir(_game);
            Directory.CreateDirectory(_gameDir);
            _bundlePath = Path.Combine(Path.GetTempPath(), "wuwa-bundle-" + Guid.NewGuid().ToString("N") + ".json");
            _protector = new FileProtectionHelper(TestProtection.Create());
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { if (File.Exists(_bundlePath)) File.Delete(_bundlePath); } catch { /* best-effort */ }
            try { if (Directory.Exists(_gameDir)) Directory.Delete(_gameDir, true); } catch { /* best-effort */ }
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GI-Subtitles.sln"))) dir = dir.Parent;
            Assert.IsNotNull(dir, "repo root (GI-Subtitles.sln) not found above the test assembly");
            return dir.FullName;
        }

        /// <summary>Node ids of every dialog_graph entry whose English (through textmap_en) is <paramref name="text"/>.</summary>
        private List<long> NodesOf(string text)
        {
            var tm = (JObject)_bundle["textmap_en"];
            return ((JObject)_bundle["dialog_graph"]).Properties()
                .Where(p => (string)tm[(string)p.Value["h"]] == text)
                .Select(p => long.Parse(p.Name))
                .ToList();
        }

        private NormalizedDialogueContext InstallAndLoad()
        {
            var copy = (JObject)_bundle.DeepClone();
            copy["extension"]["game"] = _game;
            File.WriteAllText(_bundlePath, JsonConvert.SerializeObject(copy, Formatting.None));

            var install = GamedataSyncService.InstallBundleFromFile(_bundlePath, _game, _protector);
            Assert.IsTrue(install.Success, $"install failed: {install.Message}");
            Assert.IsTrue(install.TextMapEnInstalled, "the bundle carries textmap_en, so TextMapEN.json must be written");

            var engine = new NormalizedDialogueContext(_game);
            engine.Load(_gameDir, GameDataPaths.TextMapJson(_game, "EN"), progress: null, protectionHelper: _protector);
            Assert.IsTrue(engine.IsLoaded, "engine refused to prepare: check BundleMeta");
            return engine;
        }

        [TestMethod]
        public void Bundle_ResolvesLinesAndPredictsTheNextOne()
        {
            var engine = InstallAndLoad();
            Assert.AreEqual(NodesOf(DediLine).Single(), engine.FindNodeByText(DediLine));

            var dict = new Dictionary<string, string> { { DediLine, "PL 1" }, { DediNext, "PL 2" } };
            engine.OnTextMatched(DediLine, detectedNpcName: "Dedi", translationDict: dict);
            Assert.IsTrue(engine.HasSingleChainPrediction, "a line in a linear talk list predicts the next item");
            var next = engine.GetSingleChainPrediction();
            Assert.IsNotNull(next);
            Assert.AreEqual(DediNext, next.Value.EnText);
            Assert.AreEqual("PL 2", next.Value.Translation);
        }

        [TestMethod]
        public void Bundle_OffersTheOptionsAsPredictedAnswers()
        {
            var engine = InstallAndLoad();
            var dict = new Dictionary<string, string>
            {
                { DediAsks, "PL" }, { "Reluctantly agree.", "Niechętnie się zgódź." }, { "Politely decline.", "Grzecznie odmów." },
            };
            engine.OnTextMatched(DediAsks, detectedNpcName: "Dedi", translationDict: dict);
            var answers = engine.GetPredictedAnswers(dict).Select(a => (a.EnText, a.Translation)).ToList();
            CollectionAssert.AreEquivalent(
                new[] { ("Reluctantly agree.", "Niechętnie się zgódź."), ("Politely decline.", "Grzecznie odmów.") },
                answers);
        }

        [TestMethod]
        public void Bundle_CarriesTheQuestBannerOfTheTalk()
        {
            var engine = InstallAndLoad();
            engine.OnTextMatched(DediLine, detectedNpcName: "Dedi", translationDict: new Dictionary<string, string>());
            var quest = engine.GetCurrentQuestInfo();
            Assert.IsNotNull(quest, "the talk's state is named by a quest node");
            Assert.AreEqual("Wait for Godo", quest.Value.title);
            Assert.AreEqual("SQ", quest.Value.type);
        }

        [TestMethod]
        public void Bundle_KeepsBothRoverGenderVariantsAndCaptionChains()
        {
            var engine = InstallAndLoad();
            long mr = engine.FindNodeByText(Mr), ms = engine.FindNodeByText(Ms);
            Assert.AreNotEqual(0L, mr);
            Assert.AreNotEqual(0L, ms);
            Assert.AreNotEqual(mr, ms, "each Rover variant is its own node");

            var dict = new Dictionary<string, string> { { Caption, "PL" }, { CaptionNext, "PL next" } };
            engine.OnTextMatched(Caption, detectedNpcName: null, translationDict: dict);
            Assert.AreEqual(CaptionNext, engine.GetSingleChainPrediction()?.EnText, "video captions chain in on-screen order");
        }
    }
}
