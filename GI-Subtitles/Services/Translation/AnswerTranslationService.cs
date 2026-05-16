using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// Translates player dialogue choice options captured from a separate screen region.
    /// Designed for short text (1-15 words) with a stricter matching strategy than main dialogue.
    /// </summary>
    public partial class AnswerTranslationService
    {
        // OCR artifacts commonly found in answer/choice UI elements
        private static readonly char[] ArtifactChars = { '\u2610', '\u25B8', '\u25CF', '\u25CB', '\u25A0', '\u25A1', '\u2022', '\u2023', '\u25BA', '\u25B6' };
        // Net8: RegexOptions.Compiled → [GeneratedRegex]. Hot path — called once
        // per OCR tick per answer line (typically 4 choices). Source generator
        // emits a state machine at compile time; no runtime IL emit.
        [GeneratedRegex(@"^[\s\-\.\>\|\*\#\+]+|[\s\-\.\>\|\*\#\+]+$", RegexOptions.CultureInvariant)]
        private static partial Regex ArtifactPattern();

        /// <summary>
        /// Translate a set of OCR-detected answer lines.
        /// Strategy: predicted answers -> exact dict -> hot cache -> predicted fuzzy -> full fuzzy.
        /// Predicted answers from the dialogue graph are the highest-quality match since
        /// we know exactly which choices the game is offering.
        /// </summary>
        public string[] TranslateAnswers(
            string[] answerLines,
            Dictionary<string, string> contentDict,
            OptimizedMatcher matcher,
            IGameDialogueContext contextEngine)
        {
            if (answerLines == null || answerLines.Length == 0)
                return Array.Empty<string>();

            // Pre-fetch predictions: graph next-nodes + full NPC cache for fuzzy matching
            List<PredictionResult> predicted = null;
            List<PredictionResult> npcCache = null;
            if (contextEngine?.IsLoaded == true)
            {
                predicted = contextEngine.GetPredictedAnswers(contentDict);
                npcCache = contextEngine.GetNpcCachePredictions();
            }

            var results = new string[answerLines.Length];

            for (int i = 0; i < answerLines.Length; i++)
            {
                string cleaned = CleanAnswerText(answerLines[i]);

                if (!IsValidAnswer(cleaned))
                {
                    results[i] = null;
                    continue;
                }

                // 1. Graph prediction match (immediate next nodes)
                if (predicted != null && predicted.Count > 0)
                {
                    string predMatch = TryMatchPredicted(cleaned, predicted);
                    if (predMatch != null)
                    {
                        results[i] = predMatch;
                        continue;
                    }
                }

                // 2. Exact match in contentDict (case-insensitive)
                string translation = TryExactMatch(cleaned, contentDict);
                if (translation != null)
                {
                    results[i] = translation;
                    continue;
                }

                // 3. Hot cache match via context engine (exact normalized)
                if (contextEngine?.IsLoaded == true)
                {
                    string normalized = OptimizedMatcher.NormalizeInput(cleaned, matcher?.isEng ?? true);
                    string hotResult = contextEngine.TryHotCacheMatch(normalized, out string hotKey, out bool isPartial);
                    if (hotResult != null && !isPartial)
                    {
                        results[i] = hotResult;
                        continue;
                    }
                }

                // 4. NPC cache fuzzy match — catches partial OCR text like
                //    "something made" → "I'd like something made."
                //    Works for shop NPCs where graph predictions are empty.
                if (npcCache != null && npcCache.Count > 0)
                {
                    string cacheMatch = TryMatchPredicted(cleaned, npcCache);
                    if (cacheMatch != null)
                    {
                        results[i] = cacheMatch;
                        continue;
                    }
                }

                // 5. Fuzzy match against full dictionary (5+ words only)
                int wordCount = cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                if (wordCount >= 5 && matcher?.Loaded == true)
                {
                    string fuzzyResult = matcher.FindClosestMatch(cleaned, out string matchKey);
                    if (!string.IsNullOrEmpty(fuzzyResult))
                    {
                        results[i] = fuzzyResult;
                        continue;
                    }
                }

                // 6. No match — show original English text
                results[i] = cleaned;
            }

            // Filter out null entries (invalid answers)
            return results.Where(r => r != null).ToArray();
        }

        /// <summary>
        /// Match OCR text against predicted answer options from the dialogue graph.
        /// Since we only compare against a handful of expected choices, we can use
        /// fuzzy matching even for very short text without false positives.
        /// Uses normalized Levenshtein similarity: match if >65% similar.
        /// </summary>
        private string TryMatchPredicted(string ocrText, List<PredictionResult> predicted)
        {
            if (predicted == null || predicted.Count == 0)
                return null;

            string ocrLower = ocrText.ToLowerInvariant();
            string bestTranslation = null;
            double bestSimilarity = 0;

            foreach (var pred in predicted)
            {
                if (string.IsNullOrEmpty(pred.EnText))
                    continue;

                string predLower = pred.EnText.ToLowerInvariant();

                // Exact match
                if (ocrLower == predLower)
                    return pred.Translation ?? pred.EnText;

                // Containment: OCR captured part of the answer or vice versa
                if (predLower.Contains(ocrLower) && ocrLower.Length > 4)
                    return pred.Translation ?? pred.EnText;
                if (ocrLower.Contains(predLower) && predLower.Length > 4)
                    return pred.Translation ?? pred.EnText;

                // Normalized Levenshtein similarity
                int maxLen = Math.Max(ocrLower.Length, predLower.Length);
                if (maxLen == 0) continue;
                int dist = QuickLevenshtein(ocrLower, predLower, maxLen);
                double similarity = 1.0 - (double)dist / maxLen;

                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestTranslation = pred.Translation ?? pred.EnText;
                }
            }

            // Accept if >65% similar (tolerant of OCR errors on short text)
            return bestSimilarity >= 0.65 ? bestTranslation : null;
        }

        /// <summary>
        /// Fast Levenshtein distance with early exit at maxDist.
        /// </summary>
        private static int QuickLevenshtein(string s, string t, int maxDist)
        {
            int sLen = s.Length, tLen = t.Length;
            if (Math.Abs(sLen - tLen) > maxDist) return maxDist;
            if (sLen == 0) return tLen;
            if (tLen == 0) return sLen;

            var prev = new int[tLen + 1];
            var curr = new int[tLen + 1];
            for (int j = 0; j <= tLen; j++) prev[j] = j;

            for (int i = 1; i <= sLen; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= tLen; j++)
                {
                    int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var tmp = prev; prev = curr; curr = tmp;
            }
            return prev[tLen];
        }

        /// <summary>
        /// Try exact dictionary lookup with case-insensitive key matching.
        /// Checks both the raw text and a trimmed version.
        /// </summary>
        private string TryExactMatch(string text, Dictionary<string, string> contentDict)
        {
            if (contentDict == null) return null;

            // Direct key lookup
            if (contentDict.TryGetValue(text, out string val) && !string.IsNullOrEmpty(val))
                return val;

            // Case-insensitive scan for short text (avoid full scan for long text)
            if (text.Length <= 80)
            {
                string lower = text.ToLowerInvariant();
                foreach (var kvp in contentDict)
                {
                    if (kvp.Key.Length == text.Length &&
                        kvp.Key.ToLowerInvariant() == lower &&
                        !string.IsNullOrEmpty(kvp.Value))
                    {
                        return kvp.Value;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Clean OCR artifacts from answer text. Answer regions often contain
        /// speech bubble icons or decorative characters that OCR picks up.
        /// </summary>
        private string CleanAnswerText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            string result = raw;

            // Remove known OCR artifact characters (bullets, checkboxes, arrows)
            foreach (char c in ArtifactChars)
            {
                result = result.Replace(c.ToString(), "");
            }

            // Remove leading/trailing punctuation and whitespace patterns
            result = ArtifactPattern().Replace(result, "");

            return result.Trim();
        }

        /// <summary>
        /// Check if text is too short, numeric, or otherwise invalid to be a real dialogue choice.
        /// </summary>
        private bool IsValidAnswer(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2)
                return false;

            // Count letters vs non-letters
            int letters = 0;
            int total = 0;
            foreach (char c in text)
            {
                if (char.IsLetter(c)) letters++;
                total++;
            }

            // Must have at least some letters
            if (letters < 2) return false;

            // Mostly non-letters means it's probably game UI
            if (total > 0 && (double)letters / total < 0.4) return false;

            return true;
        }
    }
}
