using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GI_Subtitles.Services.Characters;

namespace GI_Subtitles.Services.Today
{
    /// <summary>What the card can say about the player's own characters.</summary>
    public enum FarmPlayerState
    {
        /// <summary>No UID saved: the card invites one.</summary>
        NoUid,
        /// <summary>Rows to show.</summary>
        Rows,
        /// <summary>The showcase was read, but none of its characters takes today's books.</summary>
        NothingToday,
        /// <summary>The showcase is empty or hidden in the game.</summary>
        ShowcaseEmpty,
        /// <summary>A UID is saved but its showcase could not be read (and nothing is cached).</summary>
        Unavailable,
        /// <summary>The API says no player has the saved UID: the player is pointed to the field.</summary>
        UnknownUid,
        /// <summary>The showcase was read, but this PC has no Genshin character data yet to plan with.</summary>
        CatalogMissing,
    }

    /// <summary>One open book series today: the Philosophies tile with its region.</summary>
    public sealed class FarmBook
    {
        public string Domain { get; init; } = "";
        public string Series { get; init; } = "";
        public string Region { get; init; } = "";
        /// <summary>English item names by tier (Teachings, Guide, Philosophies), the game's own.</summary>
        public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();
        public string IconUrl { get; init; }
        /// <summary>Local file once downloaded; the view shows a tier glyph without it.</summary>
        public string IconPath { get; set; }
        /// <summary>How many of the player's characters take these books today.</summary>
        public int Mine { get; init; }
    }

    public sealed class FarmStep
    {
        public string Kind { get; init; } = "";
        public string Short { get; init; } = "";
        public int Level { get; init; }
        public int Target { get; init; }
        public int Bonus { get; init; }
        /// <summary>0 Teachings, 1 Guide, 2 Philosophies.</summary>
        public int Tier { get; init; }
        public int Count { get; init; }
        public bool Crown { get; init; }
        /// <summary>The character's most important talent to level (first in its leveling order).</summary>
        public bool IsTop { get; init; }
        public string Item { get; init; } = "";
        public string Series { get; init; } = "";
        public string IconUrl { get; init; }
        public string IconPath { get; set; }
    }

