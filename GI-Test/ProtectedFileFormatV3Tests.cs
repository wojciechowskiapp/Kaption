// ─────────────────────────────────────────────────────────────────────────────
//  ProtectedFileFormatV3Tests.cs
//  ---------------------------------------------------------------------------
//  Covers the v3 .gisub file format end-to-end: header round-trip, block
//  structure, HMAC tamper detection at every layer (header + per block),
//  seekable-stream reader, and the mmap random-access decryptor.
//
//  All tests run without the machine-bound key derivation — they use caller-
//  supplied keys through the internal AesCtrFileProtection surface. This
//  keeps CI reproducible and avoids depending on WMI.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.IO;
using System.Security.Cryptography;
using GI_Subtitles.Services.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class ProtectedFileFormatV3Tests
    {
        private static byte[] Key(byte seed)
        {
            var k = new byte[32];
            for (int i = 0; i < k.Length; i++) k[i] = (byte)(i * 11 ^ seed);
            return k;
        }

        private static string TempPath(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), "gisub-v3-tests");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, tag + "-" + Guid.NewGuid().ToString("N") + ".gisub");
        }

        private static void DeleteQuiet(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ── Header round-trip ─────────────────────────────────────────────

        [TestMethod]
        [TestCategory("Security")]
        public void RoundTrip_EmptyPayload()
        {
            var path = TempPath("empty");
            try
            {
                AesCtrFileProtection.EncryptBytesToV3(
                    Array.Empty<byte>(), path,
                    ProtectedFileFormatV3.FileCategory.CustomTranslation,
                    Key(1), Key(2));

                Assert.IsTrue(ProtectedFileFormatV3.HasV3Header(path));
                using (var s = AesCtrFileProtection.OpenDecryptStream(path, Key(1), Key(2)))
                {
                    Assert.AreEqual(0L, s.Length);
                    Assert.AreEqual(-1, s.ReadByte()); // past EOF
                }
            }
            finally { DeleteQuiet(path); }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void RoundTrip_SmallPayload_OneBlock()
        {
            var path = TempPath("small");
            try
            {
                var plain = new byte[32];
                for (int i = 0; i < plain.Length; i++) plain[i] = (byte)i;
                AesCtrFileProtection.EncryptBytesToV3(
                    plain, path,
                    ProtectedFileFormatV3.FileCategory.GeneratedGraph,
                    Key(1), Key(2));

                var decoded = ReadAll(path, Key(1), Key(2));
                CollectionAssert.AreEqual(plain, decoded);
            }
            finally { DeleteQuiet(path); }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void RoundTrip_ExactBlockBoundary()
        {
            var path = TempPath("boundary");
            try
            {
                var plain = new byte[ProtectedFileFormatV3.DefaultBlockSize * 3];
                new Random(1).NextBytes(plain);
                AesCtrFileProtection.EncryptBytesToV3(plain, path,
                    ProtectedFileFormatV3.FileCategory.CustomTranslation,
                    Key(1), Key(2));

                CollectionAssert.AreEqual(plain, ReadAll(path, Key(1), Key(2)));
            }
            finally { DeleteQuiet(path); }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void RoundTrip_RaggedTail_MultiBlock()
        {
            var path = TempPath("ragged");
            try
            {
                var plain = new byte[ProtectedFileFormatV3.DefaultBlockSize * 5 + 137];
                new Random(2).NextBytes(plain);
                AesCtrFileProtection.EncryptBytesToV3(plain, path,
                    ProtectedFileFormatV3.FileCategory.GeneratedGraph,
                    Key(3), Key(4));

                CollectionAssert.AreEqual(plain, ReadAll(path, Key(3), Key(4)));
            }
            finally { DeleteQuiet(path); }
        }

        // ── Tamper detection ──────────────────────────────────────────────

        [TestMethod]
        [TestCategory("Security")]
        public void HeaderHmacTamper_NonceBase_Rejects()
        {
            var path = TempPath("tamper-nonce");
            try
            {
                var plain = new byte[1000];
                new Random(3).NextBytes(plain);
                AesCtrFileProtection.EncryptBytesToV3(plain, path,
                    ProtectedFileFormatV3.FileCategory.CustomTranslation,
                    Key(5), Key(6));

                // Flip a bit in the nonce base (byte 8 is nonce[0]).
                FlipByte(path, ProtectedFileFormatV3.NonceBaseOffset, 0x01);

                Assert.ThrowsException<CryptographicException>(() =>
                {
                    using (var s = AesCtrFileProtection.OpenDecryptStream(path, Key(5), Key(6)))
                    { /* open alone triggers header verify */ }
                });
            }
            finally { DeleteQuiet(path); }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void HeaderHmacTamper_BlockSize_Rejects()
        {
            var path = TempPath("tamper-bs");
            try
            {
                var plain = new byte[500];
                AesCtrFileProtection.EncryptBytesToV3(plain, path,
                    ProtectedFileFormatV3.FileCategory.CustomTranslation,
                    Key(7), Key(8));

                // Flip block-size field (byte at BlockSizeOffset = 24).
                FlipByte(path, ProtectedFileFormatV3.BlockSizeOffset, 0x01);

                Assert.ThrowsException<CryptographicException>(() =>
                {
                    using (var s = AesCtrFileProtection.OpenDecryptStream(path, Key(7), Key(8)))
                    { }
                });
            }
            finally { DeleteQuiet(path); }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void BlockCiphertext_Tamper_Rejects()
        {
            var path = TempPath("tamper-ct");
            try
            {
                var plain = new byte[ProtectedFileFormatV3.DefaultBlockSize * 2 + 10];
                new Random(4).NextBytes(plain);
                AesCtrFileProtection.EncryptBytesToV3(plain, path,
                    ProtectedFileFormatV3.FileCategory.CustomTranslation,
                    Key(9), Key(10));

                // Flip first byte of block 1's ciphertext.
                long offset = ProtectedFileFormatV3.BlockFileOffset(1, ProtectedFileFormatV3.DefaultBlockSize);
                FlipByte(path, (int)offset, 0x01);

                Assert.ThrowsException<CryptographicException>(() =>
                {
                    using (var s = AesCtrFileProtection.OpenDecryptStream(path, Key(9), Key(10)))
                    {
                        // Must read far enough to touch block 1.
                        var buf = new byte[s.Length];
                        int total = 0;
                        int n;
                        while ((n = s.Read(buf, total, buf.Length - total)) > 0) total += n;
                    }
                });
            }
            finally { DeleteQuiet(path); }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void BlockHmacTag_Tamper_Rejects()
        {
            var path = TempPath("tamper-tag");
            try
            {
                var plain = new byte[ProtectedFileFormatV3.DefaultBlockSize * 2];
                new Random(5).NextBytes(plain);
                AesCtrFileProtection.EncryptBytesToV3(plain, path,
                    ProtectedFileFormatV3.FileCategory.CustomTranslation,
                    Key(11), Key(12));

                // Flip inside block 0's HMAC tag (offset = headerSize + blockSize).
                int tagOffset = ProtectedFileFormatV3.HeaderSize + ProtectedFileFormatV3.DefaultBlockSize + 5;
                FlipByte(path, tagOffset, 0x01);

                Assert.ThrowsException<CryptographicException>(() =>
                {
                    using (var s = AesCtrFileProtection.OpenDecryptStream(path, Key(11), Key(12)))
                    {
                        var buf = new byte[64];
                        // ReadExactly (net7+) avoids CA2022 and reads the full
                        // buffer. Tamper detection triggers during decryption
                        // before EOF, so behavior is unchanged.
                        s.ReadExactly(buf);
                    }
                });
            }
            finally { DeleteQuiet(path); }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void WrongHmacKey_Rejects()
        {
            var path = TempPath("wrong-hmac");
            try
            {
                var plain = new byte[500];
                AesCtrFileProtection.EncryptBytesToV3(plain, path,
                    ProtectedFileFormatV3.FileCategory.CustomTranslation,
                    Key(13), Key(14));

                Assert.ThrowsException<CryptographicException>(() =>
                {
                    using (var s = AesCtrFileProtection.OpenDecryptStream(path, Key(13), Key(99)))
                    { }
                });
            }
            finally { DeleteQuiet(path); }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void TruncatedFile_Rejects()
        {
            var path = TempPath("truncated");
            try
            {
                var plain = new byte[ProtectedFileFormatV3.DefaultBlockSize * 2];
                AesCtrFileProtection.EncryptBytesToV3(plain, path,
                    ProtectedFileFormatV3.FileCategory.CustomTranslation,
                    Key(15), Key(16));

                // Lop off the last 10 bytes.
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
                    fs.SetLength(fs.Length - 10);

                Assert.ThrowsException<InvalidDataException>(() =>
                {
                    using (var s = AesCtrFileProtection.OpenDecryptStream(path, Key(15), Key(16)))
                    { }
                });
            }
            finally { DeleteQuiet(path); }
        }

        // ── Probe ─────────────────────────────────────────────────────────

        [TestMethod]
        [TestCategory("Security")]
        public void HasV3Header_FalseForV1()
        {
            var path = TempPath("v1");
            try
            {
                // Craft a v1 header (version byte = 1) so HasV3Header returns false.
                var header = new byte[ProtectedFileFormatV3.HeaderSize];
                header[0] = (byte)'G'; header[1] = (byte)'I'; header[2] = (byte)'S'; header[3] = (byte)'B';
                header[4] = 0x01; // v1
                File.WriteAllBytes(path, header);

                Assert.IsFalse(ProtectedFileFormatV3.HasV3Header(path));
            }
            finally { DeleteQuiet(path); }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void HasV3Header_FalseForTooShort()
        {
            var path = TempPath("short");
            try
            {
                File.WriteAllBytes(path, new byte[5]);
                Assert.IsFalse(ProtectedFileFormatV3.HasV3Header(path));
            }
            finally { DeleteQuiet(path); }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void HasV3Header_TrueForValidV3()
        {
            var path = TempPath("valid");
            try
            {
                AesCtrFileProtection.EncryptBytesToV3(
                    new byte[] { 1, 2, 3 }, path,
                    ProtectedFileFormatV3.FileCategory.CustomTranslation,
                    Key(17), Key(18));
                Assert.IsTrue(ProtectedFileFormatV3.HasV3Header(path));
            }
            finally { DeleteQuiet(path); }
        }

        // ── Mmap decryptor ────────────────────────────────────────────────

        [TestMethod]
        [TestCategory("Security")]
        public void MmapDecryptor_ReadsFullFile_SequentialChunks()
        {
            var path = TempPath("mmap-seq");
            try
            {
                var plain = new byte[ProtectedFileFormatV3.DefaultBlockSize * 4 + 17];
                new Random(6).NextBytes(plain);
                AesCtrFileProtection.EncryptBytesToV3(plain, path,
                    ProtectedFileFormatV3.FileCategory.CustomTranslation,
                    Key(19), Key(20));

                var outBuf = new byte[plain.Length];
                using (var dec = AesCtrFileProtection.OpenMmapDecryptor(path, Key(19), Key(20)))
                {
                    Assert.AreEqual(plain.Length, dec.Length);

                    // Read in 1024-byte chunks.
                    int off = 0;
                    while (off < plain.Length)
                    {
                        int toRead = Math.Min(1024, plain.Length - off);
                        dec.ReadPlaintext(off, new Span<byte>(outBuf, off, toRead));
                        off += toRead;
                    }
                }
                CollectionAssert.AreEqual(plain, outBuf);
            }
            finally { DeleteQuiet(path); }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void MmapDecryptor_RandomAccess_CorrectBytes()
        {
            var path = TempPath("mmap-rand");
            try
            {
                var plain = new byte[ProtectedFileFormatV3.DefaultBlockSize * 10];
                new Random(7).NextBytes(plain);
                AesCtrFileProtection.EncryptBytesToV3(plain, path,
                    ProtectedFileFormatV3.FileCategory.CustomTranslation,
                    Key(21), Key(22));

                var rng = new Random(42);
                using (var dec = AesCtrFileProtection.OpenMmapDecryptor(path, Key(21), Key(22)))
                {
                    for (int i = 0; i < 50; i++)
                    {
                        long offset = rng.Next(0, plain.Length - 100);
                        int len = rng.Next(1, 100);
                        var tmp = new byte[len];
                        dec.ReadPlaintext(offset, tmp);
                        for (int j = 0; j < len; j++)
                        {
                            if (tmp[j] != plain[offset + j])
                                Assert.Fail("mismatch at offset " + (offset + j));
                        }
                    }
                }
            }
            finally { DeleteQuiet(path); }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void MmapDecryptor_SpanningMultipleBlocks()
        {
            var path = TempPath("mmap-span");
            try
            {
                var plain = new byte[ProtectedFileFormatV3.DefaultBlockSize * 3];
                new Random(8).NextBytes(plain);
                AesCtrFileProtection.EncryptBytesToV3(plain, path,
                    ProtectedFileFormatV3.FileCategory.CustomTranslation,
                    Key(23), Key(24));

                using (var dec = AesCtrFileProtection.OpenMmapDecryptor(path, Key(23), Key(24)))
                {
                    // Read that straddles blocks 0+1+2.
                    int start = ProtectedFileFormatV3.DefaultBlockSize - 5;
                    int len = ProtectedFileFormatV3.DefaultBlockSize + 100;
                    var buf = new byte[len];
                    dec.ReadPlaintext(start, buf);
                    for (int i = 0; i < len; i++)
                        Assert.AreEqual(plain[start + i], buf[i]);
                }
            }
            finally { DeleteQuiet(path); }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void MmapDecryptor_ReadOutOfRange_Throws()
        {
            var path = TempPath("mmap-oor");
            try
            {
                var plain = new byte[100];
                AesCtrFileProtection.EncryptBytesToV3(plain, path,
                    ProtectedFileFormatV3.FileCategory.CustomTranslation,
                    Key(25), Key(26));

                using (var dec = AesCtrFileProtection.OpenMmapDecryptor(path, Key(25), Key(26)))
                {
                    Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                        dec.ReadPlaintext(50, new byte[100]));
                }
            }
            finally { DeleteQuiet(path); }
        }

        [TestMethod]
        [TestCategory("Security")]
        public void MmapDecryptor_SteadyState_Allocates_Under_1MB_For_1000_Reads()
        {
            // Requirement: open an mmap decryptor on a 10 MB v3 file, do 1000
            // random 64-byte reads, assert allocated bytes delta < 1 MB.
            const int fileBytes = 10 * 1024 * 1024;
            var path = TempPath("mmap-alloc");
            try
            {
                var plain = new byte[fileBytes];
                new Random(42).NextBytes(plain);
                AesCtrFileProtection.EncryptBytesToV3(plain, path,
                    ProtectedFileFormatV3.FileCategory.CustomTranslation,
                    Key(27), Key(28));

                using (var dec = AesCtrFileProtection.OpenMmapDecryptor(path, Key(27), Key(28)))
                {
                    // Warm up — first call populates ArrayPool buckets and JITs.
                    var warm = new byte[64];
                    for (int i = 0; i < 64; i++)
                        dec.ReadPlaintext(i * 512L, warm);

                    // Measurement.
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    var rng = new Random(99);
                    var buf = new byte[64];
                    for (int i = 0; i < 1000; i++)
                    {
                        long off = rng.Next(0, fileBytes - 64);
                        dec.ReadPlaintext(off, buf);
                    }
                    long delta = GC.GetAllocatedBytesForCurrentThread() - before;

                    Assert.IsTrue(delta < 1024 * 1024,
                        "Expected < 1 MB of allocation for 1000 random reads, got " + delta + " bytes");
                }
            }
            finally { DeleteQuiet(path); }
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private static byte[] ReadAll(string path, byte[] encKey, byte[] hmacKey)
        {
            using (var s = AesCtrFileProtection.OpenDecryptStream(path, encKey, hmacKey))
            {
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        private static void FlipByte(string path, int offset, byte xorMask)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                fs.Position = offset;
                int b = fs.ReadByte();
                fs.Position = offset;
                fs.WriteByte((byte)(b ^ xorMask));
            }
        }
    }
}
