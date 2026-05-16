using System;
using System.IO;
using System.Security.Cryptography;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Network;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// AES-256-CBC + HMAC-SHA256 file protection with server-issued device
    /// secret. The upstream secret is the 32-byte <c>DeviceSecret</c> issued
    /// by <c>POST /api/app/file-protection-key</c> (persisted DPAPI-wrapped
    /// in <c>activation.dat</c>) combined with the local
    /// <see cref="MachineFingerprint"/>.
    ///
    /// A stolen <c>.gisub</c> from machine A cannot be decrypted on machine B
    /// because the machine fingerprint is mixed into PBKDF2. Publishing the
    /// desktop source reveals nothing usable: the secret lives per-device in
    /// the backend D1 and never appears in source.
    ///
    /// On-disk format: writes <see cref="ProtectedFileFormat.HeaderVersion2ServerKey"/>
    /// (header byte = 2) for the CBC bulk-encrypt path. Also issues v3
    /// (AES-CTR + per-block HMAC, mmap-friendly) for the matcher-blob path
    /// by delegating to the key-source-agnostic <see cref="AesCtrFileProtection"/>
    /// with ServerKey-derived keys.
    ///
    /// Reads only v2 and v3 — files written under the retired v1 (embedded
    /// AppSecret) scheme are not decryptable here and should be detected
    /// upstream and force-deleted so the next sync re-encrypts under v2/v3.
    ///
    /// Source-available stance: this is the only file-protection service
    /// in the tree. Nothing usable to a source reader leaks because the
    /// upstream secret lives per-device in the backend D1 and is fetched
    /// at first launch via <c>POST /api/app/file-protection-key</c>.
    /// </summary>
    public sealed class ServerKeyFileProtectionService : IFileProtectionService
    {
        private static readonly byte[] HmacSalt =
        {
            0x68, 0x6D, 0x61, 0x63, // "hmac"
            0x2D, 0x73, 0x61, 0x6C, // "-sal"
            0x74, 0x2D, 0x76, 0x32  // "t-v2"
        };

        private const int KeySizeBytes = 32; // AES-256
        private const int IvSizeBytes = 16;

        // Function returns the activation row at the moment it's called.
        // Indirection lets callers swap the source (production, tests).
        private readonly Func<ActivationData> _loadActivation;

        private readonly object _keyLock = new object();
        private byte[] _encryptionKey;
        private byte[] _hmacKey;
        private long _derivedFromIssuedAtUnixMs;   // matches activation field; lets us re-derive on rotation

        /// <summary>
        /// Production constructor. Reads the activation row from
        /// <see cref="ActivationStore.Load"/> on every key derivation —
        /// rotation handled by checking the issued-at timestamp.
        /// </summary>
        public ServerKeyFileProtectionService()
            : this(ActivationStore.Load)
        {
        }

        /// <summary>Test-friendly constructor. <paramref name="activationLoader"/> may return null.</summary>
        public ServerKeyFileProtectionService(Func<ActivationData> activationLoader)
        {
            _loadActivation = activationLoader ?? throw new ArgumentNullException(nameof(activationLoader));
        }

        // ─────────────────────────────────────────────────────────────────
        //  Public encrypt path — always emits v2.
        // ─────────────────────────────────────────────────────────────────

        public void EncryptFile(string plaintextPath, string encryptedPath)
        {
            if (!File.Exists(plaintextPath))
                throw new FileNotFoundException("Plaintext file not found", plaintextPath);

            byte[] plaintext = File.ReadAllBytes(plaintextPath);
            EncryptBytes(plaintext, encryptedPath);
        }

        public void EncryptBytes(byte[] plaintext, string encryptedPath)
        {
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
            if (string.IsNullOrEmpty(encryptedPath))
                throw new ArgumentException("encryptedPath required", nameof(encryptedPath));

            EnsureKeys();

            byte[] iv = new byte[IvSizeBytes];
            RandomNumberGenerator.Fill(iv);

            byte[] ciphertext;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _encryptionKey;
                aes.IV = iv;
                using (var encryptor = aes.CreateEncryptor())
                {
                    ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
                }
            }

            byte[] hmac;
            using (var sha = new HMACSHA256(_hmacKey))
            {
                hmac = sha.ComputeHash(ciphertext);
            }

            var category = DetectCategory(encryptedPath);
            string tmp = encryptedPath + ".tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                {
                    ProtectedFileFormat.WriteHeaderVersion(
                        stream, iv, hmac, category,
                        ProtectedFileFormat.HeaderVersion2ServerKey);
                    stream.Write(ciphertext, 0, ciphertext.Length);
                }
                if (File.Exists(encryptedPath)) File.Delete(encryptedPath);
                File.Move(tmp, encryptedPath);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
                throw;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  Public decrypt path — auto-routes by header version.
        // ─────────────────────────────────────────────────────────────────

        public MemoryStream DecryptToStream(string encryptedPath)
        {
            if (!File.Exists(encryptedPath))
                throw new FileNotFoundException("Encrypted file not found", encryptedPath);

            byte version = ProtectedFileFormat.PeekFormatVersion(encryptedPath);
            if (version != ProtectedFileFormat.HeaderVersion2ServerKey)
                throw new InvalidDataException(
                    $"Unsupported .gisub version {version} — ServerKey scheme reads v2 only. "
                    + "Legacy v1 files from before the source-available migration must be deleted "
                    + "by the startup sweep in App.OnStartup so the next sync re-encrypts as v2.");

            EnsureKeys();
            byte[] data = File.ReadAllBytes(encryptedPath);
            if (data.Length < ProtectedFileFormat.HeaderSize)
                throw new InvalidDataException("File too short to be a valid .gisub file");

            using (var headerStream = new MemoryStream(data, 0, ProtectedFileFormat.HeaderSize))
            {
                var (_, iv, storedHmac, _) = ProtectedFileFormat.ReadHeaderAny(headerStream);

                int ctLen = data.Length - ProtectedFileFormat.HeaderSize;
                byte[] ciphertext = new byte[ctLen];
                Buffer.BlockCopy(data, ProtectedFileFormat.HeaderSize, ciphertext, 0, ctLen);

                byte[] computed;
                using (var sha = new HMACSHA256(_hmacKey))
                {
                    computed = sha.ComputeHash(ciphertext);
                }
                if (!ConstantTimeEquals(storedHmac, computed))
                    throw new CryptographicException(
                        "HMAC verification failed — file tampered or encrypted on a different machine");

                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = _encryptionKey;
                    aes.IV = iv;
                    using (var dec = aes.CreateDecryptor())
                    {
                        byte[] plaintext = dec.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
                        return new MemoryStream(plaintext, writable: false);
                    }
                }
            }
        }

        public Stream OpenDecryptStream(string encryptedPath)
        {
            // True streaming decrypt — bounded peak memory regardless of
            // file size. Required for the matcher load path which can be
            // 25+ MB per file; materialising the whole plaintext into a
            // MemoryStream adds ~payload-size to peak working set.
            //
            // Two-pass design preserves Encrypt-then-MAC semantics:
            //   Pass 1 — re-open the file, stream the ciphertext through
            //            HMAC-SHA256 in 64 KB chunks, compare to stored HMAC.
            //   Pass 2 — re-open, seek past header, return a CryptoStream
            //            wrapping the FileStream. Reads flow disk → AES → caller.
            //
            // ServerKey reads v2 only; v1 files are wiped at startup, v3
            // goes through OpenDecryptStreamV3.
            if (!File.Exists(encryptedPath))
                throw new FileNotFoundException("Encrypted file not found", encryptedPath);

            byte version = ProtectedFileFormat.PeekFormatVersion(encryptedPath);
            if (version != ProtectedFileFormat.HeaderVersion2ServerKey)
                throw new InvalidDataException(
                    $"Unsupported .gisub version {version} — ServerKey scheme reads v2 only.");

            EnsureKeys();

            long fileLength = new FileInfo(encryptedPath).Length;
            if (fileLength < ProtectedFileFormat.HeaderSize)
                throw new InvalidDataException("File too short to be a valid .gisub file");

            // Pass 1: HMAC-verify in chunks. OS page cache keeps this read
            // hot for pass 2, so the cost is mostly the AES throughput.
            byte[] iv;
            byte[] storedHmac;
            using (var verifyStream = new FileStream(
                encryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 65536, useAsync: false))
            {
                (_, iv, storedHmac, _) = ProtectedFileFormat.ReadHeaderAny(verifyStream);

                using (var hmacSha = new HMACSHA256(_hmacKey))
                {
                    byte[] buffer = new byte[65536];
                    int read;
                    while ((read = verifyStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        hmacSha.TransformBlock(buffer, 0, read, null, 0);
                    }
                    hmacSha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    byte[] computedHmac = hmacSha.Hash;
                    if (!ConstantTimeEquals(storedHmac, computedHmac))
                        throw new CryptographicException(
                            "HMAC verification failed — file tampered or encrypted on a different machine");
                }
            }

            // Pass 2: return CryptoStream over a fresh FileStream seeked
            // past the header. HMAC is authenticated above so any plaintext
            // the caller reads is trustworthy.
            FileStream cipherStream = null;
            ICryptoTransform decryptor = null;
            Aes aes = null;
            try
            {
                cipherStream = new FileStream(
                    encryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 65536, useAsync: false);
                cipherStream.Position = ProtectedFileFormat.PayloadOffset;

                aes = Aes.Create();
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _encryptionKey;
                aes.IV = iv;

                decryptor = aes.CreateDecryptor();
                var crypto = new CryptoStream(cipherStream, decryptor, CryptoStreamMode.Read);
                var wrapper = new DisposingCryptoStream(crypto, decryptor, aes);
                cipherStream = null;
                decryptor = null;
                aes = null;
                return wrapper;
            }
            catch
            {
                try { decryptor?.Dispose(); } catch { /* best-effort */ }
                try { aes?.Dispose(); } catch { /* best-effort */ }
                try { cipherStream?.Dispose(); } catch { /* best-effort */ }
                throw;
            }
        }

        // Wrapper that disposes the underlying CryptoStream + ICryptoTransform + Aes
        // when disposed. CryptoStream.Dispose doesn't touch its transform, so we
        // have to hold them and reap on dispose.
        private sealed class DisposingCryptoStream : Stream
        {
            private readonly CryptoStream _inner;
            private readonly ICryptoTransform _transform;
            private readonly Aes _aes;
            private bool _disposed;

            public DisposingCryptoStream(CryptoStream inner, ICryptoTransform transform, Aes aes)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _transform = transform ?? throw new ArgumentNullException(nameof(transform));
                _aes = aes ?? throw new ArgumentNullException(nameof(aes));
            }

            public override bool CanRead => !_disposed && _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) =>
                _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (_disposed) return;
                _disposed = true;
                if (disposing)
                {
                    try { _inner.Dispose(); } catch { /* best-effort */ }
                    try { _transform.Dispose(); } catch { /* best-effort */ }
                    try { _aes.Dispose(); } catch { /* best-effort */ }
                }
                base.Dispose(disposing);
            }
        }

        public void EncryptFileStreaming(string plaintextPath, string encryptedPath)
        {
            if (!File.Exists(plaintextPath))
                throw new FileNotFoundException("Plaintext file not found", plaintextPath);

            using (var source = new FileStream(plaintextPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: false))
            using (var sink = OpenEncryptStream(encryptedPath))
            {
                // 16 KB copy buffer keeps peak RAM bounded; the underlying
                // CryptoStream + HmacForwardingStream are block-oriented
                // and won't benefit from a larger buffer past ~64 KB.
                source.CopyTo(sink, 16 * 1024);

                // Two-phase commit: only call Complete() when CopyTo finished
                // successfully. An exception unwinds past this point and the
                // `using` disposes sink WITHOUT a Complete() call, which reaps
                // the .tmp and leaves no half-written .gisub behind.
                sink.Complete();
            }
        }

        /// <summary>
        /// Returns a write-only stream that encrypts what is written into it
        /// and materialises a v2 (server-key) <c>.gisub</c> file at
        /// <paramref name="encryptedPath"/> on <see cref="EncryptingStream.Complete"/>.
        ///
        /// Pipeline layout — outer <see cref="CryptoStream"/> for AES-256-CBC,
        /// middle <see cref="HmacForwardingStream"/> taps post-AES bytes
        /// into HMAC-SHA256, inner <see cref="FileStream"/> writes to .tmp.
        /// On Complete we patch the real HMAC into
        /// <see cref="ProtectedFileFormat.HmacOffset"/> and atomically rename.
        /// Disposing without first calling Complete reaps the .tmp.
        ///
        /// Required for <c>DictionarySync</c>'s streaming re-encryption path
        /// (downloaded `.gisub-dist` → machine-bound `.gisub`) once Phase B
        /// flips <c>FileProtectionFactory</c> to ServerKey.
        /// </summary>
        public EncryptingStream OpenEncryptStream(string encryptedPath)
        {
            if (string.IsNullOrEmpty(encryptedPath))
                throw new ArgumentException("encryptedPath required", nameof(encryptedPath));

            EnsureKeys();

            byte[] iv = new byte[IvSizeBytes];
            RandomNumberGenerator.Fill(iv);

            var category = DetectCategory(encryptedPath);
            string tmpPath = encryptedPath + ".tmp";

            FileStream fileStream = null;
            HmacForwardingStream hmacStream = null;
            CryptoStream cryptoStream = null;
            Aes aes = null;

            try
            {
                fileStream = new FileStream(
                    tmpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                    65536, useAsync: false);

                // v2 header: magic + version=2 + category + IV + 32-byte placeholder HMAC.
                // The placeholder is overwritten on Complete() once the body's HMAC tag
                // is known. WriteHeaderVersion is required — WriteHeader defaults to v1.
                byte[] placeholderHmac = new byte[32];
                ProtectedFileFormat.WriteHeaderVersion(
                    fileStream, iv, placeholderHmac, category,
                    ProtectedFileFormat.HeaderVersion2ServerKey);

                // HMAC tap: ciphertext is fed to HMAC and forwarded to disk in one pass.
                // Realises Encrypt-then-MAC because only post-AES bytes flow through.
                hmacStream = new HmacForwardingStream(fileStream, _hmacKey);

                aes = Aes.Create();
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _encryptionKey;
                aes.IV = iv;

                var encryptor = aes.CreateEncryptor();
                // leaveOpen:true keeps hmacStream alive after CryptoStream.Dispose so
                // EncryptingWriteStream can still seek/patch the FileStream below it.
                cryptoStream = new CryptoStream(hmacStream, encryptor, CryptoStreamMode.Write, leaveOpen: true);

                return new EncryptingWriteStream(
                    cryptoStream, hmacStream, aes, fileStream,
                    tmpPath, encryptedPath);
            }
            catch
            {
                try { cryptoStream?.Dispose(); } catch { /* ignore */ }
                try { hmacStream?.Dispose(); } catch { /* ignore */ }
                try { aes?.Dispose(); } catch { /* ignore */ }
                try { fileStream?.Dispose(); } catch { /* ignore */ }
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* ignore */ }
                throw;
            }
        }


        public bool IsEncrypted(string filePath) =>
            ProtectedFileFormat.HasMagicHeader(filePath);

        public string GetProtectedPath(string originalPath)
        {
            if (string.IsNullOrEmpty(originalPath)) return originalPath;
            string dir = Path.GetDirectoryName(originalPath);
            string name = Path.GetFileNameWithoutExtension(originalPath);
            return Path.Combine(dir ?? "", name + ".gisub");
        }

        // ─────────────────────────────────────────────────────────────────
        //  v3 (AES-CTR + per-block HMAC, mmap-friendly) — same ServerKey-
        //  derived encryption + HMAC keys as the v2 CBC path. The low-level
        //  AesCtrFileProtection helper is key-source-agnostic; we just hand
        //  it our derived keys.
        // ─────────────────────────────────────────────────────────────────

        public Stream OpenDecryptStreamV3(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path required", nameof(path));
            if (!ProtectedFileFormatV3.HasV3Header(path))
                throw new InvalidOperationException("File is not a v3 .gisub: " + path);

            EnsureKeys();
            return AesCtrFileProtection.OpenDecryptStream(path, _encryptionKey, _hmacKey);
        }

        public IMmapDecryptor OpenMmapDecryptor(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path required", nameof(path));
            if (!ProtectedFileFormatV3.HasV3Header(path))
                throw new InvalidOperationException("File is not a v3 .gisub: " + path);

            EnsureKeys();
            return AesCtrFileProtection.OpenMmapDecryptor(path, _encryptionKey, _hmacKey);
        }

        public void EncryptStreamToV3(Stream source, long plaintextLength, string encryptedPath)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (plaintextLength < 0)
                throw new ArgumentOutOfRangeException(nameof(plaintextLength));
            if (string.IsNullOrEmpty(encryptedPath))
                throw new ArgumentException("encryptedPath required", nameof(encryptedPath));

            EnsureKeys();
            var category = (ProtectedFileFormatV3.FileCategory)(byte)DetectCategory(encryptedPath);
            AesCtrFileProtection.EncryptStreamToV3(
                source, plaintextLength, encryptedPath,
                category, _encryptionKey, _hmacKey);
        }

        // ─────────────────────────────────────────────────────────────────
        //  Key derivation
        // ─────────────────────────────────────────────────────────────────

        private void EnsureKeys()
        {
            ActivationData activation = _loadActivation();
            if (activation == null || !activation.HasDeviceFileProtectionSecret)
                throw new InvalidOperationException(
                    "ServerKeyFileProtectionService called before a server-issued "
                    + "device secret was provisioned. The foreground bootstrap "
                    + "in App.OnStartup.EnsureFileProtectionSecret must run before "
                    + "any consumer of FileProtectionFactory.Create() is built.");

            long issuedAt = activation.DeviceFileProtectionIssuedAtUnixMs ?? 0;
            int iterations = activation.DeviceFileProtectionPbkdf2Iterations;

            if (_encryptionKey != null && _hmacKey != null
                && _derivedFromIssuedAtUnixMs == issuedAt)
                return;

            lock (_keyLock)
            {
                if (_encryptionKey != null && _hmacKey != null
                    && _derivedFromIssuedAtUnixMs == issuedAt)
                    return;

                DeriveKeysLocked(activation.DeviceFileProtectionSecret, iterations);
                _derivedFromIssuedAtUnixMs = issuedAt;
            }
        }

        private void DeriveKeysLocked(byte[] deviceSecret, int iterations)
        {
            byte[] machineId = MachineFingerprint.GetFingerprint();

            // Combined input: device secret || machine fingerprint. The
            // device secret gives "this app, this device, this user" — the
            // fingerprint adds the per-machine binding that survives a
            // stolen activation.dat.
            byte[] combined = new byte[deviceSecret.Length + machineId.Length];
            Buffer.BlockCopy(deviceSecret, 0, combined, 0, deviceSecret.Length);
            Buffer.BlockCopy(machineId, 0, combined, deviceSecret.Length, machineId.Length);

            // PBKDF2-SHA256 (server algorithm string says SHA-256). Legacy
            // SHA-1 v1 files are no longer readable; the startup sweep in
            // App.WipeLegacyProtectedCacheFiles deletes them so the next
            // sync re-encrypts under SHA-256 / v2.
            _encryptionKey = Rfc2898DeriveBytes.Pbkdf2(
                combined, machineId, iterations, HashAlgorithmName.SHA256, KeySizeBytes);

            byte[] hmacSaltFull = new byte[machineId.Length + HmacSalt.Length];
            Buffer.BlockCopy(machineId, 0, hmacSaltFull, 0, machineId.Length);
            Buffer.BlockCopy(HmacSalt, 0, hmacSaltFull, machineId.Length, HmacSalt.Length);

            _hmacKey = Rfc2898DeriveBytes.Pbkdf2(
                combined, hmacSaltFull, iterations, HashAlgorithmName.SHA256, KeySizeBytes);

            // Defence against accidental disclosure: the combined buffer is
            // about to be GC'd; explicitly clear it so a heap dump after
            // EnsureKeys() doesn't carry the device secret. Doesn't catch
            // every copy, but catches the obvious one.
            Array.Clear(combined, 0, combined.Length);

            Logger.Log.Info(
                $"ServerKey: derived AES + HMAC keys (iterations={iterations}, deviceSecret=<redacted, len={deviceSecret.Length}>).");
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static ProtectedFileFormat.FileCategory DetectCategory(string path)
        {
            string baseName = Path.GetFileNameWithoutExtension(path);
            if (baseName.StartsWith("TextMap", StringComparison.OrdinalIgnoreCase))
                return ProtectedFileFormat.FileCategory.CustomTranslation;
            return ProtectedFileFormat.FileCategory.GeneratedGraph;
        }
    }
}
