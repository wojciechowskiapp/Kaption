using System;
using System.Globalization;
using System.Text;

namespace GI_Subtitles.Services.Characters
{
    /// <summary>
    /// A goal's value exactly as the site writes it (landing/src/data/characters/goals.ts; keep the two in step):
    /// English "2,500–3,000+", "200%+", "100–130%", "≤130%"; Polish "2 500–3 000+" with a narrow no-break space
    /// between thousands and a decimal comma. Pure; safe on any thread.
    /// </summary>
    public static class StatGoalFormat
    {
        public const char EnDash = '\u2013';
        public const char NarrowNoBreakSpace = '\u202F';
        /// <summary>A goal with only an upper bound: "≤130%".</summary>
        public const char AtMost = '\u2264';

        /// <summary>
        /// Cents rounded half away from zero; the whole part grouped by three from four digits up (EN ",", PL U+202F);
        /// the fraction as two digits without trailing zeros (EN ".", PL ","). Values are 0 or more.
        /// </summary>
        /// <param name="language">The interface language: "pl…" writes Polish numbers, anything else English.</param>
        public static string Number(double value, string language)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "";
            if (value < 0) return "-" + Number(-value, language);
            bool polish = CharacterPageLink.IsPolish(language);
            double cents = Math.Round(value * 100, MidpointRounding.AwayFromZero);
            double whole = Math.Floor(cents / 100);
            string fraction = ((long)(cents - whole * 100)).ToString("00", CultureInfo.InvariantCulture).TrimEnd('0');
            string grouped = Group(((long)whole).ToString(CultureInfo.InvariantCulture), polish ? NarrowNoBreakSpace : ',');
            return fraction.Length > 0 ? grouped + (polish ? ',' : '.') + fraction : grouped;
        }

        private static string Group(string digits, char separator)
        {
            if (digits.Length < 4) return digits;
            var sb = new StringBuilder(digits.Length + digits.Length / 3);
            int head = digits.Length % 3;
            if (head > 0) sb.Append(digits, 0, head);
            for (int i = head; i < digits.Length; i += 3)
            {
                if (sb.Length > 0) sb.Append(separator);
                sb.Append(digits, i, 3);
            }
            return sb.ToString();
        }

        /// <summary>
        /// "min–max" when both are known and differ, else "min", else "≤max"; then the unit once, then "+" when open
        /// (only with a min). Never words.
        /// </summary>
        public static string Value(double? min, double? max, string unit, bool open, string language)
        {
            string value;
            if (min is double a && max is double b && a != b) value = Number(a, language) + EnDash + Number(b, language);
            else if (min is double low) value = Number(low, language);
            else if (max is double high) value = AtMost + Number(high, language);
            else return "";
            return value + (unit ?? "") + (open && min != null ? "+" : "");
        }

        public static string Value(StatGoal goal, string language) =>
            goal == null ? "" : Value(goal.Min, goal.Max, goal.Unit, goal.Open, language);
    }
}
