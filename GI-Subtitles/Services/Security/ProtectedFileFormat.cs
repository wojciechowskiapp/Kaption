using System;
using System.IO;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// Binary file format for AES-CBC encrypted data files (.gisub).
    ///
    /// Layout (versions 1 and 2 share the same byte structure — only the key
    /// source differs between them, see <see cref="HeaderVersion"/>):
    ///   [4 bytes]  Magic: "GISB" (0x47, 0x49, 0x53, 0x42)
    ///   [1 byte]   Format version (1 = legacy embedded AppSecret,
    ///                              2 = server-issued per-device secret)
    ///   [1 byte]   File category (0 = CustomTranslation, 1 = GeneratedGraph,
    ///                              2 = Reserved)
    ///   [16 bytes] AES-CBC IV (random per file)
    ///   [32 bytes] HMAC-SHA256 of the encrypted payload
    ///   [N bytes]  AES-256-CBC encrypted payload (PKCS7 padded)
    ///
    /// Total header: 54 bytes. Payload starts at offset 54.
    ///
    /// v3 (AES-CTR per-block + per-block HMAC, mmap-friendly) is a separate
    /// format with a different magic + header layout — see
    /// <see cref="ProtectedFileFormatV3"/>. Don't confuse the two.
    /// </summary>
    public static class ProtectedFileFormat
    {
        public static readonly byte[] Magic = { 0x47, 0x49, 0x53, 0x42 }; // "GISB"

        /// <summary>v1: keys derived from embedded AppSecret + machine fingerprint.</summary>
        public const byte HeaderVersion1Legacy = 1;

        /// <summary>v2: keys derived from server-issued device secret + machine fingerprint.</summary>
        public const byte HeaderVersion2ServerKey = 2;

        /// <summary>
        /// Default version emitted by the parameter-less
        /// <see cref="WriteHeader(Stream, byte[], byte[], FileCategory)"/>.
        /// Kept at v1 for backward compatibility with any external caller that
        /// still uses that overload. The current writer
        /// (<see cref="ServerKeyFileProtectionService"/>) passes
        /// <see cref="HeaderVersion2ServerKey"/> explicitly via
        /// <see cref="WriteHeaderVersion"/>.
        /// </summary>
        public const byte CurrentVersion = HeaderVersion1Legacy;

        public const int HeaderSize = 4 + 1 + 1 + 16 + 32; // 54 bytes

        public const int MagicOffset = 0;
        public const int VersionOffset = 4;
        public const int CategoryOffset = 5;
        public const int IvOffset = 6;
        public const int HmacOffset = 22;
        public const int PayloadOffset = 54;

        public enum FileCategory : byte
        {
            CustomTranslation = 0,
            GeneratedGraph = 1,
            Reserved = 2
        }

        /// <summary>
        /// Write the .gisub file header to a stream using the default version
        /// (v1 / legacy). Equivalent to calling
        /// <see cref="WriteHeaderVersion(Stream, byte[], byte[], FileCategory, byte)"/>
        /// with version = <see cref="CurrentVersion"/>.
        /// </summary>
        public static void WriteHeader(Stream stream, byte[] iv, byte[] hmac, FileCategory category)
        {
            WriteHeaderVersion(stream, iv, hmac, category, CurrentVersion);
        }

        /// <summary>
        /// Write the .gisub file header to a stream with an explicit version
        /// byte. Use <see cref="HeaderVersion1Legacy"/> for the embedded-AppSecret
        /// scheme or <see cref="HeaderVersion2ServerKey"/> for the
        /// server-issued-secret scheme.
        /// </summary>
        public static void WriteHeaderVersion(
            Stream stream,
            byte[] iv,
            byte[] hmac,
            FileCategory category,
            byte version)
        {
            if (iv == null || iv.Length != 16)
                throw new ArgumentException("IV must be 16 bytes", nameof(iv));
            if (hmac == null || hmac.Length != 32)
                throw new ArgumentException("HMAC must be 32 bytes", nameof(hmac));
            if (version != HeaderVersion1Legacy && version != HeaderVersion2ServerKey)
                throw new ArgumentOutOfRangeException(nameof(version), version, "Unsupported header version");

            stream.Write(Magic, 0, Magic.Length);
            stream.WriteByte(version);
            stream.WriteByte((byte)category);
            stream.Write(iv, 0, iv.Length);
            stream.Write(hmac, 0, hmac.Length);
        }

        /// <summary>
        /// Read and validate the .gisub file header.
        /// Returns the IV, HMAC, and category. Throws on invalid format or
        /// an unsupported version. v1 is the legacy default; calls that need
        /// the version byte should use <see cref="ReadHeaderAny"/>.
        /// </summary>
        public static (byte[] iv, byte[] hmac, FileCategory category) ReadHeader(Stream stream)
        {
            var (_, iv, hmac, category) = ReadHeaderAny(stream);
            return (iv, hmac, category);
        }

        /// <summary>
        /// Read and validate the .gisub file header, returning the version
        /// byte alongside the rest of the header data so callers can dispatch
        /// to the right key-derivation path. Accepts both v1 (legacy
        /// AppSecret) and v2 (server-issued secret) layouts.
        /// </summary>
        public static (byte version, byte[] iv, byte[] hmac, FileCategory category)
            ReadHeaderAny(Stream stream)
        {
            var header = new byte[HeaderSize];
            int bytesRead = 0;
            while (bytesRead < HeaderSize)
            {
                int read = stream.Read(header, bytesRead, HeaderSize - bytesRead);
                if (read == 0)
                    throw new InvalidDataException("File too short — not a valid .gisub file");
                bytesRead += read;
            }

            // Validate magic.
            if (header[0] != Magic[0] || header[1] != Magic[1] ||
                header[2] != Magic[2] || header[3] != Magic[3])
            {
                throw new InvalidDataException("Invalid file format — missing GISB magic header");
            }

            byte version = header[VersionOffset];
            if (version != HeaderVersion1Legacy && version != HeaderVersion2ServerKey)
                throw new InvalidDataException($"Unsupported .gisub version: {version}");

            var category = (FileCategory)header[CategoryOffset];

            var iv = new byte[16];
            Buffer.BlockCopy(header, IvOffset, iv, 0, 16);

            var hmac = new byte[32];
            Buffer.BlockCopy(header, HmacOffset, hmac, 0, 32);

            return (version, iv, hmac, category);
        }

        /// <summary>
        /// Quick check: does this file start with the GISB magic bytes?
        /// </summary>
        public static bool HasMagicHeader(string filePath)
        {
            try
            {
                using (var stream = File.OpenRead(filePath))
                {
                    if (stream.Length < HeaderSize)
                        return false;

                    // Net10 (CA2022): Stream.Read is not guaranteed to fill the
                    // buffer in a single call — may return less than requested
                    // even for local files (rare but possible under I/O
                    // fragmentation). Use ReadExactly which loops internally.
                    Span<byte> magic = stackalloc byte[4];
                    stream.ReadExactly(magic);
                    return magic[0] == Magic[0] && magic[1] == Magic[1] &&
                           magic[2] == Magic[2] && magic[3] == Magic[3];
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Quick read of the format-version byte from a .gisub file. Returns
        /// 0 if the file is missing, too short, or not a recognised .gisub.
        /// Useful for "do I need to migrate this file?" cheap-scans without
        /// triggering a full HMAC verification.
        /// </summary>
        public static byte PeekFormatVersion(string filePath)
        {
            try
            {
                using (var stream = File.OpenRead(filePath))
                {
                    if (stream.Length < HeaderSize)
                        return 0;

                    Span<byte> head = stackalloc byte[5];
                    stream.ReadExactly(head);
                    if (head[0] != Magic[0] || head[1] != Magic[1] ||
                        head[2] != Magic[2] || head[3] != Magic[3])
                        return 0;
                    return head[4];
                }
            }
            catch
            {
                return 0;
            }
        }
    }
}
