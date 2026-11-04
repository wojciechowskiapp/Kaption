using System;
using System.Collections.Generic;
using System.Linq;

namespace GI_Subtitles.Services.Characters
{
    /// <summary>A talent domain of the day's schedule (today.json): its name, region, open weekdays and its books.</summary>
    public sealed record TalentDomain(string Name, int City, IReadOnlyList<int> Days, IReadOnlyList<DomainItem> Items, string Since = null);

    /// <summary>One book of a domain: the game's item id, its English name and rarity (2 Teachings, 3 Guide, 4 Philosophies).</summary>
    public sealed record DomainItem(int Id, string Name, int Rarity);

    /// <summary>A material a character's talents take, by English name and stored icon (CharacterCatalog Materials.Skills).</summary>
    public sealed record MaterialRef(string Name, string Image);

    /// <summary>A released character the planner knows: leveled talents in priority order and its book domains.</summary>
    public sealed record TalentEntry(string Id, string Name, string Icon, IReadOnlyList<string> Order, IReadOnlyList<string> Books);

    /// <summary>A talent as the showcase gives it: <c>Level</c> is the base level, <c>Bonus</c> the constellation extra.</summary>
    public sealed record KeptTalent(string Kind, string Short, int Level, int Bonus);

    public sealed record KeptCharacter(string Id, int Level, IReadOnlyList<KeptTalent> Talents);

    /// <summary>One level worth taking today: base level now and after, the books (tier 0-2, count), Crown for 10.</summary>
    public sealed record TalentStep(string Kind, string Short, int Level, int Target, int Bonus, int Tier, int Count, bool Crown,
                                    int? Priority, string Domain);

    public sealed record TalentRow(string Id, string Name, string Icon, int Level, IReadOnlyList<TalentStep> Steps, int? Best);

    /// <summary>
    /// "Talents worth levelling today" (Genshin), the C# twin of landing/src/lib/today-talents.ts. Pure. Both run
    /// tools/fixtures/talent-farm-cases.json (TalentFarmPlannerTests), so change the rule there first.
    /// </summary>
    public static class TalentFarmPlanner
    {
        public const int TalentMax = 10;

        /// <summary>The leveled talents in the game's kit order.</summary>
        public static readonly IReadOnlyList<string> TalentKinds = new[] { "basic", "skill", "ultimate" };

        /// <summary>What raising a talent to a level takes: book tier, how many, and a Crown at 10.</summary>
        public static readonly IReadOnlyDictionary<int, (int Tier, int Count, bool Crown)> Steps = new Dictionary<int, (int, int, bool)>
        {
            [2] = (0, 3, false), [3] = (1, 2, false), [4] = (1, 4, false), [5] = (1, 6, false), [6] = (1, 9, false),
            [7] = (2, 4, false), [8] = (2, 6, false), [9] = (2, 12, false), [10] = (2, 16, true),
        };

        // The first weekday a domain opens, Sunday aside: the order a rotating kit takes its books in.
        private static int Rotation(TalentDomain d)
        {
            var days = d.Days.Where(x => x != 0).ToList();
            return days.Count == 0 ? 7 : days.Min();
        }

        /// <summary>
        /// The domains whose books a character's talents take, in rotation order. Books from two regions (Traveler
        /// (Geo)) have no single rotation: none, rather than a guess.
        /// </summary>
        public static IReadOnlyList<TalentDomain> BookDomains(IReadOnlyList<MaterialRef> materials, IReadOnlyList<TalentDomain> domains)
        {
            materials ??= Array.Empty<MaterialRef>();
            var names = new HashSet<string>(materials.Select(m => m.Name ?? ""), StringComparer.Ordinal);
            var images = materials.Select(m => m.Image ?? "").ToList();
            var found = domains.Where(d => d.Items.Any(i => names.Contains(i.Name) ||
                                                            images.Any(src => src.Contains($"UI_ItemIcon_{i.Id}.", StringComparison.Ordinal))))
                               .ToList();
            if (found.Select(d => d.City).Distinct().Count() > 1) return Array.Empty<TalentDomain>();
            // OrderBy is stable, like the TS sort: equal rotations keep the schedule's order.
            return found.OrderBy(Rotation).ToList();
        }

