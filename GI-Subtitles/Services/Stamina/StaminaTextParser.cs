using System;
using System.Globalization;
using System.Text;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Stamina
{
    /// <summary>
    /// Turns the text read beside the stamina icon into a value, or refuses it. The line must start with digits, a
    /// slash and the game's own cap ("121/200", "93 / 200"); the cap must be the manifest's, digit for digit. After it
    /// only the "+" button or a divider may follow ("+", "|"); anything else refuses the line. No letter is ever taken
    /// for a digit: a doubtful read is dropped and the next look gets its turn.
    /// </summary>
    public static class StaminaTextParser
    {
        /// <summary>The value's digits: what "current" can show at most.</summary>
        public const int MaxValueDigits = 4;

        /// <summary>The highest value a read may claim: the manifest's hold, else what four digits can show.</summary>
        public static int Ceiling(GameStamina stamina) => stamina?.Hold ?? 9999;

        public static bool TryParse(string text, GameStamina stamina, out int value)
        {
            value = 0;
            if (stamina == null || string.IsNullOrWhiteSpace(text)) return false;
            ReadOnlySpan<char> s = text.Normalize(NormalizationForm.FormKC).AsSpan().Trim();

            int i = 0, digits = 0, current = 0;
            while (i < s.Length && IsDigit(s[i]) && digits < MaxValueDigits)
            {
                current = current * 10 + (s[i] - '0');
                i++;
                digits++;
            }
            if (digits == 0 || (i < s.Length && IsDigit(s[i]))) return false;
            i = SkipSpaces(s, i);
            if (i >= s.Length || s[i] != '/') return false;
            i = SkipSpaces(s, i + 1);

            string cap = stamina.Max.ToString(CultureInfo.InvariantCulture);
            if (s.Length - i < cap.Length || !s.Slice(i, cap.Length).SequenceEqual(cap)) return false;
            i += cap.Length;
            // After the cap: nothing, or the "+" button and the divider beside it. A letter or a digit there (also a
            // digit after a space, or one in another script) means the cap was longer than the game's.
            for (; i < s.Length; i++)
                if (!char.IsWhiteSpace(s[i]) && s[i] != '+' && s[i] != '|') return false;

            if (current > Ceiling(stamina)) return false;
            value = current;
            return true;
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private static int SkipSpaces(ReadOnlySpan<char> s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }
    }
}
