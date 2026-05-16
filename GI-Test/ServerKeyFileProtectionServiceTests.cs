// ─────────────────────────────────────────────────────────────────────────────
//  ServerKeyFileProtectionServiceTests.cs
//  ---------------------------------------------------------------------------
//  Smoke tests for the ServerKey path of the file-protection stack:
//
//   * Encrypt / decrypt round-trip on a v2 (server-key) file.
//   * HMAC tampering produces a CryptographicException, never a silent
//     plaintext corruption.
//   * Reading a v1 (legacy AppSecret) file is no longer supported — the
//     class and its embedded AppSecret are gone; legacy files are deleted
//     by App.WipeLegacyProtectedCacheFiles on first launch and re-downloaded.
//   * Throws InvalidOperationException when called without a provisioned
//     activation secret. Surfacing it loudly catches misconfiguration
//     instead of silently corrupting cache files.
//
//  These tests stamp a deterministic ActivationData so they don't require
//  a real activation.dat on disk. Machine fingerprint comes from the
//  same MachineFingerprint helper that production uses, so derivation is
//  consistent within a single test run on a given machine.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.IO;
using System.Security.Cryptography;
using GI_Subtitles.Services.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class ServerKeyFileProtectionServiceTests
    {
        private string _scratchDir;

        [TestInitialize]
        public void SetUp()
        {
            _scratchDir = Path.Combine(
                Path.GetTempPath(),
                "kaption-skp-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_scratchDir);
        }

        [TestCleanup]
        public void TearDown()
        {
            try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
            catch { /* best effort */ }
        }

        private static ActivationData FakeActivation(byte[] secret = null, int iterations = 100_000)
        {
            byte[] s = secret ?? new byte[32];
            if (secret == null)
            {
                // Deterministic per-test key so a failure is reproducible.
                for (int i = 0; i < s.Length; i++) s[i] = (byte)(i * 11 + 7);
            }
            return new ActivationData
            {
                UserId = "test-user",
                Email = "test@example.invalid",
                ActivationId = "test-activation",
                DeviceSessionJwt = "fake-jwt",
                ExpiresAtUnix = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(),
                StoredAtUtc = DateTime.UtcNow,
                DeviceFileProtectionSecret = s,
                DeviceFileProtectionIssuedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DeviceFileProtectionExpiresAtUnixMs =
                    DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds(),
                DeviceFileProtectionSchemeVersion = 1,
                DeviceFileProtectionPbkdf2Iterations = iterations,
            };
        }

        [TestMethod]
        [TestCategory("Security")]
        public void RoundTrip_V2_Succeeds()
        {
            var activation = FakeActivation();
            var svc = new ServerKeyFileProtectionService(() => activation);

            byte[] plaintext = new byte[1024];
            new Random(1234).NextBytes(plaintext);

            string path = Path.Combine(_scratchDir, "TestPack.gisub");
            svc.EncryptBytes(plaintext, path);

            Assert.IsTrue(File.Exists(path), "encrypted output written");
            Assert.AreEqual(
                ProtectedFileFormat.HeaderVersion2ServerKey,
                ProtectedFileFormat.PeekFormatVersion(path),
                "header version should be v2 (server-key)");

            using (var ms = svc.DecryptToStream(path))
            {
                byte[] decrypted = ms.ToArray();
                CollectionAssert.AreEqual(plaintext, decrypted, "round-trip plaintext mismatch");
            }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void TamperedCiphertext_FailsHmac()
        {
            var activation = FakeActivation();
            var svc = new ServerKeyFileProtectionService(() => activation);

            byte[] plaintext = new byte[512];
            new Random(7).NextBytes(plaintext);

            string path = Path.Combine(_scratchDir, "Tampered.gisub");
            svc.EncryptBytes(plaintext, path);

            // Flip a single byte well inside the ciphertext (past the 54-byte header).
            byte[] data = File.ReadAllBytes(path);
            int target = ProtectedFileFormat.PayloadOffset + 4;
            data[target] ^= 0x01;
            File.WriteAllBytes(path, data);

            try
            {
                svc.DecryptToStream(path);
                Assert.Fail("Expected CryptographicException on tampered ciphertext");
            }
            catch (CryptographicException)
            {
                // Expected — Encrypt-then-MAC verifies before decrypt.
            }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void NoActivationSecret_ThrowsInvalidOperation()
        {
            // Activation present but no file-protection secret yet — the
            // factory should never have picked us in this case. Make sure
            // we surface loudly instead of silently producing garbage.
            var activation = new ActivationData
            {
                UserId = "test-user",
                Email = "test@example.invalid",
                DeviceSessionJwt = "fake",
                ExpiresAtUnix = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(),
                StoredAtUtc = DateTime.UtcNow,
                // intentionally no DeviceFileProtectionSecret
            };
            var svc = new ServerKeyFileProtectionService(() => activation);

            string path = Path.Combine(_scratchDir, "Unprovisioned.gisub");
            try
            {
                svc.EncryptBytes(new byte[16], path);
                Assert.Fail("Expected InvalidOperationException when activation has no file-protection secret");
            }
            catch (InvalidOperationException)
            {
                // Expected.
            }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void StreamingEncrypt_RoundTrip_Succeeds()
        {
            // Verify OpenEncryptStream + EncryptingWriteStream produce a v2
            // file that round-trips cleanly. Streaming is the hot path for
            // DictionarySync once Phase B flips the factory.
            var activation = FakeActivation();
            var svc = new ServerKeyFileProtectionService(() => activation);

            byte[] plaintext = new byte[64 * 1024 + 137]; // crosses block + odd tail
            new Random(2025).NextBytes(plaintext);

            string path = Path.Combine(_scratchDir, "Streamed.gisub");
            using (var sink = svc.OpenEncryptStream(path))
            {
                sink.Write(plaintext, 0, plaintext.Length);
                sink.Complete();
            }

            Assert.IsTrue(File.Exists(path));
            Assert.AreEqual(
                ProtectedFileFormat.HeaderVersion2ServerKey,
                ProtectedFileFormat.PeekFormatVersion(path));
            Assert.IsFalse(File.Exists(path + ".tmp"), "tmp should have been renamed away");

            using (var ms = svc.DecryptToStream(path))
            {
                CollectionAssert.AreEqual(plaintext, ms.ToArray());
            }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void StreamingEncrypt_AbortPath_ReapsTmp()
        {
            // Disposing without Complete() must delete the .tmp and leave
            // no .gisub at the final path. This is the contract that lets
            // callers use plain `using` blocks safely on the exception path.
            var activation = FakeActivation();
            var svc = new ServerKeyFileProtectionService(() => activation);

            string path = Path.Combine(_scratchDir, "Aborted.gisub");
            using (var sink = svc.OpenEncryptStream(path))
            {
                sink.Write(new byte[1024], 0, 1024);
                // Intentionally do NOT call Complete().
            }

            Assert.IsFalse(File.Exists(path), "no final file should be produced on abort");
            Assert.IsFalse(File.Exists(path + ".tmp"), "tmp should be reaped on abort");
        }

        [TestMethod]
        [TestCategory("Security")]
        public void V3_RoundTrip_Succeeds()
        {
            // ServerKey owns the v3 (AES-CTR + per-block HMAC) path now —
            // delegates to AesCtrFileProtection with the same ServerKey-
            // derived encryption + HMAC keys it uses for v2. Verify that an
            // EncryptStreamToV3 → OpenDecryptStreamV3 round-trip yields the
            // original bytes, and that the file is recognised as v3 on disk.
            var activation = FakeActivation();
            var svc = new ServerKeyFileProtectionService(() => activation);

            byte[] plaintext = new byte[16 * 1024 + 91]; // crosses one block + odd tail
            new Random(2026).NextBytes(plaintext);

            string path = Path.Combine(_scratchDir, "v3-roundtrip.gisub");
            using (var ms = new MemoryStream(plaintext, writable: false))
            {
                svc.EncryptStreamToV3(ms, plaintext.Length, path);
            }

            Assert.IsTrue(File.Exists(path));
            Assert.IsTrue(
                ProtectedFileFormatV3.HasV3Header(path),
                "expected v3 header on encrypted output");

            using (var stream = svc.OpenDecryptStreamV3(path))
            using (var dest = new MemoryStream())
            {
                stream.CopyTo(dest);
                CollectionAssert.AreEqual(plaintext, dest.ToArray(), "v3 round-trip mismatch");
            }
        }
    }
}
