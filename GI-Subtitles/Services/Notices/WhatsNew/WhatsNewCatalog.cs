using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Notices.WhatsNew
{
    public enum WhatsNewKind
    {
        /// <summary>Something that is simply there now; may carry one action.</summary>
        Info,
        /// <summary>An opt-in: "Turn on", "Not now", "Don't ask again".</summary>
        Ask,
    }

    /// <summary>One entry's words in one language. <see cref="Done"/> replaces the row once the player turned it on.</summary>
    public sealed record WhatsNewText(string Title, string Body, string Done, string Action);

    /// <summary>
    /// One line of "New in Kaption": a feature in a release (Resources/WhatsNew/entries.json). A release adds entries,
    /// not code, unless it brings a new switch (<see cref="WhatsNewFeatures"/>) or condition (<see cref="WhatsNewRequirements"/>).
    /// </summary>
    public sealed record WhatsNewEntry(
        string Id,
        Version Since,
        int Order,
        WhatsNewKind Kind,
        string Feature,
        string Requires,
        IReadOnlyList<string> Games,
        string Icon,
        string Action,
        WhatsNewText English,
        WhatsNewText Polish)
    {
        public WhatsNewText Text(bool polish) => polish ? Polish : English;
    }

    /// <summary>The entries this build ships, newest release first, then by <see cref="WhatsNewEntry.Order"/>.</summary>
    public sealed class WhatsNewCatalog
    {
        public const int Schema = 1;
        public const string ResourceName = "Kaption.WhatsNew.entries.json";
        /// <summary>The shipped file is a few kilobytes; anything far larger is not ours.</summary>
        public const int MaxBytes = 512 * 1024;

        public static readonly WhatsNewCatalog Empty = new(Array.Empty<WhatsNewEntry>());

        private static readonly Lazy<WhatsNewCatalog> Shipped = new(LoadEmbedded);
        private static readonly Regex Placeholder = new(@"\{([A-Za-z]+)\}", RegexOptions.Compiled);

        public WhatsNewCatalog(IEnumerable<WhatsNewEntry> entries)
        {
            Entries = (entries ?? Array.Empty<WhatsNewEntry>())
                .OrderByDescending(e => e.Since).ThenBy(e => e.Order).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        }

        public IReadOnlyList<WhatsNewEntry> Entries { get; }

        /// <summary>The newest release any entry belongs to; null for an empty catalog.</summary>
        public Version Level => Entries.Count > 0 ? Entries[0].Since : null;

        /// <summary>The catalog embedded in this build; <see cref="Empty"/> when it cannot be read (logged once).</summary>
        public static WhatsNewCatalog Default => Shipped.Value;

        private static WhatsNewCatalog LoadEmbedded()
        {
            try
            {
                using Stream stream = typeof(WhatsNewCatalog).Assembly.GetManifestResourceStream(ResourceName);
                if (stream == null)
                {
                    Logger.Log.Warn("New in Kaption: the entries are not in this build.");
                    return Empty;
                }
                using var reader = new StreamReader(stream);
                var catalog = Parse(reader.ReadToEnd(), out IReadOnlyList<string> skipped);
                foreach (string why in skipped) Logger.Log.Warn($"New in Kaption: entry skipped: {why}");
                return catalog;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"New in Kaption: the entries could not be read: {ex.Message}");
                return Empty;
            }
        }

        /// <summary>
        /// Tolerant: an entry this build cannot show (no id, no release, a language missing) is skipped and named in
        /// <paramref name="skipped"/>; a file that is not ours gives <see cref="Empty"/>.
        /// </summary>
        public static WhatsNewCatalog Parse(string json, out IReadOnlyList<string> skipped)
        {
            var problems = new List<string>();
            skipped = problems;
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBytes) return Empty;
            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema)
                    || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != Schema
                    || !root.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array)
                {
                    problems.Add("the file is not a schema " + Schema + " entry list");
                    return Empty;
                }
                var entries = new List<WhatsNewEntry>();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonElement e in list.EnumerateArray())
                {
                    string id = Str(e, "id");
                    if (string.IsNullOrWhiteSpace(id)) { problems.Add("an entry without an id"); continue; }
                    if (!ids.Add(id)) { problems.Add($"{id}: the id is used twice"); continue; }
                    if (!Version.TryParse(Str(e, "since"), out Version since)) { problems.Add($"{id}: no release in \"since\""); continue; }
                    WhatsNewKind kind;
                    switch (Str(e, "kind"))
                    {
                        case "ask": kind = WhatsNewKind.Ask; break;
                        case "info": kind = WhatsNewKind.Info; break;
                        default: problems.Add($"{id}: kind must be \"ask\" or \"info\""); continue;
                    }
                    string feature = Str(e, "feature");
                    if (kind == WhatsNewKind.Ask && string.IsNullOrWhiteSpace(feature)) { problems.Add($"{id}: a question needs a feature to turn on"); continue; }
                    string action = Str(e, "action");
                    WhatsNewText en = Text(e, "en"), pl = Text(e, "pl");
                    if (en == null || pl == null) { problems.Add($"{id}: needs a title and a body in English and in Polish"); continue; }
                    if (action != null && (en.Action == null || pl.Action == null)) { problems.Add($"{id}: its action has no label in both languages"); continue; }
                    var games = e.TryGetProperty("games", out var g) && g.ValueKind == JsonValueKind.Array
                        ? g.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())
                            .Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
                        : new List<string>();
                    int order = e.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out int n) ? n : int.MaxValue;
                    entries.Add(new WhatsNewEntry(id, since, order, kind, NullIfBlank(feature), NullIfBlank(Str(e, "requires")), games,
                        NullIfBlank(Str(e, "icon")), NullIfBlank(action), en, pl));
                }
                return new WhatsNewCatalog(entries);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                problems.Add("the file is not readable: " + ex.Message);
                return Empty;
            }
        }

        /// <summary>The <c>{name}</c> placeholders a text uses.</summary>
        public static IReadOnlyCollection<string> PlaceholdersOf(string text) =>
            text == null ? Array.Empty<string>() : Placeholder.Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

        /// <summary>
        /// Puts the values in; null when the text names a placeholder there is no value for (the row is then left out
        /// rather than shown with a hole in it).
        /// </summary>
        public static string Fill(string text, IReadOnlyDictionary<string, string> values)
        {
            if (text == null) return null;
            bool missing = false;
            string filled = Placeholder.Replace(text, m =>
            {
                if (values != null && values.TryGetValue(m.Groups[1].Value, out string v) && !string.IsNullOrWhiteSpace(v)) return v;
                missing = true;
                return m.Value;
            });
            return missing ? null : filled;
        }

        private static WhatsNewText Text(JsonElement entry, string lang)
        {
            if (!entry.TryGetProperty(lang, out var t) || t.ValueKind != JsonValueKind.Object) return null;
            string title = NullIfBlank(Str(t, "title")), body = NullIfBlank(Str(t, "body"));
            return title == null || body == null ? null : new WhatsNewText(title, body, NullIfBlank(Str(t, "done")), NullIfBlank(Str(t, "action")));
        }

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
