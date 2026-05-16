using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GI_Subtitles.Services.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GI_Subtitles.Common
{
    /// <summary>
    /// Helper class for voice content processing
    /// </summary>
    public static partial class VoiceContentHelper
    {
        // ---------------------------------------------------------------
        // Cached regexes — [GeneratedRegex] source generation (net8+)
        //
        // The legacy rebuild path used to construct a fresh Regex on every
        // iteration of a ~500k-entry TextMap join. Migrated from
        // `new Regex(pattern, RegexOptions.Compiled)` → [GeneratedRegex],
        // which emits a compile-time state-machine C# class instead of the
        // runtime Reflection.Emit path that Compiled uses. 1.3–2× faster
        // than Compiled on hot paths, zero startup cost, AOT-friendly,
        // and the JIT sees concrete C# so PGO applies.
        //
        // RegexOptions.Compiled is IMPLICIT for GeneratedRegex — adding it
        // emits a warning. Keep only CultureInvariant.
        //
        // Every Regex used ANYWHERE in this file lives here. Never call
        // `new Regex(...)` or a static `Regex.Replace(string, string, string)`
        // with a literal pattern from another method — that's a silent
        // recompile per invocation.
        // ---------------------------------------------------------------

        /// <summary> {foo} placeholder (non-greedy). Strips game formatting tokens. </summary>
        [GeneratedRegex(@"\{.*?\}", RegexOptions.CultureInvariant)]
        private static partial Regex BracedPlaceholderRegex();

        /// <summary> &lt;unbreak&gt; / &lt;/unbreak&gt; HoYo soft-break markers. </summary>
        [GeneratedRegex(@"</?unbreak>", RegexOptions.CultureInvariant)]
        private static partial Regex UnbreakTagRegex();

        /// <summary> &lt;color=...&gt;inner&lt;/color&gt; strip, keeps inner text. </summary>
        [GeneratedRegex(@"<color=.*?>(.*?)</color>", RegexOptions.CultureInvariant)]
        private static partial Regex ColorTagRegex();

        /// <summary>
        /// ProcessGender pattern: {F#female-text} or {M#male-text}.
        /// Captured group 1 is the leading char + '#' + payload.
        /// </summary>
        [GeneratedRegex(@"\{([FM]#.*?)}", RegexOptions.CultureInvariant)]
        private static partial Regex GenderMarkerRegex();

        /// <summary>
        /// Cache format version. Bump whenever the built dictionary's on-disk representation
        /// changes in a way that old caches are no longer valid for the current app version.
        ///
        ///   v1 (implicit): original format. Stored "\\n" literal escape sequences; MainWindow
        ///                  replaced them at display time inside UpdateText on every tick.
        ///   v2:            pre-processes "\\n" → "\n" at build time (see line ~260) so the hot
        ///                  display path does no string.Replace work. v1 caches rendered with
        ///                  literal "\n" visible in subtitles once the display-time replace was
        ///                  removed, so they must be rebuilt.
        /// </summary>
        private const string CacheVersion = "v2";

        /// <summary>
        /// Compute the cache filename for a given (input, output) TextMap pair.
        /// Embeds CacheVersion so format bumps invalidate stale caches without requiring
        /// users to manually clear %APPDATA%\Kaption.
        /// </summary>
        private static string BuildCachePath(string inputFilePath, string outputFilePath)
        {
            var dir = Path.GetDirectoryName(inputFilePath);
            var baseName = $"{Path.GetFileNameWithoutExtension(inputFilePath)}_{Path.GetFileNameWithoutExtension(outputFilePath)}";
            return Path.Combine(dir, $"{baseName}.{CacheVersion}.json");
        }

        /// <summary>
        /// Delete cache files from prior CacheVersion values so they don't accumulate on disk.
        /// Safe to call even if no legacy files exist. Does not throw on failure — logs and
        /// continues (legacy file cleanup is best-effort, not load-bearing).
        /// </summary>
        private static void CleanupLegacyCache(string inputFilePath, string outputFilePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(inputFilePath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                var baseName = $"{Path.GetFileNameWithoutExtension(inputFilePath)}_{Path.GetFileNameWithoutExtension(outputFilePath)}";

                // Delete the unversioned v1 file and any .gisub counterpart.
                foreach (var legacy in new[] {
                    Path.Combine(dir, $"{baseName}.json"),
                    Path.Combine(dir, $"{baseName}.gisub")
                })
                {
                    if (File.Exists(legacy))
                    {
                        try
                        {
                            File.Delete(legacy);
                            Logger.Log.Info($"Removed legacy cache: {Path.GetFileName(legacy)}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log.Warn($"Could not delete legacy cache {legacy}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Legacy cache cleanup failed: {ex.Message}");
            }
        }

        public static Dictionary<string, string> CreateVoiceContentDictionary(string inputFilePath, string outputFilePath, string userName)
        {
            return CreateVoiceContentDictionary(inputFilePath, outputFilePath, userName, null);
        }

        /// <summary>
        /// Load a pre-merged { english: target } dictionary produced by
        /// <c>tools/merge-textmap.cjs</c> and served as a <c>.gisub</c> by
        /// DictionarySync. Applies the single runtime substitution
        /// {NICKNAME} → the user's chosen traveler name. Everything else
        /// (regex cleanups, gender-variant selection, hash-ID joining) was
        /// already done at publish time, so this path is O(n) over entries
        /// with no per-value regex work.
        ///
        /// Returns null when the file doesn't look like a pre-merged dict
        /// (first few keys are numeric hash IDs) so the caller can fall
        /// back to the legacy ID-join merge.
        ///
        /// Memory note: previously loaded via <c>JObject.Load</c>, which
        /// materialized the entire ~80 MB file as a JToken DOM (~250 MB of
        /// JProperty/JValue nodes) before building the output dict. This
        /// path now streams via <c>Utf8JsonReader</c> directly into the
        /// output Dictionary — peak RAM during parse is O(entries-so-far ×
        /// 2 strings), not O(whole-file-DOM).
        /// </summary>
        private static Dictionary<string, string> LoadPreMergedDictionary(
            string outputPath,
            string resolvedPath,
            FileProtectionHelper protectionHelper,
            string userName,
            IProgress<(int percent, string message)> progress)
        {
            progress?.Report((10, "Loading translation pack..."));

            // Two on-disk shapes are tolerated per entry:
            //   * "hash-or-english": "translation"                   (normal)
            //   * "hash-or-english": { "value": "translation", ... } (HSR)
            // HoYo's HSR TextMap mixes plain strings with object-wrapped
            // values for a subset of entries (UI widgets with metadata
            // attached). A strict Dictionary<string,string> parse blows
            // up on the first wrapped value — fell through to legacy
            // merge, which wasted ~10s rebuilding the matcher from
            // TextMapEN + TextMapPL every launch.
            Dictionary<string, string> raw;
            using (var stream = protectionHelper.OpenForReading(outputPath))
            {
                raw = ReadFlatStringDictionaryFromJson(
                    stream,
                    flattenWrappedObjects: true,
                    entryCapacityHint: 0);
            }

            if (raw == null || raw.Count == 0)
            {
                Logger.Log.Warn("LoadPreMergedDictionary: empty dict on disk.");
                return null;
            }

            // Discriminate pre-merged (english-keyed) packs from legacy
            // ID-keyed TextMaps. Legacy packs have 100% numeric string
            // keys (hash IDs like "9340"). Pre-merged packs have mostly
            // English phrase keys — BUT the first ~20 entries of a
            // merge-textmap.cjs output are typically numeric too, because
            // HoYo puts UI enumeration strings ("0".."19") at the start
            // of TextMapEN and the merge preserves EN insertion order.
            // The old 20-key heuristic false-positived on every pack.
            //
            // Fix: scan up to 2000 keys, bail IMMEDIATELY on the first
            // non-numeric one. A real pre-merged pack hits a non-numeric
            // key within the first ~30 samples (only 107 of ~488k keys
            // are numeric, clustered at the start). A real legacy pack
            // shows 2000 numeric keys in a row — astronomically unlikely
            // for merged output. Only conclude "legacy" if we scan the
            // full sample without finding a single non-numeric key.
            // "Numeric" here means all-digits — NOT "fits in long". HSR's
            // TextMap hash IDs are xxhash64 values that routinely exceed
            // Int64.MaxValue, so a long.TryParse-based check would mis-tag
            // them as non-numeric and wrongly conclude the pack is pre-
            // merged. Digit-only string check handles arbitrarily large ids.
            int sampled = 0;
            bool foundNonNumeric = false;
            foreach (var k in raw.Keys)
            {
                if (sampled >= 2000) break;
                sampled++;
                if (!IsAllDigits(k))
                {
                    foundNonNumeric = true;
                    break;
                }
            }
            if (!foundNonNumeric && sampled >= 20)
            {
                Logger.Log.Info(
                    $"LoadPreMergedDictionary: {sampled} consecutive numeric keys at start of " +
                    $"{Path.GetFileName(resolvedPath)} — assuming legacy ID-keyed pack, caller will merge.");
                return null;
            }

            progress?.Report((30, $"Applying substitutions to {raw.Count:N0} entries..."));

            // Apply runtime NICKNAME substitution. String.Replace is O(n)
            // per entry; for 500k entries this is a sub-second hit.
            var final = new Dictionary<string, string>(raw.Count);
            string replacement = userName ?? "Traveler";
            foreach (var kv in raw)
            {
                string value = kv.Value;
                if (value != null && value.IndexOf("{NICKNAME}", StringComparison.Ordinal) >= 0)
                {
                    value = value.Replace("{NICKNAME}", replacement);
                }
                final[kv.Key] = value;
            }

            return final;
        }

        /// <summary>
        /// Coerce a JToken value into a plain string. Accepts:
        ///   * JTokenType.String      → the string as-is
        ///   * JTokenType.Object      → try common wrapper keys ("value",
        ///                              "text", "str") before giving up
        ///   * JTokenType.Null / other → null (caller skips the entry)
        /// HSR TextMap mixes plain strings and "{\"value\": \"...\"}"
        /// wrappers in the same file; Genshin uses only plain strings but
        /// this helper is harmless either way.
        /// </summary>
        private static bool IsAllDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
                if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        // ---------------------------------------------------------------
        // Streaming STJ dictionary readers
        //
        // These are the core memory-saving primitives for this file. Both
        // read a top-level JSON object whose values are either strings or
        // (optionally) objects with a "value"/"text"/"str" key, and emit
        // a Dictionary<string,string> WITHOUT materializing a DOM.
        //
        // Utf8JsonReader can't operate on a Stream directly — it's a ref
        // struct over a ReadOnlySpan<byte>. For files larger than the
        // rented buffer we refill via JsonReaderHelpers.Read-loop pattern:
        // when TryRead hits end-of-available-data we grow or shift the
        // buffer and pull more bytes from the stream.
        // ---------------------------------------------------------------

        /// <summary>
        /// Stream a top-level `{ "key": "value" (or {"value":"..."}) ... }`
        /// JSON object from <paramref name="stream"/> and materialize it as
        /// a plain Dictionary&lt;string, string&gt;. Malformed or non-string
        /// entries are silently skipped — matches the Newtonsoft JToken path
        /// tolerance exactly.
        /// </summary>
        /// <param name="stream">UTF-8 JSON input. Does not close the stream.</param>
        /// <param name="flattenWrappedObjects">
        /// When true, object-valued entries are unwrapped to their "value",
        /// "text", or "str" string if present; others skipped. When false,
        /// only plain string values are kept.
        /// </param>
        /// <param name="entryCapacityHint">Optional initial dict capacity; 0 = default.</param>
        private static Dictionary<string, string> ReadFlatStringDictionaryFromJson(
            Stream stream,
            bool flattenWrappedObjects,
            int entryCapacityHint)
        {
            var result = entryCapacityHint > 0
                ? new Dictionary<string, string>(entryCapacityHint, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            // 64 KiB is a reasonable sweet spot: big enough that common
            // large string values fit in one refill without thrashing,
            // small enough that it doesn't live on the LOH. The buffer
            // grows geometrically when a single token exceeds capacity
            // (rare — even the longest TextMap value is well under 1 KiB).
            const int initialBufferSize = 64 * 1024;
            var pool = ArrayPool<byte>.Shared;
            byte[] buffer = pool.Rent(initialBufferSize);
            int bytesInBuffer = 0;

            // Options match STJ defaults except we're forgiving about
            // comments and trailing commas. The TextMap files are
            // machine-produced but some community edits sneak these in.
            var readerOptions = new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            };

            JsonReaderState readerState = new JsonReaderState(readerOptions);
            bool isFinalBlock = false;

            // State machine across refill boundaries. `phase` drives the
            // main loop; `pendingKey` holds the last PropertyName seen so
            // that if we run out of bytes between key and value we can
            // continue on the next refill.
            //   0 = expect StartObject
            //   1 = expect PropertyName or EndObject
            //   2 = expect value for `pendingKey`
            int phase = 0;
            string pendingKey = null;

            try
            {
                while (true)
                {
                    // Refill buffer from the stream if we need more data.
                    if (!isFinalBlock)
                    {
                        // Grow on demand: if we shifted unconsumed bytes to
                        // the front and there's no room left, a single token
                        // exceeds the buffer. Rare for our input shape but
                        // cheap to handle correctly.
                        if (bytesInBuffer >= buffer.Length)
                        {
                            int newSize = buffer.Length * 2;
                            byte[] bigger = pool.Rent(newSize);
                            Buffer.BlockCopy(buffer, 0, bigger, 0, bytesInBuffer);
                            pool.Return(buffer);
                            buffer = bigger;
                        }

                        int read = FillBuffer(stream, buffer, bytesInBuffer);
                        if (read == 0)
                        {
                            isFinalBlock = true;
                        }
                        else
                        {
                            bytesInBuffer += read;
                        }
                    }

                    var reader = new Utf8JsonReader(
                        new ReadOnlySpan<byte>(buffer, 0, bytesInBuffer),
                        isFinalBlock,
                        readerState);

                    bool done = false;
                    bool malformedFinalTail = false;

                    // Rewind anchor: snapshot reader state BEFORE each Read()
                    // so that if a value token needs more bytes (value is an
                    // object that straddles the buffer boundary), we can
                    // rewind to just before the value and retry on refill.
                    JsonReaderState rewindState = reader.CurrentState;
                    long rewindConsumed = reader.BytesConsumed;
                    int rewindPhase = phase;
                    string rewindPendingKey = pendingKey;
                    bool rewindRequested = false;

                    try
                    {
                        // Consume as many tokens as the current buffer slice
                        // allows. When Read() returns false without EndObject
                        // at the top level we break out to refill.
                        while (true)
                        {
                            // Snapshot BEFORE the read advances. If we end up
                            // needing to rewind (wrapper straddles buffer),
                            // we revert to exactly this state and the outer
                            // refill re-reads the value token cleanly.
                            var preReadState = reader.CurrentState;
                            long preReadConsumed = reader.BytesConsumed;
                            int preReadPhase = phase;
                            string preReadPendingKey = pendingKey;

                            if (!reader.Read()) break;

                            if (phase == 0)
                            {
                                if (reader.TokenType != JsonTokenType.StartObject)
                                {
                                    return result;
                                }
                                phase = 1;
                            }
                            else if (phase == 1)
                            {
                                if (reader.TokenType == JsonTokenType.EndObject)
                                {
                                    done = true;
                                }
                                else if (reader.TokenType == JsonTokenType.PropertyName)
                                {
                                    pendingKey = reader.GetString();
                                    phase = 2;
                                }
                            }
                            else // phase == 2
                            {
                                string extracted = null;
                                if (reader.TokenType == JsonTokenType.String)
                                {
                                    extracted = reader.GetString();
                                }
                                else if (reader.TokenType == JsonTokenType.StartObject && flattenWrappedObjects)
                                {
                                    // Probe with a cloned reader to confirm the
                                    // whole wrapper fits in the current buffer.
                                    // If not, rewind to just before this Read()
                                    // and break to refill.
                                    var probe = reader;
                                    if (!probe.TrySkip())
                                    {
                                        rewindState = preReadState;
                                        rewindConsumed = preReadConsumed;
                                        rewindPhase = preReadPhase;
                                        rewindPendingKey = preReadPendingKey;
                                        rewindRequested = true;
                                        break;
                                    }
                                    extracted = ReadFlattenedWrappedObject(ref reader);
                                }
                                else if (reader.TokenType == JsonTokenType.StartObject ||
                                         reader.TokenType == JsonTokenType.StartArray)
                                {
                                    if (!reader.TrySkip())
                                    {
                                        rewindState = preReadState;
                                        rewindConsumed = preReadConsumed;
                                        rewindPhase = preReadPhase;
                                        rewindPendingKey = preReadPendingKey;
                                        rewindRequested = true;
                                        break;
                                    }
                                }
                                // Null / Number / Bool → extracted stays null → skipped.

                                if (extracted != null && pendingKey != null)
                                {
                                    result[pendingKey] = extracted;
                                }
                                pendingKey = null;
                                phase = 1;
                            }

                            if (done) break;
                        }
                    }
                    catch (System.Text.Json.JsonException) when (isFinalBlock)
                    {
                        malformedFinalTail = true;
                    }

                    if (done || malformedFinalTail) break;

                    // Persist reader state for the next slice. Prefer the
                    // rewind snapshot when a wrapper probe forced us to
                    // back up; otherwise use the reader's live state.
                    JsonReaderState nextState;
                    long consumed;
                    if (rewindRequested)
                    {
                        nextState = rewindState;
                        consumed = rewindConsumed;
                        phase = rewindPhase;
                        pendingKey = rewindPendingKey;
                    }
                    else
                    {
                        nextState = reader.CurrentState;
                        consumed = reader.BytesConsumed;
                    }
                    readerState = nextState;

                    if (consumed < bytesInBuffer)
                    {
                        // Unconsumed tail — shift to front for the refill.
                        int remaining = bytesInBuffer - (int)consumed;
                        Buffer.BlockCopy(buffer, (int)consumed, buffer, 0, remaining);
                        bytesInBuffer = remaining;
                    }
                    else
                    {
                        bytesInBuffer = 0;
                    }

                    if (isFinalBlock)
                    {
                        // Nothing more to read AND the reader didn't finish —
                        // malformed tail. Return what we have (tolerant).
                        break;
                    }
                }
            }
            finally
            {
                pool.Return(buffer);
            }

            return result;
        }

        /// <summary>
        /// When we're positioned on a <c>StartObject</c> inside the value slot
        /// of a dictionary entry, consume the whole object and return its
        /// "value" / "text" / "str" string payload, or null if none present.
        /// Caller's reader position is left on the matching <c>EndObject</c>.
        /// </summary>
        private static string ReadFlattenedWrappedObject(ref Utf8JsonReader reader)
        {
            // Reader already positioned on StartObject. The caller has
            // ALREADY proven the whole wrapper object is within the current
            // buffer via a probe TrySkip, so every Read()/TrySkip() below is
            // guaranteed to succeed — but TrySkip (not Skip) is still used
            // because Skip asserts isFinalBlock=true, which our outer reader
            // generally has NOT set.
            string picked = null;
            bool havePick = false;
            int depth = 1;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    depth--;
                    if (depth == 0) break;
                    continue;
                }
                if (reader.TokenType == JsonTokenType.StartObject ||
                    reader.TokenType == JsonTokenType.StartArray)
                {
                    // Nested subtree. Skip it; we only inspect top-level props.
                    reader.TrySkip();
                    continue;
                }
                if (reader.TokenType == JsonTokenType.PropertyName && depth == 1)
                {
                    // Match any of the wrapper-key aliases against the raw UTF-8
                    // span — avoids allocating a string per property name.
                    bool isValue = reader.ValueTextEquals("value");
                    bool isText = !isValue && reader.ValueTextEquals("text");
                    bool isStr = !isValue && !isText && reader.ValueTextEquals("str");

                    if (!reader.Read()) break;

                    if ((isValue || isText || isStr) && reader.TokenType == JsonTokenType.String)
                    {
                        // Prefer "value" over later hits.
                        if (isValue)
                        {
                            picked = reader.GetString();
                            havePick = true;
                        }
                        else if (!havePick)
                        {
                            picked = reader.GetString();
                            havePick = true;
                        }
                    }
                    else if (reader.TokenType == JsonTokenType.StartObject ||
                             reader.TokenType == JsonTokenType.StartArray)
                    {
                        reader.TrySkip();
                    }
                }
            }

            return picked;
        }

        private static int FillBuffer(Stream stream, byte[] buffer, int offset)
        {
            int toRead = buffer.Length - offset;
            if (toRead <= 0) return 0;
            return stream.Read(buffer, offset, toRead);
        }

        /// <summary>
        /// Stream a flat-dictionary JSON file from disk. Wraps
        /// <see cref="ReadFlatStringDictionaryFromJson"/> with FileStream
        /// lifecycle and actionable path-included error messages.
        /// </summary>
        private static Dictionary<string, string> ReadFlatStringDictionaryFromFile(
            string path, bool flattenWrappedObjects)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Path is null or empty.", nameof(path));
            }

            FileStream stream = null;
            try
            {
                stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 4096, useAsync: false);

                if (stream.Length == 0)
                {
                    return new Dictionary<string, string>(StringComparer.Ordinal);
                }

                return ReadFlatStringDictionaryFromJson(
                    stream, flattenWrappedObjects, entryCapacityHint: 0);
            }
            catch (System.Text.Json.JsonException jex)
            {
                throw new InvalidDataException(
                    $"Malformed JSON at '{path}' (byte offset {jex.BytePositionInLine?.ToString() ?? "?"}, line {jex.LineNumber?.ToString() ?? "?"}): {jex.Message}",
                    jex);
            }
            catch (IOException ioex)
            {
                throw new IOException($"I/O error while reading '{path}': {ioex.Message}", ioex);
            }
            finally
            {
                stream?.Dispose();
            }
        }

        /// <summary>
        /// Stream a dictionary out to disk as a top-level JSON object of
        /// <c>"key": "value"</c> pairs. Replaces
        /// <c>JsonConvert.SerializeObject(dict) + File.WriteAllText</c> to
        /// avoid building a multi-megabyte intermediate string on the heap.
        /// </summary>
        private static void WriteFlatStringDictionaryToJsonFile(
            string path,
            Dictionary<string, string> dict)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Path is null or empty.", nameof(path));
            }
            if (dict == null)
            {
                throw new ArgumentNullException(nameof(dict));
            }

            FileStream stream = null;
            Utf8JsonWriter writer = null;
            try
            {
                stream = new FileStream(
                    path, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 64 * 1024, useAsync: false);

                writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                {
                    Indented = true,
                });

                writer.WriteStartObject();
                foreach (var kv in dict)
                {
                    writer.WriteString(kv.Key, kv.Value ?? string.Empty);
                }
                writer.WriteEndObject();
                writer.Flush();
            }
            catch (IOException ioex)
            {
                throw new IOException($"I/O error while writing '{path}': {ioex.Message}", ioex);
            }
            finally
            {
                writer?.Dispose();
                stream?.Dispose();
            }
        }

        /// <summary>
        /// Creates the voice content dictionary with optional progress reporting.
        /// Uses streaming JSON deserialization when loading from cache.
        /// Transparently handles encrypted (.gisub) and plaintext (.json) files.
        /// Progress ranges: 0-50% for dictionary loading, caller is responsible for 50-100%.
        /// </summary>
        public static Dictionary<string, string> CreateVoiceContentDictionary(
            string inputFilePath, string outputFilePath, string userName,
            IProgress<(int percent, string message)> progress,
            FileProtectionHelper protectionHelper = null, string outputLanguage = null)
        {
            // v2.0.0+ fast-path: if the output file is an encrypted .gisub it
            // came from DictionarySync (R2), which since the same date ships
            // PRE-MERGED `{english_text: target_text}` dictionaries produced
            // by tools/merge-textmap.cjs. The desktop just loads that dict
            // straight into the matcher corpus — no ID-joining, no cache
            // rebuild. Everything the legacy path below does (stripping
            // {color}/<unbreak>/etc) is already done at publish time;
            // {NICKNAME} is the only runtime substitution left.
            if (protectionHelper != null)
            {
                try
                {
                    var (resolvedOutput, _) = protectionHelper.ResolveFile(outputFilePath);
                    if (resolvedOutput != null &&
                        resolvedOutput.EndsWith(".gisub", StringComparison.OrdinalIgnoreCase))
                    {
                        var preMerged = LoadPreMergedDictionary(
                            outputFilePath, resolvedOutput, protectionHelper, userName, progress);
                        if (preMerged != null && preMerged.Count > 0)
                        {
                            progress?.Report((50, $"Pre-merged dictionary loaded: {preMerged.Count:N0} entries"));
                            return preMerged;
                        }
                        // Fall through to legacy merge if the file happened to
                        // be a legacy ID-keyed pack (older publish, or custom
                        // encrypted file a user placed manually).
                        Logger.Log.Warn("Pre-merged load returned empty — falling back to ID-join path.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log.Warn($"Pre-merged dictionary load failed, falling back to legacy merge: {ex.Message}");
                }
            }

            var jsonFilePath = BuildCachePath(inputFilePath, outputFilePath);

            // If a cached file for the current version doesn't exist, proactively clean
            // up any older-version cache files to reclaim disk space. Only runs on the
            // rebuild path so there's no cost when the cache is already warm.
            bool cacheExists = protectionHelper != null
                ? protectionHelper.FileExists(jsonFilePath)
                : File.Exists(jsonFilePath);
            if (!cacheExists)
            {
                CleanupLegacyCache(inputFilePath, outputFilePath);
            }

            if (cacheExists)
            {
                try
                {
                    progress?.Report((5, "Loading cached dictionary..."));

                    // If we have a protection helper, use it for transparent encrypted/plain reading
                    if (protectionHelper != null)
                    {
                        var dict = protectionHelper.LoadDictionary(jsonFilePath, progress, 5, 50);
                        if (dict != null)
                        {
                            progress?.Report((50, $"Dictionary loaded: {dict.Count:N0} entries"));
                            return dict;
                        }
                    }
                    else
                    {
                        // Fallback: plain JSON streaming via System.Text.Json (no encryption).
                        // Previously used Newtonsoft's JsonTextReader, which is fine; the STJ
                        // path is noticeably faster and allocates less per-token.
                        Dictionary<string, string> dict;
                        using (var stream = File.OpenRead(jsonFilePath))
                        {
                            long fileSize = stream.Length;
                            dict = ReadFlatStringDictionaryFromJson(
                                stream,
                                flattenWrappedObjects: false,
                                entryCapacityHint: 0);
                            // Progress reporter is best-effort on the legacy cache
                            // format (we don't emit intra-parse events here —
                            // parsing a cache is sub-second). Fire a single
                            // mid-parse 25% tick for UX continuity.
                            if (dict.Count > 0)
                            {
                                progress?.Report((25, $"Loading dictionary... {dict.Count:N0} entries"));
                            }
                        }
                        progress?.Report((50, $"Dictionary loaded: {dict.Count:N0} entries"));
                        return dict;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log.Error(ex);
                }
            }

            // Build from raw TextMap files.
            //
            // Previously: loaded each TextMap with JsonConvert.DeserializeObject
            // <Dictionary<string, JToken>>(File.ReadAllText(...)). That's a
            // full ~50 MB string in memory, then a full JToken DOM (~250 MB of
            // JProperty/JValue heap allocations) before we even start the join.
            // Now: stream each TextMap through Utf8JsonReader directly into a
            // Dictionary<string,string>. Peak RAM during parse drops to roughly
            // the final dict size (each language file's entries, strings only).
            progress?.Report((5, "Loading input language..."));
            Dictionary<string, string> chsData;
            using (var inStream = File.OpenRead(inputFilePath))
            {
                chsData = ReadFlatStringDictionaryFromJson(
                    inStream, flattenWrappedObjects: true, entryCapacityHint: 0);
            }

            progress?.Report((20, "Loading output language..."));

            // Output file may be encrypted (custom language TextMap).
            Dictionary<string, string> enData;
            if (protectionHelper != null)
            {
                var (resolvedOutput, _) = protectionHelper.ResolveFile(outputFilePath);
                if (resolvedOutput != null)
                {
                    using (var outStream = protectionHelper.OpenForReading(outputFilePath))
                    {
                        enData = ReadFlatStringDictionaryFromJson(
                            outStream, flattenWrappedObjects: true, entryCapacityHint: 0);
                    }
                }
                else
                {
                    using (var outStream = File.OpenRead(outputFilePath))
                    {
                        enData = ReadFlatStringDictionaryFromJson(
                            outStream, flattenWrappedObjects: true, entryCapacityHint: 0);
                    }
                }
            }
            else
            {
                using (var outStream = File.OpenRead(outputFilePath))
                {
                    enData = ReadFlatStringDictionaryFromJson(
                        outStream, flattenWrappedObjects: true, entryCapacityHint: 0);
                }
            }

            progress?.Report((35, "Building dictionary..."));
            var voiceContentDict2 = new Dictionary<string, string>();
            int total = chsData.Count;
            int processed = 0;

            foreach (var chsItem in chsData)
            {
                if (enData.TryGetValue(chsItem.Key, out var enVoiceContent))
                {
                    string temp = chsItem.Value;
                    temp = BracedPlaceholderRegex().Replace(temp, "");
                    temp = ColorTagRegex().Replace(temp, "$1");
                    enVoiceContent = ProcessGender(enVoiceContent);
                    enVoiceContent = ColorTagRegex().Replace(enVoiceContent, "$1");
                    enVoiceContent = enVoiceContent.Replace("{NICKNAME}", userName).Replace("#", "");
                    enVoiceContent = BracedPlaceholderRegex().Replace(enVoiceContent, "");
                    temp = UnbreakTagRegex().Replace(temp, "").Replace("#", "").Replace("\\n", "");
                    enVoiceContent = UnbreakTagRegex().Replace(enVoiceContent, "");
                    // Pre-process: replace literal \n with actual newlines at load time
                    // so we don't pay this cost every 200ms in UpdateText
                    enVoiceContent = enVoiceContent.Replace("\\n", "\n");
                    voiceContentDict2[temp] = enVoiceContent;
                }
                processed++;
                if (processed % 10000 == 0)
                {
                    int pct = (int)(35 + (processed * 15.0 / total));
                    progress?.Report((pct, $"Processing entries... {processed:N0}/{total:N0}"));
                }
            }

            progress?.Report((48, "Saving cache..."));

            // Save cache: encrypted if custom language, plain if public
            if (protectionHelper != null && !string.IsNullOrEmpty(outputLanguage))
            {
                protectionHelper.SaveDictionary(voiceContentDict2, jsonFilePath, outputLanguage);
            }
            else
            {
                WriteFlatStringDictionaryToJsonFile(jsonFilePath, voiceContentDict2);
            }

            progress?.Report((50, $"Dictionary built: {voiceContentDict2.Count:N0} entries"));
            return voiceContentDict2;
        }

        /// <summary>
        /// Build a merged output json where each key maps to two-language content joined by '\n'.
        /// Returns the merged json path (cached on disk). Handles encrypted files transparently.
        /// </summary>
        public static string BuildMultiOutputJson(string inputFilePath, string outputFilePath1, string outputFilePath2,
            FileProtectionHelper protectionHelper = null, string outputLanguage = null)
        {
            var dir = Path.GetDirectoryName(inputFilePath);
            var name1 = Path.GetFileNameWithoutExtension(outputFilePath1);
            var name2 = Path.GetFileNameWithoutExtension(outputFilePath2);
            // Version the merged-pair cache the same way single-pair caches are versioned.
            // Legacy unversioned files are cleaned up on the rebuild path below.
            var mergedName = $"{name1}_{name2}.{CacheVersion}.json";
            var mergedPath = Path.Combine(dir, mergedName);

            // Best-effort cleanup of pre-versioned merged cache files if no current one exists.
            if (!File.Exists(mergedPath))
            {
                try
                {
                    var legacyMerged = Path.Combine(dir, $"{name1}_{name2}.json");
                    if (File.Exists(legacyMerged))
                    {
                        File.Delete(legacyMerged);
                        Logger.Log.Info($"Removed legacy merged cache: {Path.GetFileName(legacyMerged)}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log.Warn($"Legacy merged cache cleanup failed: {ex.Message}");
                }
            }

            // Check if cached (either .json or .gisub)
            if (protectionHelper != null && protectionHelper.FileExists(mergedPath))
                return mergedPath;
            if (protectionHelper == null && File.Exists(mergedPath))
                return mergedPath;

            // Load both language files (may be encrypted). Streams each file
            // through Utf8JsonReader instead of round-tripping a ~50 MB string
            // through File.ReadAllText + JsonConvert — same peak-RAM story as
            // the rebuild path.
            Dictionary<string, string> lang1, lang2;
            if (protectionHelper != null)
            {
                lang1 = protectionHelper.LoadDictionary(outputFilePath1) ??
                    ReadFlatStringDictionaryFromFile(outputFilePath1, flattenWrappedObjects: true);
                lang2 = protectionHelper.LoadDictionary(outputFilePath2) ??
                    ReadFlatStringDictionaryFromFile(outputFilePath2, flattenWrappedObjects: true);
            }
            else
            {
                lang1 = ReadFlatStringDictionaryFromFile(outputFilePath1, flattenWrappedObjects: true);
                lang2 = ReadFlatStringDictionaryFromFile(outputFilePath2, flattenWrappedObjects: true);
            }

            var merged = new Dictionary<string, string>();
            foreach (var kv in lang1)
            {
                if (lang2.TryGetValue(kv.Key, out var v2))
                    merged[kv.Key] = kv.Value + "\n" + v2;
                else
                    merged[kv.Key] = kv.Value;
            }
            foreach (var kv in lang2)
            {
                if (!merged.ContainsKey(kv.Key))
                    merged[kv.Key] = kv.Value;
            }

            // Save merged: encrypted if any custom language involved
            if (protectionHelper != null && !string.IsNullOrEmpty(outputLanguage) &&
                LanguageClassification.IsCustomLanguage(outputLanguage))
            {
                protectionHelper.SaveDictionary(merged, mergedPath, outputLanguage);
            }
            else
            {
                WriteFlatStringDictionaryToJsonFile(mergedPath, merged);
            }
            return mergedPath;
        }

        public static string FindMatchWithHeader(string ocrText, Dictionary<string, string> voiceContentDict, out string key)
        {
            key = "";

            if (string.IsNullOrEmpty(ocrText))
                return "";

            string[] lines = ocrText.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 1)
            {
                return FindClosestMatch(lines[0], voiceContentDict, out key);
            }

            // Find the longest line (body text starts from here)
            int maxLength = 0;
            int maxIndex = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length > maxLength)
                {
                    maxLength = lines[i].Length;
                    maxIndex = i;
                }
            }

            // Headers are lines above the longest line (1-2 lines)
            List<string> headers = new List<string>();
            // If the longest line is title case and English, and there's content after it, use the next line
            if (IsTitleCase(lines[maxIndex]) && IsEnglish(lines[maxIndex]) && maxIndex < lines.Length - 1)
            {
                maxIndex = maxIndex + 1;
            }

            for (int i = 0; i < maxIndex; i++)
            {
                headers.Add(lines[i]);
            }

            // Body text is the longest line and all lines after it
            string bodyText = string.Join(" ", lines.Skip(maxIndex));

            // Exact match for headers (no fuzzy matching)
            string headerMatch = "";
            foreach (string header in headers)
            {
                if (voiceContentDict.ContainsKey(header))
                {
                    if (!string.IsNullOrEmpty(headerMatch))
                        headerMatch += " ";
                    headerMatch += voiceContentDict[header];
                }
            }

            string bodyMatch = FindClosestMatch(bodyText, voiceContentDict, out string bodyKey);
            if (string.IsNullOrEmpty(bodyMatch))
            {
                return headerMatch;
            }

            key = bodyKey;
            if (!string.IsNullOrEmpty(headerMatch))
            {
                return headerMatch + " " + bodyMatch;
            }
            else
            {
                return bodyMatch;
            }
        }

        public static string FindClosestMatch(string input, Dictionary<string, string> voiceContentDict, out string Key)
        {
            // 1. Fast path: if there is an exact match, return it directly
            if (voiceContentDict.TryGetValue(input, out var exactMatch))
            {
                Key = input;
                return exactMatch;
            }

            int inputLen = input.Length;

            // Global best-result container (thread-safe updates)
            // Use an Object for the small amount of locking during final aggregation
            object globalLock = new object();
            string globalBestKey = null;
            int globalBestDistance = int.MaxValue;

            // 2. Parallel processing: use thread-local variables to avoid lock contention
            // This logic follows a Map-Reduce style:
            // localInit: each thread initializes its own best result
            // body: performs computation and updates the thread-local best result (lock-free)
            // localFinally: when a thread finishes, it merges its best result into the global result (locked, but rarely hit)
            Parallel.ForEach(
                voiceContentDict,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, // 用满所有核心
                () => new { BestKey = (string)null, BestDist = int.MaxValue }, // 线程局部变量初始化
                (kvp, loopState, localState) =>
                {
                    string key = kvp.Key;
                    int keyLen = key.Length;

                    int currentDistance;

                    // --- Contains logic (kept to preserve business behavior) ---
                    bool isContains = false;
                    if (inputLen > 10)
                    {
                        // Use Ordinal comparison for best performance
                        if (key.IndexOf(input, StringComparison.Ordinal) >= 0 ||
                           (keyLen > 10 && input.IndexOf(key, StringComparison.Ordinal) >= 0))
                        {
                            isContains = true;
                        }
                    }

                    // --- Original filtering logic ---
                    if (inputLen <= 5 && keyLen >= inputLen * 3)
                        return localState;
                    // --- Pruning optimization ---
                    // If the length difference is already greater than the current best distance in this thread, skip calculation
                    // Note: this uses localState.BestDist, and pruning becomes more effective as the loop progresses
                    if (Math.Abs(keyLen - inputLen) >= localState.BestDist)
                        return localState;
                    if (isContains)
                    {
                        currentDistance = 0;
                    }
                    else
                    {
                        // --- Levenshtein calculation (zero allocation) ---
                        // Only calculate distance when not in the "contains" case
                        // To keep compatible with the original logic: if input > 5 and key is longer, take only the first inputLen chars of key
                        // Use Span to avoid allocations from Substring
                        ReadOnlySpan<char> targetSpan = key.AsSpan();
                        if (inputLen > 5 && keyLen > inputLen)
                        {
                            targetSpan = targetSpan.Slice(0, inputLen);
                        }

                        // Pass localState.BestDist as a threshold; stop early if the distance exceeds this value during calculation
                        currentDistance = CalculateLevenshteinDistance(input.AsSpan(), targetSpan, localState.BestDist);
                    }

                    // Update the best result in this thread
                    if (currentDistance < localState.BestDist)
                    {
                        return new { BestKey = key, BestDist = currentDistance };
                    }

                    return localState;
                },
                (finalLocalState) =>
                {
                    // 3. Final merge: only this step needs a lock, and it runs at most once per thread (e.g. 8 or 16 times), so cost is minimal
                    if (finalLocalState.BestKey != null)
                    {
                        lock (globalLock)
                        {
                            if (finalLocalState.BestDist < globalBestDistance)
                            {
                                globalBestDistance = finalLocalState.BestDist;
                                globalBestKey = finalLocalState.BestKey;
                            }
                        }
                    }
                }
            );

            // Logger.Log.Debug($"closestKey {globalBestKey} length {inputLen} closestDistance {globalBestDistance}");

            // Original threshold decision logic
            if (globalBestDistance < inputLen / 1.5)
            {
                Key = globalBestKey;
                return voiceContentDict[globalBestKey];
            }
            else
            {
                Key = "";
                return "";
            }
        }

        // Highly optimized Levenshtein algorithm
        // Features: uses Span, uses stackalloc, supports threshold-based early exit
        private static int CalculateLevenshteinDistance(ReadOnlySpan<char> source, ReadOnlySpan<char> target, int threshold)
        {
            int sourceLen = source.Length;
            int targetLen = target.Length;

            // If the length difference already exceeds the threshold, return immediately
            if (Math.Abs(sourceLen - targetLen) >= threshold) return threshold + 1;
            if (sourceLen == 0) return targetLen;
            if (targetLen == 0) return sourceLen;

            // Ensure source is the shorter string to reduce stack memory usage
            if (sourceLen > targetLen)
            {
                var temp = source; source = target; target = temp;
                var tempLen = sourceLen; sourceLen = targetLen; targetLen = tempLen;
            }

            // Allocate using stackalloc: extremely fast, does not trigger GC
            // Voice commands are usually short, so stackalloc is safe. If you are worried about overflow, you can add a length check and fall back to ArrayPool.
            Span<int> prev = stackalloc int[sourceLen + 1];
            Span<int> curr = stackalloc int[sourceLen + 1];

            for (int i = 0; i <= sourceLen; i++) prev[i] = i;

            for (int j = 1; j <= targetLen; j++)
            {
                curr[0] = j;
                int minDistanceInRow = j;

                for (int i = 1; i <= sourceLen; i++)
                {
                    int cost = (source[i - 1] == target[j - 1]) ? 0 : 1;

                    // Core state transition equation
                    int d1 = curr[i - 1] + 1;
                    int d2 = prev[i] + 1;
                    int d3 = prev[i - 1] + cost;

                    int dist = d1 < d2 ? d1 : d2;
                    dist = dist < d3 ? dist : d3;

                    curr[i] = dist;

                    if (dist < minDistanceInRow) minDistanceInRow = dist;
                }

                // Row-level pruning: if the minimum value in this row already exceeds the threshold,
                // then it is impossible for later matches to have a distance below the threshold; exit early
                if (minDistanceInRow >= threshold) return threshold + 1;

                // Swap buffers to avoid reinitializing arrays
                var tempRow = prev;
                prev = curr;
                curr = tempRow;
            }

            return prev[sourceLen];
        }

        static string ProcessGender(string input)
        {
            // Cached Regex; the original new Regex(pattern) per-call instance was
            // the per-entry allocation the scout report flagged. Match semantics
            // are identical — same pattern, same group indexing.
            var matches = GenderMarkerRegex().Matches(input);
            if (matches.Count >= 1)
            {
                foreach (Match match in matches)
                {
                    if (match.Groups[1].Value.StartsWith("F#"))
                    {
                        string replacement = match.Groups[1].Value.Substring(2);
                        input = input.Replace(match.Value, replacement);
                    }
                    else
                    {
                        input = input.Replace(match.Value, "");
                    }
                }
            }

            return input;
        }

        public static string CalculateMd5Hash(string content, string SALE = "TIMWANG")
        {
            string combinedStr = content + SALE;
            byte[] inputBytes = Encoding.UTF8.GetBytes(combinedStr);
            using (MD5 md5Hash = MD5.Create())
            {
                byte[] hashBytes = md5Hash.ComputeHash(inputBytes);

                StringBuilder sb = new StringBuilder();
                foreach (byte b in hashBytes)
                {
                    sb.Append(b.ToString("X2").ToLower());
                }
                return sb.ToString();
            }
        }

        private static bool IsTitleCase(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            string[] words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                return false;

            foreach (string word in words)
            {
                if (word.Length > 0 && char.IsLetter(word[0]))
                {
                    if (!char.IsUpper(word[0]))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool IsEnglish(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            foreach (char c in text)
            {
                if (char.IsLetter(c))
                {
                    // Check if character is in English alphabet range (A-Z, a-z)
                    if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // ---------------------------------------------------------------
        // Production entry point for streaming a flat JSON dict
        //
        // Used by both the voice-content pipeline in this file AND by
        // DialogueContextBase.LoadCore so the ~60 MB TextMapEN parse no
        // longer round-trips a multi-megabyte string + JToken DOM through
        // the heap. Matches Test_StreamFlatJsonDictionary's semantics
        // exactly — Test_* alias is retained for call-site legibility in
        // GI-Test where "Test_" prefixes signal "exposed only for tests".
        // ---------------------------------------------------------------

        /// <summary>
        /// Stream a top-level <c>{"key":"value", …}</c> JSON object from
        /// <paramref name="utf8Json"/> into a plain
        /// <see cref="Dictionary{String,String}"/>. Does not close the
        /// stream. Matches the Newtonsoft JToken-flatten path's tolerance
        /// (malformed entries silently skipped).
        /// </summary>
        /// <param name="utf8Json">UTF-8 JSON input. Caller owns disposal.</param>
        /// <param name="flattenWrappedObjects">
        /// When true, object-valued entries are unwrapped to their
        /// <c>value</c> / <c>text</c> / <c>str</c> child string if present.
        /// When false, only plain string values are kept.
        /// </param>
        /// <param name="entryCapacityHint">
        /// Optional initial dict capacity. 0 = default. For TextMap-sized
        /// inputs a hint in the 100k-500k range measurably cuts rehash
        /// overhead during the parse.
        /// </param>
        internal static Dictionary<string, string> StreamFlatJsonDictionary(
            Stream utf8Json,
            bool flattenWrappedObjects,
            int entryCapacityHint = 0) =>
            ReadFlatStringDictionaryFromJson(utf8Json, flattenWrappedObjects, entryCapacityHint);

        /// <summary>
        /// Test-only: stream a top-level string/object JSON dictionary from
        /// the given stream. Thin alias over <see cref="StreamFlatJsonDictionary"/>
        /// retained so GI-Test call sites still read as test-only.
        /// </summary>
        internal static Dictionary<string, string> Test_StreamFlatJsonDictionary(
            Stream utf8Json,
            bool flattenWrappedObjects) =>
            ReadFlatStringDictionaryFromJson(utf8Json, flattenWrappedObjects, 0);

        /// <summary>
        /// Test-only: Newtonsoft JObject.Load equivalent, kept so benchmarks
        /// can diff old-vs-new parse behaviour against identical input.
        /// </summary>
        internal static Dictionary<string, string> Test_LoadFlatJsonDictionary_Newtonsoft(
            Stream utf8Json,
            bool flattenWrappedObjects)
        {
            using (var sr = new StreamReader(utf8Json, Encoding.UTF8, true, 4096, leaveOpen: true))
            using (var jr = new Newtonsoft.Json.JsonTextReader(sr))
            {
                var tokenMap = Newtonsoft.Json.Linq.JObject.Load(jr);
                var raw = new Dictionary<string, string>(tokenMap.Count, StringComparer.Ordinal);
                foreach (var prop in tokenMap.Properties())
                {
                    string flat = FlattenDictValueLegacy(prop.Value, flattenWrappedObjects);
                    if (flat != null)
                        raw[prop.Name] = flat;
                }
                return raw;
            }
        }

        private static string FlattenDictValueLegacy(JToken tok, bool flattenWrappedObjects)
        {
            if (tok == null) return null;
            switch (tok.Type)
            {
                case JTokenType.String:
                    return (string)tok;
                case JTokenType.Null:
                    return null;
                case JTokenType.Object:
                {
                    if (!flattenWrappedObjects) return null;
                    var o = (JObject)tok;
                    var pick = o["value"] ?? o["text"] ?? o["str"];
                    return pick?.Type == JTokenType.String ? (string)pick : null;
                }
                default:
                    return null;
            }
        }
    }
}