    public sealed class FarmRow
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Element { get; init; } = "";
        public int Level { get; init; }
        public string IconUrl { get; init; }
        public string IconPath { get; set; }
        public IReadOnlyList<FarmStep> Steps { get; init; } = Array.Empty<FarmStep>();
        public string Initial => Name.Length > 0 ? Name.Substring(0, 1).ToUpperInvariant() : "?";
    }

    /// <summary>Everything the card shows, built once per showing. Pure data: the view only lays it out.</summary>
    public sealed class FarmCardModel
    {
        public const int MaxRows = 5;
        public const int MaxStepsPerRow = 2;

        public DayOfWeek Weekday { get; init; }
        public bool IsSunday => Weekday == DayOfWeek.Sunday;
        public DateTimeOffset Reset { get; init; }
        public DateTimeOffset Now { get; init; }
        public TimeSpan ResetIn => Reset > Now ? Reset - Now : TimeSpan.Zero;
        public IReadOnlyList<FarmBook> Books { get; init; } = Array.Empty<FarmBook>();
        public FarmPlayerState Player { get; init; }
        public IReadOnlyList<FarmRow> Rows { get; init; } = Array.Empty<FarmRow>();
        /// <summary>Characters past <see cref="MaxRows"/> that could also level today.</summary>
        public int MoreRows { get; init; }
        public string FullListUrl { get; init; } = "";

        /// <summary>Roughly the characters a player reads, for ReadingTime.</summary>
        public int ReadingChars =>
            40 + Books.Sum(b => b.Series.Length + b.Region.Length) + Rows.Sum(r => r.Name.Length + r.Steps.Count * 14) +
            (Player == FarmPlayerState.Rows ? 0 : 60);

        public IEnumerable<string> IconUrls() =>
            Books.Select(b => b.IconUrl).Concat(Rows.Select(r => r.IconUrl)).Concat(Rows.SelectMany(r => r.Steps).Select(s => s.IconUrl))
                 .Where(u => !string.IsNullOrEmpty(u)).Distinct();
    }

    /// <summary>Builds the card from the schedule, the day, the cached showcase and the character data. Pure.</summary>
    public static class FarmCardBuilder
    {
        public const string PolishPath = "/pl/dzisiaj/";
        public const string EnglishPath = "/today/";

        public static FarmCardModel Build(TodaySchedule schedule, ServerDay day, DateTimeOffset now, bool hasUid, ShowcaseRoster roster,
                                          CharacterCatalog catalog, string uiLanguage, bool unknownUid = false)
        {
            catalog ??= CharacterCatalog.Empty;
            var catalogOut = CatalogVersionsOut(catalog, now);
            var live = schedule.Live(catalogOut);
            var open = schedule.OpenOn(day.Weekday, catalogOut);
            string ItemIcon(int id) => catalog.IconUrl(IconKind.Material, "UI_ItemIcon_" + id.ToString(CultureInfo.InvariantCulture));

            FarmPlayerState state;
            IReadOnlyList<TalentRow> plan = Array.Empty<TalentRow>();
            if (!hasUid) state = FarmPlayerState.NoUid;
            else if (unknownUid) state = FarmPlayerState.UnknownUid;
            else if (roster == null) state = FarmPlayerState.Unavailable;
            else if (roster.Characters.Count == 0) state = FarmPlayerState.ShowcaseEmpty;
            else if (catalog.IsEmpty) state = FarmPlayerState.CatalogMissing;
            else
            {
                var entries = TalentFarmPlanner.Catalog(catalog.Characters, live, c => IsReleased(c, now));
                plan = TalentFarmPlanner.Plan(roster.Kept(), entries, new HashSet<string>(open.Select(d => d.Name), StringComparer.Ordinal));
                state = plan.Count > 0 ? FarmPlayerState.Rows : FarmPlayerState.NothingToday;
            }

            var domainByName = live.ToDictionary(d => d.Name, StringComparer.Ordinal);
            var mineByDomain = plan.SelectMany(r => r.Steps.Select(s => (r.Id, s.Domain))).Distinct()
                .GroupBy(x => x.Domain).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            // Sunday opens everything: the tiles would be every series, so the card shows only the books the rows take.
            var shownDomains = day.Weekday == DayOfWeek.Sunday
                ? open.Where(d => mineByDomain.ContainsKey(d.Name)).ToList()
                : open.ToList();
            var books = shownDomains.Select(d => new FarmBook
            {
                Domain = d.Name,
                Series = Series(d),
                Region = schedule.RegionName(d.City),
                Items = d.Items.Select(i => i.Name).ToList(),
                IconUrl = ItemIcon(d.Items[2].Id),
                Mine = mineByDomain.TryGetValue(d.Name, out int n) ? n : 0,
            }).OrderByDescending(b => b.Mine > 0).ToList();

            var roster2 = roster?.Characters.ToDictionary(c => c.Id, StringComparer.Ordinal) ?? new Dictionary<string, ShowcaseCharacter>();
            var rows = plan.Take(FarmCardModel.MaxRows).Select(r =>
            {
                roster2.TryGetValue(r.Id, out var sc);
                var record = catalog.Find(r.Id);
                string icon = (record != null ? catalog.IconUrl(IconKind.Character, record.Icon) : null) ?? sc?.Icon;
                return new FarmRow
                {
                    Id = r.Id,
                    Name = record?.Name ?? sc?.Name ?? r.Name,
                    Element = record?.Element ?? sc?.Element ?? "",
                    Level = r.Level,
                    IconUrl = icon,
                    Steps = r.Steps.Take(FarmCardModel.MaxStepsPerRow).Select((s, i) =>
                    {
                        var d = domainByName.TryGetValue(s.Domain, out var dd) ? dd : null;
                        return new FarmStep
                        {
                            Kind = s.Kind, Short = ShortOf(s), Level = s.Level, Target = s.Target, Bonus = s.Bonus,
                            Tier = s.Tier, Count = s.Count, Crown = s.Crown, IsTop = s.Priority == 1,
                            Item = d?.Items[s.Tier].Name ?? "", Series = d != null ? Series(d) : "",
                            IconUrl = d != null ? ItemIcon(d.Items[s.Tier].Id) : null,
                        };
                    }).ToList(),
                };
            }).ToList();

            return new FarmCardModel
            {
                Weekday = day.Weekday,
                Reset = day.Reset,
                Now = now,
                Books = books,
                Player = state,
                Rows = rows,
                MoreRows = Math.Max(0, plan.Count - rows.Count),
                FullListUrl = CharacterPageLink.Site + (CharacterPageLink.IsPolish(uiLanguage) ? PolishPath : EnglishPath),
            };
        }

        /// <summary>A series as players name it: "Teachings of Freedom" → "Freedom".</summary>
        public static string Series(TalentDomain d)
        {
            string first = d.Items.Count > 0 ? d.Items[0].Name : "";
            foreach (string prefix in new[] { "Teachings of the ", "Teachings of " })
                if (first.StartsWith(prefix, StringComparison.Ordinal)) return first.Substring(prefix.Length);
            return first;
        }

        // The showcase gives NA / E / Q; a missing one falls back to the kind's own letter.
        private static string ShortOf(TalentStep s) =>
            !string.IsNullOrEmpty(s.Short) ? s.Short : s.Kind switch { "basic" => "NA", "skill" => "E", "ultimate" => "Q", _ => s.Kind };

        /// <summary>A character with no release day is out (the data lists none for the launch roster).</summary>
        public static bool IsReleased(CharacterRecord c, DateTimeOffset now) =>
            string.IsNullOrEmpty(c.ReleaseDate) ||
            !DateTime.TryParseExact(c.ReleaseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ||
            d <= now.UtcDateTime;

        /// <summary>Versions with a released character, as the site counts them (versionsOut in data/today.ts).</summary>
        public static IReadOnlySet<string> CatalogVersionsOut(CharacterCatalog catalog, DateTimeOffset now)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in catalog.Characters)
                if (!string.IsNullOrEmpty(c.ReleaseVersion) && !string.IsNullOrEmpty(c.ReleaseDate) && IsReleased(c, now)) set.Add(c.ReleaseVersion);
            return set;
        }
    }
}
