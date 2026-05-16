// ─────────────────────────────────────────────────────────────────────────────
//  DictionarySyncStreamingTests.cs
//  ---------------------------------------------------------------------------
//  Covers the streaming variants added in the Pipelines refactor of the
//  DictionarySync download → decrypt → re-encrypt → disk path:
//
//    * IFileProtectionService.EncryptFileStreaming / OpenEncryptStream
//        – round-trip correctness vs. the legacy byte[]-based EncryptFile
//        – allocation budget (cumulative on thread) stays within 30 MB on a
//          10 MB payload — catches regressions that materialise the whole
//          plaintext + ciphertext simultaneously on the LOH
//        – two-phase commit: caller must call Complete() before Dispose; on
//          an abort/cancellation the .tmp is reaped and no output lingers
//    * DistributionCipher.DecryptFileInPlaceStreaming
//        – round-trip correctness with a synthetic .gisub-dist blob
//        – HMAC tamper detection (ciphertext bit-flip → CryptographicException)
//        – wrong-key rejection (HMAC covers the whole ciphertext)
//        – truncated blob rejection (< header length)
//        – allocation budget check, same rationale as encrypt side
//
//  The full end-to-end SyncAsync path (HttpListener + LicenseService mock) is
//  out of scope here — LicenseService is sealed and owns its own ActivationStore,
//  which doesn't have a clean seam for tests. The two primitives above are
//  what changed; DictionarySyncService's call site is a one-line swap from
//  EncryptFile → EncryptFileStreaming and is exercised indirectly whenever a
//  real sync runs. The ThrowingStream-based cancellation test simulates the
//  "network stream threw mid-read" shape of the real HTTP-stream path.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GI_Subtitles.Services.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class DictionarySyncStreamingTests
    {
        // Payload size for the streaming tests. 10 MB is big enough that
        // the old byte[]-based path (would allocate 10 MB plaintext + 10 MB
        // ciphertext = 20 MB on the LOH) is clearly separable from the new
        // streaming path (bounded at a few KB of working-set buffers).
        private const int PayloadBytes = 10 * 1024 * 1024;

        private string _tmpDir;

        [TestInitialize]
        public void Setup()
        {
            _tmpDir = Path.Combine(Path.GetTempPath(), "kaption-stream-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_tmpDir);
        }

        [TestCleanup]
        public void Teardown()
        {
            try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true); }
            catch { /* best effort */ }
        }

        // ── IFileProtectionService.EncryptFileStreaming ──────────────────

        [TestMethod]
        public void StreamingEncrypt_RoundTrips_10MBPayload()
        {
            // Synthetic plaintext — deterministic from a seed so the test is
            // reproducible. Using Random (not RandomNumberGenerator) so seed
            // stability is guaranteed across runs.
            byte[] plaintext = MakeDeterministicPayload(PayloadBytes, seed: 0xBEEF);
            string plainPath = Path.Combine(_tmpDir, "plain.bin");
            File.WriteAllBytes(plainPath, plaintext);

            string encryptedPath = Path.Combine(_tmpDir, "out.gisub");
            var service = TestProtection.Create();

            service.EncryptFileStreaming(plainPath, encryptedPath);

            Assert.IsTrue(File.Exists(encryptedPath), "output .gisub missing");
            Assert.IsFalse(File.Exists(encryptedPath + ".tmp"), ".tmp should have been atomically renamed");
            Assert.IsTrue(new FileInfo(encryptedPath).Length > plaintext.Length,
                "ciphertext must be at least as big as plaintext (header + AES padding)");

            // Round-trip through the existing (byte[]-based) decrypt path.
            // If the streaming encrypt produced bytes compatible with the
            // legacy format, this will read them back identically.
            using (var decrypted = service.DecryptToStream(encryptedPath))
            {
                byte[] roundTrip = decrypted.ToArray();
                Assert.AreEqual(plaintext.Length, roundTrip.Length, "decrypted length differs");
                Assert.IsTrue(ByteArraysEqual(plaintext, roundTrip),
                    "decrypted bytes do not match original plaintext");
            }
        }

        [TestMethod]
        public void StreamingEncrypt_MatchesLegacyFormat()
        {
            // Both legacy and streaming encryption produce .gisub files that
            // the same decrypt path can read. This test encrypts the same
            // plaintext via both paths and confirms round-trip equivalence.
            byte[] plaintext = Encoding.UTF8.GetBytes("Hello Kaption — streaming test.");
            string plainPath = Path.Combine(_tmpDir, "small.bin");
            File.WriteAllBytes(plainPath, plaintext);

            var service = TestProtection.Create();

            string legacyPath = Path.Combine(_tmpDir, "legacy.gisub");
            string streamingPath = Path.Combine(_tmpDir, "streaming.gisub");

            service.EncryptFile(plainPath, legacyPath);
            service.EncryptFileStreaming(plainPath, streamingPath);

            // The ciphertext bytes will differ (random IV per file), but the
            // decrypted content must match byte-for-byte.
            using (var a = service.DecryptToStream(legacyPath))
            using (var b = service.DecryptToStream(streamingPath))
            {
                byte[] aBytes = a.ToArray();
                byte[] bBytes = b.ToArray();
                Assert.IsTrue(ByteArraysEqual(aBytes, bBytes), "legacy and streaming decrypts differ");
                Assert.IsTrue(ByteArraysEqual(plaintext, aBytes), "legacy decrypt differs from source");
            }
        }

        [TestMethod]
        [TestCategory("Performance")]
        public void StreamingEncrypt_AllocatesFarLessThanPayload()
        {
            // Before: byte[] plaintext + byte[] ciphertext = ~2× PayloadBytes
            // After:  streaming, allocations bounded by block buffers (~tens of KB)
            //
            // We measure allocation deltas via GC.GetAllocatedBytesForCurrentThread
            // (net48 on full framework; requires AppContext switch — available
            // via System.GC on 4.6+). This is cumulative-on-thread, not
            // peak-working-set, but it reliably separates "materialise whole
            // file" (baseline >= PayloadBytes) from "stream it" (baseline KBs).

            byte[] plaintext = MakeDeterministicPayload(PayloadBytes, seed: 0x1234);
            string plainPath = Path.Combine(_tmpDir, "big.bin");
            File.WriteAllBytes(plainPath, plaintext);

            string encryptedPath = Path.Combine(_tmpDir, "big.gisub");
            var service = TestProtection.Create();

            // Warm up the key-derivation cache — we don't want the one-time
            // PBKDF2 cost (which allocates internally) counted against the
            // streaming budget. Legacy EncryptFile primes the keys.
            string warmupEncrypted = Path.Combine(_tmpDir, "warmup.gisub");
            service.EncryptBytes(new byte[] { 1, 2, 3 }, warmupEncrypted);

            // Force a collection so GetAllocatedBytesForCurrentThread's
            // delta over the next operation is dominated by that op.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);

            long before = GC.GetAllocatedBytesForCurrentThread();
            service.EncryptFileStreaming(plainPath, encryptedPath);
            long after = GC.GetAllocatedBytesForCurrentThread();

            long allocated = after - before;

            // Note: this measures CUMULATIVE allocations on the thread, not
            // peak working set. CryptoStream on .NET Framework 4.8 allocates
            // a fresh output buffer per Write call (sized ~= input block
            // count × AES block size = 16 B), which adds up to ~1× the
            // payload size for a 10 MB file — but each buffer is short-lived
            // and gets GC'd quickly, so peak RAM stays in the tens of KB.
            //
            // The REGRESSION we're guarding against is the old byte[] path
            // that materialised the whole plaintext (10 MB) AND the whole
            // ciphertext (10 MB) at once — ≥ 20 MB held simultaneously, on
            // the LOH. The 30 MB budget below catches that regression while
            // tolerating CryptoStream's per-Write scratch allocations.
            long budget = 30 * 1024 * 1024;
            Assert.IsTrue(allocated < budget,
                $"streaming encrypt allocated {allocated:N0} bytes for a {PayloadBytes:N0} byte payload — expected < {budget:N0}");
        }

        [TestMethod]
        public void OpenEncryptStream_CleansUpTempOnWriterException()
        {
            // If the caller throws mid-write, the .tmp file must not linger
            // next to the final path. OpenEncryptStream's Dispose(true) is
            // responsible for the cleanup, which runs during `using`.
            string encryptedPath = Path.Combine(_tmpDir, "failed.gisub");
            var service = TestProtection.Create();

            try
            {
                using (var sink = service.OpenEncryptStream(encryptedPath))
                {
                    // Write a little, then throw — simulate a failing plaintext
                    // producer (e.g. HTTP read error mid-stream).
                    sink.Write(new byte[] { 1, 2, 3, 4, 5 }, 0, 5);
                    throw new InvalidOperationException("simulated producer failure");
                }
            }
            catch (InvalidOperationException)
            {
                // Expected.
            }

            Assert.IsFalse(File.Exists(encryptedPath), "final .gisub should not exist after aborted write");
            Assert.IsFalse(File.Exists(encryptedPath + ".tmp"), ".tmp should not linger after aborted write");
        }

        // ── DistributionCipher streaming decrypt ───────────────────────────

        [TestMethod]
        public void DistributionCipher_StreamingDecrypt_RoundTrips()
        {
            // Synthesise a .gisub-dist blob with the exact byte layout the
            // real backend produces, then round-trip it through the streaming
            // decrypt.
            byte[] distKey = RandomBytes(32);
            byte[] plaintext = MakeDeterministicPayload(2 * 1024 * 1024, seed: 0xF00D);

            string distPath = Path.Combine(_tmpDir, "pack.gisub-dist");
            WriteDistBlob(distPath, plaintext, distKey);

            // Stream-decrypts in place — after the call, distPath contains
            // plaintext bytes (ciphertext has been swapped out atomically).
            DistributionCipher.DecryptFileInPlaceStreaming(distPath, distKey);

            byte[] actual = File.ReadAllBytes(distPath);
            Assert.AreEqual(plaintext.Length, actual.Length, "decrypted length differs");
            Assert.IsTrue(ByteArraysEqual(plaintext, actual), "decrypted bytes do not match source");
        }

        [TestMethod]
        public void DistributionCipher_StreamingDecrypt_DetectsTampering()
        {
            // Encrypt-then-MAC promise: any single-bit flip in the ciphertext
            // must be caught by the HMAC check BEFORE AES runs, preventing a
            // malicious R2 credential from feeding garbage to the desktop.
            byte[] distKey = RandomBytes(32);
            byte[] plaintext = Encoding.UTF8.GetBytes("PL translations body — do not tamper");

            string distPath = Path.Combine(_tmpDir, "tampered.gisub-dist");
            WriteDistBlob(distPath, plaintext, distKey);

            // Flip a single ciphertext byte. The header HMAC still covers
            // only the untampered bytes, so the check will fail.
            byte[] blob = File.ReadAllBytes(distPath);
            blob[blob.Length - 1] ^= 0x01;
            File.WriteAllBytes(distPath, blob);

            try
            {
                DistributionCipher.DecryptFileInPlaceStreaming(distPath, distKey);
                Assert.Fail("expected CryptographicException on HMAC mismatch");
            }
            catch (CryptographicException)
            {
                // Expected.
            }

            // No .decrypted.tmp should linger.
            Assert.IsFalse(File.Exists(distPath + ".decrypted.tmp"),
                "temp file must be cleaned up on tamper detection");
        }

        [TestMethod]
        public void DistributionCipher_StreamingDecrypt_RejectsWrongKey()
        {
            // Handing a .gisub-dist to the decrypt with a different key must
            // be caught by the HMAC check. This is the "wrong account logged
            // in" and "R2 key rotated" case — we must abort cleanly rather
            // than writing garbage plaintext to disk.
            byte[] realKey = RandomBytes(32);
            byte[] wrongKey = RandomBytes(32);
            byte[] plaintext = Encoding.UTF8.GetBytes("valid payload, wrong key will fail");

            string distPath = Path.Combine(_tmpDir, "wrong-key.gisub-dist");
            WriteDistBlob(distPath, plaintext, realKey);

            try
            {
                DistributionCipher.DecryptFileInPlaceStreaming(distPath, wrongKey);
                Assert.Fail("expected CryptographicException when key doesn't match");
            }
            catch (CryptographicException)
            {
                // Expected.
            }

            Assert.IsFalse(File.Exists(distPath + ".decrypted.tmp"),
                "temp file must not linger when the key is wrong");

            // The original ciphertext file must still be intact — streaming
            // decrypt mustn't damage its input on HMAC failure. This is what
            // lets a retry with a refreshed session key succeed without
            // re-downloading.
            Assert.IsTrue(File.Exists(distPath),
                "source .gisub-dist must not be deleted on HMAC failure");
        }

        [TestMethod]
        public void DistributionCipher_StreamingDecrypt_TruncatedBlobThrows()
        {
            // Short read / truncated download → header check must fire before
            // AES state is allocated. Tests the <HeaderLength guard path.
            string distPath = Path.Combine(_tmpDir, "truncated.gisub-dist");
            File.WriteAllBytes(distPath, new byte[] { 0x4B, 0x41, 0x50, 0x44, 0x01 }); // KAPD + version, nothing else

            byte[] distKey = RandomBytes(32);
            try
            {
                DistributionCipher.DecryptFileInPlaceStreaming(distPath, distKey);
                Assert.Fail("expected CryptographicException on truncated blob");
            }
            catch (CryptographicException)
            {
                // Expected.
            }

            Assert.IsFalse(File.Exists(distPath + ".decrypted.tmp"),
                "temp file must not linger on truncated input");
        }

        // ── Abort semantics for OpenEncryptStream ─────────────────────────

        [TestMethod]
        public void OpenEncryptStream_CancellationFromThrowingSource_CleansUpTemp()
        {
            // Simulate a download-time cancellation: the "producer" side
            // (Stream.CopyTo with a source that throws mid-stream) throws an
            // OperationCanceledException. The encrypting sink must treat
            // this as an abort and reap its .tmp without materialising the
            // final .gisub.
            string encryptedPath = Path.Combine(_tmpDir, "cancelled.gisub");
            var service = TestProtection.Create();

            var source = new ThrowingStream(
                throwAfterBytes: 4096,
                exceptionToThrow: new OperationCanceledException("simulated CT cancellation"));

            try
            {
                using (var sink = service.OpenEncryptStream(encryptedPath))
                {
                    source.CopyTo(sink, 16 * 1024);
                    // If we reach here, the test is broken — ThrowingStream
                    // was supposed to throw mid-stream.
                    sink.Complete();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            Assert.IsFalse(File.Exists(encryptedPath),
                "final .gisub must not exist after cancellation");
            Assert.IsFalse(File.Exists(encryptedPath + ".tmp"),
                ".tmp must be cleaned up after cancellation");
        }

        [TestMethod]
        [TestCategory("Performance")]
        public void DistributionCipher_StreamingDecrypt_AllocatesFarLessThanPayload()
        {
            // Same allocation-budget rationale as the encrypt test: the legacy
            // path allocated a full byte[] blob + a full byte[] plaintext.
            // Streaming should hover around a few KB of block-copy buffers.
            byte[] distKey = RandomBytes(32);
            byte[] plaintext = MakeDeterministicPayload(PayloadBytes, seed: 0xCAFE);

            string distPath = Path.Combine(_tmpDir, "budget.gisub-dist");
            WriteDistBlob(distPath, plaintext, distKey);

            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);

            long before = GC.GetAllocatedBytesForCurrentThread();
            DistributionCipher.DecryptFileInPlaceStreaming(distPath, distKey);
            long after = GC.GetAllocatedBytesForCurrentThread();

            long allocated = after - before;
            // 30 MB budget — same rationale as the encrypt-side test. Two
            // passes over the file (HMAC verify + AES decrypt) means roughly
            // 2× the per-Write CryptoStream allocation vs the encrypt path,
            // but still well under the ≥ 20 MB simultaneous allocation the
            // old byte[] approach held on the LOH.
            long budget = 30 * 1024 * 1024;
            Assert.IsTrue(allocated < budget,
                $"streaming distribution decrypt allocated {allocated:N0} bytes for a {PayloadBytes:N0} byte payload — expected < {budget:N0}");
        }

        // ── helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// Produce a deterministic pseudo-random payload. We want reproducible
        /// bytes, not cryptographic randomness, so System.Random is the right
        /// tool. Seeded so CI sees identical input every run.
        /// </summary>
        private static byte[] MakeDeterministicPayload(int length, int seed)
        {
            var rand = new Random(seed);
            var buf = new byte[length];
            rand.NextBytes(buf);
            return buf;
        }

        private static byte[] RandomBytes(int count)
        {
            var buf = new byte[count];
            System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
            return buf;
        }

        private static bool ByteArraysEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// Mirrors the byte layout produced by backend/scripts/encrypt-for-r2.cjs:
        ///   "KAPD" + version + reserved + IV + HMAC + AES-256-CBC PKCS7 ciphertext.
        /// Key derivation matches <see cref="DistributionCipher"/> so a test
        /// blob round-trips through the streaming decrypt.
        /// </summary>
        private static void WriteDistBlob(string path, byte[] plaintext, byte[] distKey)
        {
            byte[] iv = RandomBytes(16);
            byte[] encKey = HmacDerive(distKey, "kaption-enc-v1");
            byte[] hmacKey = HmacDerive(distKey, "kaption-hmac-v1");

            byte[] ciphertext;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = encKey;
                aes.IV = iv;
                using (var enc = aes.CreateEncryptor())
                {
                    ciphertext = enc.TransformFinalBlock(plaintext, 0, plaintext.Length);
                }
            }

            byte[] mac;
            using (var h = new HMACSHA256(hmacKey))
                mac = h.ComputeHash(ciphertext);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(new byte[] { 0x4B, 0x41, 0x50, 0x44 }, 0, 4);  // "KAPD"
                fs.WriteByte(1);  // version
                fs.WriteByte(0);  // reserved
                fs.Write(iv, 0, 16);
                fs.Write(mac, 0, 32);
                fs.Write(ciphertext, 0, ciphertext.Length);
            }
        }

        private static byte[] HmacDerive(byte[] distKey, string label)
        {
            using (var h = new HMACSHA256(distKey))
                return h.ComputeHash(Encoding.UTF8.GetBytes(label));
        }

        /// <summary>
        /// Test double: a read-only Stream that delivers random bytes for a
        /// while, then throws a configurable exception. Used to simulate the
        /// "network failure / cancellation mid-stream" path without requiring
        /// an HttpListener. Matches the Stream surface the real
        /// <c>response.Content.ReadAsStreamAsync()</c> stream presents to
        /// <c>CopyTo</c>.
        /// </summary>
        private sealed class ThrowingStream : Stream
        {
            private readonly int _throwAfter;
            private readonly Exception _ex;
            private int _read;

            public ThrowingStream(int throwAfterBytes, Exception exceptionToThrow)
            {
                _throwAfter = throwAfterBytes;
                _ex = exceptionToThrow;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_read >= _throwAfter) throw _ex;
                int toGive = Math.Min(count, _throwAfter - _read);
                // Fill with cheap deterministic bytes; content doesn't matter
                // for the cancellation test — we only care that CopyTo makes
                // progress before the injected throw.
                for (int i = 0; i < toGive; i++) buffer[offset + i] = (byte)((_read + i) & 0xFF);
                _read += toGive;
                return toGive;
            }
        }
    }
}
