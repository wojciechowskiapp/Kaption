using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using GI_Subtitles.Common;
using Newtonsoft.Json;

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
        /// Returns a Stream suitable for JsonTextReader.
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
            using (var streamReader = new StreamReader(stream))
            using (var jsonReader = new JsonTextReader(streamReader))
            {
                long totalSize = stream.Length;
                jsonReader.Read(); // Start object
                while (jsonReader.Read())
                {
                    if (jsonReader.TokenType == JsonToken.PropertyName)
                    {
                        string key = (string)jsonReader.Value;
                        jsonReader.Read();
                        string value = (string)jsonReader.Value;
                        dict[key] = value;

                        if (progress != null && dict.Count % 5000 == 0)
                        {
                            long pos = stream.CanSeek ? stream.Position : 0;
                            int pct = totalSize > 0
                                ? (int)(progressMin + (pos * (double)(progressMax - progressMin) / totalSize))
                                : progressMin;
                            progress.Report((pct, $"Loading dictionary... {dict.Count:N0} entries"));
                        }
                    }
                }
            }

            return dict;
        }

        /// <summary>
        /// Save a dictionary as encrypted JSON if the language is custom,
        /// or as plain JSON if public.
        /// </summary>
        public void SaveDictionary(Dictionary<string, string> dict, string jsonPath,
            string outputLanguage)
        {
            string json = JsonConvert.SerializeObject(dict, Formatting.None);
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
            var json = JsonConvert.SerializeObject(data, Formatting.None);
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
