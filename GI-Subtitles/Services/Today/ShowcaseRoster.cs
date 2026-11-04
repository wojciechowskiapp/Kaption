using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using GI_Subtitles.Services.Characters;

namespace GI_Subtitles.Services.Today
{
    /// <summary>One showcase character as the card keeps it: what the planner and the row need, nothing else.</summary>
    public sealed record ShowcaseCharacter(string Id, string Name, string Element, int Level, string Icon, IReadOnlyList<KeptTalent> Talents);

    /// <summary>
    /// The player's showcase for the farm card: read from GET /api/cards/genshin/&lt;uid&gt; (roster schema 2,
    /// backend/docs/cards-api.md) and kept slim in %APPDATA%\Kaption\Genshin\showcase.json. Tolerant: a bad character
    /// is skipped, a bad document is null.
    /// </summary>
    public sealed class ShowcaseRoster
    {
        public const int CacheSchema = 1;
        public const int MaxBytes = 8 * 1024 * 1024;

        public string Uid { get; init; } = "";
        /// <summary>The server day (ServerDay.Key) the roster was read on.</summary>
        public string Day { get; init; } = "";
        public DateTimeOffset ReadAt { get; init; }
        public IReadOnlyList<ShowcaseCharacter> Characters { get; init; } = Array.Empty<ShowcaseCharacter>();

        public IReadOnlyList<KeptCharacter> Kept() =>
            Characters.Select(c => new KeptCharacter(c.Id, c.Level, c.Talents)).ToList();

        /// <summary>The API's roster JSON; null when it is not a Genshin roster.</summary>
        public static ShowcaseRoster FromApi(string json, string uid, string day, DateTimeOffset readAt)
        {
            var root = ParseRoot(json);
            if (root == null) return null;
            using (root)
            {
                var r = root.RootElement;
                if (r.ValueKind != JsonValueKind.Object || Str(r, "game") is string g && g != "genshin") return null;
                if (!r.TryGetProperty("characters", out var list) || list.ValueKind != JsonValueKind.Array) return null;
                var chars = new List<ShowcaseCharacter>();
                foreach (var c in list.EnumerateArray())
                {
                    string id = Str(c, "id");
                    if (string.IsNullOrEmpty(id) || id.Length > 24) continue;
                    var talents = Items(c, "talents").Select(t => new KeptTalent(Str(t, "kind") ?? "", Str(t, "short") ?? "",
                        Int(t, "level") ?? 0, Int(t, "bonus") ?? 0)).Where(t => t.Kind.Length > 0).ToList();
                    string icon = c.TryGetProperty("image", out var img) ? Str(img, "icon") : null;
                    chars.Add(new ShowcaseCharacter(id, Loc(c, "name") ?? id, Str(c, "elementKey") ?? Loc(c, "element") ?? "",
                        Int(c, "level") ?? 0, SafeHttps(icon), talents));
                }
                return new ShowcaseRoster { Uid = uid ?? "", Day = day ?? "", ReadAt = readAt, Characters = chars };
            }
        }

        /// <summary>
        /// The cards API's "no player with this UID" (404, error unknown_uid). Any other 404, such as a route this
        /// server does not have, is an outage, not a wrong UID.
        /// </summary>
        public static bool IsUnknownUidAnswer(int status, string body)
        {
            if (status != 404 || string.IsNullOrEmpty(body) || body.Length > 64 * 1024) return false;
            var root = ParseRoot(body);
            if (root == null) return false;
            using (root) return Str(root.RootElement, "error") == "unknown_uid";
        }

        // ── the slim cache ──

        private sealed class CacheFile
        {
            [JsonPropertyName("schema")] public int Schema { get; set; }
            [JsonPropertyName("uid")] public string Uid { get; set; }
            [JsonPropertyName("day")] public string Day { get; set; }
            [JsonPropertyName("read")] public DateTimeOffset Read { get; set; }
            [JsonPropertyName("characters")] public List<CacheChar> Characters { get; set; }
        }

        private sealed class CacheChar
        {
            [JsonPropertyName("id")] public string Id { get; set; }
            [JsonPropertyName("name")] public string Name { get; set; }
            [JsonPropertyName("element")] public string Element { get; set; }
            [JsonPropertyName("level")] public int Level { get; set; }
            [JsonPropertyName("icon")] public string Icon { get; set; }
            [JsonPropertyName("talents")] public List<CacheTalent> Talents { get; set; }
        }

        private sealed class CacheTalent
        {
            [JsonPropertyName("kind")] public string Kind { get; set; }
            [JsonPropertyName("short")] public string Short { get; set; }
            [JsonPropertyName("level")] public int Level { get; set; }
            [JsonPropertyName("bonus")] public int Bonus { get; set; }
        }

        public string ToCacheJson() => JsonSerializer.Serialize(new CacheFile
        {
            Schema = CacheSchema,
            Uid = Uid,
            Day = Day,
            Read = ReadAt,
            Characters = Characters.Select(c => new CacheChar
            {
                Id = c.Id, Name = c.Name, Element = c.Element, Level = c.Level, Icon = c.Icon,
                Talents = c.Talents.Select(t => new CacheTalent { Kind = t.Kind, Short = t.Short, Level = t.Level, Bonus = t.Bonus }).ToList(),
            }).ToList(),
        });

        /// <summary>The cache file's text; null when unreadable, of another schema, or for another UID.</summary>
        public static ShowcaseRoster FromCache(string json, string uid)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBytes) return null;
            try
            {
                var f = JsonSerializer.Deserialize<CacheFile>(json);
                if (f == null || f.Schema != CacheSchema || f.Uid != uid || f.Characters == null) return null;
                return new ShowcaseRoster
                {
                    Uid = f.Uid,
                    Day = f.Day ?? "",
                    ReadAt = f.Read,
                    Characters = f.Characters.Where(c => !string.IsNullOrEmpty(c?.Id))
                        .Select(c => new ShowcaseCharacter(c.Id, c.Name ?? c.Id, c.Element ?? "", c.Level, SafeHttps(c.Icon),
                            (c.Talents ?? new List<CacheTalent>()).Where(t => !string.IsNullOrEmpty(t?.Kind))
                                .Select(t => new KeptTalent(t.Kind, t.Short ?? "", t.Level, t.Bonus)).ToList()))
                        .ToList(),
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static JsonDocument ParseRoot(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBytes) return null;
            try { return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 }); }
            catch (JsonException) { return null; }
        }

        // Only absolute https images reach the image cache.
        private static string SafeHttps(string url) =>
            url != null && url.Length <= 500 && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? url : null;

        private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string Loc(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : Str(v, "en");
        }

        private static int? Int(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;
    }
}
