using System;
using System.Buffers;
using System.Security.Cryptography;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// Stateless AES-CTR crypt over a plaintext "block" (matches the v3 file
    /// format's logical block size — typically 4 KB). One CTR stream is
    /// generated per (key, nonceBase, blockIndex) tuple.
    ///
    /// .NET Framework 4.8 does not expose AES-CTR natively (only CBC / ECB /
    /// CFB / CTS). We implement it the classic way:
    ///   ciphertext = plaintext XOR AES-ECB(key, counter_block)
    /// The counter block is constructed so the (nonce, counter) pair is unique
    /// for every 16-byte AES block inside the whole file:
    ///
    ///   counter_block = nonce_base XOR (block_index_big_endian shifted to the
    ///                                   low 8 bytes), then the low 4 bytes
    ///                   increment once per AES block within the file block.
    ///
    /// Design notes
    /// ------------
    /// - The Aes/ICryptoTransform is NOT reused across calls: AesManaged's
    ///   transform is not safe to share between threads, and stamping a fresh
    ///   one on each call (typically one per 4 KB block) costs ~2 µs on modern
    ///   hardware — negligible compared to the AES-NI block encryption itself.
    /// - All intermediate buffers come from ArrayPool&lt;byte&gt;.Shared and are
    ///   returned with Array.Clear on the used region (no key material / no
    ///   plaintext leaks back into the pool).
    /// - ECB padding is disabled (PaddingMode.None) — we hand-feed exact AES
    ///   blocks, and the final XOR handles ragged plaintext tails.
    /// - CTR is symmetric: EncryptBlock and DecryptBlock call the same core.
    /// </summary>
    internal static class AesCtrProtection
    {
        private const int AesBlockBytes = 16;

        /// <summary>
        /// Encrypt a single logical file block. CTR is symmetric so this is
        /// an alias for <see cref="CryptBlock"/>.
        /// </summary>
        public static void EncryptBlock(
            ReadOnlySpan<byte> plaintext,
            Span<byte> ciphertextOut,
            byte[] key,
            ReadOnlySpan<byte> nonceBase,
            ulong blockIndex)
        {
            CryptBlock(plaintext, ciphertextOut, key, nonceBase, blockIndex);
        }

        /// <summary>
        /// Decrypt a single logical file block. CTR is symmetric so this is
        /// an alias for <see cref="CryptBlock"/>.
        /// </summary>
        public static void DecryptBlock(
            ReadOnlySpan<byte> ciphertext,
            Span<byte> plaintextOut,
            byte[] key,
            ReadOnlySpan<byte> nonceBase,
            ulong blockIndex)
        {
            CryptBlock(ciphertext, plaintextOut, key, nonceBase, blockIndex);
        }

        /// <summary>
        /// Core XOR-with-keystream routine. Accepts any input length up to
        /// the format's block size; generates just enough keystream to cover
        /// it. Allocates one pooled keystream buffer (returned before exit)
        /// regardless of input length.
        /// </summary>
        internal static void CryptBlock(
            ReadOnlySpan<byte> input,
            Span<byte> output,
            byte[] key,
            ReadOnlySpan<byte> nonceBase,
            ulong blockIndex)
        {
            CryptBlockCore(input, output, key, ecbEncryptor: null, nonceBase, blockIndex);
        }

        /// <summary>
        /// Variant of <see cref="CryptBlock"/> that reuses a caller-supplied
        /// AES-ECB encryptor. Used by hot paths (mmap decryptor) that want to
        /// amortise the ICryptoTransform creation across many blocks —
        /// <see cref="Aes.Create"/> + <see cref="Aes.CreateEncryptor"/> together
        /// allocate ~500 bytes on net48, and a 1000-random-read benchmark
        /// needs those out of the steady-state budget.
        ///
        /// The supplied encryptor MUST be ECB / no-padding / keyed with the
        /// same key intended for this file. Not thread-safe — callers that
        /// share across threads must serialise externally.
        /// </summary>
        internal static void CryptBlockWith(
            ICryptoTransform ecbEncryptor,
            ReadOnlySpan<byte> input,
            Span<byte> output,
            ReadOnlySpan<byte> nonceBase,
            ulong blockIndex)
        {
            if (ecbEncryptor == null) throw new ArgumentNullException(nameof(ecbEncryptor));
            CryptBlockCore(input, output, key: null, ecbEncryptor, nonceBase, blockIndex);
        }

        private static void CryptBlockCore(
            ReadOnlySpan<byte> input,
            Span<byte> output,
            byte[] key,
            ICryptoTransform ecbEncryptor,
            ReadOnlySpan<byte> nonceBase,
            ulong blockIndex)
        {
            if (nonceBase.Length != ProtectedFileFormatV3.NonceBaseSize)
                throw new ArgumentException("Nonce base must be 16 bytes", nameof(nonceBase));
            if (output.Length < input.Length)
                throw new ArgumentException("Output span shorter than input", nameof(output));
            if (ecbEncryptor == null)
            {
                if (key == null) throw new ArgumentNullException(nameof(key));
                if (key.Length != 16 && key.Length != 24 && key.Length != 32)
                    throw new ArgumentException("AES key must be 16/24/32 bytes", nameof(key));
            }
            if (input.Length == 0) return;

            // Number of 16-byte AES sub-blocks we need (round up).
            int aesBlocks = (input.Length + AesBlockBytes - 1) / AesBlockBytes;
            int keystreamBytes = aesBlocks * AesBlockBytes;

            byte[] counterBuf = ArrayPool<byte>.Shared.Rent(keystreamBytes);
            byte[] keystream  = ArrayPool<byte>.Shared.Rent(keystreamBytes);
            int counterUsed   = 0;
            int keystreamUsed = 0;
            Aes ownedAes = null;
            ICryptoTransform ownedEnc = null;
            try
            {
                // Build the counter blocks in counterBuf[0..keystreamBytes).
                BuildCounterBlocks(counterBuf, aesBlocks, nonceBase, blockIndex);
                counterUsed = keystreamBytes;

                ICryptoTransform enc = ecbEncryptor;
                if (enc == null)
                {
                    ownedAes = Aes.Create();
                    ownedAes.Mode    = CipherMode.ECB;
                    ownedAes.Padding = PaddingMode.None;
                    ownedAes.KeySize = key.Length * 8;
                    ownedAes.Key     = key;
                    ownedEnc = ownedAes.CreateEncryptor();
                    enc = ownedEnc;
                }

                int produced = enc.TransformBlock(
                    counterBuf, 0, keystreamBytes,
                    keystream, 0);
                if (produced != keystreamBytes)
                {
                    throw new CryptographicException(
                        "AES-ECB produced wrong output length: " + produced);
                }
                keystreamUsed = keystreamBytes;

                // XOR keystream into plaintext to produce ciphertext (or vice
                // versa — CTR is symmetric).
                for (int i = 0; i < input.Length; i++)
                {
                    output[i] = (byte)(input[i] ^ keystream[i]);
                }
            }
            finally
            {
                // Scrub both buffers before returning to the pool. counterBuf
                // contains the raw counter (not secret, but contains the
                // nonce) and keystream contains key-derived output (very
                // secret).
                if (counterUsed > 0) Array.Clear(counterBuf, 0, counterUsed);
                if (keystreamUsed > 0) Array.Clear(keystream, 0, keystreamUsed);
                ArrayPool<byte>.Shared.Return(counterBuf, clearArray: false);
                ArrayPool<byte>.Shared.Return(keystream,  clearArray: false);

                // Only dispose if we created them. A caller-supplied encryptor
                // is owned by the caller.
                ownedEnc?.Dispose();
                ownedAes?.Dispose();
            }
        }

        /// <summary>
        /// Create a reusable AES-ECB encryptor for the given key. Used by the
        /// mmap decryptor and stream reader to amortise CreateEncryptor cost
        /// across many blocks. Caller OWNS the returned transform and its
        /// parent Aes (both dispose by calling <see cref="DisposeReusable"/>).
        /// </summary>
        internal static ReusableEcbEncryptor CreateReusableEncryptor(byte[] key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (key.Length != 16 && key.Length != 24 && key.Length != 32)
                throw new ArgumentException("AES key must be 16/24/32 bytes", nameof(key));

            Aes aes = null;
            ICryptoTransform enc = null;
            try
            {
                aes = Aes.Create();
                aes.Mode    = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.KeySize = key.Length * 8;
                aes.Key     = key;
                enc = aes.CreateEncryptor();
                var r = new ReusableEcbEncryptor(aes, enc);
                aes = null;
                enc = null;
                return r;
            }
            catch
            {
                enc?.Dispose();
                aes?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Holds a paired <see cref="Aes"/> + <see cref="ICryptoTransform"/>
        /// and disposes them together. Behaves like the minimal helper struct
        /// we'd want; we keep it a class so Dispose is idempotent via the
        /// owning field nulling.
        /// </summary>
        internal sealed class ReusableEcbEncryptor : IDisposable
        {
            private Aes _aes;
            private ICryptoTransform _encryptor;

            internal ReusableEcbEncryptor(Aes aes, ICryptoTransform encryptor)
            {
                _aes = aes;
                _encryptor = encryptor;
            }

            public ICryptoTransform Encryptor => _encryptor;

            public void Dispose()
            {
                try { _encryptor?.Dispose(); } catch { }
                try { _aes?.Dispose(); } catch { }
                _encryptor = null;
                _aes = null;
            }
        }

        /// <summary>
        /// Fill <paramref name="dest"/> with <paramref name="count"/>
        /// consecutive 16-byte counter blocks. The first counter is
        /// (nonce_base XOR block_index_be), and subsequent counters
        /// increment the low 32 bits (big-endian sub-counter).
        /// </summary>
        private static void BuildCounterBlocks(
            byte[] dest,
            int count,
            ReadOnlySpan<byte> nonceBase,
            ulong blockIndex)
        {
            // First counter: copy the nonce, then XOR the big-endian block
            // index into the low 8 bytes. This keeps the high 8 bytes of the
            // nonce intact as a per-file fingerprint.
            for (int i = 0; i < AesBlockBytes; i++)
                dest[i] = nonceBase[i];

            dest[8]  ^= (byte)((blockIndex >> 56) & 0xFF);
            dest[9]  ^= (byte)((blockIndex >> 48) & 0xFF);
            dest[10] ^= (byte)((blockIndex >> 40) & 0xFF);
            dest[11] ^= (byte)((blockIndex >> 32) & 0xFF);
            dest[12] ^= (byte)((blockIndex >> 24) & 0xFF);
            dest[13] ^= (byte)((blockIndex >> 16) & 0xFF);
            dest[14] ^= (byte)((blockIndex >>  8) & 0xFF);
            dest[15] ^= (byte)((blockIndex      ) & 0xFF);

            // Subsequent counters copy block 0 and add a 32-bit big-endian
            // sub-counter (j) to the last four bytes with carry. CTR mode
            // as specified in NIST SP 800-38A treats the last N bytes as an
            // integer to increment; we choose N=4 (room for 2^32 sub-blocks,
            // far above any plausible block size).
            for (int j = 1; j < count; j++)
            {
                int dstOff = j * AesBlockBytes;
                Buffer.BlockCopy(dest, 0, dest, dstOff, AesBlockBytes);

                // Add j (unsigned 32-bit) to the last 4 bytes, big-endian.
                uint subCounter = (uint)j;
                uint b3 = (uint)dest[dstOff + 15] + (subCounter & 0xFF);
                dest[dstOff + 15] = (byte)(b3 & 0xFF);
                uint carry = b3 >> 8;

                uint b2 = (uint)dest[dstOff + 14] + ((subCounter >> 8) & 0xFF) + carry;
                dest[dstOff + 14] = (byte)(b2 & 0xFF);
                carry = b2 >> 8;

                uint b1 = (uint)dest[dstOff + 13] + ((subCounter >> 16) & 0xFF) + carry;
                dest[dstOff + 13] = (byte)(b1 & 0xFF);
                carry = b1 >> 8;

                uint b0 = (uint)dest[dstOff + 12] + ((subCounter >> 24) & 0xFF) + carry;
                dest[dstOff + 12] = (byte)(b0 & 0xFF);
                // We deliberately drop any further carry — 2^32 sub-blocks
                // is >64 GB; anything that large violates MaxBlockSize.
            }
        }
    }
}