        /// <summary>A character's priority order as talent kinds, from leveling positions into its skills.</summary>
        public static IReadOnlyList<string> PriorityKinds(IReadOnlyList<string> skillKinds, IReadOnlyList<int> positions)
        {
            var output = new List<string>();
            if (positions == null || skillKinds == null) return output;
            foreach (int p in positions)
            {
                if (p < 0 || p >= skillKinds.Count) continue;
                string kind = skillKinds[p];
                if (kind != null && TalentKinds.Contains(kind) && !output.Contains(kind)) output.Add(kind);
            }
            return output;
        }

        /// <summary>The domain whose books raise a talent to <paramref name="target"/>: the one, or the rotation's turn.</summary>
        public static string BookFor(IReadOnlyList<string> books, int target)
        {
            if (books == null || books.Count == 0) return null;
            if (books.Count == 1) return books[0];
            int n = books.Count;
            return books[(((target - 2) % n) + n) % n];
        }

        /// <summary>
        /// The rows for one player and one day: every known character with a talent under 10 whose next level's books
        /// drop in an <paramref name="open"/> domain. Steps run in priority order (kit order without one); rows rank by
        /// their most important step, then the higher character level, then the showcase's own order.
        /// </summary>
        public static IReadOnlyList<TalentRow> Plan(IReadOnlyList<KeptCharacter> characters, IReadOnlyList<TalentEntry> catalog,
                                                    IReadOnlySet<string> open)
        {
            var byId = new Dictionary<string, TalentEntry>(StringComparer.Ordinal);
            foreach (var e in catalog ?? Array.Empty<TalentEntry>()) byId.TryAdd(e.Id, e);
            var rows = new List<(TalentRow Row, int At)>();
            for (int at = 0; at < (characters?.Count ?? 0); at++)
            {
                var c = characters[at];
                if (c == null || !byId.TryGetValue(c.Id ?? "", out var entry)) continue;
                var steps = new List<TalentStep>();
                foreach (var t in c.Talents ?? Array.Empty<KeptTalent>())
                {
                    if (!TalentKinds.Contains(t.Kind) || t.Level < 1 || t.Level >= TalentMax) continue;
                    int target = t.Level + 1;
                    string domain = BookFor(entry.Books, target);
                    if (domain == null || !Steps.TryGetValue(target, out var cost) || !open.Contains(domain)) continue;
                    int i = IndexOf(entry.Order, t.Kind);
                    steps.Add(new TalentStep(t.Kind, t.Short, t.Level, target, Math.Max(0, t.Bonus), cost.Tier, cost.Count, cost.Crown,
                                             i >= 0 ? i + 1 : null, domain));
                }
                if (steps.Count == 0) continue;
                var ordered = steps.OrderBy(s => s.Priority ?? int.MaxValue).ThenBy(s => IndexOf(TalentKinds, s.Kind)).ToList();
                rows.Add((new TalentRow(entry.Id, entry.Name, entry.Icon, c.Level, ordered, ordered[0].Priority), at));
            }
            return rows.OrderBy(r => r.Row.Best ?? int.MaxValue).ThenByDescending(r => r.Row.Level).ThenBy(r => r.At)
                       .Select(r => r.Row).ToList();
        }

        /// <summary>
        /// The planner's catalog from the character data: released characters with their book domains and order
        /// (official leveling, else the guide's priority). <paramref name="isReleased"/> decides what counts as out.
        /// </summary>
        public static IReadOnlyList<TalentEntry> Catalog(IEnumerable<CharacterRecord> characters, IReadOnlyList<TalentDomain> domains,
                                                         Func<CharacterRecord, bool> isReleased, Func<CharacterRecord, string> icon = null)
        {
            var output = new List<TalentEntry>();
            foreach (var c in characters ?? Array.Empty<CharacterRecord>())
            {
                if (c == null || !isReleased(c)) continue;
                var mats = (c.Materials?.Skills ?? Array.Empty<MaterialCost>()).Select(m => new MaterialRef(m.Name, m.Icon + ".")).ToList();
                var books = BookDomains(mats, domains).Select(d => d.Name).ToList();
                if (books.Count == 0) continue;
                IReadOnlyList<int> positions = c.Leveling.Count > 0 ? c.Leveling : c.Ai?.Priority?.Skills ?? Array.Empty<int>();
                var order = PriorityKinds(c.Skills.Select(s => s.Kind).ToList(), positions);
                output.Add(new TalentEntry(c.Id, c.Name, icon?.Invoke(c), order, books));
            }
            return output;
        }

        private static int IndexOf(IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], value, StringComparison.Ordinal)) return i;
            return -1;
        }
    }
}
