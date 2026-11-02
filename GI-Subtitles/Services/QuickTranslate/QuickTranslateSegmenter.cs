using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GI_Subtitles.Services.Text;
using GI_Subtitles.Services.Translation;

namespace GI_Subtitles.Services.QuickTranslate
{
    public sealed class QuickTranslateSegment
    {
        public string Source { get; }
        public string EnglishKey { get; }
        public string Translation { get; }
        public bool IsMatched { get; }

        public QuickTranslateSegment(string source, string englishKey, string translation, bool isMatched)
        {
            Source = source ?? "";
            EnglishKey = englishKey ?? "";
            Translation = translation ?? "";
            IsMatched = isMatched;
        }

        public static QuickTranslateSegment Unmatched(string source) => new QuickTranslateSegment(source, "", "", false);
    }

    /// <summary>One matcher answer; Ambiguous = several keys continue the text, so read more.</summary>
    public readonly struct MatchProbe
    {
        public string Value { get; }
        public string Key { get; }
        public bool Ambiguous { get; }

        public MatchProbe(string value, string key, bool ambiguous)
        {
            Value = value;
            Key = key;
            Ambiguous = ambiguous;
        }
    }

    /// <summary>
    /// Splits a quick-translate selection into dictionary entries. A skill panel is a title,
    /// labels and a long description; matching it as one string returned only the title
    /// (a shorter key that prefixes the input scores distance 0). Each line is anchored to the
    /// start of a key instead, and a partially matched key is extended line by line.
    /// </summary>
    public static class QuickTranslateSegmenter
    {
        public const int MaxLines = 200;
        public const int MaxChars = 6000;
        public const int MaxLeadLines = 12;
        public static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

        private const int ExactBelow = 6;
        private const double MatchTolerance = 0.25;
        private const double AlignTolerance = 0.30;
        private const double CompleteCoverage = 0.9;
        private const int MaxSpanChars = 2000;

        public static IReadOnlyList<string> OrderLines(IEnumerable<(string text, float top, float left, float height)> blocks)
        {
            if (blocks == null) return Array.Empty<string>();
            var items = blocks.Where(b => !string.IsNullOrWhiteSpace(b.text)).OrderBy(b => b.top).ToList();
            if (items.Count == 0) return Array.Empty<string>();

            var heights = items.Select(b => b.height).Where(h => h > 0).OrderBy(h => h).ToList();
            float median = heights.Count > 0 ? heights[heights.Count / 2] : 1f;
            float tolerance = 0.4f * median;

            var rows = new List<List<(string text, float top, float left, float height)>>();
            float rowTop = float.NaN;
            foreach (var b in items)
            {
                if (rows.Count == 0 || Math.Abs(b.top - rowTop) > tolerance)
                {
                    rows.Add(new List<(string, float, float, float)>());
                    rowTop = b.top;
                }
                rows[rows.Count - 1].Add(b);
            }
            return rows.Select(r => string.Join(" ", r.OrderBy(b => b.left).Select(b => b.text.Trim()))).ToList();
        }

        /// <summary>The probe production and tests share: the live matcher, null-safe.</summary>
        public static Func<string, MatchProbe> ProbeWith(OptimizedMatcher matcher) => text =>
        {
            if (matcher == null) return default;
            string value = matcher.FindClosestMatch(text, out string key, out _, out bool ambiguous);
            return new MatchProbe(value, key, ambiguous);
        };

        public static IReadOnlyList<QuickTranslateSegment> Segment(
            IReadOnlyList<string> lines, Func<string, MatchProbe> match)
        {
            var result = new List<QuickTranslateSegment>();
            if (lines == null || lines.Count == 0 || match == null) return result;

            var raw = new List<string>();
            var norm = new List<string>();
            int chars = 0;
            foreach (string line in lines)
            {
                if (raw.Count >= MaxLines || chars >= MaxChars) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                string n = OptimizedMatcher.NormalizeInput(line, true);
                if (n.Length == 0) continue; // bullets, dashes and other symbol-only noise
                raw.Add(line.Trim());
                norm.Add(n);
                chars += line.Length;
            }

            var unmatched = new List<string>();
            var clock = Stopwatch.StartNew();
            int i = 0;
            while (i < raw.Count)
            {
                if (clock.Elapsed > Budget)
                {
                    unmatched.AddRange(raw.Skip(i));
                    break;
                }

                var seg = TryMatchAt(raw, norm, i, match, out int end);
                if (seg == null)
                {
                    unmatched.Add(raw[i]);
                    i++;
                    continue;
                }
                Flush(unmatched, result);
                result.Add(seg);
                i = end;
            }
            Flush(unmatched, result);
            return result;
        }

        private sealed class Candidate
        {
            public string Key;
            public string Value;
            public string NormKey;
            public int Covered;
            public int End;
            /// <summary>The key literally continues the selection, so the matcher proved it unique.</summary>
            public bool ExactPrefix;
            public bool NeedsMore => Covered < CompleteCoverage * NormKey.Length;
        }

