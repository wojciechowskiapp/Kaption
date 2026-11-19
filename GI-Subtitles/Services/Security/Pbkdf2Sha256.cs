using System;
using System.Security.Cryptography;
using GI_Subtitles.Common;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// PBKDF2-HMAC-SHA256 for the machine-bound file keys. Asks Windows first
    /// (<see cref="Rfc2898DeriveBytes.Pbkdf2(byte[], byte[], int, HashAlgorithmName, int)"/>,
    /// CNG's PBKDF2 key-derivation call) and, when that call throws a
    /// <see cref="CryptographicException"/>, derives the same bytes in managed code on top of HMAC-SHA256.
    /// On one Windows 10 machine (KAPTION-DESKTOP-3) the OS call failed every time with
    /// "Unknown error (0xc1000008)" while HMAC-SHA256 and AES worked, which left the player without a pack.
    /// </summary>
    internal sealed class Pbkdf2Sha256
    {
        /// <summary>The OS call: password, salt, iterations, output length.</summary>
        internal delegate byte[] Derivation(byte[] password, byte[] salt, int iterations, int outputLength);

        private const int HashSizeBytes = 32; // SHA-256

        /// <summary>The process-wide instance: once Windows' call has failed, every later derivation goes managed.</summary>
        public static Pbkdf2Sha256 Shared { get; } = new(NativeDerive);

        private readonly Derivation _native;
        private volatile bool _nativeFailed;

        internal Pbkdf2Sha256(Derivation native)
        {
            _native = native ?? throw new ArgumentNullException(nameof(native));
        }

        /// <summary>True once the OS call has failed and this instance derives in managed code.</summary>
        public bool UsesManagedFallback => _nativeFailed;

        public byte[] Derive(byte[] password, byte[] salt, int iterations, int outputLength)
        {
            if (!_nativeFailed)
            {
                try
                {
                    return _native(password, salt, iterations, outputLength);
                }
                catch (CryptographicException ex)
                {
                    // Expected on a machine whose CNG key derivation is broken and self-healing here,
                    // so Warn (stays out of Sentry), once per instance.
                    _nativeFailed = true;
                    Logger.Log.Warn(
                        $"PBKDF2: Windows key derivation failed (0x{ex.HResult:x8}: {ex.Message}); "
                        + "deriving the file keys in managed code from now on.");
                }
            }
            return DeriveManaged(password, salt, iterations, outputLength);
        }

        private static byte[] NativeDerive(byte[] password, byte[] salt, int iterations, int outputLength) =>
            Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, outputLength);

        /// <summary>
        /// PBKDF2 (RFC 8018 section 5.2) with HMAC-SHA256 as the PRF. Byte-identical to the OS call, so files
        /// written under either stay readable under the other.
        /// </summary>
        internal static byte[] DeriveManaged(byte[] password, byte[] salt, int iterations, int outputLength)
        {
            ArgumentNullException.ThrowIfNull(password);
            ArgumentNullException.ThrowIfNull(salt);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputLength);

            byte[] output = new byte[outputLength];
            Span<byte> u = stackalloc byte[HashSizeBytes];
            Span<byte> t = stackalloc byte[HashSizeBytes];
            Span<byte> blockIndex = stackalloc byte[4];

            // One keyed HMAC reused for every iteration: CNG keeps the key schedule, each round is append + reset.
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, password);
            try
            {
                int blocks = (outputLength + HashSizeBytes - 1) / HashSizeBytes;
                for (int block = 1; block <= blocks; block++)
                {
                    // U1 = PRF(P, S || INT(i)), big-endian block index.
                    blockIndex[0] = (byte)(block >> 24);
                    blockIndex[1] = (byte)(block >> 16);
                    blockIndex[2] = (byte)(block >> 8);
                    blockIndex[3] = (byte)block;
                    hmac.AppendData(salt);
                    hmac.AppendData(blockIndex);
                    hmac.GetHashAndReset(u);
                    u.CopyTo(t);

                    // Uj = PRF(P, Uj-1); T = U1 xor U2 xor ... xor Uc.
                    for (int j = 1; j < iterations; j++)
                    {
                        hmac.AppendData(u);
                        hmac.GetHashAndReset(u);
                        for (int k = 0; k < HashSizeBytes; k++) t[k] ^= u[k];
                    }

                    int offset = (block - 1) * HashSizeBytes;
                    t.Slice(0, Math.Min(HashSizeBytes, outputLength - offset)).CopyTo(output.AsSpan(offset));
                }
                return output;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(u);
                CryptographicOperations.ZeroMemory(t);
            }
        }
    }
}
