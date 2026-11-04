using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GI_Subtitles.Services.Characters
{
    /// <summary>A skill name as it may appear in text, and the marker that follows it there.</summary>
    public sealed class SkillReference
    {
        public SkillReference(string name, string marker, int index)
        {
            Name = name ?? "";
            Marker = marker ?? "";
            Index = index;
        }

        public string Name { get; }
        public string Marker { get; }
        /// <summary>Index into the record's full skill list, so a skill's own text can skip itself.</summary>
        public int Index { get; }
    }

    /// <summary>
    /// The game's shorthand for a skill's type, written after its name wherever it appears outside its own card:
    /// "Fluttering Hasode (E)", "Six Paths of Sage Silkcraft (C1)". Skill cards keep their own badge.
    /// </summary>
    public static class SkillMarkers
    {
        // Words that already name the type in a skill's own name ("Ultimate: Lingering Snow", "Basic Attack: …").
        private static readonly string[] TypeWords =
            { "Attack", "Ultimate", "Dodge", "Dash", "Assist", "Counter", "Passive", "Ability", "Technique", "Talent" };

        /// <summary>"(E)", "(Q)", "(Atak)", "(Skill)", "(C3)", …; "" for kinds the game has no shorthand for.</summary>
        /// <param name="game">Wire id: "genshin", "starrail" or "zzz".</param>
        /// <param name="rank">1-based position among the character's constellations / eidolons / mindscapes.</param>
        /// <param name="englishName">The skill's name; a name that already states its type gets no marker.</param>
        public static string For(string game, string kind, int rank = 0, string englishName = null)
        {
            string k = (kind ?? "").ToLowerInvariant();
            switch (k)
            {
                case "constellation": return rank > 0 ? $"(C{rank})" : "";
                case "eidolon": return rank > 0 ? $"(E{rank})" : "";
                case "mindscape": return rank > 0 ? $"(M{rank})" : "";
            }
            string label = Label((game ?? "").ToLowerInvariant(), k);
            if (label.Length == 0 || NamesItsType(englishName)) return "";
            return "(" + label + ")";
        }

        private static string Label(string game, string kind)
        {
            switch (game)
            {
                case "genshin":
                    return kind == "basic" ? "Atak" : kind == "skill" ? "E" : kind == "ultimate" ? "Q" : "";
                case "starrail":
                    return kind == "basic" ? "Basic" : kind == "skill" ? "Skill" : kind == "ultimate" ? "Ult"
                        : kind == "talent" ? "Talent" : kind == "technique" ? "Technique" : "";
                case "zzz":
                    return kind == "basic" ? "Basic" : kind == "special" ? "Special" : kind == "dodge" ? "Dodge"
                        : kind == "chain" ? "Chain" : kind == "assist" ? "Assist" : "";
                default:
                    return "";
            }
        }

        private static bool NamesItsType(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            int colon = name.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0 || colon > 28) return false;
            string head = name.Substring(0, colon);
            return TypeWords.Any(w => head.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>Markers for every skill of a record, by index; ranks count within the kit shown.</summary>
        public static string[] ForRecord(string game, IReadOnlyList<CharacterSkill> skills, string variant)
        {
            var markers = new string[skills?.Count ?? 0];
            var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < markers.Length; i++)
            {
                var s = skills[i];
                bool shown = s.Variant.Length == 0 || s.Variant == (variant ?? "");
                if (!shown) { markers[i] = ""; continue; }
                ranks.TryGetValue(s.Kind ?? "", out int n);
                ranks[s.Kind ?? ""] = ++n;
                markers[i] = For(game, s.Kind, n, s.Name);
            }
            return markers;
        }

        /// <summary>The game's letter for a rank: Genshin C(onstellation), Star Rail E(idolon), ZZZ M(indscape); "" otherwise.</summary>
        public static string RankLetter(string game)
        {
            switch ((game ?? "").ToLowerInvariant())
            {
                case "genshin": return "C";
                case "starrail": return "E";
                case "zzz": return "M";
                default: return "";
            }
        }

        /// <summary>"C1", "E0", "M6"; "" for a game without ranks or a rank outside 0..6.</summary>
        public static string RankLabel(string game, int rank)
        {
            string letter = RankLetter(game);
            return letter.Length == 0 || rank < 0 || rank > CharacterCatalog.MaxRank ? "" : letter + rank;
        }

        /// <summary>"Name (E)", or the name alone when the kind has no marker.</summary>
        public static string WithMarker(string name, string marker) =>
            string.IsNullOrEmpty(marker) || string.IsNullOrEmpty(name) ? name ?? "" : name + " " + marker;

        /// <summary>
        /// Puts each skill's marker after its name in free text, once per mention: whole names only, never twice,
        /// never inside a possessive ("Hasode's"). <paramref name="self"/> skips the skill whose own text this is.
        /// </summary>
        public static string Annotate(string text, IReadOnlyList<SkillReference> references, int self = -1)
        {
            if (string.IsNullOrEmpty(text) || references == null || references.Count == 0) return text ?? "";
            var refs = references.Where(r => r.Index != self && r.Name.Length >= 4 && r.Marker.Length > 0)
                                 .OrderByDescending(r => r.Name.Length).ToList();
            if (refs.Count == 0) return text;

            StringBuilder sb = null;
            int copied = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (i > 0 && char.IsLetterOrDigit(text[i - 1])) continue;
                foreach (var r in refs)
                {
                    if (text[i] != r.Name[0] || string.CompareOrdinal(text, i, r.Name, 0, r.Name.Length) != 0) continue;
                    int end = i + r.Name.Length;
                    if (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '\'' || text[end] == '’')) continue;
                    if (!AlreadyMarked(text, end))
                    {
                        sb = sb ?? new StringBuilder(text.Length + 32);
                        sb.Append(text, copied, end - copied).Append(' ').Append(r.Marker);
                        copied = end;
                    }
                    i = end - 1; // the whole name is consumed: a shorter name inside it never matches
                    break;
                }
            }
            if (sb == null) return text;
            return sb.Append(text, copied, text.Length - copied).ToString();
        }

        private static bool AlreadyMarked(string text, int at)
        {
            while (at < text.Length && text[at] == ' ') at++;
            return at < text.Length && text[at] == '(';
        }
    }
}
