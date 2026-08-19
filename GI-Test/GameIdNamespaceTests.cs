// ─────────────────────────────────────────────────────────────────────────────
//  GameIdNamespaceTests.cs
//  ---------------------------------------------------------------------------
//  A game id lives in three namespaces at once and NOTHING in the build makes
//  them agree:
//
//    1. wire id   — `zzz`. R2 key, D1 `game`, CLI `--game`, and the value
//                   GameDialogueContextFactory gates the bundle on. Derived as
//                   GameId.ToLowerInvariant().
//    2. folder id — `ZZZ`. GameRegionProfile.GameId, which also names
//                   %APPDATA%\Kaption\<GameId>\.
//    3. resource  — `Game_ZZZ`. Built at RUNTIME by L("Game_" + tag, tag) and
//                   therefore invisible to scripts/validate-xaml-resources.ps1,
//                   which only walks literal {StaticResource ...} references.
//
//  Namespace 1 already has cover (StrategyUnitTests.Factory_EveryRegisteredGame_*).
//  Namespace 3 had none, and it is the one that fails SILENTLY: WPF resource
//  keys are case-sensitive, L() swallows the miss and returns its fallback, so
//  a `Game_zzz` / `Game_ZZZ` mismatch surfaces as a restart prompt reading
//  "Switching from Genshin Impact to ZZZ" — a cosmetic-looking symptom nobody
//  traces back to a key.
//
//  Data-driven off GameRegionProfile.RegisteredProfiles so game #4 is covered
//  the moment it is registered, without anyone remembering to come back here.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using GI_Subtitles.Services.Data;
using GI_Subtitles.Services.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class GameIdNamespaceTests
    {
        /// <summary>
        /// The four shipped UI dictionaries. Only one is merged at a time
        /// (App.xaml.cs swaps the Source on the single Strings entry), so a key
        /// missing from any one of them degrades for the users on that locale
        /// and for nobody else — exactly the kind of gap that survives a manual
        /// check.
        /// </summary>
        private static readonly string[] Cultures = { "en-US", "pl-PL", "zh-CN", "ja-JP" };

        /// <summary>
        /// Load a compiled Strings dictionary out of the GI-Subtitles assembly.
        ///
        /// <para>The BAML that ships in the assembly is what gets asserted, not
        /// the .xaml on disk: a file can be present and correct while being
        /// excluded from the build, and it is the compiled copy that
        /// <c>L()</c> reads at runtime.</para>
        /// </summary>
        private static ResourceDictionary LoadStrings(string culture)
        {
            // The "pack" WebRequest prefix is registered by Application's static
            // constructor, which in a normal run fires when the app starts. A
            // test host never constructs an Application, so without this the
            // load dies on NotSupportedException("The URI prefix is not
            // recognized") — a message that reads like a bad Uri rather than a
            // missing initialisation. Running the class constructor is enough;
            // instantiating Application would need an STA thread and would
            // publish a global Application.Current the rest of the suite can see.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
                typeof(Application).TypeHandle);

            // Asked of a type in the assembly rather than written out: the
            // project is GI-Subtitles but the assembly is Kaption, and a
            // hardcoded name here would break on the next rename with an
            // "unable to locate resource" that reads like a missing file.
            string assembly = typeof(GameRegionProfile).Assembly.GetName().Name;

            var uri = new Uri(
                "pack://application:,,,/" + assembly + ";component/Resources/Strings." + culture + ".xaml",
                UriKind.Absolute);
            return new ResourceDictionary { Source = uri };
        }

        [TestMethod]
        public void EveryRegisteredGame_HasADisplayNameKeyInEveryStringsDictionary()
        {
            foreach (string culture in Cultures)
            {
                ResourceDictionary dict = LoadStrings(culture);

                foreach (var profile in GameRegionProfile.RegisteredProfiles)
                {
                    string key = "Game_" + profile.GameId;

                    Assert.IsTrue(dict.Contains(key),
                        $"Strings.{culture}.xaml has no \"{key}\". The key is built at runtime by " +
                        $"L(\"Game_\" + tag) from GameRegionProfile.GameId \"{profile.GameId}\", " +
                        "WPF resource keys are case-sensitive, and validate-xaml-resources.ps1 only " +
                        "checks literal {StaticResource} uses — so nothing else in the build catches this.");

                    Assert.IsInstanceOfType(dict[key], typeof(string),
                        $"Strings.{culture}.xaml: \"{key}\" must be a sys:String — L() casts with " +
                        "`as string` and silently falls back on anything else.");

                    Assert.IsFalse(string.IsNullOrWhiteSpace((string)dict[key]),
                        $"Strings.{culture}.xaml: \"{key}\" is blank, so the label renders empty " +
                        "instead of falling back to the raw tag.");
                }
            }
        }

        [TestMethod]
        public void EveryRegisteredGame_DisplayNameKeyMatchesTheRegistryDisplayName()
        {
            // en-US is the authoring locale, so DisplayName in the registry and
            // Game_<GameId> in Strings.en-US.xaml are two copies of one string
            // read by different call sites (the Dashboard strip vs. the restart
            // prompt). If they drift, the user sees two names for one game
            // inside a single session.
            ResourceDictionary dict = LoadStrings("en-US");

            foreach (var profile in GameRegionProfile.RegisteredProfiles)
            {
                string key = "Game_" + profile.GameId;
                Assert.IsTrue(dict.Contains(key), $"Strings.en-US.xaml has no \"{key}\".");

                Assert.AreEqual(profile.DisplayName, (string)dict[key],
                    $"\"{key}\" disagrees with GameRegionProfile.DisplayNameOf(\"{profile.GameId}\").");
            }
        }

        [TestMethod]
        public void EveryRegisteredGame_GameIdSurvivesTheAppDataPathSanitiser()
        {
            // The second namespace. GameDataPaths.Sanitise strips path-hostile
            // characters and preserves case verbatim, so %APPDATA%\Kaption\<dir>\
            // is the GameId spelled exactly as the registry spells it. A GameId
            // that did NOT survive would split one game's data across two
            // folders — the bundle written to one, the engine reading the other.
            foreach (var profile in GameRegionProfile.RegisteredProfiles)
            {
                string dir = GameDataPaths.GameDir(profile.GameId);
                Assert.AreEqual(profile.GameId, Path.GetFileName(dir),
                    $"GameId \"{profile.GameId}\" does not round-trip through GameDataPaths.Sanitise; " +
                    "the on-disk folder name would not match the registry tag.");
            }
        }

        [TestMethod]
        public void EveryRegisteredGame_WireIdIsTheLowercasedGameId()
        {
            // Pins the derivation the other two namespaces are defined against.
            // Anything that makes ToLowerInvariant() lossy or ambiguous — a
            // GameId carrying padding, or two profiles collapsing onto one wire
            // id — breaks the R2 key, the D1 row and the bundle gate together,
            // in three different-looking ways.
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var profile in GameRegionProfile.RegisteredProfiles)
            {
                string wire = profile.GameId.ToLowerInvariant();

                Assert.AreEqual(profile.GameId.Trim(), profile.GameId,
                    $"GameId \"{profile.GameId}\" carries padding — it is used verbatim as a folder name.");
                Assert.IsFalse(wire.Contains(" "),
                    $"Wire id \"{wire}\" contains a space; it goes into R2 keys and a `--game` argument.");
                Assert.IsTrue(seen.Add(wire),
                    $"Two profiles collapse to the same wire id \"{wire}\".");
            }
        }
    }
}
