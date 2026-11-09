using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GI_Subtitles.Services.Data;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// Wuthering Waves' desktop surfaces, held to games/wuwa.json: the Game_WuWa label in every compiled Strings
    /// dictionary, the embedded ambience art, and the manifest's accent, rail and glow colours on the palettes. These
    /// must already be right before the manifest loader registers the game; GameIdNamespaceTests sees registered games only.
    /// </summary>
    [TestClass]
    public class WuWaSurfaceTests
    {
        private static readonly string[] Cultures = { "en-US", "pl-PL" };

        private static JsonObject Manifest()
        {
            string root = RepoRoot();
            if (root == null) Assert.Inconclusive("not running inside the repository: games/wuwa.json is not reachable");
            string path = Path.Combine(root, "games", "wuwa.json");
            Assert.IsTrue(File.Exists(path), "games/wuwa.json is missing");
            return JsonNode.Parse(File.ReadAllText(path)).AsObject();
        }

        private static Color ColorOf(object resource) => resource switch
        {
            SolidColorBrush brush => brush.Color,
            Color color => color,
            _ => throw new AssertFailedException($"not a colour: {resource?.GetType().Name ?? "null"}"),
        };

        private static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value);

        [TestMethod]
        public void GameWuWa_HasItsNameInEveryStringsDictionary()
        {
            var manifest = Manifest();
            string key = "Game_" + (string)manifest["gameId"];
            RunSta(() =>
            {
                foreach (string culture in Cultures)
                {
                    var dict = Dictionary($"Resources/Strings.{culture}.xaml");
                    Assert.IsTrue(dict.Contains(key), $"Strings.{culture}.xaml has no {key}");
                    Assert.IsFalse(string.IsNullOrWhiteSpace(dict[key] as string), $"Strings.{culture}.xaml: {key} is blank or not a string");
                    // The legacy key from the upstream fork stays in place (append-only files) and must say the same.
                    if (dict.Contains("Game_Wuthering"))
                        Assert.AreEqual((string)dict["Game_Wuthering"], (string)dict[key], $"Strings.{culture}.xaml: two names for one game");
                }
                Assert.AreEqual((string)manifest["name"], (string)Dictionary("Resources/Strings.en-US.xaml")[key],
                    "the English label is the manifest's display name");
                Assert.AreEqual((string)manifest["name"], (string)Dictionary("Resources/Strings.pl-PL.xaml")[key],
                    "the Polish interface keeps the game's English name");
            });
        }

        [TestMethod]
        public void Ambience_IsEmbeddedAtTheSameSizeAsTheOtherGames()
        {
            string asset = (string)Manifest()["look"]?["ambience"];
            Assert.AreEqual("wuwa", asset);
            RunSta(() =>
            {
                BitmapSource Load(string name) => AmbienceArt.LoadFile(Pack($"Assets/Ambience/{name}.jpg").ToString()) as BitmapSource;
                var reference = Load("genshin");
                Assert.IsNotNull(reference, "the Genshin ambience must decode for the comparison to mean anything");
                foreach (string other in new[] { "starrail", "zzz", asset })
                {
                    var art = Load(other);
                    Assert.IsNotNull(art, $"Assets/Ambience/{other}.jpg is not embedded or does not decode");
                    Assert.AreEqual(reference.PixelWidth, art.PixelWidth, $"{other}: width");
                    Assert.AreEqual(reference.PixelHeight, art.PixelHeight, $"{other}: height");
                }
            });
        }

        [TestMethod]
        public void Colours_ReadOnBothPalettesAndStandApartFromTheOtherGames()
        {
            // The manifest is the only copy: the app derives GameWuWaBrush, GameWuWaGlowColor and RailIndicatorWuWaBrush
            // from look.accent, look.glow and look.accentDark, so these values must work on the surfaces they land on.
            var look = Manifest()["look"].AsObject();
            Color accent = Hex((string)look["accent"]);
            Color accentDark = Hex((string)look["accentDark"]);
            Color glow = Hex((string)look["glow"]);
            RunSta(() =>
            {
                var dark = Dictionary("Themes/Palette.Dark.xaml");
                var light = Dictionary("Themes/Palette.Light.xaml");
                Assert.IsFalse(dark.Contains("RailIndicatorWuWaBrush") || light.Contains("RailIndicatorWuWaBrush"),
                    "a second copy of the manifest's colours in XAML would drift");

                // The rail indicator sits on the selected item (Surface2): at least 4:1 there, like the other games.
                double onLight = ContrastRatio(RelativeLuminance(accentDark), RelativeLuminance(ColorOf(light["Surface2Brush"])));
                double onDark = ContrastRatio(RelativeLuminance(accent), RelativeLuminance(ColorOf(dark["Surface2Brush"])));
                Assert.IsTrue(onLight >= 4.0, $"light rail {onLight:F2}:1");
                Assert.IsTrue(onDark >= 4.5, $"dark rail {onDark:F2}:1");

                var tokens = Dictionary("Themes/Tokens.xaml");
                foreach (string other in new[] { "Genshin", "StarRail", "Zzz" })
                {
                    Assert.AreNotEqual(ColorOf(tokens[$"Game{other}Color"]), accent, $"WuWa's accent must differ from {other}'s");
                    Assert.AreNotEqual(ColorOf(tokens[$"Game{other}GlowColor"]), glow, $"WuWa's glow must differ from {other}'s");
                }
            });
        }

        [TestMethod]
        public void DialoguePreset_CoversBothDialogueHeightsAndSkipsTheUserIdStamp()
        {
            // Steam screenshots at 1920x1200, 1920x1080 and 1440x900 (.plan/in-progress/SESSION-55-W2-WUWA-SURFACES.md): story
            // text and the User ID stamp keep their distance from the bottom edge, scaled by width / 1920. Pixels at 1920 wide:
            const double nameTop = 364, lastLineBottom = 89, stampTop = 18;   // from the bottom: open-world name (seen at 1200 only), third story line
            const double lineLeft = 494, lineRight = 1420, stampLeft = 1702;  // from the left edge
            var d = Manifest()["desktop"]["dialogue"].AsObject();
            var profile = new GameRegionProfile
            {
                DialogueXPct = (double)d["x"], DialogueYPct = (double)d["y"], DialogueWPct = (double)d["w"], DialogueHPct = (double)d["h"],
            };
            foreach (var (w, h) in new[] { (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160), (1440, 900), (1680, 1050), (1920, 1200), (2560, 1600) })
            {
                double s = w / 1920.0;
                var (dialogue, _) = GameWindowDetector.CalculateFromRatios(profile, 0, 0, w, h);
                int[] r = dialogue.Split(',').Select(int.Parse).ToArray();
                int top = r[1], bottom = r[1] + r[3], left = r[0], right = r[0] + r[2];
                string at = $"{w}x{h}: region {dialogue}";
                Assert.IsTrue(top <= h - (nameTop + 12) * s, $"{at} clips the open-world speaker name");
                Assert.IsTrue(bottom >= h - (lastLineBottom - 6) * s && bottom <= h, $"{at} cuts the third line of a story scene");
                Assert.IsTrue(left <= (lineLeft - 16) * s && right >= (lineRight + 16) * s, $"{at} is narrower than a full line");
                Assert.IsTrue(right <= (stampLeft - 16) * s || bottom <= h - (stampTop + 4) * s, $"{at} reads the User ID stamp");
            }
        }

        [TestMethod]
        public void Ids_RoundTripThroughTheNamespacesTheAppDerives()
        {
            var manifest = Manifest();
            string gameId = (string)manifest["gameId"];
            Assert.AreEqual((string)manifest["id"], gameId.ToLowerInvariant(), "wire id = GameId.ToLowerInvariant()");
            Assert.AreEqual(gameId, Path.GetFileName(GameDataPaths.GameDir(gameId)), "the %APPDATA% folder keeps the GameId");

            // A generic Unreal process name alone would claim every UE4 game: the title has to be there too.
            var detect = manifest["detect"].AsObject();
            CollectionAssert.Contains(detect["processNames"].AsArray().Select(n => (string)n).ToList(), "Client-Win64-Shipping");
            Assert.IsTrue(detect["windowTitles"].AsArray().Any(t => !string.IsNullOrWhiteSpace((string)t)), "window title required");

            // Minted key space: no upstream TextMap mirror (GameDataUpdateService.IsUpstreamMirrored = false).
            Assert.AreEqual("minted", (string)manifest["source"]?["keySpace"]);
            Assert.AreEqual("none", (string)manifest["desktop"]?["upstreamTextMap"]);
        }
    }
}
