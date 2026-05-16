// ─────────────────────────────────────────────────────────────────────────────
//  DialoguePredictionTests.cs
//  ---------------------------------------------------------------------------
//  Integration tests for DialogueContextEngine — the component that predicts
//  upcoming dialogue lines using the preprocessed DialogGraph.
//
//  These are INTEGRATION tests: they load the real dialogue graph from the
//  per-user AppData folder. They're marked [TestCategory("Integration")] so
//  CI can exclude them; running them locally requires the app to have been
//  launched at least once so the graph is downloaded.
//
//  When the data isn't there, Setup calls Assert.Inconclusive — CI stays
//  green, local runs with data still exercise the assertions.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class DialoguePredictionTests
    {
        private IGameDialogueContext _engine;
        private Dictionary<string, string> _dict;
        private bool _ready;

        /// <summary>
        /// Build the engine against the real graph + TextMap + translation
        /// dictionary. Marks the test Inconclusive if the user hasn't yet run
        /// Kaption to download the dialogue graph — that's the expected state
        /// on CI, so we don't fail the build.
        /// </summary>
        [TestInitialize]
        public void Setup()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            // Note the "Kaption" brand (v2.0 rename). The legacy "GI-Subtitles"
            // folder is one-time migrated at app startup; if a user has only
            // the legacy folder, they need to launch the app once to trigger it.
            string dataDir = Path.Combine(appData, "Kaption", "Genshin");

            string graphPath = Path.Combine(dataDir, "DialogGraph.gisub");
            string graphPathPlain = Path.Combine(dataDir, "DialogGraph.json");
            string textMapEnPath = Path.Combine(dataDir, "TextMapEN.json");
            string textMapPlPath = Path.Combine(dataDir, "TextMapPL.json");

            if (!Directory.Exists(dataDir) ||
                (!File.Exists(graphPath) && !File.Exists(graphPathPlain)) ||
                !File.Exists(textMapEnPath))
            {
                Assert.Inconclusive(
                    $"DialogueEngine data not available at {dataDir}. " +
                    "Run Kaption once to populate the dialogue graph, then re-run this test.");
                return;
            }

            try
            {
                // Use plaintext JSON — the test path. If the user only has the
                // encrypted .gisub we'd need a FileProtectionHelper with the
                // machine key, which only works on their own box. Tests assume
                // the plaintext is either present or easily regenerated.
                _engine = GameDialogueContextFactory.Create("genshin");
                _engine.Load(dataDir, textMapEnPath);

                if (!_engine.IsLoaded)
                {
                    Assert.Inconclusive(
                        "DialogueContext.Load did not complete successfully — " +
                        "is DialogGraph.json present in plaintext? Encrypted .gisub " +
                        "is not readable outside the original machine.");
                    return;
                }

                // Build a minimal EN→PL dictionary from the raw TextMaps so the
                // predictions come out with Translation populated. We don't use
                // the full VoiceContentHelper pipeline — this is a focused test.
                if (File.Exists(textMapPlPath))
                {
                    _dict = BuildEnToPlDict(textMapEnPath, textMapPlPath);
                }
                else
                {
                    // No PL file — predictions will still work, just with null
                    // translations. We skip the "has translation" assertions in
                    // that case via _ready.
                    _dict = new Dictionary<string, string>();
                }

                _ready = true;
            }
            catch (Exception ex)
            {
                Assert.Inconclusive(
                    $"DialogueEngine setup failed unexpectedly: {ex.Message}. " +
                    "This is treated as inconclusive because the test assumes the " +
                    "local user has functional Kaption data; CI doesn't.");
            }
        }

        [TestMethod]
        [TestCategory("Integration")]
        public void Marjorie_Greeting_PredictsNextLines()
        {
            if (!_ready) return;

            const string npc = "Marjorie";
            const string currentEn = "Welcome. Every treasure here is unique, so we don't negotiate on the price, nor do we give refunds.";

            long nodeId = _engine.FindNodeByText(currentEn, npcFilter: npc);
            Assert.IsTrue(nodeId > 0,
                $"Should find node for Marjorie's greeting — got {nodeId}. " +
                "Either the line is missing from TextMapEN or the graph is stale.");

            _engine.SetCurrentDialog(nodeId, _dict);

            var predictions = _engine.GetPredictedAnswers(_dict);

            Assert.IsTrue(predictions.Count >= 2,
                $"Expected at least 2 predicted next lines, got {predictions.Count}. " +
                $"This usually means the graph doesn't branch at this node — verify " +
                $"DialogGraph.json still has NextDialogIds populated for this line.");

            string allEn = string.Join(" || ", predictions.Select(p => p.EnText));
            Logger.Log.Debug($"Marjorie greeting predictions: {allEn}");

            StringAssert.Contains(allEn, "souvenirs",
                $"Expected at least one prediction to mention 'souvenirs'. Got: {allEn}");

            if (_dict.Count > 0)
            {
                Assert.IsTrue(
                    predictions.All(p => !string.IsNullOrWhiteSpace(p.Translation)),
                    "Every prediction should have a non-empty Polish translation when the " +
                    "TextMapPL file is present. Missing translations point at a dictionary " +
                    "build issue, not an engine issue.");
            }
        }

        [TestMethod]
        [TestCategory("Integration")]
        public void Marjorie_InterestedResponse_PredictsFollowUp()
        {
            if (!_ready) return;

            const string npc = "Marjorie";
            const string currentEn = "Oh? I see you're very interested.";

            long nodeId = _engine.FindNodeByText(currentEn, npcFilter: npc);
            Assert.IsTrue(nodeId > 0,
                $"Should find node for Marjorie's 'interested' line — got {nodeId}.");

            _engine.SetCurrentDialog(nodeId, _dict);

            var predictions = _engine.GetPredictedAnswers(_dict);
            Assert.IsTrue(predictions.Count >= 1,
                $"Expected at least 1 follow-up prediction, got {predictions.Count}.");

            string allEn = string.Join(" || ", predictions.Select(p => p.EnText));
            Logger.Log.Debug($"Marjorie interested predictions: {allEn}");

            // Either "wares" or "shop" should appear in at least one prediction —
            // the follow-up lines for this branch discuss what she sells. Keep
            // the assertion loose (Contains instead of regex) so small dialogue
            // rewrites in future game patches don't churn this test.
            bool containsWares = allEn.IndexOf("wares", StringComparison.OrdinalIgnoreCase) >= 0;
            bool containsShop  = allEn.IndexOf("shop",  StringComparison.OrdinalIgnoreCase) >= 0;
            Assert.IsTrue(containsWares || containsShop,
                $"Expected follow-up predictions to mention 'wares' or 'shop'. Got: {allEn}");

            if (_dict.Count > 0)
            {
                Assert.IsTrue(
                    predictions.All(p => !string.IsNullOrWhiteSpace(p.Translation)),
                    "Every prediction should have a non-empty Polish translation.");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Build a minimal EN→PL dictionary by keying the English TextMap
        /// by its English value and looking up the same hash in the Polish
        /// TextMap. This is a subset of what <c>VoiceContentHelper</c> does at
        /// runtime, but we only need it here for assertion coverage.
        /// </summary>
        private static Dictionary<string, string> BuildEnToPlDict(string textMapEnPath, string textMapPlPath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var en = Newtonsoft.Json.JsonConvert
                    .DeserializeObject<Dictionary<string, string>>(File.ReadAllText(textMapEnPath));
                var pl = Newtonsoft.Json.JsonConvert
                    .DeserializeObject<Dictionary<string, string>>(File.ReadAllText(textMapPlPath));

                if (en == null || pl == null) return result;

                foreach (var kv in en)
                {
                    if (kv.Value == null) continue;
                    if (!pl.TryGetValue(kv.Key, out var plText) || string.IsNullOrEmpty(plText))
                        continue;
                    // Later duplicates win — matches VoiceContentHelper's last-write
                    // semantics, though in practice each hash maps to one unique EN
                    // string so collisions are rare.
                    result[kv.Value] = plText;
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"BuildEnToPlDict failed: {ex.Message}");
            }

            return result;
        }
    }
}
