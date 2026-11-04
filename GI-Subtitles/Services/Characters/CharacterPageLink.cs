using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace GI_Subtitles.Services.Characters
{
    /// <summary>
    /// The character's own page on kaption.one: /pl/postacie/{game}/{slug}/ for a Polish interface,
    /// /characters/{game}/{slug}/ for any other (the site has Polish and English pages). The slug is the site's:
    /// a slug or page URL from the bundle when it carries one, else <see cref="AssignSlugs"/>, an exact port of
    /// tools/landing-characters/transform.cjs. The hub search (?q=name) is the last resort.
    /// </summary>
    public static class CharacterPageLink
    {
        public const string Site = "https://kaption.one";

        // The site's own check for a slug (transform.cjs), also applied to slugs that come with the bundle.
        private static readonly Regex ValidSlug = new Regex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex NotSlug = new Regex("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex PagePath = new Regex("^/(?:pl/postacie|characters)/([a-z0-9-]+)/([a-z0-9-]+)/?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Slugs per catalog: the namesake rule needs the whole roster, and a catalog never changes once loaded.
        private static readonly ConditionalWeakTable<CharacterCatalog, IReadOnlyDictionary<string, string>> SlugsByCatalog =
            new ConditionalWeakTable<CharacterCatalog, IReadOnlyDictionary<string, string>>();

        /// <summary>The site's URL segment for a game ("genshin" → "genshin-impact"); "" for a game it has no pages for.</summary>
        public static string GameSlug(string game)
        {
            switch ((game ?? "").Trim().ToLowerInvariant())
            {
                case "genshin": return "genshin-impact";
                case "starrail": case "hsr": return "honkai-star-rail";
                case "zzz": case "zenless": return "zenless-zone-zero";
                case "wuwa": return "wuthering-waves";
                default: return "";
            }
        }

        /// <summary>Polish pages for a Polish interface ("pl", "pl-PL"); English pages for every other language.</summary>
        public static bool IsPolish(string uiLanguage) =>
            uiLanguage != null && uiLanguage.StartsWith("pl", StringComparison.OrdinalIgnoreCase);

        public static string PageUrl(string gameSlug, string characterSlug, string uiLanguage) =>
            string.IsNullOrEmpty(gameSlug) || string.IsNullOrEmpty(characterSlug) ? ""
            : $"{Site}/{Section(uiLanguage)}/{gameSlug}/{characterSlug}/";

        /// <summary>The game's character list with the name searched: only when no slug can be built.</summary>
        public static string HubUrl(string gameSlug, string name, string uiLanguage) =>
            string.IsNullOrEmpty(gameSlug) ? ""
            : $"{Site}/{Section(uiLanguage)}/{gameSlug}/" + (string.IsNullOrWhiteSpace(name) ? "" : "?q=" + Uri.EscapeDataString(name));

        private static string Section(string uiLanguage) => IsPolish(uiLanguage) ? "pl/postacie" : "characters";

        /// <summary>
        /// The page for <paramref name="record"/> in the interface language: the bundle's page URL or slug, else the
        /// site's slug rule over the catalog's roster, else the hub search; "" for a game the site has no pages for.
        /// </summary>
        public static string For(CharacterRecord record, CharacterCatalog catalog, string uiLanguage)
        {
            if (record == null) return "";
            string game = GameSlug(catalog?.Game);

            // A page URL from the bundle: its slug is canonical; the language follows the interface.
            if (TryParsePageUrl(record.PageUrl, out string urlGame, out string urlSlug))
                return PageUrl(urlGame, urlSlug, uiLanguage);
            if (IsSitePage(record.PageUrl)) return record.PageUrl;

            if (game.Length == 0) return "";
            string slug = SlugFor(record, catalog);
            return slug.Length > 0 ? PageUrl(game, slug, uiLanguage) : HubUrl(game, record.Name, uiLanguage);
        }

        /// <summary>The bundle's slug when it carries a valid one, else the site's rule over the whole roster.</summary>
        public static string SlugFor(CharacterRecord record, CharacterCatalog catalog)
        {
            if (record == null) return "";
            if (IsValidSlug(record.Slug)) return record.Slug;
            if (catalog == null || catalog.Characters.Count == 0) return Fallback(record);
            var slugs = SlugsByCatalog.GetValue(catalog, c => AssignSlugs(c.Characters));
            return slugs.TryGetValue(record.Id ?? "", out string slug) ? slug : Fallback(record);
        }

        private static string Fallback(CharacterRecord record) =>
            Slugify(record.Name) is string s && s.Length > 0 ? s : Slugify(record.Id ?? "");

        public static bool IsValidSlug(string slug) => !string.IsNullOrEmpty(slug) && ValidSlug.IsMatch(slug);

        /// <summary>
        /// Port of transform.cjs <c>slugify</c>: '&amp;' reads as "and", accents and ligatures fold to ASCII, anything
        /// else becomes one hyphen, no hyphen at either end. Empty when nothing is left.
        /// </summary>
        public static string Slugify(string name)
        {
            string s = (name ?? "").Replace("&", " and ").Normalize(NormalizationForm.FormKD);
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c >= '̀' && c <= 'ͯ') continue; // combining marks the decomposition split off
                switch (c)
                {
                    case 'ł': case 'Ł': sb.Append('l'); break;
                    case 'ø': case 'Ø': sb.Append('o'); break;
                    case 'æ': case 'Æ': sb.Append("ae"); break;
                    case 'ß': sb.Append("ss"); break;
                    default: sb.Append(c); break;
                }
            }
            return NotSlug.Replace(sb.ToString().ToLowerInvariant(), "-").Trim('-');
        }

        /// <summary>
        /// Port of transform.cjs <c>assignSlugs</c>: by id (numeric when both are numbers), the lowest id keeps the
        /// plain name; later namesakes try weapon type (Star Rail: path), element, both, then the id, then -2, -3...
        /// </summary>
        public static IReadOnlyDictionary<string, string> AssignSlugs(IEnumerable<CharacterRecord> characters)
        {
            var ordered = (characters ?? Enumerable.Empty<CharacterRecord>()).Where(c => c != null).ToList();
            ordered.Sort(IdOrder); // List.Sort is not stable, but ids are unique, so equal keys never meet
            var groups = new List<KeyValuePair<string, List<CharacterRecord>>>();
            var index = new Dictionary<string, List<CharacterRecord>>(StringComparer.Ordinal);
            foreach (var c in ordered)
            {
                string slug = Slugify(c.Name);
                string baseSlug = slug.Length > 0 ? slug : "character-" + Slugify(c.Id ?? "");
                if (!index.TryGetValue(baseSlug, out var members))
                {
                    index[baseSlug] = members = new List<CharacterRecord>();
                    groups.Add(new KeyValuePair<string, List<CharacterRecord>>(baseSlug, members));
                }
                members.Add(c);
            }

            var taken = new HashSet<string>(StringComparer.Ordinal);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in groups)
            {
                string b = group.Key;
                for (int i = 0; i < group.Value.Count; i++)
                {
                    var c = group.Value[i];
                    string[] tries = i == 0 ? new[] { b } : new[]
                    {
                        $"{b}-{Slugify(c.WeaponType)}",
                        $"{b}-{Slugify(c.Element)}",
                        $"{b}-{Slugify(c.WeaponType)}-{Slugify(c.Element)}",
                        $"{b}-{Slugify(c.Id ?? "")}",
                    };
                    string slug = tries.FirstOrDefault(s => s.Length > 0 && !s.EndsWith("-", StringComparison.Ordinal) && !taken.Contains(s));
                    for (int n = 2; slug == null; n++)
                        if (!taken.Contains($"{b}-{n}")) slug = $"{b}-{n}";
                    taken.Add(slug);
                    result[c.Id ?? ""] = slug;
                }
            }
            return result;
        }

        // transform.cjs idOrder: numeric when both ids are numbers and differ, else plain code-unit order.
        private static int IdOrder(CharacterRecord a, CharacterRecord b)
        {
            bool na = double.TryParse(a.Id, NumberStyles.Float, CultureInfo.InvariantCulture, out double x);
            bool nb = double.TryParse(b.Id, NumberStyles.Float, CultureInfo.InvariantCulture, out double y);
            if (na && nb && x != y) return x < y ? -1 : 1;
            return string.CompareOrdinal(a.Id ?? "", b.Id ?? "");
        }

        /// <summary>An https URL on kaption.one (the only site the page link may open).</summary>
        public static bool IsSitePage(string url) =>
            Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
            && (u.Host == "kaption.one" || u.Host.EndsWith(".kaption.one", StringComparison.Ordinal));

        /// <summary>The game and character slugs of a kaption.one character page URL, in either language.</summary>
        public static bool TryParsePageUrl(string url, out string gameSlug, out string characterSlug)
        {
            gameSlug = characterSlug = "";
            if (!IsSitePage(url)) return false;
            var m = PagePath.Match(new Uri(url).AbsolutePath);
            if (!m.Success || !IsValidSlug(m.Groups[2].Value) || !IsValidSlug(m.Groups[1].Value)) return false;
            gameSlug = m.Groups[1].Value;
            characterSlug = m.Groups[2].Value;
            return true;
        }
    }
}
