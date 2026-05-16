using System;
using System.Collections.Generic;
using System.IO;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// File protection service interface for encrypting/decrypting proprietary data files.
    /// Designed for easy swap between machine-bound encryption (current) and
    /// server-key encryption (future login system).
    /// </summary>
    public interface IFileProtectionService
    {
        /// <summary>
        /// Encrypt a plaintext file and write the encrypted output. The plaintext
        /// file is preserved — caller decides when (or whether) to remove it.
        /// </summary>
        void EncryptFile(string plaintextPath, string encryptedPath);

        /// <summary>
        /// Decrypt an encrypted file and return a readable stream.
        /// The returned MemoryStream contains the full plaintext — never written to disk.
        /// Caller must dispose the stream.
        /// </summary>
        MemoryStream DecryptToStream(string encryptedPath);

        /// <summary>
        /// Decrypt an encrypted file and return a forward-only, read-only stream that
        /// streams plaintext on demand without materialising the whole file in memory.
        ///
        /// Implementation contract: HMAC integrity MUST be verified BEFORE any plaintext
        /// byte is returned (Encrypt-then-MAC). Implementations that cannot verify
        /// upfront should throw — callers rely on the returned bytes being authentic.
        ///
        /// Caller must dispose the stream. The returned stream wraps an underlying
        /// FileStream + CryptoStream and releases both on Dispose.
        ///
        /// Use this in preference to <see cref="DecryptToStream"/> for large payloads
        /// (GSMX matcher indexes, TextMap JSON) where peak memory matters. The fully-
        /// buffered variant still has its place for small files and for callers that
        /// rewind the stream (CryptoStream is non-seekable).
        /// </summary>
        Stream OpenDecryptStream(string encryptedPath);

        /// <summary>
        /// Check whether a file has the encrypted format header.
        /// </summary>
        bool IsEncrypted(string filePath);

        /// <summary>
        /// Encrypt raw bytes (e.g. serialized JSON) and write to disk.
        /// Used when generating files (graph build, cache save).
        /// </summary>
        void EncryptBytes(byte[] plaintext, string encryptedPath);

        /// <summary>
        /// Get the encrypted file path for a given logical file path.
        /// Replaces .json with .gisub extension.
        /// </summary>
        string GetProtectedPath(string originalPath);

        /// <summary>
        /// Open a write-only <see cref="EncryptingStream"/> that encrypts data
        /// as it's written and materialises the resulting <c>.gisub</c> file
        /// at <paramref name="encryptedPath"/> when the caller calls
        /// <see cref="EncryptingStream.Complete"/>. The caller writes plaintext
        /// to the returned stream (e.g. via <see cref="Stream.CopyToAsync(Stream)"/>);
        /// the implementation takes care of:
        ///
        ///   * generating a random IV,
        ///   * writing the .gisub header with a placeholder HMAC,
        ///   * AES-256-CBC encryption,
        ///   * HMAC-SHA256 over ciphertext (Encrypt-then-MAC),
        ///   * atomically replacing any existing file at the final path,
        ///   * cleaning up the temp file if the caller disposes without
        ///     first calling <see cref="EncryptingStream.Complete"/>.
        ///
        /// <b>Contract — the two-phase commit:</b> after writing the last byte
        /// the caller MUST call <c>Complete()</c>, then Dispose (the standard
        /// <c>using</c> pattern handles the second step). If an exception
        /// bubbles out of the write loop, let the <c>using</c> dispose run
        /// WITHOUT calling <c>Complete()</c> — the implementation will reap
        /// the <c>.tmp</c> file and leave no output behind. This is the only
        /// way a Stream-shaped API can distinguish "caller finished cleanly"
        /// from "caller threw mid-stream", because the Stream interface has
        /// no exception-in-finally signal.
        ///
        /// Peak memory is bounded by the underlying pipe/block buffer (≈16 KB)
        /// — no whole-file plaintext or ciphertext buffer is ever materialised
        /// in memory. This is the streaming alternative to
        /// <see cref="EncryptFile(string, string)"/>, used by DictionarySync to
        /// avoid holding the full translation pack in RAM during re-encryption.
        /// </summary>
        /// <param name="encryptedPath">Final <c>.gisub</c> location. Written
        /// atomically via a sibling <c>.tmp</c> file.</param>
        /// <returns>A writable stream. Caller calls <c>Complete()</c> on success,
        /// or just lets Dispose run on the exception path to reap the .tmp.</returns>
        EncryptingStream OpenEncryptStream(string encryptedPath);

        /// <summary>
        /// Streaming counterpart of <see cref="EncryptFile(string, string)"/>.
        /// Streams <paramref name="plaintextPath"/> through the encryptor into
        /// <paramref name="encryptedPath"/> without materialising the whole
        /// plaintext or ciphertext in memory. Suitable for multi-MB files.
        /// The plaintext file is NOT deleted — caller decides when to remove it.
        /// </summary>
        void EncryptFileStreaming(string plaintextPath, string encryptedPath);

        // ── v3 format support (AES-CTR + per-block HMAC) ──────────────────
        //
        // v3 is the mmap-friendly format: each 4 KB block stands alone so a
        // random-access reader doesn't have to decrypt from offset 0. Opt-in
        // in this commit — v2 remains the default write format for
        // backward compat. See ProtectedFileFormatV3 for the layout.

        /// <summary>
        /// Open a seekable, read-only plaintext stream over a v3 .gisub file.
        /// Decrypts lazily, one block at a time, verifying each block's HMAC
        /// before returning bytes. Disposing the stream releases the file
        /// handle. Throws <see cref="InvalidOperationException"/> if the file
        /// isn't v3 (caller can fall back to <see cref="DecryptToStream"/>).
        /// </summary>
        Stream OpenDecryptStreamV3(string path);

        /// <summary>
        /// Open a zero-alloc, random-access decryptor over a v3 .gisub file.
        /// Intended for mmap-based consumers (e.g. the Phase 2 matcher) that
        /// want to read arbitrary ranges out of a large dictionary without
        /// ever materialising the full plaintext in managed memory.
        /// </summary>
        IMmapDecryptor OpenMmapDecryptor(string path);

        /// <summary>
        /// Stream-based v3 writer. Reads exactly <paramref name="plaintextLength"/>
        /// bytes from <paramref name="source"/> and emits a v3 (AES-CTR + per-block
        /// HMAC) .gisub file at <paramref name="encryptedPath"/>. The caller owns
        /// the lifetime of <paramref name="source"/>.
        ///
        /// Used by the matcher-blob save path to wrap a serialised KMX blob in
        /// the mmap-friendly v3 container without materialising a second
        /// ciphertext copy. Implementations write to a sibling <c>.tmp</c> and
        /// rename atomically; failure removes the temp file.
        /// </summary>
        void EncryptStreamToV3(Stream source, long plaintextLength, string encryptedPath);
    }

    /// <summary>
    /// Write-only stream returned by <see cref="IFileProtectionService.OpenEncryptStream"/>.
    /// Encrypts data as it's written and finalises the output file when
    /// <see cref="Complete"/> is called. Disposing without calling
    /// <see cref="Complete"/> first treats the operation as aborted — the
    /// temp file is reaped and no output is materialised.
    ///
    /// See <see cref="IFileProtectionService.OpenEncryptStream"/> for the
    /// full contract rationale.
    /// </summary>
    public abstract class EncryptingStream : Stream
    {
        /// <summary>
        /// Finalise the encrypted file: flush the last AES block, patch the
        /// real HMAC into the header, and atomically rename the temp file to
        /// the target location. Must be called on the success path before
        /// Dispose. Idempotent — second call is a no-op.
        ///
        /// On the exception path, do NOT call this method: just let the
        /// <c>using</c> block's Dispose run — it will reap the temp file
        /// and leave no output behind.
        /// </summary>
        public abstract void Complete();
    }

    /// <summary>
    /// Classifies files/languages as public (game data) or custom (our translations).
    /// Public languages ship with the game and are freely available.
    /// Custom languages are our proprietary translations that need protection.
    /// </summary>
    public static class LanguageClassification
    {
        // All languages that ship with the game and are publicly available
        // from Dimbreath/game data repositories
        private static readonly HashSet<string> PublicLanguageCodes = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "EN", "CHS", "CHT", "JP", "KR", "DE", "ES", "FR", "ID", "PT", "RU", "TH", "VI"
        };

        /// <summary>
        /// Returns true if this language code is a custom/proprietary translation.
        /// Any language NOT in the public game data = custom.
        /// </summary>
        public static bool IsCustomLanguage(string langCode)
        {
            if (string.IsNullOrEmpty(langCode)) return false;
            return !PublicLanguageCodes.Contains(langCode);
        }

        /// <summary>
        /// Names of generated graph/index files that are always protected
        /// (our proprietary processing regardless of language).
        /// </summary>
        private static readonly HashSet<string> ProtectedFileNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "DialogGraph",
            "HashToDialogs",
            "NpcNames",
            "TalkIndex",
            "QuestInfo",
            "CharacterProfiles"
        };

        /// <summary>
        /// Check if a file should be encrypted based on its name and language context.
        /// </summary>
        public static bool ShouldProtectFile(string fileName, string outputLanguage)
        {
            if (string.IsNullOrEmpty(fileName)) return false;

            string baseName = Path.GetFileNameWithoutExtension(fileName);

            // Generated graph files are always protected
            if (ProtectedFileNames.Contains(baseName))
                return true;

            // TextMap files containing custom language data
            if (baseName.StartsWith("TextMap", StringComparison.OrdinalIgnoreCase))
            {
                // Pure input-language TextMap (e.g., TextMapEN.json) — public, don't protect
                // But TextMapPL.json or TextMapEN_TextMapPL.json — protect
                if (!string.IsNullOrEmpty(outputLanguage) && IsCustomLanguage(outputLanguage))
                {
                    // Check if this filename contains the custom language code
                    if (baseName.IndexOf(outputLanguage, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }

            return false;
        }
    }
}
