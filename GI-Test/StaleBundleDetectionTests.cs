// ─────────────────────────────────────────────────────────────────────────────
//  StaleBundleDetectionTests.cs
//  ---------------------------------------------------------------------------
//  GlitchTip 7884595: a gamedata bundle encrypted under another device key
//  fails HMAC on every launch and nothing re-downloads it. These tests pin how
//  that failure is told apart from other bundle problems, which is what lets
//  SettingsWindow wipe and re-sync only where re-downloading actually helps.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GI_Subtitles.Services.Security;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class StaleBundleDetectionTests
    {
        // Two device keys: what wrote the bundle, and what this launch derives.
        private const int OldDeviceSeed = 0x51A1E;
        private const int ThisDeviceSeed = 0x0DD1;

        private string _dir;
        private string _textMapEnPath;

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "StaleBundleDetectionTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _textMapEnPath = Path.Combine(_dir, "TextMapEN.json");
            File.WriteAllText(_textMapEnPath, "{}");
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best-effort */ }
        }

        private string MetaPath => Path.Combine(_dir, "BundleMeta.json");

        private static FileProtectionHelper Device(int seed) =>
            new FileProtectionHelper(TestProtection.Create(seed));

        private static JsonObject Meta(string game) => new JsonObject
        {
            ["bundle_version"] = 2,
            ["extension"] = new JsonObject { ["game"] = game },
        };

        // ── exception classification ─────────────────────────────────────

        [TestMethod]
        public void IsCryptographic_HmacFailure_IsRecognised()
        {
            Assert.IsTrue(CryptoFailure.IsCryptographic(
                new CryptographicException("HMAC verification failed")));
        }

        [TestMethod]
        public void IsCryptographic_HmacFailureDeepInTheChain_IsRecognised()
        {
            var wrapped = new InvalidOperationException("load failed",
                new IOException("read failed", new CryptographicException("HMAC verification failed")));

            Assert.IsTrue(CryptoFailure.IsCryptographic(wrapped));
        }

        [TestMethod]
        public void IsCryptographic_OtherFailures_AreNot()
        {
            Assert.IsFalse(CryptoFailure.IsCryptographic(null));
            Assert.IsFalse(CryptoFailure.IsCryptographic(new JsonException("bad json")));
            Assert.IsFalse(CryptoFailure.IsCryptographic(new InvalidDataException("File too short")));
            Assert.IsFalse(CryptoFailure.IsCryptographic(
                new InvalidOperationException("no device secret", new IOException("disk"))));
        }

        // ── what Load reports ────────────────────────────────────────────

        [TestMethod]
        public void Load_BundleMetaFromAnotherDevice_ReportsStaleMachineBoundFile()
        {
            Device(OldDeviceSeed).SaveProtectedJson(MetaPath, Meta("genshin"));
            var engine = new NormalizedDialogueContext(expectedGame: "genshin");

            engine.Load(_dir, _textMapEnPath, null, Device(ThisDeviceSeed));

            Assert.IsFalse(engine.IsLoaded);
            Assert.AreEqual(DialoguePrepareFailure.StaleMachineBoundFile, engine.LastPrepareFailure);
        }

        [TestMethod]
        public void Load_BundleMetaForAnotherGame_ReportsOther()
        {
            var device = Device(ThisDeviceSeed);
            device.SaveProtectedJson(MetaPath, Meta("starrail"));
            var engine = new NormalizedDialogueContext(expectedGame: "genshin");

            engine.Load(_dir, _textMapEnPath, null, device);

            Assert.IsFalse(engine.IsLoaded);
            Assert.AreEqual(DialoguePrepareFailure.Other, engine.LastPrepareFailure,
                "Re-downloading a mismatched bundle fetches the same mismatch; it must not self-heal.");
        }

        [TestMethod]
        public void Load_TruncatedBundleMeta_ReportsOther()
        {
            File.WriteAllText(MetaPath, "{ \"bundle_version\": 2, \"extension\": ");
            var engine = new NormalizedDialogueContext(expectedGame: "genshin");

            engine.Load(_dir, _textMapEnPath, null, Device(ThisDeviceSeed));

            Assert.IsFalse(engine.IsLoaded);
            Assert.AreEqual(DialoguePrepareFailure.Other, engine.LastPrepareFailure);
        }

        [TestMethod]
        public void Load_WithoutBundleMeta_ReportsNone()
        {
            var engine = new NormalizedDialogueContext(expectedGame: "genshin");

            engine.Load(_dir, _textMapEnPath, null, Device(ThisDeviceSeed));

            Assert.IsTrue(engine.IsLoaded, "v1 bundles carry no meta and must still load.");
            Assert.AreEqual(DialoguePrepareFailure.None, engine.LastPrepareFailure);
        }

        [TestMethod]
        public void Load_AfterTheBundleIsRewrittenHere_ClearsTheEarlierFailure()
        {
            Device(OldDeviceSeed).SaveProtectedJson(MetaPath, Meta("genshin"));
            var engine = new NormalizedDialogueContext(expectedGame: "genshin");
            engine.Load(_dir, _textMapEnPath, null, Device(ThisDeviceSeed));
            Assert.AreEqual(DialoguePrepareFailure.StaleMachineBoundFile, engine.LastPrepareFailure);

            // What the self-heal produces: the same bundle, re-encrypted under this device's key.
            var device = Device(ThisDeviceSeed);
            device.SaveProtectedJson(MetaPath, Meta("genshin"));
            engine.Load(_dir, _textMapEnPath, null, device);

            Assert.IsTrue(engine.IsLoaded);
            Assert.AreEqual(DialoguePrepareFailure.None, engine.LastPrepareFailure);
        }
    }
}
