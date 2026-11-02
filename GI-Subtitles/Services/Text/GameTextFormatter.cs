using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GI_Subtitles.Services.Text
{
    /// <summary>
    /// Fills HSR-style number tokens ("1[i]%", "2[f1]") left in pack strings after merge-textmap
    /// strips the '#'. Values come either from data params or from the numbers on screen.
    /// </summary>
    public static class GameTextFormatter
    {
        private const string Unknown = "?";

        private static readonly Regex TokenRegex =
            new Regex(@"(\d+)\[(i|f(\d))\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Token first, so the digit inside "1[i]" is never read as a literal number.
        private static readonly Regex SlotRegex =
            new Regex(@"(?<tok>(?<idx>\d+)\[(?:i|f\d)\])|(?<num>\d+(?:[.,]\d+)?)",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex NumberRegex =
            new Regex(@"\d+(?:[.,]\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Inline game markup that pack values keep: HSR <u>/<icon>, Genshin <i>, link tags (some broken
        // by translation), colour tags missing their '>'. Same rules as tools/landing-characters.
        private static readonly Regex MarkupRegex = new Regex(
            @"</?(?:i|u|b)>|<icon\b[^<>]*>|</?link[^<>]*>|\{/?link[^{}<>\s]*[}>]|</color>?|<color=[^<>\s]*>?|</?(?:size|sprite|unbreak|ruby)\b[^<>]*>",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex SpaceRuns = new Regex(@"[ \t]{2,}", RegexOptions.Compiled);
        private static readonly Regex TrailingSpace = new Regex(@"[ \t]+(?=\n|$)", RegexOptions.Compiled);

        public static bool HasTokens(string s) => !string.IsNullOrEmpty(s) && TokenRegex.IsMatch(s);

        public static string StripMarkup(string s)
        {
            if (string.IsNullOrEmpty(s) || (s.IndexOf('<') < 0 && s.IndexOf('{') < 0)) return s ?? "";
            string t = MarkupRegex.Replace(s, "");
            return ReferenceEquals(t, s) ? s : TrailingSpace.Replace(SpaceRuns.Replace(t, " "), "").Trim();
        }

        /// <summary>Formats tokens from numeric params; a '%' after token N in English means ×100.</summary>
        public static string FillFromParams(string englishTemplate, string target, IReadOnlyList<double> p)
        {
            if (string.IsNullOrEmpty(target)) return target ?? "";
            if (!HasTokens(target)) return target;
            var percent = PercentIndices(englishTemplate ?? target);

            return Replace(target, percent, (idx, format) =>
            {
                if (p == null || idx < 1 || idx > p.Count) return Unknown;
                double v = p[idx - 1];
                if (double.IsNaN(v) || double.IsInfinity(v)) return Unknown;
                if (percent.Contains(idx)) v *= 100;
                return FormatValue(v, format);
            });
        }

        /// <summary>
        /// Fills tokens with the numbers OCR read, aligned by position against the English key.
        /// Any doubt about the alignment yields "?" rather than a number from the wrong slot.
        /// </summary>
        public static string FillFromSource(string englishKey, string target, string sourceText)
        {
            if (string.IsNullOrEmpty(target)) return target ?? "";
            if (!HasTokens(target)) return target;
            var percent = PercentIndices(englishKey ?? target);
            var values = AlignSourceNumbers(englishKey, sourceText);

            return Replace(target, percent, (idx, _) =>
                values != null && values.TryGetValue(idx, out var v) ? v : Unknown);
        }

        private static Dictionary<int, string> AlignSourceNumbers(string englishKey, string sourceText)
        {
            if (string.IsNullOrEmpty(englishKey) || string.IsNullOrEmpty(sourceText)) return null;
            MatchCollection slots = SlotRegex.Matches(englishKey);
            MatchCollection numbers = NumberRegex.Matches(sourceText);
            if (slots.Count == 0 || slots.Count != numbers.Count) return null;

            var map = new Dictionary<int, string>();
            for (int i = 0; i < slots.Count; i++)
            {
                string seen = numbers[i].Value;
                if (slots[i].Groups["num"].Success)
                {
                    if (!SameNumber(slots[i].Groups["num"].Value, seen)) return null;
                    continue;
                }
                if (int.TryParse(slots[i].Groups["idx"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int idx) &&
                    !map.ContainsKey(idx))
                {
                    map[idx] = seen;
                }
            }
            return map;
        }

        private static string Replace(string target, HashSet<int> percent, Func<int, string, string> valueFor)
        {
            var sb = new StringBuilder(target.Length + 8);
            int last = 0;
            foreach (Match m in TokenRegex.Matches(target))
            {
                sb.Append(target, last, m.Index - last);
                int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int idx);
                sb.Append(valueFor(idx, m.Groups[2].Value));
                last = m.Index + m.Length;
                bool targetHasPercent = last < target.Length && target[last] == '%';
                if (percent.Contains(idx) && !targetHasPercent) sb.Append('%');
            }
            sb.Append(target, last, target.Length - last);
            return sb.ToString();
        }

        private static HashSet<int> PercentIndices(string english)
        {
            var set = new HashSet<int>();
            if (string.IsNullOrEmpty(english)) return set;
            foreach (Match m in TokenRegex.Matches(english))
            {
                int end = m.Index + m.Length;
                if (end < english.Length && english[end] == '%' &&
                    int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int idx))
                {
                    set.Add(idx);
                }
            }
            return set;
        }

        private static string FormatValue(double v, string format)
        {
            if (format.Length == 2 && format[0] == 'f' && char.IsDigit(format[1]))
                return v.ToString("F" + format[1], CultureInfo.InvariantCulture);
            return Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        }

        private static bool SameNumber(string a, string b) =>
            string.Equals(a.Replace(',', '.'), b.Replace(',', '.'), StringComparison.Ordinal);
    }
}
