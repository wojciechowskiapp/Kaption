using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GI_Subtitles.Common;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// High-level helper that wraps IFileProtectionService with convenience methods
    /// for the specific file types used in the app. Handles:
    ///   - Smart file resolution (try .gisub first, fallback to .json)
    ///   - Transparent migration (encrypt unprotected files on first access)
    ///   - Streaming JSON deserialization from encrypted data
    ///   - Parallel migration of multiple files
    /// </summary>
    public sealed class FileProtectionHelper
    {
        private readonly IFileProtectionService _protection;

        public FileProtectionHelper(IFileProtectionService protection)
        {
            _protection = protection ?? throw new ArgumentNullException(nameof(protection));
        }

        /// <summary>
        /// Resolve the actual file path: prefer .gisub if it exists, fallback to .json.
        /// Returns (path, isEncrypted).
        /// </summary>
        public (string path, bool isEncrypted) ResolveFile(string jsonPath)
        {
            string gisubPath = _protection.GetProtectedPath(jsonPath);

            if (File.Exists(gisubPath))
                return (gisubPath, true);

            if (File.Exists(jsonPath))
                return (jsonPath, false);

            return (null, false);
        }

        /// <summary>
        /// Read a JSON file that may or may not be encrypted.
        /// If unencrypted, reads normally. If encrypted, decrypts to memory first.
        /// Returns a Stream the caller can parse JSON from.
        /// Caller must dispose the returned stream.
        /// </summary>
        public Stream OpenForReading(string jsonPath)
        {
            var (resolvedPath, isEncrypted) = ResolveFile(jsonPath);

            if (resolvedPath == null)
                throw new FileNotFoundException($"Neither .gisub nor .json found for: {jsonPath}");

            if (isEncrypted)
            {
                return _protection.DecryptToStream(resolvedPath);
            }
            else
            {
                // Plain file — read into MemoryStream for consistent behavior
                return File.OpenRead(resolvedPath);
            }
        }

        /// <summary>
        /// Load a Dictionary&lt;string, string&gt; from a potentially encrypted JSON file.
        /// Uses streaming JSON parsing for memory efficiency.
        /// </summary>
        public Dictionary<string, string> LoadDictionary(string jsonPath,
            IProgress<(int percent, string message)> progress = null,
            int progressMin = 0, int progressMax = 100)
        {
            var (resolvedPath, isEncrypted) = ResolveFile(jsonPath);
            if (resolvedPath == null)
                return null;

            var dict = new Dictionary<string, string>();

            using (var stream = isEncrypted
                ? (Stream)_protection.DecryptToStream(resolvedPath)
                : File.OpenRead(resolvedPath))
            {
                long totalSize = stream.Length;
                var buffer = new byte[64 * 1024];
                int bytesInBuffer = 0;
                bool isFinalBlock = false;
                bool bomChecked = false;
                var state = new JsonReaderState(DictionaryReaderOptions);
                string pendingKey = null;

                while (!isFinalBlock)
                {
                    if (bytesInBuffer == buffer.Length)
                        Array.Resize(ref buffer, buffer.Length * 2);

                    int read = stream.Read(buffer, bytesInBuffer, buffer.Length - bytesInBuffer);
                    isFinalBlock = read == 0;
                    bytesInBuffer += read;

                    if (!bomChecked && bytesInBuffer >= 3)
                    {
                        bomChecked = true;
                        if (buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                        {
                            Buffer.BlockCopy(buffer, 3, buffer, 0, bytesInBuffer - 3);
                            bytesInBuffer -= 3;
                        }
                    }

                    int consumed = ReadDictionarySegment(
                        buffer, bytesInBuffer, isFinalBlock, ref state, ref pendingKey,
                        dict, stream, totalSize, progress, progressMin, progressMax);

                    if (consumed < bytesInBuffer)
                        Buffer.BlockCopy(buffer, consumed, buffer, 0, bytesInBuffer - consumed);
                    bytesInBuffer -= consumed;
                }
            }

            return dict;
        }

        private static readonly JsonReaderOptions DictionaryReaderOptions = new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static int ReadDictionarySegment(
            byte[] buffer, int length, bool isFinalBlock,
            ref JsonReaderState state, ref string pendingKey,
            Dictionary<string, string> dict, Stream stream, long totalSize,
            IProgress<(int percent, string message)> progress, int progressMin, int progressMax)
        {
            var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(buffer, 0, length), isFinalBlock, state);

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    pendingKey = reader.GetString();
                    continue;
                }

                if (pendingKey == null)
                    continue;

                if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
                    dict[pendingKey] = null;
                else
                    dict[pendingKey] = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();

                pendingKey = null;

                if (progress != null && dict.Count % 5000 == 0)
                {
                    long pos = stream.CanSeek ? stream.Position : 0;
                    int pct = totalSize > 0
                        ? (int)(progressMin + (pos * (double)(progressMax - progressMin) / totalSize))
                        : progressMin;
                    progress.Report((pct, $"Loading dictionary... {dict.Count:N0} entries"));
                }
            }

            state = reader.CurrentState;
            return (int)reader.BytesConsumed;
        }

        /// <summary>
        /// Save a dictionary as encrypted JSON if the language is custom,
        /// or as plain JSON if public.
        /// </summary>
        public void SaveDictionary(Dictionary<string, string> dict, string jsonPath,
            string outputLanguage)
        {
            string json = JsonSerializer.Serialize(dict, JsonDefaults.Options);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

            if (LanguageClassification.ShouldProtectFile(jsonPath, outputLanguage))
            {
                string gisubPath = _protection.GetProtectedPath(jsonPath);
                _protection.EncryptBytes(jsonBytes, gisubPath);

                // Remove plaintext if it exists
                if (File.Exists(jsonPath))
                {
                    try { File.Delete(jsonPath); } catch { }
                }
            }
            else
            {
                File.WriteAllBytes(jsonPath, jsonBytes);
            }
        }

        /// <summary>
        /// Save raw compact JSON bytes as encrypted .gisub (for graph files).
        /// Always encrypts — graph files are always protected.
        /// </summary>
        public void SaveProtectedJson(string jsonPath, object data)
        {
            var json = JsonSerializer.Serialize(data, data?.GetType() ?? typeof(object), JsonDefaults.Options);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

            string gisubPath = _protection.GetProtectedPath(jsonPath);
            _protection.EncryptBytes(jsonBytes, gisubPath);

            // Remove plaintext if it exists
            if (File.Exists(jsonPath))
            {
                try { File.Delete(jsonPath); } catch { }
            }
        }

        /// <summary>
        /// Check whether a file exists in either encrypted or plaintext form.
        /// </summary>
        public bool FileExists(string jsonPath)
        {
            string gisubPath = _protection.GetProtectedPath(jsonPath);
            return File.Exists(gisubPath) || File.Exists(jsonPath);
        }

        /// <summary>
        /// Migrate existing plaintext files to encrypted format.
        /// Runs in parallel for speed. Only migrates files that should be protected.
        /// </summary>
        public void MigrateExistingFiles(string gameDataDir, string outputLanguage)
        {
            if (!Directory.Exists(gameDataDir))
                return;

            var filesToMigrate = new List<string>();

            foreach (string file in Directory.GetFiles(gameDataDir, "*.json"))
            {
                string fileName = Path.GetFileName(file);
                if (LanguageClassification.ShouldProtectFile(fileName, outputLanguage))
                {
                    string gisubPath = _protection.GetProtectedPath(file);
                    // Only migrate if .gisub doesn't already exist
                    if (!File.Exists(gisubPath))
                    {
                        filesToMigrate.Add(file);
                    }
                }
            }

            if (filesToMigrate.Count == 0)
                return;

            Logger.Log.Info($"Migrating {filesToMigrate.Count} files to encrypted format...");

            // Encrypt in parallel for speed
            Parallel.ForEach(filesToMigrate,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                file =>
                {
                    try
                    {
                        string gisubPath = _protection.GetProtectedPath(file);
                        _protection.EncryptFile(file, gisubPath);

                        // Delete the plaintext original
                        File.Delete(file);

                        Logger.Log.Info($"Migrated: {Path.GetFileName(file)} -> {Path.GetFileName(gisubPath)}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log.Error($"Failed to migrate {Path.GetFileName(file)}: {ex.Message}");
                    }
                });

            Logger.Log.Info("File migration complete");
        }

        /// <summary>
        /// Delete both .json and .gisub variants of a file (used when cache needs refresh).
        /// </summary>
        public void DeleteBothVariants(string jsonPath)
        {
            try
            {
                if (File.Exists(jsonPath))
                    File.Delete(jsonPath);

                string gisubPath = _protection.GetProtectedPath(jsonPath);
                if (File.Exists(gisubPath))
                    File.Delete(gisubPath);
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Failed to delete file variants: {ex.Message}");
            }
        }
    }
}
