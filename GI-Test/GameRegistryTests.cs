using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.OCR.Classification;
using GI_Subtitles.Services.Translation;
using GI_Subtitles.Services.Translation.Strategies;
using GI_Subtitles.Themes;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// The game registry built from games/*.json: the lookups that replaced hand-written tables give the old answers
    /// for Genshin, Star Rail and ZZZ, every manifest in the repository is embedded, and a fourth manifest reaches the
    /// profiles, the pickers, the dialogue factory and the colours without a code change.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class GameRegistryTests
    {
        // ── the replaced tables, answer for answer ──────────────────────────

        [TestMethod]
        [DataRow("Genshin", "genshin")]
        [DataRow("genshin", "genshin")]
        [DataRow("StarRail", "starrail")]
        [DataRow("hsr", "starrail")]
        [DataRow("Honkai Star Rail", "starrail")]
        [DataRow("honkai-star-rail", "starrail")]
        [DataRow("ZZZ", "zzz")]
        [DataRow("Zenless", "zzz")]
        [DataRow("Zenless Zone Zero", "zzz")]
        [DataRow("Zenless-Zone-Zero", "zzz")]
        [DataRow("NotAGame", "genshin")]
        [DataRow("", "genshin")]
        [DataRow(null, "genshin")]
        public void AmbienceArt_ForGame_AnswersAsTheOldTable(string tag, string asset) =>
            Assert.AreEqual(asset, AmbienceArt.ForGame(tag));

        [TestMethod]
        [DataRow("starrail", "#FF6FD6E8")]
        [DataRow("StarRail", "#FF6FD6E8")]
        [DataRow("hsr", "#FF6FD6E8")]
        [DataRow("zzz", "#FFC9CEDC")]
        [DataRow("ZZZ", "#FFC9CEDC")]
        [DataRow("zenless", "#FFC9CEDC")]
        [DataRow("zenlesszonezero", "#FFC9CEDC")]
        [DataRow("genshin", "#FFF2B84B")]
        [DataRow("Genshin", "#FFF2B84B")]
        [DataRow("unknown", "#FFF2B84B")]
        [DataRow(null, "#FFF2B84B")]
        public void GameAccents_For_AnswersAsTheOldTable(string game, string colour) =>
            Assert.AreEqual(colour, GameAccents.For(game).ToString());

        [TestMethod]
        [DataRow("Genshin", "GameGenshinBrush", "GameGenshinGlowColor", "RailIndicatorGenshinBrush")]
        [DataRow("StarRail", "GameStarRailBrush", "GameStarRailGlowColor", "RailIndicatorStarRailBrush")]
        [DataRow("starrail", "GameStarRailBrush", "GameStarRailGlowColor", "RailIndicatorStarRailBrush")]
        [DataRow("ZZZ", "GameZzzBrush", "GameZzzGlowColor", "RailIndicatorZzzBrush")]
        [DataRow("Zenless", "GameZzzBrush", "GameZzzGlowColor", "RailIndicatorZzzBrush")]
        [DataRow("NotAGame", "GameGenshinBrush", "GameGenshinGlowColor", "RailIndicatorGenshinBrush")]
        public void ColourKeys_AreTheExistingOnes(string game, string accent, string glow, string rail)
        {
            Assert.AreEqual(accent, GameLook.AccentBrushKey(game));
            Assert.AreEqual(glow, GameLook.GlowColorKey(game));
            Assert.AreEqual(rail, GameLook.RailIndicatorKey(game));
            Assert.AreEqual(rail, NavRail.AccentKeyFor(game));
        }

        [TestMethod]
        [DataRow("zzz", "#FFE6B83A")]
        [DataRow("ZZZ", "#FFE6B83A")]
        [DataRow("starrail", "#FFA3ABB5")]
        [DataRow("genshin", "#FFA3ABB5")]
        [DataRow(null, "#FFA3ABB5")]
        public void ElementHues_ZzzDrawsPhysicalYellow_TheOthersUseTheSharedGrey(string game, string colour) =>
            Assert.AreEqual(colour, ElementPalette.For(game, "Physical").ToString());

        [TestMethod]
        [DataRow("Genshin", "look_Sample_Speaker", "look_Sample_Line")]
        [DataRow("StarRail", "look_Sample_StarRail_Speaker", "look_Sample_StarRail_Line")]
        [DataRow("starrail", "look_Sample_StarRail_Speaker", "look_Sample_StarRail_Line")]
        [DataRow("ZZZ", "look_Sample_ZZZ_Speaker", "look_Sample_ZZZ_Line")]
        [DataRow("Zenless", "look_Sample_ZZZ_Speaker", "look_Sample_ZZZ_Line")]
        [DataRow("NotAGame", "look_Sample_Speaker", "look_Sample_Line")]
        public void LookSample_UsesTheSameStringsAsBefore(string game, string speakerKey, string lineKey)
        {
            var strings = new[] { "look_Sample_Speaker", "look_Sample_Line", "look_Sample_StarRail_Speaker", "look_Sample_StarRail_Line",
                                  "look_Sample_ZZZ_Speaker", "look_Sample_ZZZ_Line" }.ToDictionary(k => k, k => "<" + k + ">");
            var (speaker, line) = SettingsWindow.LookSample(game, (k, fallback) => strings.TryGetValue(k, out var v) ? v : fallback);
            Assert.AreEqual("<" + speakerKey + ">", speaker);
            Assert.AreEqual("<" + lineKey + ">", line);
        }

        [TestMethod]
        public void KeyStem_TitleCasesAnAllCapitalsId_Only() =>
            CollectionAssert.AreEqual(new[] { "Genshin", "StarRail", "Zzz", "WuWa", "Ab2" },
                new[] { "Genshin", "StarRail", "ZZZ", "WuWa", "AB2" }.Select(GameLook.KeyStem).ToArray());

        [TestMethod]
        [DataRow("genshin", "EN", true)]
        [DataRow("Genshin", "JP", true)]
        [DataRow("starrail", "EN", true)]
        [DataRow("StarRail", "KR", true)]
        [DataRow("genshin", "PL", false)]
        [DataRow("zzz", "EN", false)]
        [DataRow("ZZZ", "EN", false)]
        [DataRow("Zenless", "EN", false)]
        [DataRow("hsr", "EN", false)]
        [DataRow("NotAGame", "EN", false)]
        [DataRow("", "EN", false)]
        public void IsUpstreamMirrored_AnswersAsTheOldSwitch(string game, string lang, bool mirrored) =>
            Assert.AreEqual(mirrored, GameDataUpdateService.IsUpstreamMirrored(game, lang));

        [TestMethod]
        public void ResolveUpstream_KeepsTheExactUrls()
        {
            Assert.AreEqual(("https://gitlab.com/Dimbreath/animegamedata2/-/raw/main/TextMap/TextMapEN.json", "gitlab:Dimbreath/animegamedata2"),
                GameDataUpdateService.ResolveUpstream("genshin", "en"));
            Assert.AreEqual(("https://gitlab.com/Dimbreath/turnbasedgamedata/-/raw/main/TextMap/TextMapJP.json?inline=false", "gitlab:Dimbreath/turnbasedgamedata"),
                GameDataUpdateService.ResolveUpstream("StarRail", "JP"));
            Assert.AreEqual(((string)null, (string)null), GameDataUpdateService.ResolveUpstream("zzz", "EN"));
            Assert.AreEqual(((string)null, (string)null), GameDataUpdateService.ResolveUpstream("genshin", "PL"));
        }

        [TestMethod]
        [DataRow("Genshin", true)]
        [DataRow("genshin", true)]
        [DataRow("StarRail", false)]
        [DataRow("ZZZ", false)]
        [DataRow("Zenless", false)]
        [DataRow("NotAGame", false)]
        [DataRow(null, false)]
        public void DisplayModeTip_IsGenshinsAlone(string game, bool tip) =>
            Assert.AreEqual(tip, GameRegistry.ShowsDisplayModeTip(game));

        [TestMethod]
        [DataRow("Genshin", "Traveler|traveler")]
        [DataRow("StarRail", "Traveler|traveler")]
        [DataRow("ZZZ", "Traveler|traveler")]
        [DataRow("NotAGame", "Traveler|traveler")]
        [DataRow(null, "Traveler|traveler")]
        [DataRow("WuWa", "Rover")]
        [DataRow("Wuthering Waves", "Rover")]
        public void PlayerNameTokens_KeepTravelerForTheShippedGames_AndRoverForWuWa(string game, string tokens) =>
            Assert.AreEqual(tokens, string.Join("|", GameRegistry.PlayerNameTokens(game)));

        [TestMethod]
        public void TheChosenName_ReplacesTheGamesOwnDefaultName_AndNothingElse()
        {
            var wuwa = GameRegistry.PlayerNameTokens("WuWa");
            Assert.AreEqual("Mira, chodź tutaj.", SubtitleComposer.WithPlayerName("Rover, chodź tutaj.", "Mira", wuwa));
            Assert.AreEqual("Traveler i Rover", SubtitleComposer.WithPlayerName("Traveler i Rover", "", wuwa), "no name set: unchanged");
            Assert.AreEqual("Traveler i Mira", SubtitleComposer.WithPlayerName("Traveler i Rover", "Mira", wuwa),
                "WuWa's line keeps a Genshin word as it is");
            var genshin = GameRegistry.PlayerNameTokens("Genshin");
            Assert.AreEqual("Lumine i Rover", SubtitleComposer.WithPlayerName("Traveler i Rover", "Lumine", genshin));
            Assert.AreEqual(SubtitleComposer.WithPlayerName("a traveler, Traveler", "Aether"),
                SubtitleComposer.WithPlayerName("a traveler, Traveler", "Aether", genshin), "the default is the old rule");
        }

        [TestMethod]
        public void TheComposer_AppliesTheTokens_ItIsGiven()
        {
            var shown = SubtitleComposer.Compose(new SubtitleResolution { Key = "Rover, wait.", Content = "Rover, czekaj." },
                true, null, null, "Mira", GameRegistry.PlayerNameTokens("WuWa"));
            Assert.AreEqual("Mira, czekaj.", shown.Content);
            Assert.AreEqual("Rover, czekaj.", shown.Layered, "the fix window keeps the line as the pack knows it");
        }

        [TestMethod]
        public void ManifestColours_MatchTheXamlTokensOfTheShippedGames()
        {
            RunSta(() =>
            {
                var tokens = Dictionary("Themes/Tokens.xaml");
                var light = Dictionary("Themes/Palette.Light.xaml");
                var dark = Dictionary("Themes/Palette.Dark.xaml");
                foreach (string id in new[] { "genshin", "starrail", "zzz" })
                {
                    GameManifest m = GameRegistry.Current.FindByWireId(id);
                    string stem = GameLook.KeyStem(m.GameId);
                    Assert.AreEqual(Parse(m.Look.Accent), (Color)tokens[$"Game{stem}Color"], $"{id} accent");
                    Assert.AreEqual(Parse(m.Look.Glow), (Color)tokens[$"Game{stem}GlowColor"], $"{id} glow");
                    Assert.AreEqual(Parse(m.Look.Accent), ((SolidColorBrush)dark[$"RailIndicator{stem}Brush"]).Color, $"{id} dark rail");
                    Assert.AreEqual(Parse(m.Look.AccentDark), ((SolidColorBrush)light[$"RailIndicator{stem}Brush"]).Color, $"{id} light rail");
                }
            });
        }

        private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        // ── embedding and order ─────────────────────────────────────────────

        [TestMethod]
        public void EveryManifestInTheRepository_IsEmbedded()
        {
            string root = RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            var onDisk = Directory.GetFiles(Path.Combine(root, "games"), "*.json")
                .Select(Path.GetFileName)
                .Where(f => f != "candidates.json" && !f.EndsWith(".schema.json", StringComparison.Ordinal))
                .Select(f => GameManifestLoader.ResourcePrefix + f)
                .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            var embedded = typeof(GameManifest).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(GameManifestLoader.ResourcePrefix, StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(onDisk, embedded);
            Assert.AreEqual(onDisk.Length, GameRegistry.Current.Manifests.Count, "every embedded manifest parses");
        }

        [TestMethod]
        public void TheManifests_PassTheRepositoryValidator()
        {
            string root = RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            string script = Path.Combine(root, "tools", "games", "validate.cjs");
            if (!File.Exists(script)) Assert.Inconclusive("tools/ is not in this checkout (public source mirror).");
            var psi = new ProcessStartInfo("node")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(script);
            Process proc;
            try { proc = Process.Start(psi); }
            catch (Exception ex) { Assert.Inconclusive($"node is not on PATH: {ex.Message}"); throw; }
            using (proc)
            {
                string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                Assert.IsTrue(proc.WaitForExit(60_000), "the validator did not finish");
                Assert.AreEqual(0, proc.ExitCode, output);
            }
        }

        [TestMethod]
        public void ListedGames_AreBetaOrLive_InOrder_WithTheShippedThreeFirst()
        {
            // The app's own registry (the run's lists every shipped game, see ShippedGames): a withdrawn game is left out.
            GameRegistry embedded = ShippedGames.Embedded();
            var listed = embedded.Listed;
            var manifests = GameManifestLoader.LoadEmbedded();
            CollectionAssert.AreEqual(manifests.Where(m => m.IsListed).OrderBy(m => m.Order).Select(m => m.Id).ToArray(), listed.Select(m => m.Id).ToArray());
            CollectionAssert.AreEqual(new[] { "genshin", "starrail", "zzz" }.Where(id => embedded.FindByWireId(id) != null).ToArray(),
                listed.Select(m => m.Id).Where(id => id is "genshin" or "starrail" or "zzz").ToArray(), "the shipped three first, in order");
            Assert.IsTrue(listed.All(m => m.Status == "beta" || m.Status == "live"));
            Assert.IsTrue(listed.Zip(listed.Skip(1), (a, b) => a.Order < b.Order).All(x => x), "strictly ordered");
            using (GameRegistry.Use(embedded))
                CollectionAssert.AreEqual(listed.Select(m => m.GameId).ToArray(), GameRegionProfile.RegisteredGameIds.ToArray());
        }

        // ── loader and builder guards ───────────────────────────────────────

        [TestMethod]
        public void Loader_SkipsWhatTheAppCannotUse()
        {
            string good = FixtureJson();
            Assert.IsNotNull(GameManifestLoader.TryParse(good, "fixture"));
            Assert.IsNull(GameManifestLoader.TryParse("{ not json", "broken"));
            Assert.IsNull(GameManifestLoader.TryParse(good.Replace("\"schema\": 1", "\"schema\": 2"), "newer schema"));
            Assert.IsNull(GameManifestLoader.TryParse(good.Replace("\"gameId\": \"FixtureGame\"", "\"gameId\": \"Other\""), "id mismatch"));
            var noDesktop = GameManifestLoader.Parse(good);
            noDesktop.Desktop = null;
            CollectionAssert.Contains(GameManifestLoader.Problems(noDesktop), "desktop.dialogue and desktop.answer are required for a beta or live game");
            noDesktop.Status = GameManifest.StatusPlanned;
            Assert.AreEqual(0, GameManifestLoader.Problems(noDesktop).Count, "a planned game needs no desktop block");

            var untitled = GameManifestLoader.Parse(good);
            untitled.Detect.RequireTitle = true;
            untitled.Detect.WindowTitles = Array.Empty<string>();
            CollectionAssert.Contains(GameManifestLoader.Problems(untitled), "detect.requireTitle needs at least one windowTitles entry");
        }

        [TestMethod]
        public void Builder_LeavesOmittedKnobsAtTheirDefaults()
        {
            var m = GameManifestLoader.Parse(FixtureJson());
            m.Desktop.Vision = null;
            m.Desktop.Pacing = null;
            var p = GameProfileBuilder.Build(m);
            var defaults = new GameRegionProfile();
            Assert.AreEqual(defaults.SubtitlePadVertical, p.SubtitlePadVertical);
            Assert.AreEqual(defaults.RegionMinWidthFraction, p.RegionMinWidthFraction);
            Assert.AreEqual(defaults.RegionSplitXFraction, p.RegionSplitXFraction);
            Assert.IsFalse(p.UsesGeometryClassifier);
            Assert.IsNull(p.OcrIntervalMs, "no pacing in the manifest = the global default");
            Assert.IsNull(p.ForceOcrAfterSeconds);
            Assert.IsNull(GameProfileBuilder.Build(GameManifestLoader.Parse(FixtureJson().Replace("[\"Fixture-Game\"]", "[]"))).Aliases,
                "no aliases is null, as the hand-written profiles had it");
            Assert.IsFalse(p.RequiresWindowTitle, "no detect.requireTitle: the process name decides");
            string titled = FixtureJson().Replace("\"windowTitles\": [\"Fixture Game\"]", "\"windowTitles\": [\"Fixture Game\"], \"requireTitle\": true");
            Assert.IsTrue(GameProfileBuilder.Build(GameManifestLoader.Parse(titled)).RequiresWindowTitle);
        }

        // ── a fourth game, from a manifest alone ────────────────────────────

        [TestMethod]
        public void AFourthManifest_ReachesTheRegistryPickersFactoryAndColours()
        {
            var registry = GameRegistry.FromManifests(ShippedGames.Manifests().Append(GameManifestLoader.Parse(FixtureJson())));
            using (GameRegistry.Use(registry))
            {
                // Registry and profile lookups.
                Assert.AreEqual("FixtureGame", GameRegionProfile.RegisteredGameIds.Last());
                var profile = GameRegionProfile.Get("Fixture-Game");
                Assert.IsTrue(GameRegionProfile.RegisteredProfiles.Contains(profile), "the alias resolves to the registered profile");
                Assert.AreEqual("Fixture Game", GameRegionProfile.DisplayNameOf("FixtureGame"));
                Assert.AreEqual(0.66, profile.DialogueYPct);
                Assert.AreEqual(100, profile.OcrIntervalMs);
                Assert.IsNull(profile.StableFramesChain, "pacing the manifest leaves out stays on the global default");
                Assert.AreEqual(-140, profile.SubtitlePadVertical, "vision knobs it leaves out keep the shipping defaults");
                CollectionAssert.AreEqual(new[] { "FixtureGame-Win64-Shipping" }, profile.ProcessNames);

                // Accent classifier, passthrough dialogue context, no upstream mirror, no display-mode tip.
                Assert.IsInstanceOfType(TextBlockClassifierFactory.Create("FixtureGame"), typeof(AccentColorTextBlockClassifier));
                var context = GameDialogueContextFactory.Create("FixtureGame");
                Assert.AreEqual("fixturegame", ExpectedBundleGameOf(context));
                Assert.IsInstanceOfType(NameNormalizerOf(context), typeof(TrimNameNormalizer));
                Assert.IsFalse(GameDataUpdateService.IsUpstreamMirrored("fixturegame", "EN"));
                Assert.IsFalse(GameRegistry.ShowsDisplayModeTip("FixtureGame"));
                Assert.AreEqual("fixturegame", AmbienceArt.ForGame("FixtureGame"));
                Assert.AreEqual(Parse("#7FE0A8"), GameAccents.For("fixturegame"));

                // The three shipped games are untouched.
                Assert.AreEqual("zzz", ExpectedBundleGameOf(GameDialogueContextFactory.Create("ZZZ")));
                Assert.AreEqual("ZZZ", GameRegionProfile.Get("Zenless").GameId);

                RunSta(() =>
                {
                    // Pickers: one entry per registered game, the new one last.
                    var pills = SettingsWindow.BuildGamePills(new WrapPanel(), GameRegionProfile.RegisteredProfiles, null);
                    CollectionAssert.AreEqual(new[] { "Genshin", "StarRail", "ZZZ" }, pills.Take(3).Select(b => (string)b.Tag).ToArray());
                    Assert.AreEqual("FixtureGame", pills.Last().Tag);
                    Assert.AreEqual("Genshin Impact", pills[0].Content, "a live game is its name alone");
                    // The fixture is beta: its name and a Beta badge, and a name screen readers say in full.
                    var beta = (StackPanel)pills.Last().Content;
                    Assert.AreEqual("Fixture Game", ((TextBlock)beta.Children[0]).Text);
                    Assert.AreEqual("Beta", ((TextBlock)((Border)beta.Children[1]).Child).Text);
                    Assert.AreEqual("Fixture Game, Beta", System.Windows.Automation.AutomationProperties.GetName(pills.Last()));
                    var radios = OnboardingWindow.BuildGameRadios(new WrapPanel(), GameRegionProfile.RegisteredProfiles, null);
                    Assert.AreEqual("FixtureGame", radios.Last().Tag);
                    Assert.IsInstanceOfType(radios.Last().Content, typeof(StackPanel), "the setup window's choice carries the badge too");
                    Assert.IsTrue(radios.All(r => r.GroupName == "OnbGame"));
                    var combo = new ComboBox();
                    SettingsWindow.FillGameSelector(combo, GameRegionProfile.RegisteredProfiles);
                    Assert.AreEqual("FixtureGame", ((ComboBoxItem)combo.Items[combo.Items.Count - 1]).Tag);
                    Assert.AreEqual(0, combo.SelectedIndex);

                    // Colours: keys derived from the GameId, filled from the manifest; the shipped keys are left alone.
                    Assert.AreEqual("GameFixtureGameBrush", GameLook.AccentBrushKey("FixtureGame"));
                    var tokens = Dictionary("Themes/Tokens.xaml");
                    var app = new ResourceDictionary();
                    app.MergedDictionaries.Add(tokens);
                    GameLook.AddMissingTokens(app);
                    Assert.AreEqual(Parse("#7FE0A8"), ((SolidColorBrush)app["GameFixtureGameBrush"]).Color);
                    Assert.AreEqual(Parse("#7FE0A8"), (Color)app["GameFixtureGameGlowColor"], "no glow in the manifest: the accent");
                    // Every game Tokens.xaml doesn't define gets its keys from its manifest; the shipped three keep theirs.
                    string[] added = app.Keys.Cast<object>().Select(k => k.ToString()).ToArray();
                    CollectionAssert.IsSubsetOf(new[] { "GameFixtureGameColor", "GameFixtureGameBrush", "GameFixtureGameGlowColor" }, added);
                    Assert.IsFalse(added.Any(k => k.StartsWith("GameGenshin", StringComparison.Ordinal) || k.StartsWith("GameStarRail", StringComparison.Ordinal)
                                                  || k.StartsWith("GameZzz", StringComparison.Ordinal)),
                        "Tokens.xaml keeps defining the shipped games: " + string.Join(", ", added));
                    var light = GameLook.AddMissingPaletteKeys(Dictionary("Themes/Palette.Light.xaml"), dark: false);
                    var dark = GameLook.AddMissingPaletteKeys(Dictionary("Themes/Palette.Dark.xaml"), dark: true);
                    Assert.AreEqual(Parse("#1F7A4D"), ((SolidColorBrush)light["RailIndicatorFixtureGameBrush"]).Color);
                    Assert.AreEqual(Parse("#7FE0A8"), ((SolidColorBrush)dark["RailIndicatorFixtureGameBrush"]).Color);
                    Assert.AreEqual(Parse("#0B7C90"), ((SolidColorBrush)light["RailIndicatorStarRailBrush"]).Color);
                    Assert.AreEqual(SystemColors.HighlightColor,
                        ((SolidColorBrush)ThemeService.BuildHighContrastPalette(light)["RailIndicatorFixtureGameBrush"]).Color,
                        "high contrast maps the new rail indicator like the others");
                });
            }
            Assert.IsFalse(GameRegionProfile.RegisteredGameIds.Contains("FixtureGame"), "the embedded registry is back");
        }

        [TestMethod]
        public void AStrategyThisBuildLacks_FailsClosedLikeAnUnknownGame()
        {
            var odd = GameManifestLoader.Parse(FixtureJson());
            odd.Desktop.DialogueContext = "scripted";
            using (GameRegistry.Use(GameRegistry.FromManifests(ShippedGames.Manifests().Append(odd))))
            {
                string expected = ExpectedBundleGameOf(GameDialogueContextFactory.Create("FixtureGame"));
                Assert.AreNotEqual("fixturegame", expected, "its own bundle must not load with the wrong strategies");
                Assert.AreEqual("genshin1typofromconfig", ExpectedBundleGameOf(GameDialogueContextFactory.Create("Genshin1TypoFromConfig")));
            }
        }

        private static string FixtureJson()
        {
            string root = RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            return File.ReadAllText(Path.Combine(root, "GI-Test", "Fixtures", "games", "fixturegame.json"));
        }

        private static string ExpectedBundleGameOf(IGameDialogueContext ctx) =>
            (string)typeof(DialogueContextBase).GetProperty("ExpectedBundleGame", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ctx);

        private static INpcNameNormalizer NameNormalizerOf(IGameDialogueContext ctx) =>
            (INpcNameNormalizer)typeof(DialogueContextBase).GetField("_nameNorm", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ctx);
    }
}
