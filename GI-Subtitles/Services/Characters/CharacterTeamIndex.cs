using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace GI_Subtitles.Services.Characters
{
    /// <summary>A character whose team includes another: one row of "Gra też w drużynach" on the member's page.</summary>
    public sealed class TeamHost
    {
        public CharacterRecord Host { get; init; }
        /// <summary>The host's first shown team that includes the member.</summary>
        public AiTeam Team { get; init; }
        /// <summary>That team's place among the host's shown teams (0 is the first).</summary>
        public int TeamIndex { get; init; }
    }

    /// <summary>
    /// The reverse team index: for each character, the other characters whose teams include it, one entry per host
    /// (its first shown team with the character), the newest host first. Built once per loaded catalog and kept for
    /// as long as that catalog lives, so a bundle update brings a fresh index with the fresh catalog.
    /// </summary>
    public static class CharacterTeamIndex
    {
        private static readonly ConditionalWeakTable<CharacterCatalog, Dictionary<string, IReadOnlyList<TeamHost>>> Indexes = new();

        /// <summary>Hosts whose teams include <paramref name="characterId"/>, newest first; empty when there are none.</summary>
        public static IReadOnlyList<TeamHost> HostsOf(CharacterCatalog catalog, string characterId)
        {
            if (catalog == null || string.IsNullOrEmpty(characterId)) return Array.Empty<TeamHost>();
            var index = Indexes.GetValue(catalog, Build);
            return index.TryGetValue(characterId, out var hosts) ? hosts : Array.Empty<TeamHost>();
        }

        /// <summary>
        /// The teams a character's page shows, in its order: official first, then AI, then other sources (data order
        /// within one); members missing from the roster are dropped and a team needs two left.
        /// </summary>
        public static IReadOnlyList<AiTeam> ShownTeams(CharacterAi ai, CharacterCatalog catalog)
        {
            if (ai == null || ai.Teams.Count == 0 || catalog == null) return Array.Empty<AiTeam>();
            return ai.Teams
                .Where(t => t.Members.Count(id => catalog.Find(id) != null) >= 2)
                .OrderBy(t => SourceOrder(t.Source)) // stable: data order within a source
                .ToList();
        }

        internal static int SourceOrder(string source) => source == "official" ? 0 : IsUpdaterPick(source) ? 1 : 2;

        /// <summary>The updater's own picks: "ai", "ai+sources", "ai+curated" (the site orders and labels them alike).</summary>
        public static bool IsUpdaterPick(string source) =>
            source != null && (source == "ai" || source.StartsWith("ai+", StringComparison.Ordinal));

        private static Dictionary<string, IReadOnlyList<TeamHost>> Build(CharacterCatalog catalog)
        {
            var lists = new Dictionary<string, List<TeamHost>>(StringComparer.Ordinal);
            foreach (var host in catalog.Characters)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var teams = ShownTeams(host.Ai, catalog);
                for (int i = 0; i < teams.Count; i++)
                    foreach (string id in teams[i].Members)
                    {
                        if (id == host.Id || catalog.Find(id) == null || !seen.Add(id)) continue;
                        if (!lists.TryGetValue(id, out var list)) lists[id] = list = new List<TeamHost>();
                        list.Add(new TeamHost { Host = host, Team = teams[i], TeamIndex = i });
                    }
            }
            var index = new Dictionary<string, IReadOnlyList<TeamHost>>(StringComparer.Ordinal);
            foreach (var pair in lists)
            {
                pair.Value.Sort((a, b) => NewerFirst(a.Host, b.Host));
                index[pair.Key] = pair.Value;
            }
            return index;
        }

        /// <summary>
        /// Newest release first: by release day, then version, then id (a game's ids grow with its roster). Characters
        /// without a day or version sort after those with one. The same order as the site's list.
        /// </summary>
        public static int NewerFirst(CharacterRecord a, CharacterRecord b)
        {
            string dayA = a.ReleaseDate ?? "", dayB = b.ReleaseDate ?? "";
            if (dayA != dayB)
            {
                if (dayA.Length == 0) return 1;
                if (dayB.Length == 0) return -1;
                return -string.CompareOrdinal(dayA, dayB);
            }
            int version = CompareVersions(b.ReleaseVersion, a.ReleaseVersion);
            if (version != 0) return version;
            int number = LeadingNumber(b.Id).CompareTo(LeadingNumber(a.Id));
            return number != 0 ? number : -string.CompareOrdinal(a.Id, b.Id);
        }

        private static int CompareVersions(string x, string y)
        {
            int[] a = Parts(x), b = Parts(y);
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int d = (i < a.Length ? a[i] : 0) - (i < b.Length ? b[i] : 0);
                if (d != 0) return Math.Sign(d);
            }
            return 0;
        }

        private static int[] Parts(string version) =>
            (version ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0).ToArray();

        // "10000005-502" counts as 10000005, like parseInt on the site.
        private static long LeadingNumber(string id)
        {
            int end = 0;
            while (end < (id ?? "").Length && end < 18 && char.IsDigit(id[end])) end++;
            return end > 0 && long.TryParse(id.AsSpan(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out long n) ? n : 0;
        }
    }
}
