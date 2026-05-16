using System;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// Zero-alloc, random-access decryptor over a v3 .gisub file that is
    /// memory-mapped from disk. This is the interface the mmap matcher will
    /// use to read arbitrary byte ranges out of a 100 MB+ dictionary
    /// without ever materialising the full plaintext in managed memory.
    ///
    /// Contract
    /// --------
    /// - <see cref="ReadPlaintext"/> decrypts only the blocks that overlap
    ///   the requested range. Each block is verified against its per-block
    ///   HMAC before any plaintext is written to the destination span.
    ///   A verification failure throws <see cref="System.Security.Cryptography.CryptographicException"/>
    ///   and leaves the destination untouched.
    /// - Implementations are NOT required to be thread-safe. Per-thread use
    ///   is expected (each mmap matcher worker opens its own decryptor, or
    ///   a single consumer serialises access).
    /// - <see cref="IDisposable.Dispose"/> tears down the view accessor and
    ///   the MemoryMappedFile. Callers MUST dispose; the finalizer only
    ///   suppresses process-level leaks during unclean shutdown.
    /// </summary>
    public interface IMmapDecryptor : IDisposable
    {
        /// <summary>
        /// Plaintext length in bytes (as recorded in the v3 header).
        /// </summary>
        long Length { get; }

        /// <summary>
        /// Decrypt and copy <c>destination.Length</c> bytes of plaintext
        /// starting at absolute plaintext offset <paramref name="offset"/>
        /// into <paramref name="destination"/>. Verifies every block HMAC
        /// touched by the range; throws on corruption.
        /// </summary>
        /// <param name="offset">Absolute plaintext offset (0 = start of file).</param>
        /// <param name="destination">Output buffer, exactly as long as the requested read.</param>
        void ReadPlaintext(long offset, Span<byte> destination);
    }
}
