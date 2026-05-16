using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using GI_Subtitles.Services.Security;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// Zero-copy reader for the Kaption Matcher blob (.kmx). Loads from a
    /// <see cref="Stream"/> (for plaintext-on-disk tests and the in-process
    /// writer round-trip) or from a <see cref="MemoryMappedViewAccessor"/>
    /// for the real runtime path where the OS page cache handles paging.
    ///
    /// Thread-safety: <see cref="Lookup(string)"/>, <see cref="GetEntry"/>,
    /// <see cref="TryGetValue"/>, <see cref="GetCompressedValue"/>, and the
    /// FST accessor are all safe for concurrent callers. Mutable crypto
    /// state in the encrypted-mmap backing decryptor is serialized under
    /// its own lock; the ZstdValueDecoder maintains a bounded pool of
    /// per-rent Decompressors.
    /// </summary>
    public sealed class MatcherBlobReader : IDisposable
    {
        private readonly byte[] _buffer;           // used when loaded via Stream (full blob resident)
        private readonly MemoryMappedViewAccessor _accessor; // used when mmap'd
        private readonly MemoryMappedFile _ownedFile;        // disposed if we created it
        private readonly long _viewBase;           // absolute offset to blob start within _accessor
        private readonly int _blobLength;          // size of the blob we're mapping over

        private readonly MatcherBlobSchema.Header _header;
        private readonly MatcherBlobSchema.MatcherMeta _meta;
        private readonly FstKeyIndex _fst;
        private readonly ZstdValueDecoder _decoder;
        private readonly byte[] _zstdDict;

        // Section-resident buffer for the header/FST/entries/metadata
        // (small — ≈12 MB for the entry table + ≈35 MB for the FST on a
        // 488k corpus). Non-null on every code path. For the stream/mmap
        // plaintext path this aliases <see cref="_buffer"/>. For the
        // encrypted-mmap path this is a trimmed copy that DELIBERATELY
        // excludes the value pool (the largest section, ~70 MB at zstd)
        // — value bytes are sliced on demand through
        // <see cref="_ownedDecryptor"/>.
        private readonly byte[] _sectionBytes;
        // For the encrypted-mmap path, <see cref="_sectionBytes"/> holds
        // only the entries table (no header/FST/dict/meta) starting at
        // index zero instead of at the header's EntriesOffset. This
        // shift lets us hand out a tiny O(EntryCount * 24) buffer
        // instead of a full-blob-sized alias. GetEntry subtracts this
        // when computing a row offset.
        private readonly int _entriesBaseAdjustment;
        // When the value pool lives behind a decryptor (encrypted mmap
        // path), this is its plaintext offset into the v3 container.
        // Zero means the value pool is resident inside <see cref="_buffer"/>
        // at <see cref="_header"/>.ValuePoolOffset.
        private readonly long _valuePoolPlaintextOffset;
        // True when GetCompressedValue must go through the decryptor
        // instead of reading from <see cref="_buffer"/>.
        private readonly bool _valuePoolViaDecryptor;
        // Optional mmap decryptor owned by this reader. Non-null when the
        // reader was built via <see cref="LoadFromMmapDecryptor"/>; null
        // when built from plain bytes or a plaintext accessor. Disposing
        // the reader tears the decryptor down (which closes the mmap
        // view and file handle).
        private IMmapDecryptor _ownedDecryptor;
        private int _disposed;

        public MatcherBlobSchema.Header Header => _header;
        public MatcherBlobSchema.MatcherMeta Metadata => _meta;
        public FstKeyIndex Fst => _fst;
        public int EntryCount => (int)_header.EntryCount;

        private MatcherBlobReader(
            byte[] buffer,
            MemoryMappedViewAccessor accessor,
            MemoryMappedFile ownedFile,
            long viewBase,
            int blobLength,
            MatcherBlobSchema.Header header,
            MatcherBlobSchema.MatcherMeta meta,
            FstKeyIndex fst,
            byte[] zstdDict,
            byte[] sectionBytes = null,
            long valuePoolPlaintextOffset = 0,
            bool valuePoolViaDecryptor = false,
            int entriesBaseAdjustment = 0)
        {
            _buffer = buffer;
            _accessor = accessor;
            _ownedFile = ownedFile;
            _viewBase = viewBase;
            _blobLength = blobLength;
            _header = header;
            _meta = meta;
            _fst = fst;
            _zstdDict = zstdDict;
            _decoder = new ZstdValueDecoder(zstdDict);
            _sectionBytes = sectionBytes ?? buffer;
            _valuePoolPlaintextOffset = valuePoolPlaintextOffset;
            _valuePoolViaDecryptor = valuePoolViaDecryptor;
            _entriesBaseAdjustment = entriesBaseAdjustment;
        }

        /// <summary>
        /// Load a matcher blob from an in-memory stream. Reads the full
        /// blob into a private byte[]. Suitable for tests and for
        /// one-shot decrypt-to-memory loads (e.g. the legacy
        /// <c>AesFileProtectionService</c> path). Reads forward-only;
        /// the stream does not need to be seekable.
        /// </summary>
        public static MatcherBlobReader LoadFromStream(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            // Read the whole payload. This is the plaintext path; size is
            // governed by the header's FileSize field, but we don't know
            // it until we've read the header. Grow a MemoryStream.
            byte[] buffer;
            using (var ms = new MemoryStream())
            {
                input.CopyTo(ms);
                buffer = ms.ToArray();
            }
            return Parse(buffer, accessor: null, ownedFile: null, viewBase: 0, blobLength: buffer.Length);
        }

        /// <summary>
        /// Load a matcher blob directly from a file path via a memory-mapped
        /// view. The blob is paged in by the OS on demand — no bulk copy
        /// into managed memory. Lookups walk the view accessor in place.
        /// </summary>
        public static MatcherBlobReader LoadFromFile(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException(path);

            var fi = new FileInfo(path);
            if (fi.Length <= 0 || fi.Length > int.MaxValue)
                throw new InvalidDataException(
                    $"Matcher blob {path} has invalid length {fi.Length}.");

            MemoryMappedFile mmf = null;
            MemoryMappedViewAccessor accessor = null;
            try
            {
                mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0,
                    MemoryMappedFileAccess.Read);
                accessor = mmf.CreateViewAccessor(0, fi.Length, MemoryMappedFileAccess.Read);
                return LoadFromAccessor(accessor, 0, (int)fi.Length, takeOwnership: true, ownedFile: mmf);
            }
            catch
            {
                accessor?.Dispose();
                mmf?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Load a matcher blob from a <see cref="MemoryMappedViewAccessor"/>
        /// that the caller already has open. Used when another layer (the
        /// v3 AES-CTR mmap path, a bigger container file, etc.) owns the
        /// file lifecycle.
        /// </summary>
        public static MatcherBlobReader LoadFromAccessor(
            MemoryMappedViewAccessor accessor,
            long offset,
            int length,
            bool takeOwnership = false,
            MemoryMappedFile ownedFile = null)
        {
            if (accessor == null) throw new ArgumentNullException(nameof(accessor));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (length < MatcherBlobSchema.HeaderSizeBytes)
                throw new InvalidDataException($"Matcher blob too small ({length} bytes).");

            // For simplicity (and because 100-200 MB blobs are tiny next
            // to the PaddleOCR models in process), eager-read the blob into
            // a managed byte[] copy. This keeps the rest of the reader
            // identical whether we came from a Stream or a view accessor,
            // and sidesteps the "accessor returns structs by-value" awkward-
            // ness of MemoryMappedViewAccessor.ReadArray.
            //
            // When the v3 AES-CTR mmap path lands it will supply a
            // per-block decryptor and we'll switch this to slice-on-demand
            // — that's the "real" zero-copy mode. Today's reader still
            // eagerly copies (200 MB managed allocation), which the
            // research doc explicitly called out as the interim
            // integration point.
            byte[] buffer = new byte[length];
            accessor.ReadArray(offset, buffer, 0, length);

            try
            {
                return Parse(buffer,
                    accessor: takeOwnership ? accessor : null,
                    ownedFile: ownedFile,
                    viewBase: offset,
                    blobLength: length);
            }
            catch
            {
                if (takeOwnership)
                {
                    accessor.Dispose();
                    ownedFile?.Dispose();
                }
                throw;
            }
        }

        /// <summary>
        /// Load a matcher blob backed by an <see cref="IMmapDecryptor"/>
        /// over an encrypted v3 .gisub container.
        ///
        /// Memory layout
        /// --------------
        /// The value pool is the single largest section (~70 MB for a
        /// 488k-entry corpus). To deliver the Phase 2 RAM win, this
        /// factory decrypts ONLY the non-value-pool sections into a
        /// managed buffer (header + FST + entries + zstd_dict + metadata
        /// ≈ 50 MB for the 488k corpus). The value pool stays in the
        /// OS page cache; <see cref="GetCompressedValue"/> pulls the
        /// 10-200 bytes of a single value out of the decryptor on demand.
        ///
        /// Ownership
        /// --------------
        /// The decryptor is adopted by the reader — calling <see cref="Dispose"/>
        /// tears down the mmap view + file handle. Any failure in this
        /// factory disposes the decryptor before throwing so the caller
        /// never has to.
        /// </summary>
        public static MatcherBlobReader LoadFromMmapDecryptor(IMmapDecryptor decryptor)
        {
            if (decryptor == null) throw new ArgumentNullException(nameof(decryptor));

            try
            {
                long plaintextLen = decryptor.Length;
                if (plaintextLen <= 0 || plaintextLen > int.MaxValue)
                    throw new InvalidDataException(
                        $"Matcher blob decryptor has invalid plaintext length {plaintextLen}.");

                int length = (int)plaintextLen;

                // 1. Decrypt just the header so we can see where the
                //    value pool lives.
                if (length < MatcherBlobSchema.HeaderSizeBytes)
                    throw new InvalidDataException("Matcher blob decrypted length shorter than header.");

                byte[] headerBytes = new byte[MatcherBlobSchema.HeaderSizeBytes];
                decryptor.ReadPlaintext(0, headerBytes);
                var header = MatcherBlobSchema.Header.ReadFrom(
                    new ReadOnlySpan<byte>(headerBytes, 0, MatcherBlobSchema.HeaderSizeBytes));

                // Structural validation against the plaintext length.
                ValidateSection("entries", header.EntriesOffset,
                    header.EntryCount * (uint)MatcherBlobSchema.EntrySizeBytes, length);
                ValidateSection("fst", header.FstOffset, header.FstLength, length);
                ValidateSection("value_pool", header.ValuePoolOffset, header.ValuePoolLength, length);
                ValidateSection("zstd_dict", header.ZstdDictOffset, header.ZstdDictLength, length);
                ValidateSection("metadata", header.MetadataOffset, header.MetadataLength, length);

                // 2. Decrypt the entries section into its own small
                //    buffer. Size is EntryCount * 24 bytes — 12 MB for a
                //    488k-entry corpus, negligible. This is the only
                //    section GetEntry touches, so a section-scoped
                //    buffer is enough — no need to keep a blob-sized
                //    alias around.
                uint valuePoolStart = header.ValuePoolOffset;
                uint valuePoolLen = header.ValuePoolLength;
                long valuePoolEnd = (long)valuePoolStart + valuePoolLen;
                if (valuePoolEnd > length)
                    throw new InvalidDataException("value_pool section overruns blob length.");

                uint entriesLen = header.EntryCount * (uint)MatcherBlobSchema.EntrySizeBytes;
                byte[] entriesBuf = new byte[entriesLen];
                if (entriesLen > 0)
                {
                    decryptor.ReadPlaintext(header.EntriesOffset, entriesBuf);
                }

                // 3. Decrypt the FST section into its own buffer and hand
                //    it to FstKeyIndex. Lucene copies the bytes into its
                //    own internal structures during Load; after that the
                //    outer byte[] is GC-eligible, so we only pay the FST
                //    cost once (Lucene's internal layout).
                byte[] fstBuf = new byte[header.FstLength];
                if (fstBuf.Length > 0)
                {
                    decryptor.ReadPlaintext(header.FstOffset, fstBuf);
                }
                var fst = FstKeyIndex.LoadFromBytes(fstBuf, 0, fstBuf.Length, (int)header.EntryCount);
                fstBuf = null; // drop the transient buffer; Lucene has its own.

                // 4. Decrypt the zstd dict and metadata into their own
                //    small buffers.
                byte[] zstdDict = new byte[header.ZstdDictLength];
                if (zstdDict.Length > 0)
                {
                    decryptor.ReadPlaintext(header.ZstdDictOffset, zstdDict);
                }

                byte[] metaBytes = new byte[header.MetadataLength];
                if (metaBytes.Length > 0)
                {
                    decryptor.ReadPlaintext(header.MetadataOffset, metaBytes);
                }
                var meta = metaBytes.Length > 0
                    ? MatcherBlobSchema.MatcherMeta.Deserialize(metaBytes)
                    : new MatcherBlobSchema.MatcherMeta();

                // 5. Use entriesBuf AS the _sectionBytes with a shift:
                //    GetEntry subtracts header.EntriesOffset from its
                //    computed offset so slot 0 lands at entriesBuf[0].
                //    This is the actual RAM saving — we hold ONLY the
                //    entries table (≈12 MB for 488k) on the managed
                //    heap, not the full blob.
                var r = new MatcherBlobReader(
                    buffer: null,
                    accessor: null,
                    ownedFile: null,
                    viewBase: 0,
                    blobLength: length,
                    header: header,
                    meta: meta,
                    fst: fst,
                    zstdDict: zstdDict,
                    sectionBytes: entriesBuf,
                    valuePoolPlaintextOffset: valuePoolStart,
                    valuePoolViaDecryptor: true,
                    entriesBaseAdjustment: (int)header.EntriesOffset);
                r._ownedDecryptor = decryptor;
                return r;
            }
            catch
            {
                try { decryptor.Dispose(); } catch { /* best-effort */ }
                throw;
            }
        }

        private static MatcherBlobReader Parse(
            byte[] buffer,
            MemoryMappedViewAccessor accessor,
            MemoryMappedFile ownedFile,
            long viewBase,
            int blobLength)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (buffer.Length < MatcherBlobSchema.HeaderSizeBytes)
                throw new InvalidDataException("Matcher blob truncated — no header.");

            var header = MatcherBlobSchema.Header.ReadFrom(
                new ReadOnlySpan<byte>(buffer, 0, MatcherBlobSchema.HeaderSizeBytes));

            // Structural validation — every section offset must fit inside
            // the buffer; sections must not overlap the header; metadata
            // trails everything.
            ValidateSection("entries", header.EntriesOffset,
                header.EntryCount * (uint)MatcherBlobSchema.EntrySizeBytes, buffer.Length);
            ValidateSection("fst", header.FstOffset, header.FstLength, buffer.Length);
            ValidateSection("value_pool", header.ValuePoolOffset, header.ValuePoolLength, buffer.Length);
            ValidateSection("zstd_dict", header.ZstdDictOffset, header.ZstdDictLength, buffer.Length);
            ValidateSection("metadata", header.MetadataOffset, header.MetadataLength, buffer.Length);

            // Decode metadata.
            byte[] metaBytes = new byte[header.MetadataLength];
            Buffer.BlockCopy(buffer, (int)header.MetadataOffset, metaBytes, 0, metaBytes.Length);
            var meta = MatcherBlobSchema.MatcherMeta.Deserialize(metaBytes);

            // Decode the FST. We share the blob's underlying byte[] but
            // Lucene's FST loader copies the bytes into its own structures
            // during construction, so the outer buffer's lifetime doesn't
            // bind the FST. (We do still keep the buffer alive because
            // Lookup has to read value_pool bytes from it.)
            var fst = FstKeyIndex.LoadFromBytes(buffer,
                (int)header.FstOffset, (int)header.FstLength, (int)header.EntryCount);

            // Copy out the zstd dict — per-thread Decompressor.LoadDictionary
            // is cheap but we want a stable array that outlives the blob
            // buffer in case someone passes a short-lived stream.
            byte[] zstdDict = new byte[header.ZstdDictLength];
            if (zstdDict.Length > 0)
                Buffer.BlockCopy(buffer, (int)header.ZstdDictOffset, zstdDict, 0, zstdDict.Length);

            return new MatcherBlobReader(buffer, accessor, ownedFile, viewBase, blobLength,
                header, meta, fst, zstdDict);
        }

        private static void ValidateSection(string name, uint offset, uint length, int bufferLength)
        {
            if (length == 0 && offset == 0) return;
            // 0-length sections must still have a plausible offset.
            if (offset > bufferLength)
                throw new InvalidDataException($"Matcher blob {name} offset {offset} outside buffer.");
            if (length > (uint)bufferLength)
                throw new InvalidDataException($"Matcher blob {name} length {length} exceeds buffer.");
            if (offset + length > (uint)bufferLength)
                throw new InvalidDataException($"Matcher blob {name} section {offset}+{length} overruns buffer.");
        }

        // --- lookup API -----------------------------------------------------

        /// <summary>
        /// Look up a key and return its slot id (entry index), or -1 if
        /// the key is not present. Safe for concurrent callers.
        /// </summary>
        public int Lookup(string key)
        {
            ThrowIfDisposed();
            return _fst.Lookup(key);
        }

        /// <summary>
        /// Read the entry at the given slot id. Returns a copy (the entry
        /// is a 24-byte value struct so this is effectively free).
        /// </summary>
        public MatcherBlobSchema.MatcherEntry GetEntry(int slotId)
        {
            ThrowIfDisposed();
            if (slotId < 0 || slotId >= EntryCount)
                throw new ArgumentOutOfRangeException(nameof(slotId));
            int offset = (int)_header.EntriesOffset + slotId * MatcherBlobSchema.EntrySizeBytes - _entriesBaseAdjustment;
            return MatcherBlobSchema.MatcherEntry.ReadFrom(
                new ReadOnlySpan<byte>(_sectionBytes, offset, MatcherBlobSchema.EntrySizeBytes));
        }

        /// <summary>
        /// Return a read-only view of the compressed value bytes for the
        /// given entry. For the resident path this points directly into
        /// the blob's buffer and is zero-alloc. For the encrypted-mmap
        /// path (value pool lives behind a decryptor) the bytes are
        /// decrypted into <paramref name="scratch"/> and a span over that
        /// scratch is returned; the caller must keep <paramref name="scratch"/>
        /// live for as long as they use the returned span.
        /// </summary>
        public ReadOnlySpan<byte> GetCompressedValue(MatcherBlobSchema.MatcherEntry entry, Span<byte> scratch = default)
        {
            ThrowIfDisposed();
            if (_valuePoolViaDecryptor)
            {
                int len = (int)entry.ValueLength;
                if (scratch.Length < len)
                    throw new ArgumentException(
                        $"scratch must be at least {len} bytes for the encrypted value-pool path (got {scratch.Length}).");
                long plaintextOffset = _valuePoolPlaintextOffset + entry.ValueOffset;
                _ownedDecryptor.ReadPlaintext(plaintextOffset, scratch.Slice(0, len));
                return scratch.Slice(0, len);
            }

            uint start = _header.ValuePoolOffset + entry.ValueOffset;
            if (start + entry.ValueLength > _sectionBytes.Length)
                throw new InvalidDataException("Entry value range falls outside value pool.");
            return new ReadOnlySpan<byte>(_sectionBytes, (int)start, (int)entry.ValueLength);
        }

        /// <summary>
        /// Look up a key and decode its PL value into a managed string.
        /// Returns false when the key is not in the FST. Allocates one
        /// string per call (and pools a scratch byte buffer via
        /// <see cref="ArrayPool{T}"/>).
        /// </summary>
        public bool TryGetValue(string key, out string value)
        {
            ThrowIfDisposed();
            int slot = _fst.Lookup(key);
            if (slot < 0) { value = null; return false; }

            var entry = GetEntry(slot);
            int plain = (int)entry.PlaintextLength;
            if (plain <= 0)
            {
                value = string.Empty;
                return true;
            }

            byte[] rentedOut = ArrayPool<byte>.Shared.Rent(plain);
            byte[] rentedIn = null;
            try
            {
                ReadOnlySpan<byte> compressed;
                if (_valuePoolViaDecryptor)
                {
                    int cLen = (int)entry.ValueLength;
                    rentedIn = ArrayPool<byte>.Shared.Rent(cLen);
                    compressed = GetCompressedValue(entry, rentedIn.AsSpan(0, cLen));
                }
                else
                {
                    compressed = GetCompressedValue(entry);
                }

                int n = _decoder.Decode(compressed, rentedOut.AsSpan(0, plain));
                value = Encoding.UTF8.GetString(rentedOut, 0, n);
                return true;
            }
            finally
            {
                if (rentedIn != null) ArrayPool<byte>.Shared.Return(rentedIn);
                ArrayPool<byte>.Shared.Return(rentedOut);
            }
        }

        /// <summary>
        /// Decode a value by slot id into <paramref name="destination"/>.
        /// Hot-path accessor used by <see cref="OptimizedMatcher"/> once
        /// the winning candidate is known.
        /// </summary>
        public int DecodeValue(int slotId, Span<byte> destination)
        {
            ThrowIfDisposed();
            var entry = GetEntry(slotId);

            if (_valuePoolViaDecryptor)
            {
                // Rent one scratch for the compressed bytes. Pool-hit
                // so this is zero-alloc on steady state.
                int cLen = (int)entry.ValueLength;
                byte[] rented = ArrayPool<byte>.Shared.Rent(cLen);
                try
                {
                    var compressed = GetCompressedValue(entry, rented.AsSpan(0, cLen));
                    return _decoder.Decode(compressed, destination);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }

            return _decoder.Decode(GetCompressedValue(entry), destination);
        }

        /// <summary>
        /// Enumerate every (key, slotId) pair in byte-sorted order.
        /// Allocates — do not use from the OCR hot path.
        /// </summary>
        public System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, int>> EnumerateKeys()
        {
            ThrowIfDisposed();
            return _fst.EnumerateAll();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(MatcherBlobReader));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _decoder?.Dispose(); } catch { /* best-effort */ }
            try { _accessor?.Dispose(); } catch { /* best-effort */ }
            try { _ownedFile?.Dispose(); } catch { /* best-effort */ }
            try { _ownedDecryptor?.Dispose(); } catch { /* best-effort */ }
            _ownedDecryptor = null;
        }
    }
}