        private static QuickTranslateSegment TryMatchAt(
            List<string> raw, List<string> norm, int start, Func<string, MatchProbe> match, out int end)
        {
            end = start;
            Candidate best = null;

            // Text that prefixes several keys is ambiguous; joining the next lines settles it.
            for (int k = 1; k <= MaxLeadLines && start + k <= raw.Count; k++)
            {
                best = Evaluate(raw, norm, start, start + k, match, out bool ambiguous);
                if (best != null || !ambiguous) break;
            }
            if (best == null) return null;

            // A complete short key ("Normal Attack") can be the head of longer keys that only
            // become unique several lines later (Genshin Normal Attack panels).
            if (!best.NeedsMore)
            {
                for (int e = best.End + 1; e <= raw.Count && e - start <= MaxLeadLines; e++)
                {
                    var longer = Evaluate(raw, norm, start, e, match, out bool ambiguous);
                    if (longer != null && longer.NormKey.Length > best.NormKey.Length)
                    {
                        best = longer;
                        break;
                    }
                    if (!ambiguous) break;
                }
            }

            int stop = best.End;
            int offset = best.Covered;
            while (best.NeedsMore && stop < raw.Count && TryAlign(norm[stop], best.NormKey, ref offset))
            {
                best.Covered = offset;
                stop++;
            }

            // A fuzzy pick among keys sharing an opening is a guess until the rest of the key is seen.
            if (!best.ExactPrefix && best.NeedsMore) return null;

            end = stop;
            string source = string.Join("\n", raw.GetRange(start, stop - start));
            return new QuickTranslateSegment(source, best.Key, Finish(best.Value, best.Key, source), true);
        }

        private static Candidate Evaluate(
            List<string> raw, List<string> norm, int start, int end, Func<string, MatchProbe> match, out bool ambiguous)
        {
            ambiguous = false;
            if (end - start > 1 && SpanLength(norm, start, end) > MaxSpanChars) return null;
            string span = string.Join("\n", raw.GetRange(start, end - start));
            string normSpan = string.Concat(norm.GetRange(start, end - start));

            MatchProbe r;
            try { r = match(span); }
            catch { return null; }
            ambiguous = r.Ambiguous;
            if (string.IsNullOrEmpty(r.Value) || string.IsNullOrEmpty(r.Key)) return null;

            string normKey = OptimizedMatcher.NormalizeInput(r.Key, true);
            if (normKey.Length == 0) return null;

            // The selection must start where the key starts; junk before a key is not part of it.
            string first = norm[start];
            if (!Close(Distance(first, Prefix(normKey, first.Length)), first.Length)) return null;

            bool keyLonger = normKey.Length >= normSpan.Length;
            float d = Distance(normSpan, keyLonger ? Prefix(normKey, normSpan.Length) : normKey);
            if (!Close(d, normSpan.Length)) return null;

            return new Candidate
            {
                Key = r.Key,
                Value = r.Value,
                ExactPrefix = normKey.StartsWith(normSpan, StringComparison.Ordinal),
                NormKey = normKey,
                Covered = Math.Min(normSpan.Length, normKey.Length),
                End = end,
            };
        }

        /// <summary>Finds the next line inside the key near the expected offset; advances the offset.</summary>
        private static bool TryAlign(string normLine, string normKey, ref int offset)
        {
            int len = normLine.Length;
            if (len == 0) return true;
            int slack = Math.Max(8, len / 3);
            int from = Math.Max(0, offset - slack);
            int to = Math.Min(normKey.Length - 1, offset + slack);

            float bestD = float.MaxValue;
            int bestEnd = -1;
            for (int p = from; p <= to; p++)
            {
                int l = Math.Min(len, normKey.Length - p);
                if (l < len * 0.6) break;
                float limit = (float)Math.Max(1, AlignTolerance * len);
                float d = OcrWeightedDistance.Calculate(normLine.AsSpan(0, l), normKey.AsSpan(p, l), limit) + (len - l);
                if (d < bestD)
                {
                    bestD = d;
                    bestEnd = p + l;
                }
            }

            bool ok = len < ExactBelow ? bestD == 0 : bestD <= Math.Max(1, AlignTolerance * len);
            if (ok) offset = bestEnd;
            return ok;
        }

        private static string Finish(string value, string key, string source)
        {
            string t = GameTextFormatter.StripMarkup(value.Replace("\\n", "\n"));
            return GameTextFormatter.HasTokens(t) ? GameTextFormatter.FillFromSource(key, t, source) : t;
        }

        private static void Flush(List<string> unmatched, List<QuickTranslateSegment> result)
        {
            if (unmatched.Count == 0) return;
            result.Add(QuickTranslateSegment.Unmatched(string.Join("\n", unmatched)));
            unmatched.Clear();
        }

        private static int SpanLength(List<string> norm, int start, int end)
        {
            int n = 0;
            for (int i = start; i < end; i++) n += norm[i].Length;
            return n;
        }

        private static string Prefix(string s, int len) => s.Length <= len ? s : s.Substring(0, len);

        private static float Distance(string a, string b)
        {
            float limit = Math.Max(a.Length, b.Length);
            return OcrWeightedDistance.Calculate(a.AsSpan(), b.AsSpan(), limit);
        }

        private static bool Close(float d, int len) =>
            len < ExactBelow ? d == 0 : d <= Math.Max(1, MatchTolerance * len);
    }
}
