// ─────────────────────────────────────────────────────────────────────────────
//  AesCtrProtectionTests.cs
//  ---------------------------------------------------------------------------
//  Covers the low-level AES-CTR per-block crypt primitive used by v3 .gisub
//  files. These are pure-function tests — no file IO, no machine
//  fingerprint — so they run cleanly in any CI environment.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Security.Cryptography;
using GI_Subtitles.Services.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class AesCtrProtectionTests
    {
        private static byte[] TestKey()
        {
            // Deterministic 32-byte key so failures are reproducible.
            var k = new byte[32];
            for (int i = 0; i < k.Length; i++) k[i] = (byte)(i * 7 + 13);
            return k;
        }

        private static byte[] TestNonce()
        {
            var n = new byte[16];
            for (int i = 0; i < n.Length; i++) n[i] = (byte)(0xA0 ^ i);
            return n;
        }

        [TestMethod]
        [TestCategory("Security")]
        public void RoundTrip_SingleBlock_FullSize()
        {
            var key = TestKey();
            var nonce = TestNonce();
            var plain = new byte[4096];
            new Random(42).NextBytes(plain);

            var ct = new byte[plain.Length];
            var pt = new byte[plain.Length];

            AesCtrProtection.EncryptBlock(plain, ct, key, nonce, blockIndex: 0);
            AesCtrProtection.DecryptBlock(ct, pt, key, nonce, blockIndex: 0);

            CollectionAssert.AreEqual(plain, pt);
            // Ciphertext should NOT equal plaintext (trivially).
            Assert.IsFalse(BytesEqual(plain, ct));
        }

        [TestMethod]
        [TestCategory("Security")]
        public void RoundTrip_MultipleBlocks_DifferentIndices_ProduceDifferentCiphertext()
        {
            var key = TestKey();
            var nonce = TestNonce();
            var plain = new byte[4096];
            for (int i = 0; i < plain.Length; i++) plain[i] = 0xA5; // same plaintext everywhere

            var ct0 = new byte[plain.Length];
            var ct1 = new byte[plain.Length];
            var ct2 = new byte[plain.Length];

            AesCtrProtection.EncryptBlock(plain, ct0, key, nonce, blockIndex: 0);
            AesCtrProtection.EncryptBlock(plain, ct1, key, nonce, blockIndex: 1);
            AesCtrProtection.EncryptBlock(plain, ct2, key, nonce, blockIndex: 2);

            // Block index in the counter must change the keystream.
            Assert.IsFalse(BytesEqual(ct0, ct1), "block 0 and block 1 ciphertexts must differ");
            Assert.IsFalse(BytesEqual(ct0, ct2), "block 0 and block 2 ciphertexts must differ");
            Assert.IsFalse(BytesEqual(ct1, ct2), "block 1 and block 2 ciphertexts must differ");
        }

        [TestMethod]
        [TestCategory("Security")]
        public void RoundTrip_LastBlockNotAligned()
        {
            // Ragged lengths exercise the "input not a multiple of 16" path:
            // the keystream is still generated in whole AES blocks but the
            // XOR only covers the requested byte count.
            int[] sizes = { 1, 15, 16, 17, 31, 63, 100, 513, 1024, 4095 };
            var key = TestKey();
            var nonce = TestNonce();

            foreach (int size in sizes)
            {
                var plain = new byte[size];
                for (int i = 0; i < size; i++) plain[i] = (byte)(i * 31);

                var ct = new byte[size];
                var pt = new byte[size];

                AesCtrProtection.EncryptBlock(plain, ct, key, nonce, blockIndex: 5);
                AesCtrProtection.DecryptBlock(ct, pt, key, nonce, blockIndex: 5);

                CollectionAssert.AreEqual(plain, pt, "round-trip failed for size " + size);
            }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void WrongKey_GarbledOutput_NoCrash()
        {
            var key1 = TestKey();
            var key2 = TestKey();
            key2[0] ^= 0x01; // flip one bit

            var nonce = TestNonce();
            var plain = new byte[1024];
            new Random(7).NextBytes(plain);

            var ct = new byte[plain.Length];
            var pt = new byte[plain.Length];

            AesCtrProtection.EncryptBlock(plain, ct, key1, nonce, blockIndex: 0);
            AesCtrProtection.DecryptBlock(ct, pt, key2, nonce, blockIndex: 0);

            // Must not crash and must NOT produce the original plaintext.
            Assert.IsFalse(BytesEqual(plain, pt), "wrong key must not decrypt correctly");
        }

        [TestMethod]
        [TestCategory("Security")]
        public void WrongNonce_GarbledOutput()
        {
            var key = TestKey();
            var nonce1 = TestNonce();
            var nonce2 = TestNonce();
            nonce2[15] ^= 0x01;

            var plain = new byte[64];
            for (int i = 0; i < plain.Length; i++) plain[i] = (byte)i;

            var ct = new byte[plain.Length];
            var pt = new byte[plain.Length];

            AesCtrProtection.EncryptBlock(plain, ct, key, nonce1, 0);
            AesCtrProtection.DecryptBlock(ct, pt, key, nonce2, 0);

            Assert.IsFalse(BytesEqual(plain, pt));
        }

        [TestMethod]
        [TestCategory("Security")]
        public void NonceUniqueness_SamePlaintext_DifferentBlocks_DifferentCiphertext()
        {
            // Uniqueness property of (nonce, blockIndex) — the core security
            // requirement for CTR mode. Even identical plaintext must encrypt
            // to different ciphertext across blocks, and across files (which
            // we simulate with different nonces).
            var key = TestKey();
            var nonce = TestNonce();
            var plain = new byte[256];
            // All zeros so ciphertext = keystream.

            var ctA0 = new byte[plain.Length];
            var ctA1 = new byte[plain.Length];
            AesCtrProtection.EncryptBlock(plain, ctA0, key, nonce, 0);
            AesCtrProtection.EncryptBlock(plain, ctA1, key, nonce, 1);
            Assert.IsFalse(BytesEqual(ctA0, ctA1));

            // Different "file" (different nonce) — same block index — must
            // still differ.
            var nonce2 = TestNonce();
            nonce2[0] ^= 0x80;
            var ctB0 = new byte[plain.Length];
            AesCtrProtection.EncryptBlock(plain, ctB0, key, nonce2, 0);
            Assert.IsFalse(BytesEqual(ctA0, ctB0));
        }

        [TestMethod]
        [TestCategory("Security")]
        public void EmptyInput_NoOp()
        {
            var key = TestKey();
            var nonce = TestNonce();
            AesCtrProtection.EncryptBlock(Array.Empty<byte>(), Array.Empty<byte>(), key, nonce, 0);
            // No throw — that's the whole assertion.
        }

        [TestMethod]
        [TestCategory("Security")]
        public void InvalidKeyLength_Throws()
        {
            var badKey = new byte[20];
            Assert.ThrowsException<ArgumentException>(() =>
                AesCtrProtection.EncryptBlock(new byte[16], new byte[16], badKey, TestNonce(), 0));
        }

        [TestMethod]
        [TestCategory("Security")]
        public void InvalidNonceLength_Throws()
        {
            var key = TestKey();
            var badNonce = new byte[8];
            Assert.ThrowsException<ArgumentException>(() =>
                AesCtrProtection.EncryptBlock(new byte[16], new byte[16], key, badNonce, 0));
        }

        [TestMethod]
        [TestCategory("Security")]
        public void OutputSpan_TooSmall_Throws()
        {
            var key = TestKey();
            var nonce = TestNonce();
            Assert.ThrowsException<ArgumentException>(() =>
                AesCtrProtection.EncryptBlock(new byte[32], new byte[16], key, nonce, 0));
        }

        [TestMethod]
        [TestCategory("Security")]
        public void ReusableEncryptor_MatchesDisposable()
        {
            // CryptBlockWith (reusable encryptor) must produce byte-identical
            // output to CryptBlock (self-managed encryptor).
            var key = TestKey();
            var nonce = TestNonce();
            var plain = new byte[2048];
            new Random(99).NextBytes(plain);

            var ctFresh = new byte[plain.Length];
            var ctReuse = new byte[plain.Length];

            AesCtrProtection.EncryptBlock(plain, ctFresh, key, nonce, 3);
            using (var enc = AesCtrProtection.CreateReusableEncryptor(key))
            {
                AesCtrProtection.CryptBlockWith(enc.Encryptor, plain, ctReuse, nonce, 3);
            }

            CollectionAssert.AreEqual(ctFresh, ctReuse);
        }

        [TestMethod]
        [TestCategory("Security")]
        public void KeystreamIsSymmetric()
        {
            // Encrypt(Encrypt(x)) = x under the same key+nonce+index. This
            // proves CTR's self-inverse property — the core guarantee.
            var key = TestKey();
            var nonce = TestNonce();
            var plain = new byte[1000];
            new Random(123).NextBytes(plain);

            var once = new byte[plain.Length];
            var twice = new byte[plain.Length];

            AesCtrProtection.EncryptBlock(plain, once, key, nonce, 42);
            AesCtrProtection.EncryptBlock(once, twice, key, nonce, 42);

            CollectionAssert.AreEqual(plain, twice);
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
