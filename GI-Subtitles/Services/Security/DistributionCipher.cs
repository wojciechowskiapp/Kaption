using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// Decrypts `.gisub-dist` files that the server stores in R2. This is the
    /// first of two encryption layers the translation dictionaries pass
    /// through:
    ///
    ///   R2 (ciphertext .gisub-dist)  ---[this class]---> plaintext JSON
    ///                                                    |
    ///                                       [AesFileProtectionService]
    ///                                                    v
    ///                           local disk as machine-bound `.gisub`
    ///
    /// Without this layer, a Cloudflare R2 credential leak would expose all
    /// paid translation data in cleartext. With it, a leak just exposes
    /// encrypted blobs — an attacker would additionally need to compromise
    /// the Worker's Secrets to recover the distribution key.
    ///
    /// Format (matches backend/scripts/encrypt-for-r2.cjs byte-for-byte):
    ///   [4 B]  Magic "KAPD"
    ///   [1 B]  Version (1)
    ///   [1 B]  Reserved (0)
    ///   [16 B] AES-CBC IV
    ///   [32 B] HMAC-SHA256 over ciphertext
    ///   [N B]  AES-256-CBC PKCS7 ciphertext
    /// Total header: 54 bytes.
    ///
    /// Key derivation (input is the 32-byte distribution key):
    ///   encKey  = HMAC-SHA256(distKey, "kaption-enc-v1")
    ///   hmacKey = HMAC-SHA256(distKey, "kaption-hmac-v1")
    ///
    /// Encrypt-then-MAC: HMAC is verified BEFORE decryption. Failure on
    /// magic / version / HMAC throws <see cref="CryptographicException"/>.
    /// </summary>
    internal static class DistributionCipher
    {
        private static readonly byte[] Magic = { 0x4B, 0x41, 0x50, 0x44 }; // "KAPD"
        private const byte ExpectedVersion = 1;
        private const int IvLength = 16;
        private const int HmacLength = 32;
        private const int HeaderLength = 4 + 1 + 1 + IvLength + HmacLength; // 54

        private const string EncLabel = "kaption-enc-v1";
        private const string HmacLabel = "kaption-hmac-v1";

        /// <summary>
        /// True if the file at <paramref name="path"/> starts with the
        /// distribution-layer magic bytes. Useful to tell apart a
        /// `.gisub-dist` (needs this class) from a raw `.gisub` (needs
        /// AesFileProtectionService) from plaintext JSON (nothing).
        /// </summary>
        public static bool HasMagic(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    if (fs.Length < HeaderLength) return false;
                    byte[] buf = new byte[4];
                    if (fs.Read(buf, 0, 4) != 4) return false;
                    return buf[0] == Magic[0] && buf[1] == Magic[1]
                        && buf[2] == Magic[2] && buf[3] == Magic[3];
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Decrypt a full `.gisub-dist` blob into plaintext bytes. Verifies
        /// magic, version, HMAC before attempting AES decryption.
        /// </summary>
        public static byte[] Decrypt(byte[] blob, byte[] distributionKey)
        {
            if (blob == null) throw new ArgumentNullException(nameof(blob));
            if (distributionKey == null || distributionKey.Length != 32)
                throw new ArgumentException("distribution key must be 32 bytes", nameof(distributionKey));
            if (blob.Length < HeaderLength)
                throw new CryptographicException("blob shorter than distribution-layer header");

            // Magic
            for (int i = 0; i < Magic.Length; i++)
                if (blob[i] != Magic[i])
                    throw new CryptographicException("magic mismatch — not a .gisub-dist blob");

            byte version = blob[4];
            if (version != ExpectedVersion)
                throw new CryptographicException($"unsupported .gisub-dist version {version}");

            byte[] iv = new byte[IvLength];
            Buffer.BlockCopy(blob, 6, iv, 0, IvLength);
            byte[] mac = new byte[HmacLength];
            Buffer.BlockCopy(blob, 6 + IvLength, mac, 0, HmacLength);
            int ciphertextLen = blob.Length - HeaderLength;
            byte[] ciphertext = new byte[ciphertextLen];
            Buffer.BlockCopy(blob, HeaderLength, ciphertext, 0, ciphertextLen);

            byte[] encKey = DeriveLabelKey(distributionKey, EncLabel);
            byte[] hmacKey = DeriveLabelKey(distributionKey, HmacLabel);

            // Encrypt-then-MAC: verify BEFORE decrypting so a tampered
            // ciphertext never reaches the AES engine.
            byte[] expected;
            using (var h = new HMACSHA256(hmacKey))
                expected = h.ComputeHash(ciphertext);
            if (!ConstantTimeEquals(mac, expected))
                throw new CryptographicException("distribution HMAC mismatch — file corrupted or wrong key");

            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = encKey;
                aes.IV = iv;
                using (var decryptor = aes.CreateDecryptor())
                {
                    return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
                }
            }
        }

        /// <summary>
        /// In-place: read a `.gisub-dist` file, decrypt, write plaintext over
        /// the same path. Atomic via a `.tmp` sibling. Used by the download
        /// pipeline to turn `.gisub-dist` into plaintext that downstream
        /// code (AesFileProtectionService, JSON parsing) can consume.
        ///
        /// Delegates to <see cref="DecryptFileInPlaceStreaming"/> which keeps
        /// peak RAM bounded at ~16 KB regardless of file size. The old byte[]
        /// path materialised the entire ciphertext, then the entire plaintext,
        /// which on an 80 MB translation pack accounted for ~160 MB of the
        /// peak-sync RAM footprint.
        /// </summary>
        public static void DecryptFileInPlace(string path, byte[] distributionKey)
        {
            DecryptFileInPlaceStreaming(path, distributionKey);
        }

        /// <summary>
        /// Streaming variant of <see cref="DecryptFileInPlace"/>. Runs two
        /// passes over <paramref name="path"/>:
        ///
        ///   Pass 1: read header (magic/version/IV/HMAC), then stream the
        ///           ciphertext body through an incremental HMAC-SHA256 and
        ///           verify against the stored tag. Encrypt-then-MAC — we
        ///           abort BEFORE any AES bytes are decrypted.
        ///   Pass 2: re-open, skip header, stream ciphertext through a
        ///           CryptoStream (AES-256-CBC decrypt) into a sibling
        ///           <c>.decrypted.tmp</c> file. On success, atomically
        ///           swap the plaintext over the ciphertext source file.
        ///
        /// Peak RAM ≈ two 16 KB copy buffers plus key material. The ciphertext
        /// file is read twice from disk, so we pay modest I/O to flatten the
        /// memory peak — a worthwhile trade for 80 MB translation packs.
        /// </summary>
        public static void DecryptFileInPlaceStreaming(string path, byte[] distributionKey)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path required", nameof(path));
            if (distributionKey == null || distributionKey.Length != 32)
                throw new ArgumentException("distribution key must be 32 bytes", nameof(distributionKey));

            byte[] iv;
            byte[] storedTag;

            // ── Pass 1 ──────────────────────────────────────────────────────
            // Read header + stream ciphertext through HMAC. On mismatch we
            // throw before allocating any AES state, so a tampered blob never
            // reaches the decryptor.
            byte[] hmacKey = DeriveLabelKey(distributionKey, HmacLabel);
            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: false))
            {
                if (src.Length < HeaderLength)
                    throw new CryptographicException("blob shorter than distribution-layer header");

                byte[] header = new byte[HeaderLength];
                ReadFull(src, header, 0, HeaderLength);

                // Magic + version
                for (int i = 0; i < Magic.Length; i++)
                    if (header[i] != Magic[i])
                        throw new CryptographicException("magic mismatch — not a .gisub-dist blob");
                if (header[4] != ExpectedVersion)
                    throw new CryptographicException($"unsupported .gisub-dist version {header[4]}");

                iv = new byte[IvLength];
                Buffer.BlockCopy(header, 6, iv, 0, IvLength);
                storedTag = new byte[HmacLength];
                Buffer.BlockCopy(header, 6 + IvLength, storedTag, 0, HmacLength);

                byte[] computed;
                using (var h = new HMACSHA256(hmacKey))
                {
                    // The position is already past the header; CopyTo will
                    // stream the ciphertext body. An HMAC-forwarding wrapper
                    // would let us reuse the same bytes twice in-place, but
                    // we still need a second pass for the AES decrypt, so
                    // just compute the tag cleanly here.
                    var buffer = new byte[16 * 1024];
                    int read;
                    while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        h.TransformBlock(buffer, 0, read, null, 0);
                    }
                    h.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    computed = h.Hash;
                }

                if (!ConstantTimeEquals(storedTag, computed))
                    throw new CryptographicException("distribution HMAC mismatch — file corrupted or wrong key");
            }

            // ── Pass 2 ──────────────────────────────────────────────────────
            // Stream decrypt to a sibling .decrypted.tmp. Only on clean
            // completion do we atomically swap it in for the ciphertext.
            byte[] encKey = DeriveLabelKey(distributionKey, EncLabel);
            string tmpPath = path + ".decrypted.tmp";
            bool success = false;
            try
            {
                using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: false))
                using (var dst = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: false))
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = encKey;
                    aes.IV = iv;

                    // Skip header — src is fresh, position it past the 54 bytes.
                    src.Seek(HeaderLength, SeekOrigin.Begin);

                    using (var decryptor = aes.CreateDecryptor())
                    using (var cryptoStream = new CryptoStream(src, decryptor, CryptoStreamMode.Read, leaveOpen: true))
                    {
                        var buffer = new byte[16 * 1024];
                        int read;
                        while ((read = cryptoStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            dst.Write(buffer, 0, read);
                        }
                    }

                    dst.Flush();
                }

                if (File.Exists(path)) File.Delete(path);
                File.Move(tmpPath, path);
                success = true;
            }
            finally
            {
                if (!success)
                {
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
                }
            }
        }

        /// <summary>
        /// Blocking read that retries until <paramref name="count"/> bytes land
        /// or the stream EOFs. <see cref="FileStream.Read(byte[], int, int)"/>
        /// on a local file can technically short-read; treat that defensively.
        /// </summary>
        private static void ReadFull(Stream src, byte[] buffer, int offset, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = src.Read(buffer, offset + got, count - got);
                if (n <= 0) throw new EndOfStreamException("unexpected EOF reading .gisub-dist header");
                got += n;
            }
        }

        // --- helpers ---

        private static byte[] DeriveLabelKey(byte[] distKey, string label)
        {
            // One-shot HMAC-SHA256(distKey, label) — mirrors the Node script's
            // derivation exactly so encrypt/decrypt round-trip across
            // languages. distKey is already 256 bits of uniform randomness,
            // so we don't need PBKDF2 stretching here.
            using (var h = new HMACSHA256(distKey))
                return h.ComputeHash(Encoding.UTF8.GetBytes(label));
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
