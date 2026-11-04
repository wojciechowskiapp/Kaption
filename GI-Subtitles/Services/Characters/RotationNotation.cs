using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace GI_Subtitles.Services.Characters
{
    /// <summary>
    /// How rotation inputs are written, as on the site (landing/src/data/characters/rotations.ts; keep in step): E, Q, N as
    /// the member's skill icon where the bundle names one, a repeat as a "×2" corner badge; any other input a text chip that
    /// keeps its count ("N5", "CA ×2", "Combo"). Pure; safe on any thread.
    /// </summary>
    public static class RotationNotation
    {
        public const string Skill = "E";
        public const string Burst = "Q";
        public const string Normal = "N";
        public const string Charged = "CA";
        public const string Plunge = "P";
        public const string Dash = "D";
        public const string Combo = "combo";
        public const char Times = '\u00D7';

        private static readonly string[] Tokens = { Skill, Burst, Normal, Charged, Plunge, Dash, Combo };

        /// <summary>The grammar's tokens, case as written in the data.</summary>
        public static IReadOnlyList<string> Actions => Tokens;

        public static bool IsAction(string token) => token != null && Array.IndexOf(Tokens, token) >= 0;

        /// <summary>E, Q and N with an icon in the bundle draw as that icon (its letter until it is on disk).</summary>
        public static bool HasIconSlot(string action, bool hasIcon) => hasIcon && (action == Skill || action == Burst || action == Normal);

        /// <summary>The chip's text: "Combo" for a combo, "N5" for an N without an icon (one hit reads "N"), else the token.</summary>
        public static string Label(string action, int count, bool hasIcon) =>
            action == Combo ? "Combo"
            : action == Normal && !hasIcon && count > 1 ? Normal + count.ToString(CultureInfo.InvariantCulture)
            : action ?? "";

        /// <summary>"×2": the badge on an icon chip, after the text on a text chip; "" for one, and next to an "N5".</summary>
        public static string Count(string action, int count, bool hasIcon) =>
            count > 1 && !(action == Normal && !hasIcon) ? Times + count.ToString(CultureInfo.InvariantCulture) : "";

        /// <summary>The chip as plain text: "E ×2", "CA ×2", "N2", "Combo*".</summary>
        public static string Text(string action, int count, bool hasIcon, string marker = "")
        {
            string times = Count(action, count, hasIcon);
            return Label(action, count, hasIcon) + (times.Length > 0 ? " " + times : "") + (marker ?? "");
        }

        /// <summary>The footnote marker of the <paramref name="index"/>-th combo spelled out (0: "*", 1: "**").</summary>
        public static string Marker(int index) => new string('*', Math.Max(0, index) + 1);

        private static readonly Dictionary<string, string> Terms = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Skill] = "Skill", [Burst] = "Ultimate", [Normal] = "Normal Attack", [Charged] = "Charged Attack",
            [Plunge] = "Plunging Attack", [Dash] = "Dash", [Combo] = "Combo",
        };

        private static readonly Dictionary<string, Dictionary<string, string>> GameTerms =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["genshin"] = new Dictionary<string, string> { [Skill] = "Elemental Skill", [Burst] = "Elemental Burst" },
                ["starrail"] = new Dictionary<string, string> { [Normal] = "Basic ATK" },
                ["zzz"] = new Dictionary<string, string> { [Skill] = "Special Attack", [Normal] = "Basic Attack", [Dash] = "Dodge" },
            };

        /// <summary>The game's English name of an input ("Elemental Skill", "Basic ATK", "Charged Attack").</summary>
        public static string Term(string action, string game) =>
            game != null && GameTerms.TryGetValue(game, out var own) && own.TryGetValue(action ?? "", out string term) ? term
            : Terms.TryGetValue(action ?? "", out term) ? term : action ?? "";

        /// <summary>
        /// A chip's tooltip and accessible name, as the site's: the input's name ("Elemental Skill: Passion Overload", or the
        /// skill's own name when it starts with the term, "Ultimate: Lingering Snow"), then "×2", then the footnote marker.
        /// </summary>
        public static string Describe(string action, int count, string game, string skillName, string marker)
        {
            string term = Term(action, game);
            string label = string.IsNullOrEmpty(skillName) ? term
                : skillName.StartsWith(term + ":", StringComparison.OrdinalIgnoreCase) ? skillName
                : term + ": " + skillName;
            string times = count > 1 ? Times + count.ToString(CultureInfo.InvariantCulture) : "";
            return string.Join(" ", new[] { label, times, marker ?? "" }.Where(s => s.Length > 0));
        }

        // The site's plWith: "ze Skirk", "ze Sparkle", but "z Prune", "z Shenhe", "z Zhongli", "z Sucrose", "z Szymonem".
        private static readonly Regex PolishZe = new Regex(@"^(?:sz|[sśzźż](?![hz]))[bcdfgjklmnpqrstvwxzćłńśźż]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Polish says "ze", not "z", before this name ("ze Skirk"); English always reads "with".</summary>
        public static bool TakesPolishZe(string name) => !string.IsNullOrEmpty(name) && PolishZe.IsMatch(name);
    }
}
