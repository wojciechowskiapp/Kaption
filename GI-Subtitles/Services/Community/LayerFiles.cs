using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Community
{
    internal enum LayerFileState
    {
        Missing,
        Loaded,
        /// <summary>Not UTF-8 JSON: moved aside as <c>.bad</c>, treated as empty.</summary>
        Corrupt,
        /// <summary>
        /// Could not be read right now (locked, no access), or damaged and not movable aside yet: nothing is
        /// overwritten until it can be.
        /// </summary>
        Unreadable,
    }

    /// <summary>
    /// The two per-game layer files under <c>%APPDATA%\Kaption\&lt;Game&gt;\</c>. Reads tolerate damage entry by entry;
    /// writes go through <see cref="AtomicFile"/>.
    /// </summary>
    internal static class LayerFiles
    {
        public const int Schema = 1;

        private static readonly JsonWriterOptions WriterOptions = new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private static readonly JsonDocumentOptions ReaderOptions = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };

        // ── overrides.<lang>.json ─────────────────────────────────────────

        public static Dictionary<string, UserFix> ReadUserFixes(string path, out LayerFileState state)
        {
            var fixes = new Dictionary<string, UserFix>(StringComparer.Ordinal);
            if (!TryParse(path, out var doc, out state)) return fixes;
            using (doc)
            {
                foreach (var item in Items(doc.RootElement, "overrides"))
                {
                    string en = Str(item, "en"), text = Str(item, "text");
                    if (string.IsNullOrWhiteSpace(en) || string.IsNullOrEmpty(text)) continue;
                    var fix = new UserFix(en, text, Str(item, "was"), Str(item, "speaker"), Str(item, "note"),
                        CommunityEntry.ParseTime(Str(item, "savedAt")) ?? DateTimeOffset.MinValue);
                    fixes[fix.En] = fix;
                }
            }
            return fixes;
        }

        public static void WriteUserFixes(string path, string game, string lang, IEnumerable<UserFix> fixes)
        {
            AtomicFile.WriteAllBytes(path, Build(w =>
            {
                Header(w, game, lang);
                w.WriteStartArray("overrides");
                foreach (var f in fixes.OrderBy(f => f.En, StringComparer.Ordinal))
                {
                    w.WriteStartObject();
                    w.WriteString("en", f.En);
                    w.WriteString("text", f.Text);
                    if (f.Was.Length > 0) w.WriteString("was", f.Was);
                    if (f.Speaker.Length > 0) w.WriteString("speaker", f.Speaker);
                    if (f.Note.Length > 0) w.WriteString("note", f.Note);
                    w.WriteString("savedAt", f.SavedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }));
        }

        // ── community.<lang>.json ─────────────────────────────────────────

        public static Dictionary<string, CommunityEntry> ReadCommunity(string path, out string since, out LayerFileState state)
        {
            var entries = new Dictionary<string, CommunityEntry>(StringComparer.Ordinal);
            since = null;
            if (!TryParse(path, out var doc, out state)) return entries;
            using (doc)
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object) since = NullIfEmpty(Str(doc.RootElement, "since"));
                foreach (var item in Items(doc.RootElement, "entries"))
                {
                    var e = new CommunityEntry(Str(item, "en"), Str(item, "target"), Str(item, "acceptedAt"));
                    if (e.En.Length == 0 || e.Target.Length == 0) continue;
                    entries[e.En] = e;
                }
            }
            return entries;
        }

        public static void WriteCommunity(string path, string game, string lang, string since, IEnumerable<CommunityEntry> entries)
        {
            AtomicFile.WriteAllBytes(path, Build(w =>
            {
                Header(w, game, lang);
                if (!string.IsNullOrEmpty(since)) w.WriteString("since", since);
                w.WriteStartArray("entries");
                foreach (var e in entries.OrderBy(e => e.En, StringComparer.Ordinal))
                {
                    w.WriteStartObject();
                    w.WriteString("en", e.En);
                    w.WriteString("target", e.Target);
                    if (e.AcceptedAt.Length > 0) w.WriteString("acceptedAt", e.AcceptedAt);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }));
        }

        // ── shared ───────────────────────────────────────────────────────

        /// <summary>Opens and parses a layer file; false with the reason when there is nothing usable.</summary>
        internal static bool TryParse(string path, out JsonDocument doc, out LayerFileState state)
        {
            doc = null;
            if (!File.Exists(path) && !AtomicFile.RecoverInterruptedSave(path))
            {
                state = LayerFileState.Missing;
                return false;
            }
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.Log.Warn($"Could not read {Path.GetFileName(path)}: {ex.Message}");
                state = LayerFileState.Unreadable;
                return false;
            }
            // A file saved by hand in an editor may start with a UTF-8 BOM, which the parser rejects.
            var json = new ReadOnlyMemory<byte>(bytes);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) json = json.Slice(3);
            // The parser accepts text saved in an ANSI code page; its strings would then fail on every read.
            if (!Utf8.IsValid(json.Span)) return Damaged(path, "not UTF-8 text", out state);
            try
            {
                doc = JsonDocument.Parse(json, ReaderOptions);
                state = LayerFileState.Loaded;
                return true;
            }
            catch (JsonException ex)
            {
                return Damaged(path, ex.Message, out state);
            }
        }

        // Moved aside, the next load starts clean; a file that cannot be moved yet is not overwritten either.
        private static bool Damaged(string path, string why, out LayerFileState state)
        {
            if (AtomicFile.SetAside(path))
            {
                Logger.Log.Warn($"{Path.GetFileName(path)} is damaged ({why}) and was set aside as .bad.");
                state = LayerFileState.Corrupt;
            }
            else
            {
                state = LayerFileState.Unreadable;
            }
            return false;
        }

        /// <summary>The array under <paramref name="name"/>, or the root itself when the file is a bare array.</summary>
        internal static IEnumerable<JsonElement> Items(JsonElement root, string name)
        {
            JsonElement array = root.ValueKind == JsonValueKind.Object ? Property(root, name) : root;
            if (array.ValueKind != JsonValueKind.Array) yield break;
            foreach (var item in array.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object) yield return item;
        }

        /// <summary>A string value; "" when it is missing, not a string, or broken (half a surrogate pair).</summary>
        internal static string Str(JsonElement obj, string name)
        {
            var v = Property(obj, name);
            if (v.ValueKind != JsonValueKind.String) return "";
            try { return v.GetString() ?? ""; }
            catch (InvalidOperationException) { return ""; }
        }

        internal static int Int(JsonElement obj, string name)
        {
            var v = Property(obj, name);
            return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
        }

        internal static bool? Bool(JsonElement obj, string name)
        {
            var v = Property(obj, name);
            return v.ValueKind == JsonValueKind.True ? true : v.ValueKind == JsonValueKind.False ? false : (bool?)null;
        }

        // The lookup decodes the names it passes, so a broken escape in any of them would throw; it reads as missing.
        private static JsonElement Property(JsonElement obj, string name)
        {
            if (obj.ValueKind != JsonValueKind.Object) return default;
            try { return obj.TryGetProperty(name, out var v) ? v : default; }
            catch (InvalidOperationException) { return default; }
        }

        private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        private static void Header(Utf8JsonWriter w, string game, string lang)
        {
            w.WriteNumber("schema", Schema);
            if (!string.IsNullOrEmpty(game)) w.WriteString("game", game);
            if (!string.IsNullOrEmpty(lang)) w.WriteString("lang", lang);
        }

        /// <summary>One JSON object as UTF-8 bytes (no BOM), ready for <see cref="AtomicFile.WriteAllBytes"/>.</summary>
        internal static byte[] Build(Action<Utf8JsonWriter> body)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, WriterOptions))
            {
                w.WriteStartObject();
                body(w);
                w.WriteEndObject();
            }
            return stream.ToArray();
        }
    }
}
