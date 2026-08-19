// ─────────────────────────────────────────────────────────────────────────────
//  ZenlessBundleTextMapTests.cs
//  ---------------------------------------------------------------------------
//  Regression cover for the bug that shipped with ZZZ support: the gamedata
//  bundle defines dialogue ids that exist in no upstream file, so a bundle
//  delivered WITHOUT its matching numeric-keyed EN TextMap installs perfectly,
//  resolves zero lines, and logs nothing. The fix ships that TextMap as the
//  bundle's optional `textmap_en` section.
//
//  These tests drive the real GamedataSyncService split
//  (InstallBundleFromFile) against hand-built bundles, then load the result
//  through the real NormalizedDialogueContext. The round-trip test is the one
//  that matters: FindNodeByText only answers if LoadCore resolved the node's
//  `h` against whatever ended up at TextMapEN.json, which is exactly the join
//  that was broken.
//
//  Note on paths: GameDataPaths.Root is a static readonly rooted at %APPDATA%
//  and cannot be redirected, so these tests use a GUID-suffixed game name and
//  delete the folder in cleanup. Nothing touches a real game's data.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using GI_Subtitles.Services.Data;
using GI_Subtitles.Services.Security;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GI_Test
{
    [TestClass]
    public class ZenlessBundleTextMapTests
    {
        // Two content hashes and the lines they stand for. Values are large
        // enough to prove we're not accidentally reading array indexes, and
        // small enough to stay well inside the builder's 2^53 id ceiling.
        private const ulong Hash1 = 4242424242UL;
        private const ulong Hash2 = 7373737373UL;
        private const long Node1 = 6001;
        private const long Node2 = 6002;
        private const string Line1 = "Good morning, Wise. Did you sleep well?";
        private const string Line2 = "Another day, another Hollow to clear.";

        private string _game;
        private string _gameDir;
        private string _bundlePath;
        private FileProtectionHelper _protector;

        [TestInitialize]
        public void Setup()
        {
            _game = "ZzzBundleTest_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _gameDir = GameDataPaths.GameDir(_game);
            Directory.CreateDirectory(_gameDir);
            _bundlePath = Path.Combine(Path.GetTempPath(),
                "zzz-bundle-" + Guid.NewGuid().ToString("N") + ".json");
            _protector = new FileProtectionHelper(TestProtection.Create());
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { if (File.Exists(_bundlePath)) File.Delete(_bundlePath); } catch { /* best-effort */ }
            try { if (Directory.Exists(_gameDir)) Directory.Delete(_gameDir, true); } catch { /* best-effort */ }
        }

        // ── fixtures ─────────────────────────────────────────────────────

        /// <summary>
        /// A two-node ZZZ bundle shaped exactly like build-gamedata-zzz.cjs
        /// output: node ids and content hashes are synthetic numbers, and the
        /// only thing that can turn a hash back into English is the
        /// <c>textmap_en</c> section carried alongside them.
        /// </summary>
        private static JObject BuildBundle(string game, bool withTextMapEn)
        {
            var bundle = new JObject
            {
                ["bundle_version"] = 2,
                ["dialog_graph"] = new JObject
                {
                    [Node1.ToString()] = new JObject
                    {
                        ["h"] = Hash1,
                        ["nh"] = 0,
                        ["n"] = new JArray(Node2),
                        ["rt"] = "TALK",
                    },
                    [Node2.ToString()] = new JObject
                    {
                        ["h"] = Hash2,
                        ["nh"] = 0,
                        ["n"] = new JArray(),
                        ["rt"] = "TALK",
                    },
                },
                ["hash_to_dialogs"] = new JObject
                {
                    [Hash1.ToString()] = new JArray(Node1),
                    [Hash2.ToString()] = new JArray(Node2),
                },
                ["npc_names"] = new JObject(),
                ["talk_index"] = new JObject(),
                ["quest_info"] = new JObject(),
            };

            if (withTextMapEn)
            {
                bundle["textmap_en"] = new JObject
                {
                    [Hash1.ToString()] = Line1,
                    [Hash2.ToString()] = Line2,
                };
            }

            // extension is written LAST on purpose — it mirrors the builder's
            // key order, so a bundle that must be rejected on game identity is
            // rejected only after textmap_en has already streamed past.
            bundle["extension"] = new JObject
            {
                ["game"] = game,
                ["game_version"] = "3.1",
            };
            return bundle;
        }

        private void WriteBundle(JObject bundle)
        {
            File.WriteAllText(_bundlePath, JsonConvert.SerializeObject(bundle, Formatting.None));
        }

        private string TextMapEnPath => GameDataPaths.TextMapJson(_game, "EN");

        // ── the regression ───────────────────────────────────────────────

        /// <summary>
        /// THE test. A ZZZ bundle installs, and a line the user could OCR
        /// resolves back to the node that carries it. Before the fix the
        /// bundle installed exactly as cleanly, TextMapEN.json was never
        /// written by anyone, and this lookup returned -1 with no error
        /// anywhere in the log.
        /// </summary>
        [TestMethod]
        public void BundleWithTextMapEn_ResolvesDialogueLineEndToEnd()
        {
            WriteBundle(BuildBundle(_game, withTextMapEn: true));

            var install = GamedataSyncService.InstallBundleFromFile(_bundlePath, _game, _protector);

            Assert.IsTrue(install.Success, $"install failed: {install.Message}");
            Assert.IsTrue(install.TextMapEnInstalled,
                "bundle carried textmap_en, so the install must report it wrote TextMapEN.json");
            Assert.IsTrue(File.Exists(TextMapEnPath),
                $"TextMapEN.json missing at {TextMapEnPath} — the bundle's textmap_en section went nowhere");

            var engine = new NormalizedDialogueContext(_game);
            engine.Load(_gameDir, TextMapEnPath, progress: null, protectionHelper: _protector);
            Assert.IsTrue(engine.IsLoaded, "engine refused to prepare — check BundleMeta");

            // The round trip: OCR text → node id. Only possible if LoadCore
            // joined dialog_graph.h against the TextMap we just installed.
            Assert.AreEqual(Node1, engine.FindNodeByText(Line1),
                "dialog_graph.h did not resolve through the bundle-supplied TextMapEN");
            Assert.AreEqual(Node2, engine.FindNodeByText(Line2));

            // And the chain edge still carries the resolved text forward, which
            // is what actually puts a subtitle on screen.
            var dict = new Dictionary<string, string> { { Line1, "PL 1" }, { Line2, "PL 2" } };
            engine.OnTextMatched(Line1, detectedNpcName: null, translationDict: dict);

            Assert.IsTrue(engine.HasSingleChainPrediction,
                "a two-node chain must predict its second line after the first is matched");
            var pred = engine.GetSingleChainPrediction();
            Assert.IsNotNull(pred);
            Assert.AreEqual(Line2, pred.Value.EnText);
            Assert.AreEqual("PL 2", pred.Value.Translation);
        }

        /// <summary>
        /// The negative control for the test above: the SAME bundle without
        /// the section installs just as successfully and resolves nothing.
        /// This is the exact failure the delivery bug produced, pinned so
        /// nobody mistakes "install succeeded" for "it works".
        /// </summary>
        [TestMethod]
        public void BundleWithoutTextMapEn_InstallsButResolvesNothing()
        {
            WriteBundle(BuildBundle(_game, withTextMapEn: false));

            var install = GamedataSyncService.InstallBundleFromFile(_bundlePath, _game, _protector);

            Assert.IsTrue(install.Success, "a bundle without textmap_en is still a valid install");
            Assert.IsFalse(install.TextMapEnInstalled);
            Assert.IsFalse(File.Exists(TextMapEnPath),
                "nothing may invent a TextMapEN.json the bundle did not carry");

            var engine = new NormalizedDialogueContext(_game);
            engine.Load(_gameDir, TextMapEnPath, progress: null, protectionHelper: _protector);
            engine.ForceLoadForTests();

            // 0 is FindNodeByText's "no such node" sentinel.
            Assert.AreEqual(0L, engine.FindNodeByText(Line1),
                "without a TextMap there is nothing to resolve — if this ever passes, " +
                "the id space stopped being synthetic and this whole design needs revisiting");
        }

        /// <summary>
        /// Genshin / HSR / v1 no-regression: a bundle with no
        /// <c>textmap_en</c> must not disturb the TextMapEN.json that
        /// GameDataUpdateService fetched from the public mirror.
        /// </summary>
        [TestMethod]
        public void BundleWithoutTextMapEn_LeavesMirrorSourcedTextMapAlone()
        {
            const string mirrorContent = "{\"1\":\"mirror-sourced line\"}";
            File.WriteAllText(TextMapEnPath, mirrorContent);
            File.WriteAllText(GameDataPaths.TextMapMetaJson(_game, "EN"), "{\"etag\":\"W/\\\"abc\\\"\"}");

            WriteBundle(BuildBundle(_game, withTextMapEn: false));
            var install = GamedataSyncService.InstallBundleFromFile(_bundlePath, _game, _protector);

            Assert.IsTrue(install.Success);
            Assert.IsFalse(install.TextMapEnInstalled);
            Assert.AreEqual(mirrorContent, File.ReadAllText(TextMapEnPath),
                "a bundle that carries no TextMap must not touch the mirror's copy");
            Assert.IsTrue(File.Exists(GameDataPaths.TextMapMetaJson(_game, "EN")),
                "the conditional-GET sidecar belongs to the mirror path and must survive");
        }

        /// <summary>
        /// When the bundle DOES carry the section it replaces the file and
        /// drops the ETag sidecar — leaving a sidecar that describes different
        /// bytes would let a later conditional GET believe it is current.
        /// </summary>
        [TestMethod]
        public void BundleWithTextMapEn_ReplacesFileAndClearsStaleSidecar()
        {
            File.WriteAllText(TextMapEnPath, "{\"1\":\"stale\"}");
            File.WriteAllText(GameDataPaths.TextMapMetaJson(_game, "EN"), "{\"etag\":\"W/\\\"stale\\\"\"}");

            WriteBundle(BuildBundle(_game, withTextMapEn: true));
            var install = GamedataSyncService.InstallBundleFromFile(_bundlePath, _game, _protector);

            Assert.IsTrue(install.Success);
            Assert.IsTrue(install.TextMapEnInstalled);

            var written = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                File.ReadAllText(TextMapEnPath));
            Assert.AreEqual(2, written.Count);
            Assert.AreEqual(Line1, written[Hash1.ToString()]);
            Assert.IsFalse(File.Exists(GameDataPaths.TextMapMetaJson(_game, "EN")),
                "stale ETag sidecar must go with the file it described");
        }

        /// <summary>
        /// The section is written as PLAINTEXT, not a machine-bound .gisub.
        /// DialogueContextBase.LoadCore reads textMapEnPath with File.OpenRead
        /// and never consults FileProtectionHelper, so an encrypted write here
        /// would be invisible — and invisible in the silent way: the engine
        /// loads, and every hash misses.
        /// </summary>
        [TestMethod]
        public void BundleTextMapEn_IsWrittenAsPlaintextNotGisub()
        {
            WriteBundle(BuildBundle(_game, withTextMapEn: true));
            GamedataSyncService.InstallBundleFromFile(_bundlePath, _game, _protector);

            Assert.IsTrue(File.Exists(TextMapEnPath));
            Assert.IsFalse(File.Exists(Path.Combine(_gameDir, "TextMapEN.gisub")),
                "TextMapEN must not be encrypted — LoadCore cannot read a .gisub for this path");

            string raw = File.ReadAllText(TextMapEnPath);
            StringAssert.StartsWith(raw, "{", "expected plaintext JSON on disk");
            StringAssert.Contains(raw, Line1);
        }

        /// <summary>
        /// A bundle for a different game must leave the folder untouched —
        /// including TextMapEN, which streams past BEFORE the
        /// <c>extension.game</c> gate is even readable. That ordering is why
        /// the section is staged to a temp file and committed last.
        /// </summary>
        [TestMethod]
        public void BundleForWrongGame_IsRejectedWithoutWritingTextMapEn()
        {
            WriteBundle(BuildBundle("some-other-game", withTextMapEn: true));

            var install = GamedataSyncService.InstallBundleFromFile(_bundlePath, _game, _protector);

            Assert.IsFalse(install.Success, "cross-game bundle must be refused");
            Assert.IsFalse(install.TextMapEnInstalled);
            Assert.IsFalse(File.Exists(TextMapEnPath),
                "a rejected bundle must not leave its TextMap behind");
            Assert.IsFalse(File.Exists(Path.Combine(_gameDir, "TextMapEN.json.bundle.tmp")),
                "the staging file must be cleaned up on the rejection path");
            Assert.IsFalse(_protector.FileExists(GameDataPaths.DialogGraphJson(_game)),
                "no section may be written for a rejected bundle");
        }

        /// <summary>
        /// Dialogue text is arbitrary game copy and some of it looks like a
        /// date. Newtonsoft's default DateParseHandling would rewrite
        /// "2024-01-02" into a normalised DateTime on the way through the
        /// reader, changing the very string the matcher keys on.
        /// </summary>
        [TestMethod]
        public void BundleTextMapEn_PreservesDateLikeStringsVerbatim()
        {
            const string dateLike = "1997-06-11T00:00:00";
            var bundle = BuildBundle(_game, withTextMapEn: true);
            ((JObject)bundle["textmap_en"])["999"] = dateLike;
            WriteBundle(bundle);

            GamedataSyncService.InstallBundleFromFile(_bundlePath, _game, _protector);

            var written = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                File.ReadAllText(TextMapEnPath));
            Assert.AreEqual(dateLike, written["999"],
                "date-shaped dialogue must survive the split byte-for-byte");
        }

        // ── the routing predicate that keeps the writer single ───────────

        /// <summary>
        /// ZZZ must report no upstream mirror for ANY language. Three things
        /// hang off this: ResolveUpstream returns null so nothing fetches a
        /// string-keyed TextMap into the ZZZ folder, GameDataBootstrapService
        /// defers its input-TextMap check until after the bundle sync, and
        /// GamedataSyncService stays the only writer of TextMapEN.json.
        ///
        /// ZenlessData genuinely does mirror 13 languages, so re-adding an arm
        /// here looks like a fix. It isn't — see IsUpstreamMirrored's remarks.
        /// </summary>
        [DataTestMethod]
        [DataRow("EN")]
        [DataRow("CHS")]
        [DataRow("JP")]
        [DataRow("KR")]
        [DataRow("DE")]
        [DataRow("PL")]
        public void IsUpstreamMirrored_IsFalseForEveryZzzLanguage(string lang)
        {
            Assert.IsFalse(GameDataUpdateService.IsUpstreamMirrored("zzz", lang),
                $"ZZZ/{lang} must not be treated as mirror-sourced");
            Assert.IsFalse(GameDataUpdateService.IsUpstreamMirrored("ZZZ", lang),
                "the check is case-insensitive on the game id");

            var (url, source) = GameDataUpdateService.ResolveUpstream("zzz", lang);
            Assert.IsNull(url, $"ZZZ/{lang} must resolve to no upstream URL (got {url})");
            Assert.IsNull(source);
        }

        /// <summary>
        /// The counterweight: Genshin and Star Rail still route through their
        /// mirrors, so the change above can't be read as "mirroring is off".
        /// </summary>
        [TestMethod]
        public void IsUpstreamMirrored_StillTrueForGenshinAndStarRail()
        {
            Assert.IsTrue(GameDataUpdateService.IsUpstreamMirrored("genshin", "EN"));
            Assert.IsTrue(GameDataUpdateService.IsUpstreamMirrored("starrail", "EN"));
            Assert.IsFalse(GameDataUpdateService.IsUpstreamMirrored("genshin", "PL"),
                "Polish is Kaption-exclusive on every game");

            StringAssert.Contains(
                GameDataUpdateService.ResolveUpstream("genshin", "EN").url,
                "animegamedata2");
            StringAssert.Contains(
                GameDataUpdateService.ResolveUpstream("starrail", "EN").url,
                "turnbasedgamedata");
        }
    }
}
