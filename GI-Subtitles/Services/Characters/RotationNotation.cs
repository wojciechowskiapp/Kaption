using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace GI_Subtitles.Services.Characters
{
    /// <summary>
    /// How rotation inputs are written, as on the site (landing/src/data/characters/rotations.ts; keep in step): E, Q, N as
    /// the member's skill icon where the bundle names one, any other input a text chip ("N5", "CA ×2", "Combo", "E hold",
    /// "E / Q"); a repeat is a "×2" badge on an icon chip, a hold an "H". Pure; safe on any thread.
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
        /// <summary>A held skill's marks, the game's own English in every language: "H" on an icon chip, "E hold" as text.</summary>
        public const string HoldTag = "H";
        public const string HoldWord = "hold";

        /// <summary>A rotation's origin: transcribed from a guide page, or composed from the members' kits ("Suggested rotation").</summary>
        public const string OriginGuide = "guide";
        public const string OriginModel = "model";

        private static readonly string[] Tokens = { Skill, Burst, Normal, Charged, Plunge, Dash, Combo };

        /// <summary>"guide" or "model", case as written.</summary>
        public static bool IsOrigin(string origin) => origin == OriginGuide || origin == OriginModel;

        /// <summary>The grammar's tokens, case as written in the data.</summary>
        public static IReadOnlyList<string> Actions => Tokens;

        public static bool IsAction(string token) => token != null && Array.IndexOf(Tokens, token) >= 0;

        /// <summary>E, Q and N with an icon in the bundle draw as that icon (its letter until it is on disk).</summary>
        public static bool HasIconSlot(string action, bool hasIcon) => hasIcon && (action == Skill || action == Burst || action == Normal);

        /// <summary>A choice draws as icons side by side only when both inputs have one; otherwise as text, "E / CA".</summary>
        public static bool HasIconSlot(string action, bool hasIcon, string alt, bool altHasIcon) =>
            HasIconSlot(action, hasIcon) && (string.IsNullOrEmpty(alt) || HasIconSlot(alt, altHasIcon));

        /// <summary>The chip's text: "Combo" for a combo, "N5" for an N without an icon (one hit reads "N"), else the token.</summary>
        public static string Label(string action, int count, bool hasIcon) =>
            action == Combo ? "Combo"
            : action == Normal && !hasIcon && count > 1 ? Normal + count.ToString(CultureInfo.InvariantCulture)
            : action ?? "";

        /// <summary>"×2": the badge on an icon chip, after the text on a text chip; "" for one, and next to an "N5".</summary>
        public static string Count(string action, int count, bool hasIcon) =>
            count > 1 && !(action == Normal && !hasIcon) ? Times + count.ToString(CultureInfo.InvariantCulture) : "";

        /// <summary>A choice reads "E / Q", "N / CA": its count belongs to the whole chip, so an N never folds it in.</summary>
        public static string Label(string action, int count, bool hasIcon, string alt) =>
            string.IsNullOrEmpty(alt) ? Label(action, count, hasIcon) : Label(action, 1, hasIcon) + " / " + Label(alt, 1, hasIcon);

        /// <summary>"×2" as <see cref="Count(string,int,bool)"/>; a choice always shows its count.</summary>
        public static string Count(string action, int count, bool hasIcon, string alt) =>
            string.IsNullOrEmpty(alt) ? Count(action, count, hasIcon)
            : count > 1 ? Times + count.ToString(CultureInfo.InvariantCulture) : "";

        /// <summary>
        /// The chip as plain text, in the site's order (text, "hold", count, marker): "E ×2", "CA ×2", "N2", "Combo*",
        /// "E hold ×2", "E / Q", "N / CA ×3"; an optional input in KQM's brackets, "(Q)". <paramref name="hasIcon"/> is the
        /// chip's form (a choice has icons only when both inputs do).
        /// </summary>
        public static string Text(string action, int count, bool hasIcon, string marker = "", bool hold = false, string alt = "",
                                  bool optional = false)
        {
            string times = Count(action, count, hasIcon, alt);
            string core = Label(action, count, hasIcon, alt) + (hold ? " " + HoldWord : "") + (times.Length > 0 ? " " + times : "");
            return (optional ? "(" + core + ")" : core) + (marker ?? "");
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
        /// An input's English name: the term, then the skill's own name when found ("Elemental Skill: Passion Overload"); a
        /// skill name that starts with the term stands alone ("Ultimate: Lingering Snow").
        /// </summary>
        public static string Name(string action, string game, string skillName)
        {
            string term = Term(action, game);
            return string.IsNullOrEmpty(skillName) ? term
                : skillName.StartsWith(term + ":", StringComparison.OrdinalIgnoreCase) ? skillName
                : term + ": " + skillName;
        }

        /// <summary>A chip's tooltip and accessible name for one input, as the site's: its name, then "×2", then the marker.</summary>
        public static string Describe(string action, int count, string game, string skillName, string marker) =>
            Describe(Name(action, game, skillName), "", count, marker, null);

        /// <summary>
        /// The tooltip with the Round 4 flags, as the site's chipLabel: the name (a choice names both, "… / …"), "×2", the
        /// marker, then the flags in brackets: "Elemental Skill: Binding Flame ×2 (hold, optional)".
        /// </summary>
        public static string Describe(string name, string altName, int count, string marker, IEnumerable<string> flags)
        {
            string label = string.IsNullOrEmpty(altName) ? name ?? "" : (name ?? "") + " / " + altName;
            string times = count > 1 ? Times + count.ToString(CultureInfo.InvariantCulture) : "";
            string words = string.Join(", ", (flags ?? Enumerable.Empty<string>()).Where(w => !string.IsNullOrEmpty(w)));
            return string.Join(" ", new[] { label, times, marker ?? "", words.Length > 0 ? "(" + words + ")" : "" }.Where(s => s.Length > 0));
        }

        /// <summary>A flagged input's words, in the site's order: a choice, a hold, an optional input.</summary>
        public static IReadOnlyList<string> Flags(bool choice, bool hold, bool optional, RotationWords words)
        {
            words ??= RotationWords.English;
            var list = new List<string>(3);
            if (choice) list.Add(words.Either);
            if (hold) list.Add(words.Hold);
            if (optional) list.Add(words.Optional);
            return list;
        }

        // The site's plWith: "ze Skirk", "ze Sparkle", but "z Prune", "z Shenhe", "z Zhongli", "z Sucrose", "z Szymonem".
        private static readonly Regex PolishZe = new Regex(@"^(?:sz|[sśzźż](?![hz]))[bcdfgjklmnpqrstvwxzćłńśźż]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Polish says "ze", not "z", before this name ("ze Skirk"); English always reads "with".</summary>
        public static bool TakesPolishZe(string name) => !string.IsNullOrEmpty(name) && PolishZe.IsMatch(name);
    }

    /// <summary>
    /// The strip's words in the interface's language: a flagged chip's tooltip words (the site's rotationFlags), the heading
    /// per origin, the source link and the heading's tooltip ({0} = the site's name). The view reads its strings.
    /// </summary>
    public sealed record RotationWords(string Hold, string Optional, string Either)
    {
        /// <summary>The English strings, for rows built without a view.</summary>
        public static readonly RotationWords English = new RotationWords("hold", "optional", "either");

        /// <summary>Char_Rotation: a guide's rotation.</summary>
        public string Guide { get; init; } = "Rotation";
        /// <summary>Char_Rotation_Suggested: composed from the team's kits.</summary>
        public string Suggested { get; init; } = "Suggested rotation";
        /// <summary>Char_Rotation_Per: the source link after the heading, "per KQM".</summary>
        public string Per { get; init; } = "per {0}";
        /// <summary>Char_Rotation_TipGuideFrom: the heading's tooltip for a guide's rotation with a source.</summary>
        public string TipGuideFrom { get; init; } = "From the {0} guide. Check it in play.";
        /// <summary>Char_Rotation_TipGuide: a guide's rotation without a usable source.</summary>
        public string TipGuide { get; init; } = "From a guide. Check it in play.";
        /// <summary>Char_Rotation_TipSuggested: a rotation composed from the kits.</summary>
        public string TipSuggested { get; init; } = "Suggested from the team's kits. Check it in play.";
        /// <summary>Char_Rotation_TipSuggestedFrom: a suggested rotation adapted from a guide's rotation for a similar team.</summary>
        public string TipSuggestedFrom { get; init; } = "Suggested from the {0} guide's rotation for a similar team. Check it in play.";

        /// <summary>
        /// <paramref name="pattern"/> with the site's name for {0}; a translation without {0}, or not a valid format, falls back
        /// to <paramref name="english"/> so the name is never lost and a bad string never throws.
        /// </summary>
        public static string Fill(string pattern, string english, string name)
        {
            if (!string.IsNullOrEmpty(pattern) && pattern.Contains("{0}"))
            {
                try { return string.Format(CultureInfo.InvariantCulture, pattern, name ?? ""); }
                catch (FormatException) { }
            }
            return string.Format(CultureInfo.InvariantCulture, english, name ?? "");
        }
    }
}
