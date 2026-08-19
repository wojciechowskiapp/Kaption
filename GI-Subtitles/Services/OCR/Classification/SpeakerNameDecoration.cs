namespace GI_Subtitles.Services.OCR.Classification
{
    /// <summary>
    /// Strips ornamental characters from a recognised speaker name.
    ///
    /// <para>Zenless Zone Zero's cinematic style flanks the name with middot
    /// decorations — <c>··Remielle··</c> on screen. PaddleOCR does not reproduce
    /// them faithfully: the measured recognition is <c>-Remielle</c>, i.e. the
    /// leading pair collapses into a single hyphen and the trailing pair
    /// disappears entirely. So the strip has to handle a <em>run</em> of assorted
    /// dash/dot/bullet characters on either end, not a literal middot pair.</para>
    ///
    /// <para><b>Names only, never bodies.</b> A body line legitimately opens with
    /// a dash (em-dash interruptions, quoted asides) and legitimately ends with
    /// an ellipsis; stripping those would change the text the matcher keys on and
    /// break matches that currently work. The speaker name, by contrast, is never
    /// part of a match key — it routes the matcher, seeds the hot-cache preload
    /// and breaks disambiguation ties — so normalising it is free.</para>
    /// </summary>
    public static class SpeakerNameDecoration
    {
        /// <summary>
        /// Characters that may appear as ornament around a name. Deliberately
        /// excludes anything that could be the first or last character of a real
        /// name (letters, digits, apostrophes are all absent).
        /// </summary>
        private static bool IsDecoration(char c)
        {
            switch (c)
            {
                case ' ':
                case '\t':
                case '\r':
                case '\n':
                case '-':
                case '‐': // hyphen
                case '–': // en dash
                case '—': // em dash
                case '―': // horizontal bar
                case '~':
                case '～': // fullwidth tilde
                case '.':
                case '·': // middle dot
                case '‥': // two dot leader
                case '…': // ellipsis
                case '•': // bullet
                case '∙': // bullet operator
                case '・': // katakana middle dot
                case '*':
                case '_':
                case ':':
                case '|':
                case '<':
                case '>':
                case '«': // «
                case '»': // »
                case '[':
                case ']':
                case '"':
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Remove leading and trailing decoration runs. Returns
        /// <see cref="string.Empty"/> when the input is nothing but decoration —
        /// callers decide whether that means "drop the block" or "keep the raw
        /// text", since neither is right in every context.
        /// </summary>
        public static string Strip(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;

            int start = 0;
            int end = raw.Length - 1;

            while (start <= end && IsDecoration(raw[start])) start++;
            while (end >= start && IsDecoration(raw[end])) end--;

            if (start > end) return string.Empty;
            if (start == 0 && end == raw.Length - 1) return raw;
            return raw.Substring(start, end - start + 1);
        }
    }
}
