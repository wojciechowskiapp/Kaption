using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// Schema constants + on-disk structs for the Kaption Matcher blob (.kmx).
    ///
    /// The blob is a fixed-header + flat-section format: header first, then
    /// the FST key index, then the entry table (fixed-width rows), then the
    /// ZSTD-compressed value pool, then the trained ZSTD dictionary, then
    /// the metadata record. The header carries absolute offsets/lengths for
    /// every section so the reader can memory-map the whole file and walk
    /// directly to what it needs.
    ///
    /// Intentionally hand-authored instead of generated from a FlatBuffer
    /// schema — for our fixed-shape matcher data the vtable/union machinery
    /// in FlatBuffers is pure overhead, and hand-authoring keeps the net48
    /// build chain free of the FlatSharp compiler task.
    /// </summary>
    public static class MatcherBlobSchema
    {
        /// <summary>ASCII "KMX1" — Kaption Matcher v1.</summary>
        public static readonly byte[] Magic = new byte[] { (byte)'K', (byte)'M', (byte)'X', (byte)'1' };

        /// <summary>Bump when on-disk layout changes incompatibly.</summary>
        public const uint FormatVersion = 1;

        /// <summary>Fixed header size in bytes. Reserved space at the tail for future fields.</summary>
        public const int HeaderSizeBytes = 64;

        /// <summary>Fixed entry size in bytes. All offsets/lengths are little-endian.</summary>
        public const int EntrySizeBytes = 24;

        /// <summary>
        /// Header layout. Total size must equal <see cref="HeaderSizeBytes"/>.
        /// <list type="bullet">
        /// <item>[0..4)   magic "KMX1"</item>
        /// <item>[4..8)   format_version</item>
        /// <item>[8..16)  total file size (ulong)</item>
        /// <item>[16..20) fst_offset</item>
        /// <item>[20..24) fst_length</item>
        /// <item>[24..28) entries_offset</item>
        /// <item>[28..32) entry_count</item>
        /// <item>[32..36) value_pool_offset</item>
        /// <item>[36..40) value_pool_length</item>
        /// <item>[40..44) zstd_dict_offset</item>
        /// <item>[44..48) zstd_dict_length</item>
        /// <item>[48..52) metadata_offset</item>
        /// <item>[52..56) metadata_length</item>
        /// <item>[56..60) header_crc32 (CRC32 over bytes [0..56))</item>
        /// <item>[60..64) reserved / zero</item>
        /// </list>
        /// </summary>
        public struct Header
        {
            public uint FormatVersion;
            public ulong FileSize;
            public uint FstOffset;
            public uint FstLength;
            public uint EntriesOffset;
            public uint EntryCount;
            public uint ValuePoolOffset;
            public uint ValuePoolLength;
            public uint ZstdDictOffset;
            public uint ZstdDictLength;
            public uint MetadataOffset;
            public uint MetadataLength;

            public void WriteTo(Stream s)
            {
                // Ensure idempotent length.
                var buf = new byte[HeaderSizeBytes];
                Buffer.BlockCopy(Magic, 0, buf, 0, 4);
                LittleEndian.WriteUInt32(buf, 4, FormatVersion);
                LittleEndian.WriteUInt64(buf, 8, FileSize);
                LittleEndian.WriteUInt32(buf, 16, FstOffset);
                LittleEndian.WriteUInt32(buf, 20, FstLength);
                LittleEndian.WriteUInt32(buf, 24, EntriesOffset);
                LittleEndian.WriteUInt32(buf, 28, EntryCount);
                LittleEndian.WriteUInt32(buf, 32, ValuePoolOffset);
                LittleEndian.WriteUInt32(buf, 36, ValuePoolLength);
                LittleEndian.WriteUInt32(buf, 40, ZstdDictOffset);
                LittleEndian.WriteUInt32(buf, 44, ZstdDictLength);
                LittleEndian.WriteUInt32(buf, 48, MetadataOffset);
                LittleEndian.WriteUInt32(buf, 52, MetadataLength);
                uint crc = Crc32.Compute(buf, 0, 56);
                LittleEndian.WriteUInt32(buf, 56, crc);
                // bytes 60..64 left zero.
                s.Write(buf, 0, buf.Length);
            }

            public static Header ReadFrom(ReadOnlySpan<byte> buf)
            {
                if (buf.Length < HeaderSizeBytes)
                    throw new InvalidDataException("Matcher blob truncated — header too short.");
                for (int i = 0; i < 4; i++)
                    if (buf[i] != Magic[i])
                        throw new InvalidDataException("Matcher blob magic mismatch (expected KMX1).");

                uint diskCrc = LittleEndian.ReadUInt32(buf.Slice(56, 4));
                uint actualCrc = Crc32.Compute(buf.Slice(0, 56));
                if (diskCrc != actualCrc)
                    throw new InvalidDataException(
                        $"Matcher blob header CRC mismatch (disk 0x{diskCrc:X8}, computed 0x{actualCrc:X8}).");

                var h = new Header
                {
                    FormatVersion = LittleEndian.ReadUInt32(buf.Slice(4, 4)),
                    FileSize = LittleEndian.ReadUInt64(buf.Slice(8, 8)),
                    FstOffset = LittleEndian.ReadUInt32(buf.Slice(16, 4)),
                    FstLength = LittleEndian.ReadUInt32(buf.Slice(20, 4)),
                    EntriesOffset = LittleEndian.ReadUInt32(buf.Slice(24, 4)),
                    EntryCount = LittleEndian.ReadUInt32(buf.Slice(28, 4)),
                    ValuePoolOffset = LittleEndian.ReadUInt32(buf.Slice(32, 4)),
                    ValuePoolLength = LittleEndian.ReadUInt32(buf.Slice(36, 4)),
                    ZstdDictOffset = LittleEndian.ReadUInt32(buf.Slice(40, 4)),
                    ZstdDictLength = LittleEndian.ReadUInt32(buf.Slice(44, 4)),
                    MetadataOffset = LittleEndian.ReadUInt32(buf.Slice(48, 4)),
                    MetadataLength = LittleEndian.ReadUInt32(buf.Slice(52, 4)),
                };

                if (h.FormatVersion != MatcherBlobSchema.FormatVersion)
                    throw new InvalidDataException(
                        $"Unsupported matcher blob format_version {h.FormatVersion} " +
                        $"(expected {MatcherBlobSchema.FormatVersion}).");
                return h;
            }
        }

        /// <summary>
        /// Fixed-width entry row. Stored sequentially in the entries section
        /// (one per slot id). Size = <see cref="EntrySizeBytes"/>.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct MatcherEntry
        {
            public ulong NgramFlags;
            public uint ValueOffset;
            public uint ValueLength;
            public uint PlaintextLength;
            public uint Reserved;

            public void WriteTo(Span<byte> dest)
            {
                if (dest.Length < EntrySizeBytes) throw new ArgumentException("dest too small");
                LittleEndian.WriteUInt64(dest, 0, NgramFlags);
                LittleEndian.WriteUInt32(dest, 8, ValueOffset);
                LittleEndian.WriteUInt32(dest, 12, ValueLength);
                LittleEndian.WriteUInt32(dest, 16, PlaintextLength);
                LittleEndian.WriteUInt32(dest, 20, Reserved);
            }

            public static MatcherEntry ReadFrom(ReadOnlySpan<byte> src)
            {
                if (src.Length < EntrySizeBytes) throw new ArgumentException("src too small");
                return new MatcherEntry
                {
                    NgramFlags = LittleEndian.ReadUInt64(src.Slice(0, 8)),
                    ValueOffset = LittleEndian.ReadUInt32(src.Slice(8, 4)),
                    ValueLength = LittleEndian.ReadUInt32(src.Slice(12, 4)),
                    PlaintextLength = LittleEndian.ReadUInt32(src.Slice(16, 4)),
                    Reserved = LittleEndian.ReadUInt32(src.Slice(20, 4)),
                };
            }
        }

        /// <summary>Human-readable descriptor payload written at the tail of the blob.</summary>
        public sealed class MatcherMeta
        {
            public uint FormatVersion { get; set; }
            public string CorpusVersion { get; set; } = string.Empty;
            public string Game { get; set; } = string.Empty;
            public string Language { get; set; } = string.Empty;
            public long CreatedUtcTicks { get; set; }
            public uint EntryCount { get; set; }
            public uint AvgKeyLength { get; set; }

            public byte[] Serialize()
            {
                using (var ms = new MemoryStream())
                {
                    var fixedPart = new byte[24];
                    LittleEndian.WriteUInt32(fixedPart, 0, FormatVersion);
                    LittleEndian.WriteUInt32(fixedPart, 4, EntryCount);
                    LittleEndian.WriteUInt32(fixedPart, 8, AvgKeyLength);
                    LittleEndian.WriteInt64(fixedPart, 12, CreatedUtcTicks);
                    // 4 bytes padding at [20..24)
                    ms.Write(fixedPart, 0, fixedPart.Length);
                    WriteLengthPrefixedString(ms, CorpusVersion ?? string.Empty);
                    WriteLengthPrefixedString(ms, Game ?? string.Empty);
                    WriteLengthPrefixedString(ms, Language ?? string.Empty);
                    return ms.ToArray();
                }
            }

            public static MatcherMeta Deserialize(byte[] bytes)
            {
                if (bytes == null) throw new ArgumentNullException(nameof(bytes));
                if (bytes.Length < 24)
                    throw new InvalidDataException("MatcherMeta payload too short.");

                var meta = new MatcherMeta
                {
                    FormatVersion = LittleEndian.ReadUInt32(new ReadOnlySpan<byte>(bytes, 0, 4)),
                    EntryCount = LittleEndian.ReadUInt32(new ReadOnlySpan<byte>(bytes, 4, 4)),
                    AvgKeyLength = LittleEndian.ReadUInt32(new ReadOnlySpan<byte>(bytes, 8, 4)),
                    CreatedUtcTicks = LittleEndian.ReadInt64(new ReadOnlySpan<byte>(bytes, 12, 8)),
                };
                int offset = 24;
                meta.CorpusVersion = ReadLengthPrefixedString(bytes, ref offset);
                meta.Game = ReadLengthPrefixedString(bytes, ref offset);
                meta.Language = ReadLengthPrefixedString(bytes, ref offset);
                return meta;
            }

            private static void WriteLengthPrefixedString(Stream s, string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                if (bytes.Length > ushort.MaxValue)
                    throw new ArgumentException("Metadata string exceeds 65535 bytes.");
                s.WriteByte((byte)(bytes.Length & 0xFF));
                s.WriteByte((byte)((bytes.Length >> 8) & 0xFF));
                s.Write(bytes, 0, bytes.Length);
            }

            private static string ReadLengthPrefixedString(byte[] bytes, ref int offset)
            {
                if (offset + 2 > bytes.Length)
                    throw new InvalidDataException("Truncated metadata string length.");
                int len = bytes[offset] | (bytes[offset + 1] << 8);
                offset += 2;
                if (offset + len > bytes.Length)
                    throw new InvalidDataException("Truncated metadata string payload.");
                string s = Encoding.UTF8.GetString(bytes, offset, len);
                offset += len;
                return s;
            }
        }
    }

    /// <summary>Little-endian byte-span primitives used by the matcher schema.</summary>
    internal static class LittleEndian
    {
        public static void WriteUInt32(byte[] dest, int offset, uint value)
        {
            dest[offset] = (byte)(value & 0xFF);
            dest[offset + 1] = (byte)((value >> 8) & 0xFF);
            dest[offset + 2] = (byte)((value >> 16) & 0xFF);
            dest[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        public static void WriteUInt32(Span<byte> dest, int offset, uint value)
        {
            dest[offset] = (byte)(value & 0xFF);
            dest[offset + 1] = (byte)((value >> 8) & 0xFF);
            dest[offset + 2] = (byte)((value >> 16) & 0xFF);
            dest[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        public static void WriteUInt64(byte[] dest, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++)
                dest[offset + i] = (byte)((value >> (i * 8)) & 0xFF);
        }

        public static void WriteUInt64(Span<byte> dest, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++)
                dest[offset + i] = (byte)((value >> (i * 8)) & 0xFF);
        }

        public static void WriteInt64(byte[] dest, int offset, long value)
        {
            WriteUInt64(dest, offset, unchecked((ulong)value));
        }

        public static uint ReadUInt32(ReadOnlySpan<byte> src)
        {
            return (uint)(src[0] | (src[1] << 8) | (src[2] << 16) | (src[3] << 24));
        }

        public static ulong ReadUInt64(ReadOnlySpan<byte> src)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= ((ulong)src[i]) << (i * 8);
            return v;
        }

        public static long ReadInt64(ReadOnlySpan<byte> src)
        {
            return unchecked((long)ReadUInt64(src));
        }
    }

    /// <summary>
    /// IEEE 802.3 CRC-32 (polynomial 0xEDB88320, reversed). Used to
    /// protect the matcher blob header against silent truncation or
    /// corruption. A table-driven implementation is plenty fast for the
    /// 56-byte header payload.
    /// </summary>
    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                t[i] = c;
            }
            return t;
        }

        public static uint Compute(byte[] buf, int offset, int length)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < length; i++)
                c = Table[(c ^ buf[offset + i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        public static uint Compute(ReadOnlySpan<byte> buf)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < buf.Length; i++)
                c = Table[(c ^ buf[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
