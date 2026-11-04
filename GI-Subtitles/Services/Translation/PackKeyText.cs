using System;
using System.Text.RegularExpressions;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// A raw TextMap string as the translation pack keys it: <c>tools/merge-textmap.cjs</c>
    /// <c>cleanString(pickGender(s)).trim()</c> (male branch, colour tags kept as text, other placeholders and
    /// unbreak tags dropped, <c>#</c> removed, literal <c>\n</c> made a line break, <c>{NICKNAME}</c> kept).
    /// Change one and change the other.
    /// </summary>
    public static partial class PackKeyText
    {
        private const string NickSentinel = "\u0001NICK\u0001";

        /// <summary>True when cleaning can change the string's normalized letters and digits.</summary>
        public static bool MayDiffer(string raw) =>
            !string.IsNullOrEmpty(raw) && raw.AsSpan().IndexOfAny('{', '<', '\\') >= 0;

        public static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw ?? "";
            string t = raw;
            if (t.Contains("{M#", StringComparison.Ordinal))
            {
                t = GenderPair().Replace(t, "$1");
                t = FemaleOnly().Replace(t, "");
                t = MaleOnly().Replace(t, "$1");
            }
            t = t.Replace("{NICKNAME}", NickSentinel, StringComparison.Ordinal);
            t = ColorTag().Replace(t, "$1");
            t = Placeholder().Replace(t, "");
            t = Unbreak().Replace(t, "");
            t = t.Replace("#", "", StringComparison.Ordinal)
                 .Replace("\\n", "\n", StringComparison.Ordinal)
                 .Replace(NickSentinel, "{NICKNAME}", StringComparison.Ordinal);
            return t.Trim();
        }

        [GeneratedRegex(@"\{M#([^{}]*?)\}\{F#[^{}]*?\}", RegexOptions.CultureInvariant)]
        private static partial Regex GenderPair();

        [GeneratedRegex(@"\{F#[^{}]*?\}", RegexOptions.CultureInvariant)]
        private static partial Regex FemaleOnly();

        [GeneratedRegex(@"\{M#([^{}]*?)\}", RegexOptions.CultureInvariant)]
        private static partial Regex MaleOnly();

        [GeneratedRegex(@"<color=[^>]*?>(.*?)</color>", RegexOptions.CultureInvariant)]
        private static partial Regex ColorTag();

        [GeneratedRegex(@"\{[^{}]*?\}", RegexOptions.CultureInvariant)]
        private static partial Regex Placeholder();

        [GeneratedRegex(@"</?unbreak>", RegexOptions.CultureInvariant)]
        private static partial Regex Unbreak();
    }
}
