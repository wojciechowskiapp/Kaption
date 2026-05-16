using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Lucene.Net.Util.Fst;
using LuceneFstUtil = Lucene.Net.Util.Fst.Util;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// Thin wrapper around Lucene.Net's <see cref="FST{T}"/> with
    /// <c>ByteSequenceOutputs</c>. Each key in the FST maps to a fixed
    /// 4-byte little-endian payload carrying the key's slot id (the index
    /// into the matcher's parallel entry table).
    ///
    /// The FST is Lucene's compact byte-blob representation of a sorted
    /// set of (key → bytes) pairs. Lookups are O(|key|) byte walks with
    /// no heap allocations beyond the UTF-8 conversion of the key.
    ///
    /// Thread-safety: <see cref="Lookup"/> is safe for concurrent callers
    /// (uses ThreadStatic scratch buffers; FST itself is read-only).
    /// </summary>
    public sealed class FstKeyIndex
    {
        private readonly FST<BytesRef> _fst;

        /// <summary>Number of distinct keys indexed in this FST.</summary>
        public int Count { get; }

        [ThreadStatic]
        private static BytesRef _scratchKey;
        [ThreadStatic]
        private static Int32sRef _scratchInts;

        private FstKeyIndex(FST<BytesRef> fst, int count)
        {
            _fst = fst;
            Count = count;
        }

        /// <summary>
        /// Build a new FST from the given keys. Keys must be:
        ///   * non-null, non-empty,
        ///   * byte-sorted (UTF-8 <see cref="StringComparer.Ordinal"/>) and
        ///   * unique — duplicates are rejected with <see cref="ArgumentException"/>.
        /// </summary>
        /// <param name="sortedKeys">Keys in byte-sorted order (ordinal).</param>
        /// <param name="slotIds">
        /// Parallel array of non-negative slot ids assigned to each key.
        /// Typically the index into the matcher entry table. Caller promises
        /// the ids fit in uint32 (we have 488k entries, so this is always fine).
        /// </param>
        public static FstKeyIndex Build(IReadOnlyList<string> sortedKeys, IReadOnlyList<int> slotIds)
        {
            if (sortedKeys == null) throw new ArgumentNullException(nameof(sortedKeys));
            if (slotIds == null) throw new ArgumentNullException(nameof(slotIds));
            if (sortedKeys.Count != slotIds.Count)
                throw new ArgumentException("sortedKeys and slotIds must be the same length.");

            if (sortedKeys.Count == 0)
                return new FstKeyIndex(null, 0);

            var outputs = ByteSequenceOutputs.Singleton;
            var builder = new Builder<BytesRef>(FST.INPUT_TYPE.BYTE1, outputs);
            var scratchKey = new BytesRef();
            var scratchInts = new Int32sRef();

            string previous = null;
            for (int i = 0; i < sortedKeys.Count; i++)
            {
                string key = sortedKeys[i];
                if (string.IsNullOrEmpty(key))
                    throw new ArgumentException($"Key at index {i} is null or empty.");

                if (previous != null)
                {
                    int cmp = string.CompareOrdinal(previous, key);
                    if (cmp == 0)
                        throw new ArgumentException(
                            $"Duplicate key at index {i}: '{key}'. FST requires unique keys.");
                    if (cmp > 0)
                        throw new ArgumentException(
                            $"Keys not in ordinal-sorted order at index {i}: '{previous}' > '{key}'.");
                }
                previous = key;

                int slotId = slotIds[i];
                if (slotId < 0)
                    throw new ArgumentException(
                        $"Slot id must be non-negative (got {slotId} at index {i}).");

                scratchKey.CopyChars(key);
                LuceneFstUtil.ToInt32sRef(scratchKey, scratchInts);

                // 4 bytes little-endian slot id.
                byte[] payload = new byte[4];
                payload[0] = (byte)(slotId & 0xFF);
                payload[1] = (byte)((slotId >> 8) & 0xFF);
                payload[2] = (byte)((slotId >> 16) & 0xFF);
                payload[3] = (byte)((slotId >> 24) & 0xFF);
                builder.Add(scratchInts, new BytesRef(payload));
            }

            FST<BytesRef> fst = builder.Finish();
            // Empty corpus path is handled above; single-entry and up yield a
            // non-null FST from Finish().
            return new FstKeyIndex(fst, sortedKeys.Count);
        }

        /// <summary>
        /// Build an empty FST index. Used for the "no entries yet" short-circuit
        /// so callers can always treat the index as non-null.
        /// </summary>
        public static FstKeyIndex Empty() => new FstKeyIndex(null, 0);

        /// <summary>
        /// Serialize this FST to <paramref name="output"/>. Writes a 4-byte
        /// length prefix followed by the Lucene FST bytes (or a zero-length
        /// marker for an empty index). The caller owns the stream.
        /// </summary>
        public void Save(Stream output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));

            if (_fst == null)
            {
                WriteInt32(output, 0);
                return;
            }

            using (var scratch = new MemoryStream())
            {
                using (var dout = new OutputStreamDataOutput(scratch))
                {
                    _fst.Save(dout);
                }
                byte[] bytes = scratch.ToArray();
                WriteInt32(output, bytes.Length);
                output.Write(bytes, 0, bytes.Length);
            }
        }

        /// <summary>
        /// Deserialize an FST previously written by <see cref="Save"/>.
        /// </summary>
        public static FstKeyIndex Load(Stream input, int declaredKeyCount)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            int length = ReadInt32(input);
            if (length < 0)
                throw new InvalidDataException($"Invalid FST length prefix: {length}.");
            if (length == 0)
                return new FstKeyIndex(null, 0);

            byte[] bytes = new byte[length];
            ReadExactly(input, bytes, 0, length);

            using (var scratch = new MemoryStream(bytes, writable: false))
            using (var din = new InputStreamDataInput(scratch))
            {
                var fst = new FST<BytesRef>(din, ByteSequenceOutputs.Singleton);
                return new FstKeyIndex(fst, declaredKeyCount);
            }
        }

        /// <summary>
        /// Load the FST straight from a raw byte buffer. Used by the
        /// MatcherBlobReader when deserializing the combined matcher blob.
        /// </summary>
        public static FstKeyIndex LoadFromBytes(byte[] buffer, int offset, int length, int declaredKeyCount)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || length < 0 || offset + length > buffer.Length)
                throw new ArgumentOutOfRangeException();
            if (length == 0)
                return new FstKeyIndex(null, declaredKeyCount);

            using (var ms = new MemoryStream(buffer, offset, length, writable: false))
            using (var din = new InputStreamDataInput(ms))
            {
                var fst = new FST<BytesRef>(din, ByteSequenceOutputs.Singleton);
                return new FstKeyIndex(fst, declaredKeyCount);
            }
        }

        /// <summary>
        /// Look up a key and return its slot id, or -1 if the key is not
        /// in the FST. Safe for concurrent callers.
        /// </summary>
        public int Lookup(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (key.Length == 0 || _fst == null) return -1;

            var scratchKey = _scratchKey;
            if (scratchKey == null) _scratchKey = scratchKey = new BytesRef();
            var scratchInts = _scratchInts;
            if (scratchInts == null) _scratchInts = scratchInts = new Int32sRef();

            scratchKey.CopyChars(key);
            LuceneFstUtil.ToInt32sRef(scratchKey, scratchInts);

            BytesRef output = LuceneFstUtil.Get(_fst, scratchInts);
            if (output == null) return -1;
            if (output.Length < 4) return -1;

            int b0 = output.Bytes[output.Offset] & 0xFF;
            int b1 = output.Bytes[output.Offset + 1] & 0xFF;
            int b2 = output.Bytes[output.Offset + 2] & 0xFF;
            int b3 = output.Bytes[output.Offset + 3] & 0xFF;
            return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
        }

        /// <summary>
        /// Enumerate all (key, slotId) pairs in byte-sorted order.
        /// Used for verification / test round-trips. Allocates — do not call
        /// from hot paths.
        /// </summary>
        public IEnumerable<KeyValuePair<string, int>> EnumerateAll()
        {
            if (_fst == null) yield break;

            var enumerator = new BytesRefFSTEnum<BytesRef>(_fst);
            while (enumerator.MoveNext())
            {
                var io = enumerator.Current;
                if (io == null) continue;
                var input = io.Input;
                string key = Encoding.UTF8.GetString(input.Bytes, input.Offset, input.Length);
                var output = io.Output;
                int slotId = -1;
                if (output != null && output.Length >= 4)
                {
                    int b0 = output.Bytes[output.Offset] & 0xFF;
                    int b1 = output.Bytes[output.Offset + 1] & 0xFF;
                    int b2 = output.Bytes[output.Offset + 2] & 0xFF;
                    int b3 = output.Bytes[output.Offset + 3] & 0xFF;
                    slotId = b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
                }
                yield return new KeyValuePair<string, int>(key, slotId);
            }
        }

        // --- small helpers --------------------------------------------------

        private static void WriteInt32(Stream s, int v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
            s.WriteByte((byte)((v >> 16) & 0xFF));
            s.WriteByte((byte)((v >> 24) & 0xFF));
        }

        private static int ReadInt32(Stream s)
        {
            int b0 = s.ReadByte();
            int b1 = s.ReadByte();
            int b2 = s.ReadByte();
            int b3 = s.ReadByte();
            if ((b0 | b1 | b2 | b3) < 0)
                throw new EndOfStreamException("Truncated stream while reading int32.");
            return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
        }

        private static void ReadExactly(Stream s, byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buffer, offset + read, count - read);
                if (n <= 0) throw new EndOfStreamException(
                    $"Truncated stream: expected {count} bytes, got {read}.");
                read += n;
            }
        }
    }
}
