using System;
using System.IO;
using System.Security.Cryptography;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// Binary file format v3 for encrypted data files (.gisub).
    ///
    /// Motivation
    /// ----------
    /// v1/v2 used AES-256-CBC + one whole-file HMAC-SHA256. That can't be
    /// memory-mapped because decryption has to run serially from offset 0 and
    /// the HMAC can only be verified after the last block is read. v3 switches
    /// to AES-CTR with a per-block HMAC so any single block can be authenticated
    /// and decrypted in isolation — the requirement for zero-copy / mmap access.
    ///
    /// Layout
    /// ------
    ///   [0..4)    Magic: "GISB" (0x47 0x49 0x53 0x42)
    ///   [4..5)    Format version: 0x03
    ///   [5..6)    Cipher mode: 0x03 = AES-CTR + per-block HMAC
    ///   [6..7)    File category (enum, see FileCategory)
    ///   [7..8)    Reserved (0)
    ///   [8..24)   Nonce base (16 bytes, random per file)
    ///   [24..28)  Block size (uint32 LE, default 4096 bytes of plaintext)
    ///   [28..36)  Plaintext length (int64 LE)
    ///   [36..68)  Header HMAC-SHA256 over bytes [0..36)
    ///
    ///   Header size: 68 bytes (HeaderSize).
    ///
    /// Body
    /// ----
    ///   Blocks are laid out back-to-back. Each block is a fixed (block_size)
    ///   except the last, which may be shorter to match the remaining
    ///   plaintext. Each block is followed by its own 32-byte HMAC.
    ///
    ///   Block i:
    ///     ciphertext_i  (block_size bytes, or &lt;= block_size for last block)
    ///     hmac_i        (32 bytes) — HMAC-SHA256 of
    ///                   (nonce_base || block_index_u64_be || ciphertext_i)
    ///
    /// The per-block HMAC binds the block to its position so an attacker can't
    /// swap two blocks without detection.
    ///
    /// Nonce derivation (inside AesCtrProtection)
    /// ------------------------------------------
    ///   For each block i the CTR counter starts at
    ///     counter = nonce_base XOR block_index_big_endian
    ///   (using the low 8 bytes of nonce_base for the XOR target). This
    ///   ensures unique (nonce, counter) tuples across blocks of a single
    ///   file, and across files because nonce_base is random per file.
    ///
    /// Keys
    /// ----
    /// v3 uses the SAME derived encryption/HMAC keys as v1/v2 — derivation is
    /// PBKDF2(AppSecret + MachineFingerprint, 100K). Only the cipher mode
    /// changes. Existing key material stays valid; v2 files can be migrated
    /// to v3 without re-prompting.
    /// </summary>
    internal static class ProtectedFileFormatV3
    {
        // Magic is identical to v1/v2: "GISB". Version byte discriminates.
        public static readonly byte[] Magic = { 0x47, 0x49, 0x53, 0x42 };

        public const byte CurrentVersion = 3;
        public const byte CipherMode_AesCtr = 0x03;

        // Absolute byte offsets inside the fixed header. const int so callers
        // (including the mmap decryptor) can arithmetic-offset into a
        // MemoryMappedViewAccessor without any allocation or boxing.
        public const int MagicOffset         = 0;
        public const int VersionOffset       = 4;
        public const int CipherModeOffset    = 5;
        public const int CategoryOffset      = 6;
        public const int ReservedOffset      = 7;
        public const int NonceBaseOffset     = 8;
        public const int NonceBaseSize       = 16;
        public const int BlockSizeOffset     = 24; // uint32 LE
        public const int PlaintextLenOffset  = 28; // int64 LE
        public const int HeaderHmacOffset    = 36; // 32 bytes
        public const int HeaderHmacSize      = 32;
        public const int HeaderSize          = 68;

        // Field sizes repeated for clarity at call-sites.
        public const int MagicSize           = 4;
        public const int BlockHmacSize       = 32;

        // Size over which the header HMAC is computed (everything before the
        // header HMAC itself).
        public const int HeaderSignedPrefix  = HeaderHmacOffset; // 36

        // Default plaintext block size. Chosen to match the typical OS page
        // size (4 KB) so a MemoryMappedViewAccessor read covers at most one
        // page + HMAC tail in the common case.
        public const int DefaultBlockSize    = 4096;

        // Upper bound to refuse pathological headers early.
        public const int MaxBlockSize        = 1 << 20; // 1 MiB

        public enum FileCategory : byte
        {
            CustomTranslation = 0,
            GeneratedGraph    = 1,
            Reserved          = 2
        }

        /// <summary>
        /// Header fields, returned from <see cref="ReadAndVerifyHeader"/>.
        /// </summary>
        internal struct HeaderInfo
        {
            public byte[] NonceBase;      // 16 bytes (owned by caller)
            public int BlockSize;
            public long PlaintextLength;
            public FileCategory Category;
        }

        /// <summary>
        /// Build the first <see cref="HeaderSignedPrefix"/> bytes of the
        /// header into <paramref name="buffer"/> (must be at least
        /// HeaderSignedPrefix long). The HMAC is NOT written — the caller
        /// computes it over this slice and appends the result.
        /// </summary>
        internal static void WriteHeaderPrefix(
            byte[] buffer,
            byte[] nonceBase,
            int blockSize,
            long plaintextLength,
            FileCategory category)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (buffer.Length < HeaderSignedPrefix)
                throw new ArgumentException("Buffer too small for v3 header prefix", nameof(buffer));
            if (nonceBase == null || nonceBase.Length != NonceBaseSize)
                throw new ArgumentException("Nonce base must be 16 bytes", nameof(nonceBase));
            if (blockSize <= 0 || blockSize > MaxBlockSize)
                throw new ArgumentOutOfRangeException(nameof(blockSize));
            if (plaintextLength < 0)
                throw new ArgumentOutOfRangeException(nameof(plaintextLength));

            Array.Clear(buffer, 0, HeaderSignedPrefix);

            buffer[MagicOffset + 0] = Magic[0];
            buffer[MagicOffset + 1] = Magic[1];
            buffer[MagicOffset + 2] = Magic[2];
            buffer[MagicOffset + 3] = Magic[3];

            buffer[VersionOffset]    = CurrentVersion;
            buffer[CipherModeOffset] = CipherMode_AesCtr;
            buffer[CategoryOffset]   = (byte)category;
            buffer[ReservedOffset]   = 0;

            Buffer.BlockCopy(nonceBase, 0, buffer, NonceBaseOffset, NonceBaseSize);

            WriteUInt32LE(buffer, BlockSizeOffset, (uint)blockSize);
            WriteInt64LE(buffer, PlaintextLenOffset, plaintextLength);
        }

        /// <summary>
        /// Read the fixed header from <paramref name="stream"/>, verify its
        /// HMAC against <paramref name="hmacKey"/>, and return the parsed
        /// field set. Throws on any magic/version/size/HMAC error.
        /// </summary>
        internal static HeaderInfo ReadAndVerifyHeader(Stream stream, byte[] hmacKey)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (hmacKey == null) throw new ArgumentNullException(nameof(hmacKey));

            var header = new byte[HeaderSize];
            int got = 0;
            while (got < HeaderSize)
            {
                int n = stream.Read(header, got, HeaderSize - got);
                if (n == 0)
                    throw new InvalidDataException("File too short — v3 header truncated");
                got += n;
            }

            return ParseAndVerifyHeader(header, 0, hmacKey);
        }

        /// <summary>
        /// Parse and verify a v3 header held entirely in <paramref name="buffer"/>
        /// starting at <paramref name="offset"/>. Used by the mmap reader
        /// (which already has the whole file mapped) to avoid a second copy.
        /// </summary>
        internal static HeaderInfo ParseAndVerifyHeader(byte[] buffer, int offset, byte[] hmacKey)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (hmacKey == null) throw new ArgumentNullException(nameof(hmacKey));
            if (offset < 0 || buffer.Length - offset < HeaderSize)
                throw new InvalidDataException("Buffer too short for a v3 header");

            // Magic
            if (buffer[offset + 0] != Magic[0] || buffer[offset + 1] != Magic[1] ||
                buffer[offset + 2] != Magic[2] || buffer[offset + 3] != Magic[3])
            {
                throw new InvalidDataException("Invalid file format — missing GISB magic");
            }

            byte version = buffer[offset + VersionOffset];
            if (version != CurrentVersion)
                throw new InvalidDataException("Not a v3 file (version=" + version + ")");

            byte mode = buffer[offset + CipherModeOffset];
            if (mode != CipherMode_AesCtr)
                throw new InvalidDataException("Unsupported v3 cipher mode: 0x" + mode.ToString("X2"));

            var category = (FileCategory)buffer[offset + CategoryOffset];

            uint blockSizeU = ReadUInt32LE(buffer, offset + BlockSizeOffset);
            if (blockSizeU == 0 || blockSizeU > MaxBlockSize)
                throw new InvalidDataException("v3 block size out of range: " + blockSizeU);
            int blockSize = (int)blockSizeU;

            long plaintextLen = ReadInt64LE(buffer, offset + PlaintextLenOffset);
            if (plaintextLen < 0)
                throw new InvalidDataException("v3 plaintext length negative");

            // Verify header HMAC over the first HeaderSignedPrefix bytes.
            byte[] expected = new byte[HeaderHmacSize];
            Buffer.BlockCopy(buffer, offset + HeaderHmacOffset, expected, 0, HeaderHmacSize);

            byte[] computed;
            using (var h = new HMACSHA256(hmacKey))
            {
                computed = h.ComputeHash(buffer, offset, HeaderSignedPrefix);
            }
            if (!ConstantTimeEquals(expected, computed))
                throw new CryptographicException("v3 header HMAC verification failed");

            var info = new HeaderInfo
            {
                NonceBase       = new byte[NonceBaseSize],
                BlockSize       = blockSize,
                PlaintextLength = plaintextLen,
                Category        = category
            };
            Buffer.BlockCopy(buffer, offset + NonceBaseOffset, info.NonceBase, 0, NonceBaseSize);
            return info;
        }

        /// <summary>
        /// Detect whether <paramref name="filePath"/> starts with a v3 header
        /// (magic + version byte). Does NOT verify HMAC — cheap format probe.
        /// </summary>
        public static bool HasV3Header(string filePath)
        {
            try
            {
                using (var s = File.OpenRead(filePath))
                {
                    if (s.Length < HeaderSize) return false;
                    var probe = new byte[5];
                    int read = 0;
                    while (read < probe.Length)
                    {
                        int n = s.Read(probe, read, probe.Length - read);
                        if (n == 0) return false;
                        read += n;
                    }
                    return probe[0] == Magic[0] && probe[1] == Magic[1]
                        && probe[2] == Magic[2] && probe[3] == Magic[3]
                        && probe[4] == CurrentVersion;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns the number of whole+partial ciphertext blocks implied by
        /// a plaintext length. A zero-length payload encodes as zero blocks.
        /// </summary>
        internal static long ComputeBlockCount(long plaintextLength, int blockSize)
        {
            if (plaintextLength <= 0) return 0;
            long n = plaintextLength / blockSize;
            if ((plaintextLength % blockSize) != 0) n++;
            return n;
        }

        /// <summary>
        /// Size (in bytes) of block <paramref name="blockIndex"/> given the
        /// total plaintext length. All blocks are <paramref name="blockSize"/>
        /// except possibly the last.
        /// </summary>
        internal static int BlockPlaintextSize(long blockIndex, long plaintextLength, int blockSize)
        {
            long start = blockIndex * blockSize;
            long remaining = plaintextLength - start;
            if (remaining <= 0) return 0;
            if (remaining >= blockSize) return blockSize;
            return (int)remaining;
        }

        /// <summary>
        /// Starting file offset of block <paramref name="blockIndex"/>'s
        /// ciphertext. The per-block HMAC follows the ciphertext.
        /// </summary>
        internal static long BlockFileOffset(long blockIndex, int blockSize)
        {
            // header + blockIndex * (blockSize + HMAC)
            return HeaderSize + blockIndex * ((long)blockSize + BlockHmacSize);
        }

        /// <summary>
        /// Expected total file size for a given plaintext length + block size.
        /// </summary>
        internal static long ExpectedFileSize(long plaintextLength, int blockSize)
        {
            long blocks = ComputeBlockCount(plaintextLength, blockSize);
            if (blocks == 0) return HeaderSize;
            long fullBlocks = blocks - 1;
            int lastSize = BlockPlaintextSize(fullBlocks, plaintextLength, blockSize);
            return HeaderSize
                 + fullBlocks * ((long)blockSize + BlockHmacSize)
                 + lastSize
                 + BlockHmacSize;
        }

        // ─── Little-endian helpers ────────────────────────────────────────

        internal static void WriteUInt32LE(byte[] buf, int offset, uint value)
        {
            buf[offset + 0] = (byte)(value      );
            buf[offset + 1] = (byte)(value >>  8);
            buf[offset + 2] = (byte)(value >> 16);
            buf[offset + 3] = (byte)(value >> 24);
        }

        internal static uint ReadUInt32LE(byte[] buf, int offset)
        {
            return (uint)buf[offset + 0]
                 | ((uint)buf[offset + 1] <<  8)
                 | ((uint)buf[offset + 2] << 16)
                 | ((uint)buf[offset + 3] << 24);
        }

        internal static void WriteInt64LE(byte[] buf, int offset, long value)
        {
            ulong u = (ulong)value;
            buf[offset + 0] = (byte)(u      );
            buf[offset + 1] = (byte)(u >>  8);
            buf[offset + 2] = (byte)(u >> 16);
            buf[offset + 3] = (byte)(u >> 24);
            buf[offset + 4] = (byte)(u >> 32);
            buf[offset + 5] = (byte)(u >> 40);
            buf[offset + 6] = (byte)(u >> 48);
            buf[offset + 7] = (byte)(u >> 56);
        }

        internal static long ReadInt64LE(byte[] buf, int offset)
        {
            ulong u = (ulong)buf[offset + 0]
                    | ((ulong)buf[offset + 1] <<  8)
                    | ((ulong)buf[offset + 2] << 16)
                    | ((ulong)buf[offset + 3] << 24)
                    | ((ulong)buf[offset + 4] << 32)
                    | ((ulong)buf[offset + 5] << 40)
                    | ((ulong)buf[offset + 6] << 48)
                    | ((ulong)buf[offset + 7] << 56);
            return (long)u;
        }

        /// <summary>Constant-time byte-array equality. Two equal-length
        /// buffers compare in time that depends only on the length.</summary>
        internal static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
