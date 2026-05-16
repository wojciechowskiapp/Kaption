using System;
using System.Runtime.CompilerServices;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// OCR-aware weighted Levenshtein distance calculator.
    /// Common OCR misrecognitions (l↔1, O↔0, I↔l) are assigned low substitution costs
    /// since they indicate the same character was likely present in the original text.
    ///
    /// This gives better matching accuracy than uniform-cost Levenshtein
    /// for OCR-noisy input against a clean dictionary.
    ///
    /// Performance:
    ///   * Flat ASCII confusion table (float[128*128]) indexed by (a &lt;&lt; 7) | b.
    ///     Replaces a Dictionary&lt;(char,char), float&gt; that cost ~2-5 ms per OCR
    ///     tick from tuple-hashing + generic-dict probe inside the innermost DP
    ///     loop. Default cost 1.0f is baked in at table construction.
    ///   * Non-ASCII chars fall back to the scalar path (char == char → 0,
    ///     else 1.0f) — Polish accented chars still compare correctly against
    ///     themselves because the == check precedes the table lookup.
    ///   * [MethodImpl(AggressiveInlining)] on GetSubstitutionCost — called
    ///     tens of millions of times per OCR tick.
    ///
    /// Modular: can be enabled/disabled via Config. Falls back to uniform cost if disabled.
    /// </summary>
    public static class OcrWeightedDistance
    {
        // Flat ASCII confusion table. Index = (a << 7) | b for a,b in [0..127].
        // Self-pairs are 0.0f (a == b path short-circuits before lookup anyway).
        // Missing entries default to 1.0f (standard substitution cost).
        //
        // Size: 128*128*4 = 64 KB, allocated once at class init, zero GC
        // pressure on the hot path.
        private static readonly float[] AsciiConfusion = BuildAsciiConfusionTable();

        private static float[] BuildAsciiConfusionTable()
        {
            var t = new float[128 * 128];
            // Default: 1.0f everywhere. Self-pairs overwrite to 0.0f, known
            // OCR confusions overwrite with their actual cost.
            for (int i = 0; i < t.Length; i++) t[i] = 1.0f;
            for (int c = 0; c < 128; c++) t[(c << 7) | c] = 0.0f;

            // Substitution costs for common OCR confusions (normalized lowercase).
            // Cost range: 0.1 (nearly identical glyphs) to 0.5 (somewhat similar).

            // Nearly identical glyphs (cost 0.1)
            Pair(t, 'l', '1', 0.1f);
            Pair(t, '0', 'o', 0.1f);
            Pair(t, 'i', 'l', 0.15f);
            Pair(t, 'i', '1', 0.15f);

            // Similar shapes (cost 0.2)
            Pair(t, '5', 's', 0.2f);
            Pair(t, '8', 'b', 0.2f);
            Pair(t, '6', 'g', 0.2f);
            Pair(t, '2', 'z', 0.2f);
            Pair(t, '9', 'q', 0.25f);

            // Common OCR confusion pairs (cost 0.3)
            Pair(t, 'n', 'h', 0.3f);
            Pair(t, 'c', 'e', 0.3f);
            Pair(t, 'u', 'v', 0.3f);
            Pair(t, 'm', 'n', 0.3f);
            Pair(t, 'd', 'a', 0.35f);
            Pair(t, 'f', 't', 0.35f);

            // Partial shape similarity (cost 0.4-0.5)
            Pair(t, 'r', 'n', 0.4f); // "rn" vs "m" at character level
            Pair(t, 'w', 'v', 0.4f);
            Pair(t, 'y', 'v', 0.45f);
            Pair(t, 'k', 'x', 0.5f);

            return t;
        }

        // Set both (a,b) and (b,a) — symmetric cost.
        private static void Pair(float[] t, char a, char b, float cost)
        {
            t[(a << 7) | b] = cost;
            t[(b << 7) | a] = cost;
        }

        /// <summary>
        /// Get the substitution cost for two characters based on OCR confusion likelihood.
        /// Fast path: both chars in ASCII range → single float[] load.
        /// Slow path: non-ASCII (Polish accented, etc.) → identity or 1.0f.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float GetSubstitutionCost(char a, char b)
        {
            // Hot path: both ASCII → flat table load. The self-pair (a == b)
            // resolves to 0.0f in-table, so we don't need a separate check.
            if (a < 128 && b < 128)
            {
                return AsciiConfusion[(a << 7) | b];
            }

            // Non-ASCII fallback (e.g. Polish accented chars as dictionary
            // keys post-normalization — rare since NormalizeInput strips
            // non-[a-z0-9] for EN, but kept correct for safety).
            return a == b ? 0f : 1.0f;
        }

        /// <summary>
        /// Calculate OCR-weighted Levenshtein distance between two normalized strings.
        /// Uses stackalloc for performance. Supports early termination via threshold.
        ///
        /// Insertion/deletion cost is always 1.0.
        /// Substitution cost varies based on OCR confusion likelihood.
        /// </summary>
        /// <param name="source">Source string (OCR output, normalized)</param>
        /// <param name="target">Target string (dictionary entry, normalized)</param>
        /// <param name="threshold">Maximum acceptable distance — returns threshold+1 if exceeded</param>
        /// <returns>Weighted edit distance (float). Lower = better match.</returns>
        public static float Calculate(ReadOnlySpan<char> source, ReadOnlySpan<char> target, float threshold)
        {
            int sourceLen = source.Length;
            int targetLen = target.Length;

            if (sourceLen == 0) return targetLen;
            if (targetLen == 0) return sourceLen;

            // Length difference alone exceeds threshold
            if (Math.Abs(sourceLen - targetLen) > threshold) return threshold + 1;

            // Ensure source is the shorter string for optimal stackalloc
            if (sourceLen > targetLen)
            {
                var temp = source; source = target; target = temp;
                var tempLen = sourceLen; sourceLen = targetLen; targetLen = tempLen;
            }

            // Stack allocation for speed (up to 512 chars)
            Span<float> prev = sourceLen < 512 ? stackalloc float[sourceLen + 1] : new float[sourceLen + 1];
            Span<float> curr = sourceLen < 512 ? stackalloc float[sourceLen + 1] : new float[sourceLen + 1];

            for (int i = 0; i <= sourceLen; i++) prev[i] = i;

            for (int j = 1; j <= targetLen; j++)
            {
                curr[0] = j;
                float minInRow = j;
                char targetChar = target[j - 1];

                for (int i = 1; i <= sourceLen; i++)
                {
                    float subCost = GetSubstitutionCost(source[i - 1], targetChar);
                    float d1 = curr[i - 1] + 1.0f;      // insertion
                    float d2 = prev[i] + 1.0f;           // deletion
                    float d3 = prev[i - 1] + subCost;    // substitution (weighted)

                    float dist = d1 < d2 ? d1 : d2;
                    if (d3 < dist) dist = d3;

                    curr[i] = dist;
                    if (dist < minInRow) minInRow = dist;
                }

                // Early termination: if all values in this row exceed threshold, abort
                if (minInRow > threshold) return threshold + 1;
                var tempRow = prev; prev = curr; curr = tempRow;
            }

            return prev[sourceLen];
        }

        /// <summary>
        /// Integer-returning wrapper for backward compatibility with existing threshold checks.
        /// Returns the ceiling of the weighted distance.
        /// </summary>
        public static int CalculateAsInt(ReadOnlySpan<char> source, ReadOnlySpan<char> target, int threshold)
        {
            float result = Calculate(source, target, threshold + 0.5f);
            return (int)Math.Ceiling(result);
        }
    }
}
