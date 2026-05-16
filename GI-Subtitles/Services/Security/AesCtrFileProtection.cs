using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Threading;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// Writer and reader helpers for v3 .gisub files (AES-CTR + per-block
    /// HMAC-SHA256). Intentionally static — key material is supplied by the
    /// caller (<see cref="ServerKeyFileProtectionService"/> derives keys from
    /// the per-device server-issued secret + machine fingerprint and passes
    /// them in); this file only knows how to lay bytes out on disk.
    ///
    /// Three entry points:
    ///   - <see cref="EncryptBytesToV3(byte[], string, ProtectedFileFormatV3.FileCategory, byte[], byte[], int)"/>
    ///     (plus the stream overload) writes a new .gisub v3 file.
    ///   - <see cref="OpenDecryptStream"/> returns a seekable, read-only
    ///     <see cref="Stream"/> that decrypts + HMAC-verifies on demand.
    ///   - <see cref="OpenMmapDecryptor"/> returns a zero-alloc random-access
    ///     <see cref="IMmapDecryptor"/> backed by MemoryMappedFile.
    /// </summary>
    internal static class AesCtrFileProtection
    {
        /// <summary>
        /// Encrypt <paramref name="plaintext"/> and write a v3 .gisub file to
        /// <paramref name="destinationPath"/> atomically (write-to-tmp +
        /// rename). Throws on any IO or crypto failure; leaves the
        /// destination file untouched on failure and cleans up the temp file.
        /// </summary>
        public static void EncryptBytesToV3(
            byte[] plaintext,
            string destinationPath,
            ProtectedFileFormatV3.FileCategory category,
            byte[] encryptionKey,
            byte[] hmacKey,
            int blockSize = ProtectedFileFormatV3.DefaultBlockSize)
        {
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
            using (var src = new MemoryStream(plaintext, writable: false))
            {
                EncryptStreamToV3(src, plaintext.LongLength, destinationPath, category, encryptionKey, hmacKey, blockSize);
            }
        }

        /// <summary>
        /// Stream-based v3 writer — reads exactly <paramref name="plaintextLength"/>
        /// bytes from <paramref name="source"/> and emits the .gisub v3 file.
        /// The caller is responsible for the source stream's lifetime.
        /// </summary>
        public static void EncryptStreamToV3(
            Stream source,
            long plaintextLength,
            string destinationPath,
            ProtectedFileFormatV3.FileCategory category,
            byte[] encryptionKey,
            byte[] hmacKey,
            int blockSize = ProtectedFileFormatV3.DefaultBlockSize)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrEmpty(destinationPath)) throw new ArgumentException("destinationPath required");
            if (encryptionKey == null) throw new ArgumentNullException(nameof(encryptionKey));
            if (hmacKey == null) throw new ArgumentNullException(nameof(hmacKey));
            if (plaintextLength < 0) throw new ArgumentOutOfRangeException(nameof(plaintextLength));
            if (blockSize <= 0 || blockSize > ProtectedFileFormatV3.MaxBlockSize)
                throw new ArgumentOutOfRangeException(nameof(blockSize));

            // Fresh random nonce per file. NEVER reused across files.
            // Net8: RandomNumberGenerator.Fill replaces RNGCryptoServiceProvider
            // (SYSLIB0023) — same OS CSPRNG, zero-alloc, no IDisposable wrapper.
            var nonceBase = new byte[ProtectedFileFormatV3.NonceBaseSize];
            RandomNumberGenerator.Fill(nonceBase);

            string tmpPath = destinationPath + ".tmp";
            FileStream output = null;
            byte[] ptBuf = null;
            byte[] ctBuf = null;
            byte[] hmacInput = null;
            HMACSHA256 blockHmac = null;
            HMACSHA256 headerHmac = null;
            bool success = false;
            try
            {
                output = new FileStream(
                    tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 65536);

                // ── Write header ──────────────────────────────────────────
                var header = new byte[ProtectedFileFormatV3.HeaderSize];
                ProtectedFileFormatV3.WriteHeaderPrefix(
                    header, nonceBase, blockSize, plaintextLength, category);

                using (headerHmac = new HMACSHA256(hmacKey))
                {
                    byte[] hHmac = headerHmac.ComputeHash(header, 0, ProtectedFileFormatV3.HeaderSignedPrefix);
                    Buffer.BlockCopy(hHmac, 0, header, ProtectedFileFormatV3.HeaderHmacOffset, ProtectedFileFormatV3.HeaderHmacSize);
                }
                output.Write(header, 0, header.Length);

                // ── Write blocks ──────────────────────────────────────────
                ptBuf = ArrayPool<byte>.Shared.Rent(blockSize);
                ctBuf = ArrayPool<byte>.Shared.Rent(blockSize);
                // HMAC input per block: nonceBase (16) + blockIndex (8) + ciphertext (up to blockSize).
                hmacInput = ArrayPool<byte>.Shared.Rent(16 + 8 + blockSize);
                blockHmac = new HMACSHA256(hmacKey);

                long written = 0;
                long blockIndex = 0;
                while (written < plaintextLength)
                {
                    int toRead = (int)Math.Min((long)blockSize, plaintextLength - written);

                    int got = 0;
                    while (got < toRead)
                    {
                        int n = source.Read(ptBuf, got, toRead - got);
                        if (n == 0)
                            throw new EndOfStreamException(
                                "Source stream ended " + (plaintextLength - written - got) +
                                " bytes short of declared length");
                        got += n;
                    }

                    // Encrypt this block in place.
                    AesCtrProtection.EncryptBlock(
                        new ReadOnlySpan<byte>(ptBuf, 0, toRead),
                        new Span<byte>(ctBuf, 0, toRead),
                        encryptionKey,
                        nonceBase,
                        (ulong)blockIndex);

                    // HMAC = H(nonceBase || blockIndex_BE || ciphertext)
                    Buffer.BlockCopy(nonceBase, 0, hmacInput, 0, ProtectedFileFormatV3.NonceBaseSize);
                    WriteUInt64BE(hmacInput, ProtectedFileFormatV3.NonceBaseSize, (ulong)blockIndex);
                    Buffer.BlockCopy(ctBuf, 0, hmacInput, ProtectedFileFormatV3.NonceBaseSize + 8, toRead);

                    byte[] blockTag = blockHmac.ComputeHash(hmacInput, 0, ProtectedFileFormatV3.NonceBaseSize + 8 + toRead);

                    output.Write(ctBuf, 0, toRead);
                    output.Write(blockTag, 0, blockTag.Length);

                    written += toRead;
                    blockIndex++;
                }

                output.Flush();
                output.Dispose();
                output = null;

                // Atomic replace. File.Replace handles the case where the
                // destination already exists (no separate Delete step that
                // could race with a concurrent reader).
                if (File.Exists(destinationPath))
                {
                    File.Replace(tmpPath, destinationPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tmpPath, destinationPath);
                }
                success = true;
            }
            finally
            {
                blockHmac?.Dispose();
                // Scrub pooled buffers (contain plaintext fragments).
                if (ptBuf != null)
                {
                    Array.Clear(ptBuf, 0, Math.Min(ptBuf.Length, blockSize));
                    ArrayPool<byte>.Shared.Return(ptBuf, clearArray: false);
                }
                if (ctBuf != null)
                {
                    Array.Clear(ctBuf, 0, Math.Min(ctBuf.Length, blockSize));
                    ArrayPool<byte>.Shared.Return(ctBuf, clearArray: false);
                }
                if (hmacInput != null)
                {
                    Array.Clear(hmacInput, 0, Math.Min(hmacInput.Length, 16 + 8 + blockSize));
                    ArrayPool<byte>.Shared.Return(hmacInput, clearArray: false);
                }

                output?.Dispose();

                // Clean up the temp file on any failure (including partial write).
                if (!success)
                {
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
                }
            }
        }

        /// <summary>
        /// Open a seekable, read-only plaintext stream over a v3 .gisub file.
        /// The returned stream decrypts lazily, one block at a time, and
        /// verifies each block's HMAC before returning its bytes.
        /// Disposing the stream releases the underlying file handle.
        /// </summary>
        public static Stream OpenDecryptStream(string path, byte[] encryptionKey, byte[] hmacKey)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path required");
            if (encryptionKey == null) throw new ArgumentNullException(nameof(encryptionKey));
            if (hmacKey == null) throw new ArgumentNullException(nameof(hmacKey));

            FileStream fs = null;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var header = ProtectedFileFormatV3.ReadAndVerifyHeader(fs, hmacKey);

                // Verify the file is exactly the expected size — a truncated
                // file would otherwise only be caught when we read the last
                // block. This also rejects trailing garbage.
                long expected = ProtectedFileFormatV3.ExpectedFileSize(
                    header.PlaintextLength, header.BlockSize);
                if (fs.Length != expected)
                {
                    throw new InvalidDataException(
                        "v3 file length mismatch: expected " + expected +
                        " bytes, got " + fs.Length);
                }

                // Transfer ownership of fs to the stream wrapper.
                var stream = new V3DecryptStream(fs, header, encryptionKey, hmacKey);
                fs = null;
                return stream;
            }
            finally
            {
                fs?.Dispose();
            }
        }

        /// <summary>
        /// Open a zero-alloc, random-access decryptor over a v3 .gisub file.
        /// Uses MemoryMappedFile so the OS page cache is the working set
        /// (no managed heap growth per read). Caller must Dispose.
        /// </summary>
        public static IMmapDecryptor OpenMmapDecryptor(string path, byte[] encryptionKey, byte[] hmacKey)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path required");
            if (encryptionKey == null) throw new ArgumentNullException(nameof(encryptionKey));
            if (hmacKey == null) throw new ArgumentNullException(nameof(hmacKey));

            MemoryMappedFile mmf = null;
            MemoryMappedViewAccessor view = null;
            try
            {
                // Grab the real file length BEFORE mapping. The mapped view's
                // Capacity rounds up to the OS page size, so we can't rely
                // on it for tamper / truncation detection — check against
                // the FileInfo size instead.
                long fileLength = new FileInfo(path).Length;

                // MemoryMappedFileAccess.Read + FileAccess.Read allow other
                // readers concurrently; no writer will compete (v3 files are
                // immutable once written — migrator always writes a fresh one).
                mmf = MemoryMappedFile.CreateFromFile(
                    path,
                    FileMode.Open,
                    mapName: null,
                    capacity: 0,
                    access: MemoryMappedFileAccess.Read);

                view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

                // Read + verify the header through the view.
                var headerBytes = new byte[ProtectedFileFormatV3.HeaderSize];
                view.ReadArray(0, headerBytes, 0, headerBytes.Length);
                var header = ProtectedFileFormatV3.ParseAndVerifyHeader(headerBytes, 0, hmacKey);

                long expected = ProtectedFileFormatV3.ExpectedFileSize(
                    header.PlaintextLength, header.BlockSize);
                if (fileLength != expected)
                {
                    throw new InvalidDataException(
                        "v3 file length mismatch (mmap): expected " + expected +
                        " bytes, got " + fileLength);
                }

                var dec = new MmapDecryptorImpl(mmf, view, header, encryptionKey, hmacKey);
                mmf  = null; // ownership transferred
                view = null;
                return dec;
            }
            finally
            {
                view?.Dispose();
                mmf?.Dispose();
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────

        internal static void WriteUInt64BE(byte[] buf, int offset, ulong value)
        {
            buf[offset + 0] = (byte)(value >> 56);
            buf[offset + 1] = (byte)(value >> 48);
            buf[offset + 2] = (byte)(value >> 40);
            buf[offset + 3] = (byte)(value >> 32);
            buf[offset + 4] = (byte)(value >> 24);
            buf[offset + 5] = (byte)(value >> 16);
            buf[offset + 6] = (byte)(value >>  8);
            buf[offset + 7] = (byte)(value      );
        }

        // ══════════════════════════════════════════════════════════════════
        //  V3DecryptStream — lazy per-block decryption wrapped in a Stream.
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Seekable, read-only stream over the plaintext of a v3 file.
        /// Keeps at most one decrypted block in memory (an internal cache).
        /// Thread-affine: one thread at a time.
        /// </summary>
        private sealed class V3DecryptStream : Stream
        {
            private FileStream _file;
            private readonly byte[] _nonceBase;
            private readonly int _blockSize;
            private readonly long _plaintextLength;
            private readonly long _blockCount;

            private byte[] _blockPlain;     // pooled
            private byte[] _blockCipher;    // pooled
            private byte[] _hmacInput;      // pooled
            private HMACSHA256 _hmac;
            private AesCtrProtection.ReusableEcbEncryptor _aesEcb;

            private long _cachedBlockIndex = -1; // -1 means nothing cached
            private long _position;
            private bool _disposed;

            public V3DecryptStream(
                FileStream file,
                ProtectedFileFormatV3.HeaderInfo header,
                byte[] encryptionKey,
                byte[] hmacKey)
            {
                _file = file;
                _nonceBase = header.NonceBase;
                _blockSize = header.BlockSize;
                _plaintextLength = header.PlaintextLength;
                _blockCount = ProtectedFileFormatV3.ComputeBlockCount(_plaintextLength, _blockSize);

                _blockPlain  = ArrayPool<byte>.Shared.Rent(_blockSize);
                _blockCipher = ArrayPool<byte>.Shared.Rent(_blockSize);
                _hmacInput   = ArrayPool<byte>.Shared.Rent(16 + 8 + _blockSize);
                try
                {
                    _hmac   = new HMACSHA256(hmacKey);
                    _aesEcb = AesCtrProtection.CreateReusableEncryptor(encryptionKey);
                }
                catch
                {
                    _hmac?.Dispose();
                    _aesEcb?.Dispose();
                    if (_blockPlain  != null) ArrayPool<byte>.Shared.Return(_blockPlain,  clearArray: false);
                    if (_blockCipher != null) ArrayPool<byte>.Shared.Return(_blockCipher, clearArray: false);
                    if (_hmacInput   != null) ArrayPool<byte>.Shared.Return(_hmacInput,   clearArray: false);
                    throw;
                }
            }

            public override bool CanRead  => !_disposed;
            public override bool CanSeek  => !_disposed;
            public override bool CanWrite => false;
            public override long Length   => _plaintextLength;

            public override long Position
            {
                get => _position;
                set
                {
                    if (value < 0 || value > _plaintextLength)
                        throw new ArgumentOutOfRangeException(nameof(value));
                    _position = value;
                }
            }

            public override void Flush() { /* read-only */ }

            public override long Seek(long offset, SeekOrigin origin)
            {
                long target;
                switch (origin)
                {
                    case SeekOrigin.Begin:   target = offset; break;
                    case SeekOrigin.Current: target = _position + offset; break;
                    case SeekOrigin.End:     target = _plaintextLength + offset; break;
                    default: throw new ArgumentOutOfRangeException(nameof(origin));
                }
                if (target < 0 || target > _plaintextLength)
                    throw new IOException("Seek out of range");
                _position = target;
                return _position;
            }

            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(V3DecryptStream));
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (offset < 0 || count < 0 || offset + count > buffer.Length)
                    throw new ArgumentOutOfRangeException();

                if (_position >= _plaintextLength || count == 0) return 0;

                long remainingFile = _plaintextLength - _position;
                int toRead = (int)Math.Min(remainingFile, (long)count);
                int done = 0;

                while (done < toRead)
                {
                    long blockIndex = _position / _blockSize;
                    int intraOffset = (int)(_position - blockIndex * _blockSize);
                    int blockPtSize = ProtectedFileFormatV3.BlockPlaintextSize(
                        blockIndex, _plaintextLength, _blockSize);

                    if (_cachedBlockIndex != blockIndex)
                    {
                        DecryptBlockIntoCache(blockIndex, blockPtSize);
                        _cachedBlockIndex = blockIndex;
                    }

                    int availInBlock = blockPtSize - intraOffset;
                    int copy = Math.Min(availInBlock, toRead - done);
                    Buffer.BlockCopy(_blockPlain, intraOffset, buffer, offset + done, copy);
                    _position += copy;
                    done += copy;
                }

                return done;
            }

            private void DecryptBlockIntoCache(long blockIndex, int blockPtSize)
            {
                long fileOff = ProtectedFileFormatV3.BlockFileOffset(blockIndex, _blockSize);
                _file.Position = fileOff;

                int got = 0;
                while (got < blockPtSize)
                {
                    int n = _file.Read(_blockCipher, got, blockPtSize - got);
                    if (n == 0) throw new EndOfStreamException("Ciphertext truncated at block " + blockIndex);
                    got += n;
                }
                var tag = new byte[ProtectedFileFormatV3.BlockHmacSize];
                got = 0;
                while (got < tag.Length)
                {
                    int n = _file.Read(tag, got, tag.Length - got);
                    if (n == 0) throw new EndOfStreamException("HMAC truncated at block " + blockIndex);
                    got += n;
                }

                // Verify HMAC: H(nonceBase || blockIndex_BE || ciphertext)
                Buffer.BlockCopy(_nonceBase, 0, _hmacInput, 0, ProtectedFileFormatV3.NonceBaseSize);
                WriteUInt64BE(_hmacInput, ProtectedFileFormatV3.NonceBaseSize, (ulong)blockIndex);
                Buffer.BlockCopy(_blockCipher, 0, _hmacInput, ProtectedFileFormatV3.NonceBaseSize + 8, blockPtSize);

                byte[] computed = _hmac.ComputeHash(_hmacInput, 0,
                    ProtectedFileFormatV3.NonceBaseSize + 8 + blockPtSize);

                if (!ProtectedFileFormatV3.ConstantTimeEquals(tag, computed))
                    throw new CryptographicException(
                        "v3 block HMAC verification failed at block " + blockIndex);

                // Decrypt into the plaintext scratch buffer.
                AesCtrProtection.CryptBlockWith(
                    _aesEcb.Encryptor,
                    new ReadOnlySpan<byte>(_blockCipher, 0, blockPtSize),
                    new Span<byte>(_blockPlain, 0, blockPtSize),
                    _nonceBase, (ulong)blockIndex);
            }

            protected override void Dispose(bool disposing)
            {
                if (_disposed) { base.Dispose(disposing); return; }
                _disposed = true;
                try
                {
                    if (disposing)
                    {
                        _file?.Dispose();
                        _hmac?.Dispose();
                        _aesEcb?.Dispose();
                    }
                }
                finally
                {
                    _file = null;
                    _hmac = null;
                    _aesEcb = null;
                    if (_blockPlain != null)
                    {
                        Array.Clear(_blockPlain, 0, _blockSize);
                        ArrayPool<byte>.Shared.Return(_blockPlain, clearArray: false);
                        _blockPlain = null;
                    }
                    if (_blockCipher != null)
                    {
                        Array.Clear(_blockCipher, 0, _blockSize);
                        ArrayPool<byte>.Shared.Return(_blockCipher, clearArray: false);
                        _blockCipher = null;
                    }
                    if (_hmacInput != null)
                    {
                        Array.Clear(_hmacInput, 0, _hmacInput.Length);
                        ArrayPool<byte>.Shared.Return(_hmacInput, clearArray: false);
                        _hmacInput = null;
                    }
                    base.Dispose(disposing);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  MmapDecryptorImpl — random-access, zero-alloc steady state.
        // ══════════════════════════════════════════════════════════════════

        private sealed class MmapDecryptorImpl : IMmapDecryptor
        {
            private MemoryMappedFile _mmf;
            private MemoryMappedViewAccessor _view;
            private AesCtrProtection.ReusableEcbEncryptor _aesEcb;
            private HMACSHA256 _hmac;
            private readonly byte[] _nonceBase;
            private readonly int _blockSize;
            private readonly long _plaintextLength;
            private readonly long _blockCount;
            // Crypto primitives are stateful: HMACSHA256.ComputeHash mutates the
            // internal buffer between calls, and ICryptoTransform.TransformBlock
            // is not thread-safe either. Since MatcherBlobReader claims
            // `TryGetValue`/`GetCompressedValue` are safe for concurrent callers,
            // we have to gate the per-block crypt/MAC pair under a lock. The
            // work itself is microseconds (AES-NI + SHA-NI on modern CPUs) —
            // the cost is negligible even for the 64-thread stress scenario.
            private readonly object _cryptoLock = new object();
            private int _disposed;

            public MmapDecryptorImpl(
                MemoryMappedFile mmf,
                MemoryMappedViewAccessor view,
                ProtectedFileFormatV3.HeaderInfo header,
                byte[] encryptionKey,
                byte[] hmacKey)
            {
                _mmf = mmf;
                _view = view;
                _nonceBase = header.NonceBase;
                _blockSize = header.BlockSize;
                _plaintextLength = header.PlaintextLength;
                _blockCount = ProtectedFileFormatV3.ComputeBlockCount(_plaintextLength, _blockSize);

                // Reusable crypto primitives — one AES-ECB encryptor and one
                // HMACSHA256 live for the lifetime of the decryptor.
                // Ensures steady-state ReadPlaintext is ~0 bytes of managed
                // allocation (just the HMAC.ComputeHash internal ~32-byte
                // tag, and pooled scratch buffers).
                try
                {
                    _aesEcb = AesCtrProtection.CreateReusableEncryptor(encryptionKey);
                    _hmac = new HMACSHA256(hmacKey);
                }
                catch
                {
                    _aesEcb?.Dispose();
                    throw;
                }
            }

            public long Length => _plaintextLength;

            public void ReadPlaintext(long offset, Span<byte> destination)
            {
                if (_disposed != 0) throw new ObjectDisposedException(nameof(MmapDecryptorImpl));
                if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
                if (destination.Length == 0) return;
                if (offset + destination.Length > _plaintextLength)
                    throw new ArgumentOutOfRangeException(nameof(destination),
                        "Read range exceeds plaintext length");

                // Rent one pooled ciphertext+plaintext+hmac buffer set
                // PER CALL. On steady-state (all-pool-hit) this is 0 bytes
                // heap allocation.
                byte[] ctBuf = ArrayPool<byte>.Shared.Rent(_blockSize);
                byte[] ptBuf = ArrayPool<byte>.Shared.Rent(_blockSize);
                byte[] tagBuf = ArrayPool<byte>.Shared.Rent(ProtectedFileFormatV3.BlockHmacSize);
                byte[] hmacInput = ArrayPool<byte>.Shared.Rent(16 + 8 + _blockSize);
                try
                {
                    long cursor = offset;
                    int destCursor = 0;
                    int remaining = destination.Length;

                    while (remaining > 0)
                    {
                        long blockIndex = cursor / _blockSize;
                        int intraOffset = (int)(cursor - blockIndex * _blockSize);
                        int blockPtSize = ProtectedFileFormatV3.BlockPlaintextSize(
                            blockIndex, _plaintextLength, _blockSize);
                        int availInBlock = blockPtSize - intraOffset;
                        int copy = Math.Min(availInBlock, remaining);

                        long fileOff = ProtectedFileFormatV3.BlockFileOffset(blockIndex, _blockSize);

                        // _hmac and _aesEcb.Encryptor are both stateful and
                        // single-threaded. Serialize the whole verify+decrypt
                        // pair under _cryptoLock so concurrent readers can't
                        // corrupt each other's HMAC state. mmap view reads and
                        // destination copies happen outside the lock; only the
                        // crypto work is serialized.
                        _view.ReadArray(fileOff, ctBuf, 0, blockPtSize);
                        _view.ReadArray(fileOff + blockPtSize, tagBuf, 0, ProtectedFileFormatV3.BlockHmacSize);

                        lock (_cryptoLock)
                        {
                            if (_disposed != 0) throw new ObjectDisposedException(nameof(MmapDecryptorImpl));

                            // Verify HMAC before decrypting.
                            Buffer.BlockCopy(_nonceBase, 0, hmacInput, 0, ProtectedFileFormatV3.NonceBaseSize);
                            WriteUInt64BE(hmacInput, ProtectedFileFormatV3.NonceBaseSize, (ulong)blockIndex);
                            Buffer.BlockCopy(ctBuf, 0, hmacInput, ProtectedFileFormatV3.NonceBaseSize + 8, blockPtSize);

                            byte[] computed = _hmac.ComputeHash(hmacInput, 0,
                                ProtectedFileFormatV3.NonceBaseSize + 8 + blockPtSize);

                            // Constant-time compare against only the valid 32 bytes.
                            if (!ConstantTimeEqualsFixed32(tagBuf, computed))
                                throw new CryptographicException(
                                    "v3 block HMAC verification failed at block " + blockIndex);

                            // Decrypt and copy the requested slice out.
                            AesCtrProtection.CryptBlockWith(
                                _aesEcb.Encryptor,
                                new ReadOnlySpan<byte>(ctBuf, 0, blockPtSize),
                                new Span<byte>(ptBuf, 0, blockPtSize),
                                _nonceBase, (ulong)blockIndex);
                        }

                        new ReadOnlySpan<byte>(ptBuf, intraOffset, copy)
                            .CopyTo(destination.Slice(destCursor, copy));

                        cursor += copy;
                        destCursor += copy;
                        remaining -= copy;
                    }
                }
                finally
                {
                    Array.Clear(ctBuf, 0, _blockSize);
                    Array.Clear(ptBuf, 0, _blockSize);
                    Array.Clear(tagBuf, 0, ProtectedFileFormatV3.BlockHmacSize);
                    Array.Clear(hmacInput, 0, Math.Min(hmacInput.Length, 16 + 8 + _blockSize));
                    ArrayPool<byte>.Shared.Return(ctBuf, clearArray: false);
                    ArrayPool<byte>.Shared.Return(ptBuf, clearArray: false);
                    ArrayPool<byte>.Shared.Return(tagBuf, clearArray: false);
                    ArrayPool<byte>.Shared.Return(hmacInput, clearArray: false);
                }
            }

            private static bool ConstantTimeEqualsFixed32(byte[] rentedTag, byte[] computed)
            {
                // rentedTag has length >= 32 (pooled), first 32 bytes are the
                // tag we read; computed is exactly 32.
                if (computed.Length != 32) return false;
                int diff = 0;
                for (int i = 0; i < 32; i++)
                    diff |= rentedTag[i] ^ computed[i];
                return diff == 0;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                // Wait for any in-flight ReadPlaintext crypto work to finish
                // before tearing down _hmac / _aesEcb. Workers holding
                // _cryptoLock will see _disposed==1 on their next iteration
                // and bail cleanly; we can't preempt their current block.
                lock (_cryptoLock)
                {
                    try { _aesEcb?.Dispose(); } catch { /* best effort */ }
                    try { _hmac?.Dispose(); } catch { /* best effort */ }
                    _aesEcb = null;
                    _hmac = null;
                }
                try { _view?.Dispose(); } catch { /* best effort */ }
                try { _mmf?.Dispose(); } catch { /* best effort */ }
                _view = null;
                _mmf = null;
            }
        }
    }
}
