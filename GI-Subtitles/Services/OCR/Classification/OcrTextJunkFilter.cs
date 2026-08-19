using System;
using System.Collections.Generic;

namespace GI_Subtitles.Services.OCR.Classification
{
    /// <summary>
    /// Cheap reject list for OCR blocks that are demonstrably not dialogue:
    /// streaming watermarks, UID stamps, HUD glyphs, close buttons.
    ///
    /// <para>Why it exists: junk blocks are not merely noise in the output, they
    /// corrupt the <em>geometry</em> the speaker/body split reasons about. In the
    /// measured Zenless comic-style frame the close button "×" sits within half a
    /// glyph height of the last body line, so without this filter it merges into
    /// that row band and stretches its union width; the watermark "UBHEN92" adds
    /// a spurious band below the body entirely.</para>
    ///
    /// <para>Every rule below is justified by a block that was actually observed
    /// in <c>docs/screenshots/zzz-*.png</c>. Guessing extra rules is how a filter
    /// starts eating real dialogue, so the list stays short. Callers are expected
    /// to treat "everything was junk" as "keep the original set" rather than as
    /// an empty frame — see <see cref="GeometryTextBlockClassifier"/>.</para>
    ///
    /// <para><b>Not applied to the accent path.</b> Genshin and Star Rail must
    /// stay byte-identical across the classifier refactor, and neither has ever
    /// had a junk filter.</para>
    /// </summary>
    public static class OcrTextJunkFilter
    {
        /// <summary>
        /// Whole-block HUD labels. Exact, case-insensitive matches only — a
        /// substring rule here would delete "Skip it." and similar real lines.
        /// </summary>
        private static readonly HashSet<string> UiLabels =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "auto", "skip", "log", "menu", "close", "esc",
            };

        /// <summary>
        /// Longest run of characters a watermark handle plausibly occupies.
        /// "UBHEN92" is 7; real dialogue rarely arrives as a single whitespace-free
        /// token containing a digit, and when it does it is longer than this.
        /// </summary>
        private const int MaxWatermarkTokenLength = 12;

        /// <summary>
        /// True when <paramref name="text"/> should be dropped before geometry
        /// analysis. Pure string inspection — a couple of passes over a short
        /// string, safe to call per block on the OCR tick.
        /// </summary>
        public static bool IsJunk(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;

            string t = text.Trim();

            // A single glyph carries no dialogue. Kills the "×" close button.
            if (t.Length < 2) return true;

            if (UiLabels.Contains(t)) return true;

            int letters = 0, digits = 0;
            bool hasWhitespace = false;
            foreach (char c in t)
            {
                if (char.IsLetter(c)) letters++;
                else if (char.IsDigit(c)) digits++;
                else if (char.IsWhiteSpace(c)) hasWhitespace = true;
            }

            // No letters at all: decorative rules, ellipses, bare numbers.
            if (letters == 0) return true;

            // Digit-dominant: "UID: 1000290822." (3 letters, 10 digits), level
            // pips, timers, coordinates.
            if (digits >= letters) return true;

            // One whitespace-free token that mixes letters and digits: a handle,
            // not a sentence. "UBHEN92". Bounded by length so an in-dialogue
            // token like a long identifier is left alone.
            if (!hasWhitespace && digits > 0 && t.Length <= MaxWatermarkTokenLength) return true;

            return false;
        }
    }
}
