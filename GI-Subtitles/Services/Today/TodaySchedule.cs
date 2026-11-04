using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GI_Subtitles.Services.Characters;

namespace GI_Subtitles.Services.Today
{
    /// <summary>
    /// Genshin's domain schedule from kaption.one/data/today/genshin.json (the site's data/today/genshin.json plus
    /// <c>out</c>, the versions out at build). Parsing is tolerant: a bad domain is skipped, a bad file is null.
    /// </summary>
    public sealed class TodaySchedule
    {
        public const int Schema = 1;
        /// <summary>The published file is about 35 KB; anything far larger is not ours.</summary>
        public const int MaxBytes = 2 * 1024 * 1024;

        public IReadOnlyList<(int City, string Name)> Regions { get; init; } = Array.Empty<(int, string)>();
        public IReadOnlyList<TalentDomain> TalentDomains { get; init; } = Array.Empty<TalentDomain>();
        public IReadOnlySet<string> VersionsOut { get; init; } = new HashSet<string>();

        public string RegionName(int city) => Regions.FirstOrDefault(r => r.City == city).Name ?? "";

        /// <summary>Talent domains that exist now: no version gate, or their version is out (site build or catalog).</summary>
        public IReadOnlyList<TalentDomain> Live(IReadOnlySet<string> extraVersionsOut = null) =>
            TalentDomains.Where(d => d.Since == null || VersionsOut.Contains(d.Since) || extraVersionsOut?.Contains(d.Since) == true).ToList();

        /// <summary>The live talent domains open on a weekday (all of them on Sunday), in the schedule's region order.</summary>
        public IReadOnlyList<TalentDomain> OpenOn(DayOfWeek day, IReadOnlySet<string> extraVersionsOut = null)
        {
            var order = Regions.Select((r, i) => (r.City, i)).ToDictionary(x => x.City, x => x.i);
            return Live(extraVersionsOut).Where(d => d.Days.Contains((int)day))
                .OrderBy(d => order.TryGetValue(d.City, out int i) ? i : int.MaxValue).ToList();
        }

        /// <summary>Null when the text is not a schedule this build can read.</summary>
        public static TodaySchedule Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBytes) return null;
            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || Int(root, "schema") != Schema || Str(root, "game") != "genshin") return null;
                var regions = Items(root, "regions").Select(r => (City: Int(r, "city") ?? 0, Name: Str(r, "name") ?? ""))
                    .Where(r => r.City > 0 && r.Name.Length > 0).ToList();
                var domains = new List<TalentDomain>();
                foreach (var d in Items(root, "domains"))
                {
                    if (Str(d, "kind") != "talent") continue;
                    string name = Str(d, "name");
                    int? city = Int(d, "city");
                    var days = Items(d, "days").Select(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out int v) ? v : -1)
                        .Where(v => v is >= 0 and <= 6).Distinct().ToList();
                    // Three books, Teachings (2) to Philosophies (4), put in that order whatever the file's order:
                    // the planner and the card index them by tier.
                    var items = Items(d, "items").Select(i => new DomainItem(Int(i, "id") ?? 0, Str(i, "name") ?? "", Int(i, "rarity") ?? 0))
                        .Where(i => i.Id > 0 && i.Name.Length > 0).OrderBy(i => i.Rarity).ToList();
                    if (string.IsNullOrEmpty(name) || city is null or <= 0 || days.Count == 0 || !IsBookSet(items)) continue;
                    domains.Add(new TalentDomain(name, city.Value, days, items, Str(d, "since")));
                }
                if (domains.Count == 0) return null;
                var outVersions = new HashSet<string>(Items(root, "out").Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()).Where(s => !string.IsNullOrEmpty(s)), StringComparer.Ordinal);
                return new TodaySchedule { Regions = regions, TalentDomains = domains, VersionsOut = outVersions };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Sorted by rarity already: exactly one book of each tier.
        private static bool IsBookSet(IReadOnlyList<DomainItem> items) =>
            items.Count == 3 && items[0].Rarity == 2 && items[1].Rarity == 3 && items[2].Rarity == 4;

        private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int? Int(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;
    }
}
