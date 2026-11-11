// ─────────────────────────────────────────────────────────────────────────────
//  GamedataRecoveryTests.cs
//  ---------------------------------------------------------------------------
//  The pieces SettingsWindow combines to recover a gamedata bundle that fails
//  HMAC on this device (GlitchTip 7884595): the once-per-game gate, the list of
//  machine-bound files to wipe, and forgetting the manifest row so the next
//  sync downloads the bundle even though its published sha has not changed.
//
//  GameDataPaths.Root is the test run's temporary folder (TestEnvironment),
//  shared by every test, so the install test uses a GUID-suffixed game folder
//  (as ZenlessBundleTextMapTests does) and the manifest tests a temp manifest
//  path. The real profile is never touched.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Data;
using GI_Subtitles.Services.Security;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ManifestEntry = GI_Subtitles.Services.Translation.GamedataSyncService.GamedataManifestEntry;

namespace GI_Test
{
    [TestClass]
    public class GamedataRecoveryTests
    {
        private string _scratch;
        private string _manifestPath;
        private string _game;
        private FileProtectionHelper _protector;

        [TestInitialize]
        public void Setup()
        {
            _scratch = Path.Combine(Path.GetTempPath(), "GamedataRecoveryTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratch);
            _manifestPath = Path.Combine(_scratch, "gamedata-manifest.json");
            _game = "SelfHealTest_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _protector = new FileProtectionHelper(TestProtection.Create());
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true); } catch { /* best-effort */ }
            try
            {
                string gameDir = GameDataPaths.GameDir(_game);
                if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true);
            }
            catch { /* best-effort */ }
        }

        // ── once-per-game gate ───────────────────────────────────────────

        [TestMethod]
        public void Gate_StaleBundle_AllowsOneAttemptPerGame()
        {
            var gate = new GamedataRecoveryGate();

            Assert.IsTrue(gate.TryBegin("ZZZ", DialoguePrepareFailure.StaleMachineBoundFile));
            Assert.IsFalse(gate.TryBegin("ZZZ", DialoguePrepareFailure.StaleMachineBoundFile),
                "A second HMAC failure after re-downloading must surface, not loop.");
            Assert.IsTrue(gate.TryBegin("Genshin", DialoguePrepareFailure.StaleMachineBoundFile),
                "Each game heals independently.");
        }

        [TestMethod]
        public void Gate_GameIdSpelling_DoesNotGrantASecondAttempt()
        {
            var gate = new GamedataRecoveryGate();

            Assert.IsTrue(gate.TryBegin("ZZZ", DialoguePrepareFailure.StaleMachineBoundFile));
            Assert.IsFalse(gate.TryBegin("zzz", DialoguePrepareFailure.StaleMachineBoundFile));
            Assert.IsFalse(gate.TryBegin(" Zzz ", DialoguePrepareFailure.StaleMachineBoundFile));
        }

        [TestMethod]
        public void Gate_OtherOutcomes_NeverHealAndKeepTheAttempt()
        {
            var gate = new GamedataRecoveryGate();

            Assert.IsFalse(gate.TryBegin("Genshin", DialoguePrepareFailure.None));
            Assert.IsFalse(gate.TryBegin("Genshin", DialoguePrepareFailure.Other),
                "A game mismatch or corrupt sidecar re-downloads to the same result.");
            Assert.IsTrue(gate.TryBegin("Genshin", DialoguePrepareFailure.StaleMachineBoundFile),
                "Outcomes that did not heal must not use up the one attempt.");
        }

        [TestMethod]
        public void Gate_BlankGame_NeverHeals()
        {
            var gate = new GamedataRecoveryGate();

            Assert.IsFalse(gate.TryBegin(null, DialoguePrepareFailure.StaleMachineBoundFile));
            Assert.IsFalse(gate.TryBegin("  ", DialoguePrepareFailure.StaleMachineBoundFile));
        }

        // ── forgetting the installed bundle ──────────────────────────────

        [TestMethod]
        public void ForgetInstalled_RemovesOnlyThatGamesRow()
        {
            WriteManifest(("gamedata/genshin", "7.1"), ("gamedata/zzz", "3.3"));

            Assert.IsTrue(GamedataSyncService.ForgetInstalled("Genshin", _manifestPath));

            var rows = ReadManifest();
            Assert.IsFalse(rows.ContainsKey("gamedata/genshin"));
            Assert.AreEqual("3.3", rows["gamedata/zzz"].Version);
        }

        [TestMethod]
        public void ForgetInstalled_UsesTheKeySyncWrites_WhateverTheGameIdCase()
        {
            WriteManifest(("gamedata/zzz", "3.3"));

            Assert.IsTrue(GamedataSyncService.ForgetInstalled("ZZZ", _manifestPath));

            Assert.AreEqual(0, ReadManifest().Count);
        }

        [TestMethod]
        public void ForgetInstalled_NoRow_LeavesTheManifestUntouched()
        {
            WriteManifest(("gamedata/zzz", "3.3"));
            string before = File.ReadAllText(_manifestPath);

            Assert.IsFalse(GamedataSyncService.ForgetInstalled("StarRail", _manifestPath));

            Assert.AreEqual(before, File.ReadAllText(_manifestPath));
        }

        [TestMethod]
        public void ForgetInstalled_NoManifest_CreatesNothing()
        {
            Assert.IsFalse(GamedataSyncService.ForgetInstalled("Genshin", _manifestPath));
            Assert.IsFalse(GamedataSyncService.ForgetInstalled(null, _manifestPath));

            Assert.IsFalse(File.Exists(_manifestPath));
        }

        // ── the files a wipe removes ─────────────────────────────────────

        [TestMethod]
        public void MachineBoundDialogueFiles_AreWhatAnInstallEncrypts_NotTextMapEN()
        {
            string bundlePath = Path.Combine(_scratch, "bundle.json");
            File.WriteAllText(bundlePath, Bundle(_game).ToJsonString());
            var install = GamedataSyncService.InstallBundleFromFile(bundlePath, _game, _protector);
            Assert.IsTrue(install.Success, install.Message);
            Assert.IsTrue(install.TextMapEnInstalled);

            var files = GamedataSyncService.MachineBoundDialogueFiles(_game);

            CollectionAssert.AreEquivalent(
                new[] { "DialogGraph", "HashToDialogs", "NpcNames", "TalkIndex", "QuestInfo", "BundleMeta" },
                files.Select(Path.GetFileNameWithoutExtension).ToArray());
            foreach (string path in files)
                Assert.IsTrue(_protector.FileExists(path), $"{Path.GetFileName(path)} was not written by the install.");

            foreach (string path in files)
                _protector.DeleteBothVariants(path);

            foreach (string path in files)
                Assert.IsFalse(_protector.FileExists(path), $"{Path.GetFileName(path)} survived the wipe.");
            Assert.IsTrue(File.Exists(GameDataPaths.TextMapJson(_game, "EN")),
                "TextMapEN.json is plaintext and public; the self-heal must not throw it away.");
        }

        // ── fixtures ─────────────────────────────────────────────────────

        private void WriteManifest(params (string key, string version)[] rows)
        {
            var manifest = new Dictionary<string, ManifestEntry>();
            foreach (var (key, version) in rows)
            {
                manifest[key] = new ManifestEntry
                {
                    Game = key.Substring("gamedata/".Length),
                    Version = version,
                    FileSha256 = "ab12cd34",
                    DownloadedAtUnix = 1_700_000_000,
                };
            }
            File.WriteAllText(_manifestPath, JsonSerializer.Serialize(manifest, JsonDefaults.Indented));
        }

        private Dictionary<string, ManifestEntry> ReadManifest() =>
            JsonSerializer.Deserialize<Dictionary<string, ManifestEntry>>(
                File.ReadAllText(_manifestPath), JsonDefaults.Options);

        private static JsonObject Bundle(string game) => new JsonObject
        {
            ["bundle_version"] = 2,
            ["dialog_graph"] = new JsonObject(),
            ["hash_to_dialogs"] = new JsonObject(),
            ["npc_names"] = new JsonObject(),
            ["talk_index"] = new JsonObject(),
            ["quest_info"] = new JsonObject(),
            ["textmap_en"] = new JsonObject { ["4242"] = "Good morning, Wise." },
            ["extension"] = new JsonObject { ["game"] = game },
        };
    }
}
