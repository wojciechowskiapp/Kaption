using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ZstdSharp;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// In-process builder for the Kaption Matcher blob (.kmx).
    ///
    /// Writing pipeline:
    ///   1. Sort (key, value) pairs by ordinal key (FST requirement).
    ///   2. Reject duplicate keys up front — the FST builder can't
    ///      accept them.
    ///   3. Compute a ulong n-gram flag bitmap per entry (4-grams,
    ///      FNV-1a hash mod 64).
    ///   4. Compress each PL value with zstd, optionally using a
    ///      trained dict.
    ///   5. Build the FST over the sorted EN keys, slot id = entry index.
    ///   6. Lay out the blob sections and stamp the header CRC.
    ///
    /// Intended callers: the publish-time CLI (future), unit tests, and
    /// the runtime migrator that rewrites an old in-memory corpus to
    /// the new format.
    /// </summary>
    public sealed class MatcherBlobWriter
    {
        /// <summary>
        /// Write a full matcher blob to <paramref name="output"/>.
        /// </summary>
        /// <param name="corpus">
        /// EN → PL map. Duplicate keys trigger
        /// <see cref="ArgumentException"/>. Null keys or null values are
        /// rejected the same way. The input dictionary itself is not
        /// mutated.
        /// </param>
        /// <param name="trainedDictionary">
        /// Pre-trained ZSTD dictionary (or null / zero-length for no
        /// dictionary). Same bytes must be embedded in the blob so the
        /// reader can decompress.
        /// </param>
        /// <param name="meta">Descriptive metadata. Must not be null.</param>
        /// <param name="output">Destination stream; must be writable and seekable.</param>
        /// <param name="compressionLevel">zstd compression level, 1..22. Default 19.</param>
        public static void Write(
            IReadOnlyDictionary<string, string> corpus,
            byte[] trainedDictionary,
            MatcherBlobSchema.MatcherMeta meta,
            Stream output,
            int compressionLevel = 19)
        {
            if (corpus == null) throw new ArgumentNullException(nameof(corpus));
            if (meta == null) throw new ArgumentNullException(nameof(meta));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (!output.CanWrite) throw new ArgumentException("Output stream is not writable.", nameof(output));
            if (!output.CanSeek) throw new ArgumentException("Output stream must be seekable.", nameof(output));

            // 1. Materialize + sort + validate the corpus. Enforcing these here
            //    gives every downstream section a clean shape to work from.
            var sortedKeys = new List<string>(corpus.Count);
            foreach (var kvp in corpus)
            {
                if (kvp.Key == null) throw new ArgumentException("Null key in corpus.");
                if (kvp.Value == null) throw new ArgumentException("Null value in corpus (key='" + kvp.Key + "').");
                if (kvp.Key.Length == 0) throw new ArgumentException("Empty key in corpus.");
                sortedKeys.Add(kvp.Key);
            }
            sortedKeys.Sort(StringComparer.Ordinal);

            // Dupe detection on the sorted list — O(n), no hashing needed.
            for (int i = 1; i < sortedKeys.Count; i++)
            {
                if (string.CompareOrdinal(sortedKeys[i - 1], sortedKeys[i]) == 0)
                    throw new ArgumentException(
                        $"Duplicate key in corpus: '{sortedKeys[i]}'. " +
                        "Dedupe before calling MatcherBlobWriter.Write.");
            }

            int entryCount = sortedKeys.Count;
            byte[] dict = trainedDictionary ?? Array.Empty<byte>();

            // 2. Compress values; compute entries. Value pool is built
            //    incrementally and the per-entry offsets/lengths recorded.
            var valuePool = new MemoryStream();
            var entries = new MatcherBlobSchema.MatcherEntry[entryCount];
            var slotIds = new int[entryCount];

            using (var compressor = new Compressor(compressionLevel))
            {
                if (dict.Length > 0) compressor.LoadDictionary(dict);

                ulong avgKeyLenAccumulator = 0;

                for (int i = 0; i < entryCount; i++)
                {
                    string key = sortedKeys[i];
                    string value = corpus[key];

                    avgKeyLenAccumulator += (ulong)key.Length;

                    byte[] plainValue = Encoding.UTF8.GetBytes(value);
                    int compressBound = Compressor.GetCompressBound(plainValue.Length);
                    byte[] scratch = new byte[compressBound];
                    int compressedLen = compressor.Wrap(plainValue, scratch);
                    if (compressedLen <= 0)
                        throw new InvalidOperationException(
                            $"zstd compression returned {compressedLen} for key '{key}'.");

                    uint valueOffset = (uint)valuePool.Position;
                    valuePool.Write(scratch, 0, compressedLen);

                    entries[i] = new MatcherBlobSchema.MatcherEntry
                    {
                        NgramFlags = NgramFlags(key, 4),
                        ValueOffset = valueOffset,
                        ValueLength = (uint)compressedLen,
                        PlaintextLength = (uint)plainValue.Length,
                        Reserved = 0,
                    };
                    slotIds[i] = i;
                }

                meta.EntryCount = (uint)entryCount;
                meta.AvgKeyLength = entryCount == 0 ? 0u : (uint)(avgKeyLenAccumulator / (ulong)entryCount);
                meta.FormatVersion = MatcherBlobSchema.FormatVersion;
                if (meta.CreatedUtcTicks == 0)
                    meta.CreatedUtcTicks = DateTime.UtcNow.Ticks;
            }

            byte[] valuePoolBytes = valuePool.ToArray();

            // 3. FST over EN keys (sorted). For zero-entry corpora we still
            //    emit an empty FST section so the reader can treat all
            //    blobs uniformly.
            byte[] fstBytes;
            using (var fstBuffer = new MemoryStream())
            {
                if (entryCount == 0)
                {
                    fstBytes = Array.Empty<byte>();
                }
                else
                {
                    var fst = FstKeyIndex.Build(sortedKeys, slotIds);
                    // FstKeyIndex.Save emits a 4-byte length prefix + payload.
                    // We don't want the prefix inside the blob (the blob's
                    // header already carries the FST's byte length). Strip it.
                    fst.Save(fstBuffer);
                    byte[] prefixed = fstBuffer.ToArray();
                    if (prefixed.Length < 4)
                        fstBytes = Array.Empty<byte>();
                    else
                        fstBytes = new ArraySegment<byte>(prefixed, 4, prefixed.Length - 4).ToArrayCompat();
                }
            }

            // 4. Serialize metadata.
            byte[] metaBytes = meta.Serialize();

            // 5. Lay out sections.
            //    order: header | entries | fst | value_pool | zstd_dict | meta
            //    All section starts are 4-byte aligned, which helps both the
            //    FST loader and struct alignment on the entries array.
            long startPos = output.Position;

            uint entriesOffset = (uint)MatcherBlobSchema.HeaderSizeBytes;
            uint entriesLength = (uint)(entryCount * MatcherBlobSchema.EntrySizeBytes);
            uint fstOffset = Align4(entriesOffset + entriesLength);
            uint valuePoolOffset = Align4((uint)(fstOffset + fstBytes.Length));
            uint zstdDictOffset = Align4((uint)(valuePoolOffset + valuePoolBytes.Length));
            uint metaOffset = Align4((uint)(zstdDictOffset + dict.Length));
            ulong fileSize = metaOffset + (uint)metaBytes.Length;

            var header = new MatcherBlobSchema.Header
            {
                FormatVersion = MatcherBlobSchema.FormatVersion,
                FileSize = fileSize,
                FstOffset = fstOffset,
                FstLength = (uint)fstBytes.Length,
                EntriesOffset = entriesOffset,
                EntryCount = (uint)entryCount,
                ValuePoolOffset = valuePoolOffset,
                ValuePoolLength = (uint)valuePoolBytes.Length,
                ZstdDictOffset = zstdDictOffset,
                ZstdDictLength = (uint)dict.Length,
                MetadataOffset = metaOffset,
                MetadataLength = (uint)metaBytes.Length,
            };

            header.WriteTo(output);

            // Entries (contiguous fixed-width rows).
            byte[] entryBuf = new byte[MatcherBlobSchema.EntrySizeBytes];
            for (int i = 0; i < entryCount; i++)
            {
                entries[i].WriteTo(entryBuf);
                output.Write(entryBuf, 0, entryBuf.Length);
            }

            PadToOffset(output, startPos, fstOffset);
            if (fstBytes.Length > 0) output.Write(fstBytes, 0, fstBytes.Length);

            PadToOffset(output, startPos, valuePoolOffset);
            if (valuePoolBytes.Length > 0) output.Write(valuePoolBytes, 0, valuePoolBytes.Length);

            PadToOffset(output, startPos, zstdDictOffset);
            if (dict.Length > 0) output.Write(dict, 0, dict.Length);

            PadToOffset(output, startPos, metaOffset);
            output.Write(metaBytes, 0, metaBytes.Length);
        }

        /// <summary>
        /// Compute a 64-bit bloom of 4-gram hashes for the key. Used by the
        /// matcher's Stage 1 candidate filter: if input.NgramFlags AND
        /// entry.NgramFlags has any bit set, the entry is a candidate for
        /// the Levenshtein stage.
        /// </summary>
        internal static ulong NgramFlags(string key, int ngramSize)
        {
            if (string.IsNullOrEmpty(key)) return 0UL;
            if (key.Length < ngramSize) return 0UL;

            const ulong FNV_OFFSET_BASIS = 14695981039346656037UL;
            const ulong FNV_PRIME = 1099511628211UL;

            ulong flags = 0UL;
            for (int i = 0; i + ngramSize <= key.Length; i++)
            {
                ulong h = FNV_OFFSET_BASIS;
                for (int k = 0; k < ngramSize; k++)
                {
                    h ^= key[i + k];
                    h *= FNV_PRIME;
                }
                flags |= 1UL << (int)(h & 63UL);
            }
            return flags;
        }

        private static uint Align4(uint offset)
        {
            uint r = offset & 3u;
            return r == 0 ? offset : offset + (4u - r);
        }

        private static void PadToOffset(Stream s, long basePos, uint targetOffset)
        {
            long current = s.Position - basePos;
            while (current < targetOffset)
            {
                s.WriteByte(0);
                current++;
            }
        }
    }

    internal static class ArraySegmentExtensions
    {
        public static byte[] ToArrayCompat(this ArraySegment<byte> seg)
        {
            byte[] copy = new byte[seg.Count];
            Buffer.BlockCopy(seg.Array, seg.Offset, copy, 0, seg.Count);
            return copy;
        }
    }
}
