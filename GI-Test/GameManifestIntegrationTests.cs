using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using GI_Subtitles.Services.Community;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Games;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Where the streams meet the manifests: which games take community fixes (the backend's COMMUNITY_TARGETS and the
    /// desktop must agree, both from games/*.json), the commit feed of a minted-key game, and Wuthering Waves reaching
    /// the registry as a listed beta game.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class GameManifestIntegrationTests
    {
        [TestMethod]
        public void CommunityFixes_TheDesktopTakesWhatTheBackendCatalogTakes()
        {
            string root = RenderTestKit.RepoRoot();
            string generated = root == null ? null : Path.Combine(root, "backend", "src", "generated", "games.ts");
            if (generated == null || !File.Exists(generated)) Assert.Inconclusive("backend/src/generated/games.ts is not in this checkout.");

            Match m = Regex.Match(File.ReadAllText(generated), @"COMMUNITY_TARGETS[^=]*=\s*(\{.*?\})\s*(?:\r?\n|$)", RegexOptions.Singleline);
            Assert.IsTrue(m.Success, "COMMUNITY_TARGETS is in the generated catalog");
            var backend = JsonSerializer.Deserialize<Dictionary<string, string[]>>(m.Groups[1].Value);

            // What the app itself takes: its own registry (the run lists every shipped game, see ShippedGames).
            var registry = ShippedGames.Embedded();
            var desktop = registry.Listed
                .Select(g => (g.Id, Langs: (g.Translation?.Targets ?? Array.Empty<string>()).Where(l => registry.TakesCommunityFixes(g.Id, l)).ToArray()))
                .Where(g => g.Langs.Length > 0)
                .ToDictionary(g => g.Id, g => g.Langs);

            CollectionAssert.AreEquivalent(backend.Keys.ToArray(), desktop.Keys.ToArray(), "the same games take fixes");
            foreach (var (game, langs) in backend)
                CollectionAssert.AreEquivalent(langs, desktop[game], game);
            CollectionAssert.AreEquivalent(backend.Values.SelectMany(v => v).Distinct().ToArray(), registry.CommunityLanguages.ToArray(),
                "the languages the fix window offers to share");
        }

        [TestMethod]
        public void CommunityFixes_FollowTheManifest_NotAHandKeptList()
        {
            Assert.IsTrue(SuggestionRules.TakesSuggestions("wuwa", "pl"), "a listed game whose sources list community");

            // The fixture game is translated by the updater only: its fixes stay on the PC.
            string root = RenderTestKit.RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            var fixture = GameManifestLoader.Parse(File.ReadAllText(Path.Combine(root, "GI-Test", "Fixtures", "games", "fixturegame.json")));
            using (GameRegistry.Use(GameRegistry.FromManifests(ShippedGames.Manifests().Append(fixture))))
            {
                Assert.IsFalse(SuggestionRules.TakesSuggestions("fixturegame", "pl"), "translation.sources has no \"community\"");
                Assert.IsTrue(SuggestionRules.TakesSuggestions("genshin", "pl"));
            }

            fixture.Translation.Sources = new[] { GameTranslation.SourceAuto, GameTranslation.SourceCommunity };
            fixture.Translation.Targets = new[] { "pl", "de" };
            using (GameRegistry.Use(GameRegistry.FromManifests(ShippedGames.Manifests().Append(fixture))))
            {
                Assert.IsTrue(SuggestionRules.TakesSuggestions("fixturegame", "de"), "a new game and language from the manifest alone");
                Assert.IsTrue(SuggestionRules.TakesLanguage("de"));
                Assert.IsFalse(SuggestionRules.TakesSuggestions("genshin", "de"), "each game its own targets");
                Assert.IsFalse(SuggestionRules.TakesSuggestions("FixtureGame", "de"), "the wire id is lower case, as the server compares it");
            }

            fixture.Status = GameManifest.StatusPlanned;
            using (GameRegistry.Use(GameRegistry.FromManifests(ShippedGames.Manifests().Append(fixture))))
                Assert.IsFalse(SuggestionRules.TakesSuggestions("fixturegame", "de"), "a planned game is not listed yet");
        }

        [TestMethod]
        public void WuWa_IsAListedLiveGame_AfterTheThreeShippedOnes()
        {
            // The app's own registry: the shipped games it still lists, WuWa last; WuWa's profile whatever its status.
            GameRegistry embedded = ShippedGames.Embedded();
            CollectionAssert.AreEqual(new[] { "genshin", "starrail", "zzz", "wuwa" }.Where(id => embedded.FindByWireId(id) != null).ToArray(),
                embedded.WireIds.Where(id => id is "genshin" or "starrail" or "zzz" or "wuwa").ToArray());
            var registry = GameRegistry.Current;
            GameManifest wuwa = registry.FindByWireId("wuwa");
            Assert.AreEqual(GameManifest.StatusLive, wuwa.Status, "live since session 59 (or listed again for the run while withdrawn)");
            Assert.AreEqual("WuWa", wuwa.GameId);
            Assert.AreSame(wuwa, registry.Find("Wuthering Waves"));
            Assert.AreEqual(GameDesktop.MirrorNone, wuwa.Desktop.UpstreamTextMap, "minted key space: TextMapEN.json comes from the bundle");

            GameRegionProfile profile = GameRegionProfile.Get("WuWa");
            Assert.AreEqual("Wuthering Waves", profile.DisplayName);
            Assert.IsTrue(profile.RequiresWindowTitle, "Unreal's generic executable");
            Assert.IsFalse(profile.UsesGeometryClassifier, "gold speaker names, not the narrow-row geometry split");
            Assert.IsTrue(profile.UsesSpeakerRowClassifier, "free-roam lines have no panel: the speaker is the top row");
            Assert.IsTrue(profile.FiltersOcrJunk, "the free-roam region holds the HP and level readouts");
        }
    }
}
