using System;
using System.Collections.Generic;

namespace GI_Subtitles.Services.OCR
{
    /// <summary>
    /// Filters out game UI text that OCR picks up but isn't dialogue.
    /// Examples: "Lv.50", "5015/7380", "1:23", score numbers, menu labels.
    /// Real dialogue has mostly alphabetical characters and is longer.
    /// </summary>
    /// <remarks>
    /// Moved verbatim out of <c>MainWindow</c> so the benchmark harness applies
    /// the same filter the overlay does instead of keeping a copy.
    ///
    /// The previous implementation did a naive <c>Contains()</c> against a flat list of
    /// keywords that included common English words like "wish", "sword", "bow", "shop",
    /// "mail", "character", "def", "atk". This caused valid dialogue such as
    /// <c>"...I Wish I Could Get Back..."</c> to be silently classified as game UI —
    /// the translation would be dropped, the idle-hide timer would arm, and the
    /// subtitle would fade out mid-conversation until the player advanced the dialogue.
    ///
    /// The rewrite uses two lists with different match strategies:
    ///   • <see cref="GameUIPhrases"/>   — unambiguous multi-word expressions that don't
    ///     appear in normal speech (e.g. "base atk", "crit rate"). Safe to substring-match.
    ///   • <see cref="GameUIWords"/>     — single words rare enough to not appear
    ///     mid-sentence (e.g. "attributes", "polearm"). Word-boundary match, and only
    ///     evaluated on short text so a long dialogue that happens to contain the word
    ///     isn't flagged.
    /// Overly-generic single words that previously caused false positives have been
    /// removed from both lists entirely.
    /// </remarks>
    public static class GameUiTextFilter
    {
        // Multi-word UI phrases — unambiguous menu/stat labels; substring-matched at any length.
        private static readonly string[] GameUIPhrases = new[]
        {
            "base atk", "crit rate", "crit dmg", "energy recharge", "elemental mastery",
            "adventurer handbook", "battle pass", "party setup", "co-op",
        };

        // Single-word UI keywords — matched only as whole tokens AND only when the overall
        // text is short (see length gate below). Keep this list small and unambiguous.
        private static readonly HashSet<string> GameUIWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "attributes", "constellation", "polearm", "claymore", "refinement",
        };

        // Max length at which short-word UI keywords are allowed to flag text.
        // Real dialogue tends to exceed this; UI labels almost never do.
        private const int GameUIWordGateLength = 40;

        public static bool IsLikelyGameUI(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            string trimmed = text.Trim();
            if (trimmed.Length < 4) return true;

            // Count letter vs non-letter characters
            int letters = 0;
            int digits = 0;
            foreach (char c in trimmed)
            {
                if (char.IsLetter(c)) letters++;
                else if (char.IsDigit(c)) digits++;
            }

            // If more digits than letters, it's likely UI (HP, level, timer, score)
            if (digits > 0 && digits >= letters) return true;

            // Very short text with digits is likely UI ("Lv.50", "HP 100")
            if (trimmed.Length < 10 && digits > 0) return true;

            string lower = trimmed.ToLowerInvariant();

            // Multi-word phrase check — safe at any length because these phrases
            // don't occur in conversational English.
            foreach (var phrase in GameUIPhrases)
            {
                if (lower.Contains(phrase)) return true;
            }

            // Single-word check — only fires on short text. A long sentence containing
            // a keyword as a normal English word (e.g. "I wish I could...") won't trip
            // this branch.
            if (trimmed.Length <= GameUIWordGateLength)
            {
                if (ContainsGameUIWord(lower)) return true;
            }

            return false;
        }

        /// <summary>
        /// Tokenize <paramref name="lowerText"/> on non-letter boundaries and return true
        /// if any whole token matches an entry in <see cref="GameUIWords"/>. Whole-token
        /// matching avoids false positives like "shoplifter" matching "shop".
        /// </summary>
        private static bool ContainsGameUIWord(string lowerText)
        {
            int i = 0;
            int n = lowerText.Length;
            while (i < n)
            {
                // Skip non-letters
                while (i < n && !char.IsLetter(lowerText[i])) i++;
                int start = i;
                while (i < n && char.IsLetter(lowerText[i])) i++;
                if (i > start)
                {
                    string token = lowerText.Substring(start, i - start);
                    if (GameUIWords.Contains(token)) return true;
                }
            }
            return false;
        }
    }
}
