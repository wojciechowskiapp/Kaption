// The managed PBKDF2 fallback for machines whose Windows key derivation fails (KAPTION-DESKTOP-3).
// It must give the OS call's exact bytes, or the files a player already has stop decrypting.

using System;
using System.IO;
using System.Security.Cryptography;
using GI_Subtitles.Services.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class Pbkdf2Sha256Tests
    {
        private static byte[] Bytes(int length, int seed)
        {
            var b = new byte[length];
            new Random(seed).NextBytes(b);
            return b;
        }

        private static void AssertMatchesOs(byte[] password, byte[] salt, int iterations, int length)
        {
            byte[] expected = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, length);
            byte[] actual = Pbkdf2Sha256.DeriveManaged(password, salt, iterations, length);
            CollectionAssert.AreEqual(expected, actual,
                $"password {password.Length} B, salt {salt.Length} B, {iterations} iterations, {length} B out");
        }

        [TestMethod]
        [TestCategory("Security")]
        public void Managed_MatchesOs_Rfc6070ShapesOnSha256()
        {
            // RFC 6070's inputs, with SHA-256 as the PRF and checked against Windows instead of SHA-1 constants.
            byte[] password = "password"u8.ToArray();
            byte[] salt = "salt"u8.ToArray();
            AssertMatchesOs(password, salt, 1, 32);
            AssertMatchesOs(password, salt, 2, 32);
            AssertMatchesOs(password, salt, 4096, 32);
            AssertMatchesOs("passwordPASSWORDpassword"u8.ToArray(),
                "saltSALTsaltSALTsaltSALTsaltSALTsalt"u8.ToArray(), 4096, 40);
            AssertMatchesOs("pass\0word"u8.ToArray(), "sa\0lt"u8.ToArray(), 4096, 16);
        }

        [TestMethod]
        [TestCategory("Security")]
        public void Managed_MatchesOs_PasswordAroundTheHmacBlockSize()
        {
            // HMAC hashes a key longer than its 64-byte block first; ours is exactly 64 (secret 32 + fingerprint 32).
            byte[] salt = Bytes(32, 3);
            AssertMatchesOs(Bytes(63, 1), salt, 1000, 32);
            AssertMatchesOs(Bytes(64, 1), salt, 1000, 32);
            AssertMatchesOs(Bytes(65, 1), salt, 1000, 32);
            AssertMatchesOs(Bytes(200, 1), salt, 1000, 32);
        }

        [TestMethod]
        [TestCategory("Security")]
        public void Managed_MatchesOs_RealKeyShapeAtServerIterations()
        {
            // The production call: password = device secret || fingerprint (64 B), salt = fingerprint (32 B) for
            // the AES key and fingerprint || "hmac-salt-v2" (44 B) for the HMAC key, the Worker's 100,000 rounds.
            byte[] password = Bytes(64, 11);
            byte[] fingerprint = SHA256.HashData(Bytes(40, 12));
            byte[] hmacSalt = new byte[44];
            fingerprint.CopyTo(hmacSalt, 0);
            "hmac-salt-v2"u8.CopyTo(hmacSalt.AsSpan(32));

            AssertMatchesOs(password, fingerprint, 100_000, 32);
            AssertMatchesOs(password, hmacSalt, 100_000, 32);
            AssertMatchesOs(password, fingerprint, 1000, 100); // several output blocks, a short last one
        }

        [TestMethod]
        [TestCategory("Security")]
        public void Derive_OsCallThrows_FallsBackAndKeepsUsingManaged()
        {
            int nativeCalls = 0;
            var kdf = new Pbkdf2Sha256((_, _, _, _) =>
            {
                nativeCalls++;
                throw new CryptographicException("Unknown error (0xc1000008)");
            });
            byte[] password = Bytes(64, 21);
            byte[] salt = Bytes(32, 22);

            byte[] first = kdf.Derive(password, salt, 1000, 32);
            byte[] second = kdf.Derive(password, salt, 1000, 32);

            byte[] expected = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1000, HashAlgorithmName.SHA256, 32);
            CollectionAssert.AreEqual(expected, first);
            CollectionAssert.AreEqual(expected, second);
            Assert.AreEqual(1, nativeCalls, "after one failure the OS call is not tried again");
            Assert.IsTrue(kdf.UsesManagedFallback);
        }

        [TestMethod]
        [TestCategory("Security")]
        public void Derive_OsCallThrowsSomethingElse_Propagates()
        {
            // Only a CryptographicException means the OS derivation is broken; anything else is a real bug.
            var kdf = new Pbkdf2Sha256((_, _, _, _) => throw new ArgumentException("bad input"));
            Assert.ThrowsExactly<ArgumentException>(() => kdf.Derive(Bytes(64, 1), Bytes(32, 2), 1000, 32));
            Assert.IsFalse(kdf.UsesManagedFallback);
        }

        [TestMethod]
        [TestCategory("Security")]
        public void Service_OsDerivationBroken_ReadsFilesWrittenWithTheOsKeys()
        {
            // A pack written while Windows' derivation worked must still open once the fallback takes over,
            // and a pack written under the fallback must open on a healthy machine.
            string dir = Path.Combine(Path.GetTempPath(), "kaption-pbkdf2-tests-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                var activation = new ActivationData
                {
                    DeviceFileProtectionSecret = Bytes(32, 31),
                    DeviceFileProtectionIssuedAtUnixMs = 1,
                    DeviceFileProtectionSchemeVersion = 1,
                    DeviceFileProtectionPbkdf2Iterations = 100_000,
                };
                int nativeCalls = 0;
                var broken = new Pbkdf2Sha256((_, _, _, _) =>
                {
                    nativeCalls++;
                    throw new CryptographicException("Unknown error (0xc1000008)");
                });
                var healthySvc = new ServerKeyFileProtectionService(() => activation);
                var brokenSvc = new ServerKeyFileProtectionService(() => activation, broken);
                byte[] plaintext = Bytes(4096 + 7, 32);

                string writtenByOs = Path.Combine(dir, "TextMapPL.gisub");
                healthySvc.EncryptBytes(plaintext, writtenByOs);
                using (var ms = brokenSvc.DecryptToStream(writtenByOs))
                    CollectionAssert.AreEqual(plaintext, ms.ToArray());

                string writtenByFallback = Path.Combine(dir, "TextMapPL-fallback.gisub");
                using (var sink = brokenSvc.OpenEncryptStream(writtenByFallback))
                {
                    sink.Write(plaintext, 0, plaintext.Length);
                    sink.Complete();
                }
                using (var ms = healthySvc.DecryptToStream(writtenByFallback))
                    CollectionAssert.AreEqual(plaintext, ms.ToArray());

                Assert.AreEqual(1, nativeCalls, "both keys derive in managed code after the first failure");
                Assert.IsTrue(broken.UsesManagedFallback);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
