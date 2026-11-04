using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Community
{
    public enum SuggestionProblem
    {
        None,
        Empty,
        TooLong,
        /// <summary>The markup matches but the number of line breaks does not; the detail is the English count.</summary>
        LineBreaks,
        /// <summary>English markup is missing or changed; the detail lists what the English has.</summary>
        Markup,
        /// <summary>Markup the English does not have and the server would not strip; the detail lists it.</summary>
        ExtraMarkup,
    }

    /// <summary>
    /// The server's rules for a suggested line (backend <c>lib/community-text.ts</c>, <c>cleanSuggestion</c>): the
    /// same tags, {placeholders}, Star Rail number tokens and line breaks as the English, and at most 3 × English + 40
    /// characters. Checked in the fix window, so a fix the server would refuse is caught there, not dropped later.
    /// </summary>
    public static class SuggestionRules
    {
        public const int MaxLength = 4000;
        private const string LineBreak = "\\n";

        private static readonly Regex Tag = new Regex(@"<[^<>]*>", RegexOptions.CultureInvariant);
        private static readonly Regex Placeholder = new Regex(@"\{[^{}]*\}", RegexOptions.CultureInvariant);
        private static readonly Regex NumberToken = new Regex(@"\d+\[(?:i|f\d)\]", RegexOptions.CultureInvariant);
        // C0/C1 controls but the line feed; zero-width, bidi and other invisible format characters, the BOM.
        private static readonly Regex Controls = CharClass((0x00, 0x09), (0x0B, 0x1F), (0x7F, 0x9F));
        private static readonly Regex Invisible = CharClass((0x200B, 0x200F), (0x202A, 0x202E), (0x2060, 0x2064), (0x2066, 0x2069), (0xFEFF, 0xFEFF));
        private static readonly Regex SpacesBeforeBreak = new Regex(@"[ ]+\n", RegexOptions.CultureInvariant);
        private static readonly Regex SpacesAfterBreak = new Regex(@"\n[ ]+", RegexOptions.CultureInvariant);
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.CultureInvariant);

        // Built from code points so this file holds no invisible characters.
        private static Regex CharClass(params (int From, int To)[] ranges)
        {
            var sb = new StringBuilder("[");
            foreach (var (from, to) in ranges) sb.Append((char)from).Append('-').Append((char)to);
            return new Regex(sb.Append(']').ToString(), RegexOptions.CultureInvariant);
        }

        public const int MaxEnglishLength = 2000;

        /// <summary>
        /// A language the community reviews for some game: the manifests' translation blocks, as the backend's
        /// <c>COMMUNITY_LANGS</c> (backend/src/generated/games.ts comes from the same files).
        /// </summary>
        public static bool TakesLanguage(string lang) => lang != null && GameRegistry.Current.CommunityLanguages.Contains(lang);

        /// <summary>The server reviews fixes for this game and language, so they are sent and its overlay is fetched.</summary>
        public static bool TakesSuggestions(string wireGame, string lang) => GameRegistry.Current.TakesCommunityFixes(wireGame, lang);

        /// <summary>A fix for this line goes to review: the pair is taken and the English is a line the server accepts.</summary>
        public static bool CanSuggest(string wireGame, string lang, string en) => TakesSuggestions(wireGame, lang) && EnglishAccepted(en);

        public static string NormaliseNewlines(string s)
        {
            string v = (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            // A lone surrogate (pasted from a broken source) cannot be normalised; JavaScript leaves it, so do we.
            try { return v.Normalize(NormalizationForm.FormC); }
            catch (ArgumentException) { return v; }
        }

        /// <summary>The server takes suggestions only for an English line it accepts (cleanEnText): 1-2000 characters, no controls.</summary>
        public static bool EnglishAccepted(string en)
        {
            string v = NormaliseNewlines(en);
            return v.Trim().Length > 0 && v.Length <= MaxEnglishLength && !v.Any(c => c != '\n' && c != '\t' && char.IsControl(c));
        }

        /// <summary>The text as the server stores it: invisible characters, '#' and markup the English lacks removed.</summary>
        public static string Clean(string en, string suggestion)
        {
            string english = NormaliseNewlines(en);
            string v = Invisible.Replace(Controls.Replace(NormaliseNewlines(suggestion).Replace('\t', ' '), ""), "");
            v = KeepOnly(v, Tag, english);
            v = KeepOnly(v, Placeholder, english);
            v = v.Replace("#", "");
            v = SpacesAfterBreak.Replace(SpacesBeforeBreak.Replace(v, "\n"), "\n");
            return v.Trim();
        }

        /// <summary>Two texts are the same proposal when they differ only in spacing (the server's rule).</summary>
        public static bool SameProposal(string a, string b) =>
            string.Equals(Whitespace.Replace(a ?? "", " ").Trim(), Whitespace.Replace(b ?? "", " ").Trim(), StringComparison.Ordinal);

        public static SuggestionProblem Check(string en, string suggestion, out string detail)
        {
            detail = "";
            string english = NormaliseNewlines(en);
            string v = Clean(en, suggestion);
            if (v.Length == 0) return SuggestionProblem.Empty;
            if (v.Length > MaxLength || v.Length > 3 * english.Length + 40) return SuggestionProblem.TooLong;

            var want = MarkupTokens(english);
            var got = MarkupTokens(v);
            if (want.SequenceEqual(got)) return SuggestionProblem.None;
            var wantOther = want.Where(t => t != LineBreak).ToList();
            var gotOther = got.Where(t => t != LineBreak).ToList();
            if (wantOther.SequenceEqual(gotOther))
            {
                detail = want.Count(t => t == LineBreak).ToString();
                return SuggestionProblem.LineBreaks;
            }
            if (wantOther.Count == 0)
            {
                detail = string.Join(" ", gotOther.Distinct());
                return SuggestionProblem.ExtraMarkup;
            }
            detail = string.Join(" ", wantOther);
            return SuggestionProblem.Markup;
        }

        /// <summary>Tags, {placeholders}, number tokens and one entry per line break, sorted (ordinal, as JavaScript sorts).</summary>
        public static List<string> MarkupTokens(string s)
        {
            s ??= "";
            var tokens = new List<string>();
            foreach (Match m in Tag.Matches(s)) tokens.Add(m.Value);
            foreach (Match m in Placeholder.Matches(s)) tokens.Add(m.Value);
            foreach (Match m in NumberToken.Matches(s)) tokens.Add(m.Value);
            foreach (char c in s) if (c == '\n') tokens.Add(LineBreak);
            tokens.Sort(StringComparer.Ordinal);
            return tokens;
        }

        // Removes every match the English has no unused copy of.
        private static string KeepOnly(string s, Regex re, string english)
        {
            var left = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match m in re.Matches(english)) left[m.Value] = left.TryGetValue(m.Value, out int n) ? n + 1 : 1;
            return re.Replace(s, m =>
            {
                if (!left.TryGetValue(m.Value, out int n) || n == 0) return "";
                left[m.Value] = n - 1;
                return m.Value;
            });
        }
    }
}
