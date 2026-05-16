using System;
using System.IO;
using System.Security.Cryptography;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// Pass-through stream that forwards every <see cref="Write"/> straight
    /// to an underlying stream while feeding the same bytes into a running
    /// <see cref="HMACSHA256"/>. Used by the streaming-write pipeline for
    /// <c>.gisub</c> v1/v2 (CBC) — the AES-CBC ciphertext flows through
    /// here on its way to disk, and on <see cref="FinalizeHmac"/> we read
    /// back the authoritative tag.
    ///
    /// CryptoStream(hmacTransform, Write) on its own would compute the HMAC
    /// but discard the bytes; we need both — the bytes have to land on disk
    /// AND the HMAC has to be computed. This class does both in one pass.
    ///
    /// Disposal: drops the HMAC instance only. Does NOT dispose the inner
    /// stream — the outer <see cref="EncryptingWriteStream"/> still needs
    /// to seek-back-and-patch the header, so the FileStream's lifetime is
    /// owned by EncryptingWriteStream, not us.
    /// </summary>
    internal sealed class HmacForwardingStream : Stream
    {
        private readonly Stream _inner;
        private HMACSHA256 _hmac;
        private byte[] _finalHmac;
        private bool _disposed;

        public HmacForwardingStream(Stream inner, byte[] hmacKey)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _hmac = new HMACSHA256(hmacKey ?? throw new ArgumentNullException(nameof(hmacKey)));
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HmacForwardingStream));
            if (count <= 0) return;
            // Update HMAC first so a write that throws post-HMAC can't desync the two.
            // TransformBlock with outputBuffer=null is the documented hash-only call.
            _hmac.TransformBlock(buffer, offset, count, null, 0);
            _inner.Write(buffer, offset, count);
        }

        /// <summary>
        /// Finalise the HMAC and return the 32-byte tag. Idempotent — repeat
        /// calls return the cached result. Must be called after the last
        /// write but before the underlying stream is disposed.
        /// </summary>
        public byte[] FinalizeHmac()
        {
            if (_finalHmac != null) return _finalHmac;
            if (_hmac == null) throw new ObjectDisposedException(nameof(HmacForwardingStream));
            _hmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            _finalHmac = _hmac.Hash;
            return _finalHmac;
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                try { _hmac?.Dispose(); } catch { /* ignore */ }
                _hmac = null;
                // Do NOT dispose _inner — outer EncryptingWriteStream owns the
                // FileStream and still needs to seek-back-and-patch the header.
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Write-only stream returned by file-protection services that emit
    /// CBC-mode <c>.gisub</c> files (v1 legacy AppSecret, v2 server-issued
    /// secret). The two services build their own header-write-then-encrypt
    /// pipeline up front and hand the resulting layered stream to this
    /// class for the user-facing writes.
    ///
    /// Pipeline (outer → inner):
    ///
    ///   [caller writes plaintext]
    ///        └─ CryptoStream (AES-256-CBC encrypt, leaveOpen=true)
    ///             └─ HmacForwardingStream (HMAC-SHA256 over ciphertext)
    ///                  └─ FileStream (.tmp on disk, opened ReadWrite for header patch)
    ///
    /// Two-phase commit contract: callers MUST call <see cref="Complete"/>
    /// after the last successful write, before <see cref="Stream.Dispose()"/>.
    /// On the exception path, callers let <c>using</c> dispose without
    /// calling Complete — Dispose then reaps the .tmp and produces no
    /// output file. This is the only way a Stream-shaped API can
    /// distinguish "caller finished cleanly" from "caller threw mid-write".
    /// </summary>
    internal sealed class EncryptingWriteStream : EncryptingStream
    {
        private readonly CryptoStream _cryptoStream;
        private readonly HmacForwardingStream _hmacStream;
        private readonly Aes _aes;
        private readonly FileStream _fileStream;
        private readonly string _tmpPath;
        private readonly string _finalPath;
        private bool _disposed;
        private bool _completed;

        public EncryptingWriteStream(
            CryptoStream cryptoStream,
            HmacForwardingStream hmacStream,
            Aes aes,
            FileStream fileStream,
            string tmpPath,
            string finalPath)
        {
            _cryptoStream = cryptoStream ?? throw new ArgumentNullException(nameof(cryptoStream));
            _hmacStream = hmacStream ?? throw new ArgumentNullException(nameof(hmacStream));
            _aes = aes ?? throw new ArgumentNullException(nameof(aes));
            _fileStream = fileStream ?? throw new ArgumentNullException(nameof(fileStream));
            _tmpPath = tmpPath ?? throw new ArgumentNullException(nameof(tmpPath));
            _finalPath = finalPath ?? throw new ArgumentNullException(nameof(finalPath));
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EncryptingWriteStream));
            _cryptoStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EncryptingWriteStream));
            _cryptoStream.Write(buffer, offset, count);
        }

        /// <summary>
        /// Finalise the encrypted file: flush the AES final block, finalise
        /// the HMAC, patch the real tag into <see cref="ProtectedFileFormat.HmacOffset"/>,
        /// and atomically rename the .tmp to the final path. Must be called
        /// on the success path before Dispose. Idempotent.
        /// </summary>
        public override void Complete()
        {
            if (_completed) return;
            if (_disposed) throw new ObjectDisposedException(nameof(EncryptingWriteStream));

            // 1. Flush AES final block (PKCS7 padding) through to the HMAC tap.
            _cryptoStream.FlushFinalBlock();
            _cryptoStream.Dispose();

            // 2. All ciphertext has now flowed through; pull the authoritative tag.
            byte[] tag = _hmacStream.FinalizeHmac();
            _hmacStream.Dispose();

            // 3. Patch the placeholder HMAC at the header offset. Same offset
            //    for v1 and v2 — header layout is identical, only the version
            //    byte differs.
            _fileStream.Seek(ProtectedFileFormat.HmacOffset, SeekOrigin.Begin);
            _fileStream.Write(tag, 0, tag.Length);
            _fileStream.Flush();
            _fileStream.Dispose();
            _aes.Dispose();

            // 4. Atomic rename. A crash between delete and move leaves no
            //    file (better than a partial .gisub with valid HMAC).
            if (File.Exists(_finalPath)) File.Delete(_finalPath);
            File.Move(_tmpPath, _finalPath);

            _completed = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (!disposing)
            {
                // Finaliser path — never touch disk from a finaliser. Worst
                // case the GC reaps the streams and we leak a .tmp file.
                base.Dispose(false);
                return;
            }

            if (_completed)
            {
                base.Dispose(true);
                return;
            }

            // Abort path — Complete() never called, usually because the
            // caller threw mid-write. Best-effort release of everything,
            // then reap the .tmp so no half-written file lingers next to
            // the target. Each Dispose is wrapped because a throwing
            // Dispose from a finally block would mask the user exception.
            try { _cryptoStream?.Dispose(); } catch { /* ignore */ }
            try { _hmacStream?.Dispose(); } catch { /* ignore */ }
            try { _fileStream?.Dispose(); } catch { /* ignore */ }
            try { _aes?.Dispose(); } catch { /* ignore */ }
            try { if (File.Exists(_tmpPath)) File.Delete(_tmpPath); } catch { /* ignore */ }

            base.Dispose(true);
        }
    }
}
