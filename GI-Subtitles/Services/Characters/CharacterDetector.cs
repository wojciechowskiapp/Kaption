using System;
using System.Collections.Generic;
using System.Linq;
using GI_Subtitles.Services.Translation;

namespace GI_Subtitles.Services.Characters
{
    /// <summary>
    /// Recognises which character a quick-translate selection shows, from skill titles
    /// (weight 2) and the character name (weight 1). Titles shared by several characters are
    /// ignored; the winner needs at least 2 points and twice the runner-up's score.
    /// </summary>
    public sealed class CharacterDetector
    {
        private const int SkillWeight = 2;
        private const int NameWeight = 1;
        private const int MinKeyLength = 4;
        private const int MinFuzzyLength = 8;

        private readonly Dictionary<string, CharacterRecord> _skills = new Dictionary<string, CharacterRecord>(StringComparer.Ordinal);
        private readonly Dictionary<string, CharacterRecord> _names = new Dictionary<string, CharacterRecord>(StringComparer.Ordinal);

        public CharacterDetector(CharacterCatalog catalog)
        {
            if (catalog == null) return;
            var skillOwners = new Dictionary<string, HashSet<CharacterRecord>>(StringComparer.Ordinal);
            var nameOwners = new Dictionary<string, HashSet<CharacterRecord>>(StringComparer.Ordinal);
            foreach (var c in catalog.Characters)
            {
                foreach (var s in c.Skills)
                    foreach (string key in Keys(s.Name))
                        Add(skillOwners, key, c);
                foreach (string key in Keys(c.Name))
                    Add(nameOwners, key, c);
            }
            foreach (var kv in skillOwners.Where(kv => kv.Value.Count == 1)) _skills[kv.Key] = kv.Value.First();
            foreach (var kv in nameOwners.Where(kv => kv.Value.Count == 1)) _names[kv.Key] = kv.Value.First();
        }

        public CharacterRecord Detect(IReadOnlyList<string> lines)
        {
            if (lines == null || lines.Count == 0 || (_skills.Count == 0 && _names.Count == 0)) return null;

            var scores = new Dictionary<CharacterRecord, int>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in lines)
            {
                var keys = Keys(line).ToList();
                if (keys.Count == 0 || !seen.Add(keys[0])) continue;

                if (TryExact(_skills, keys, out var hit) || TryFuzzy(keys, out hit))
                    Bump(scores, hit, SkillWeight);
                else if (TryExact(_names, keys, out hit))
                    Bump(scores, hit, NameWeight);
            }

            if (scores.Count == 0) return null;
            var ranked = scores.OrderByDescending(kv => kv.Value).ToList();
            int best = ranked[0].Value;
            int runnerUp = ranked.Count > 1 ? ranked[1].Value : 0;
            return best >= SkillWeight && best >= 2 * runnerUp ? ranked[0].Key : null;
        }

        /// <summary>Normalized full text, plus the part after a short "Kind:" prefix.</summary>
        private static IEnumerable<string> Keys(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            string full = OptimizedMatcher.NormalizeInput(text, true);
            if (full.Length >= MinKeyLength) yield return full;

            int colon = text.IndexOf(':');
            if (colon > 0 && colon <= 32 && colon < text.Length - 1)
            {
                string tail = OptimizedMatcher.NormalizeInput(text.Substring(colon + 1), true);
                if (tail.Length >= MinKeyLength && tail != full) yield return tail;
            }
        }

        private static bool TryExact(Dictionary<string, CharacterRecord> index, List<string> keys, out CharacterRecord hit)
        {
            foreach (string k in keys)
                if (index.TryGetValue(k, out hit)) return true;
            hit = null;
            return false;
        }

        // OCR misreads ("Arr0w"): allow ~10% weighted edits on longer titles only.
        private bool TryFuzzy(List<string> keys, out CharacterRecord hit)
        {
            hit = null;
            float bestD = float.MaxValue;
            foreach (string k in keys)
            {
                if (k.Length < MinFuzzyLength) continue;
                float limit = Math.Max(1f, k.Length * 0.1f);
                foreach (var kv in _skills)
                {
                    if (Math.Abs(kv.Key.Length - k.Length) > 2) continue;
                    float d = OcrWeightedDistance.Calculate(k.AsSpan(), kv.Key.AsSpan(), limit);
                    if (d <= limit && d < bestD)
                    {
                        bestD = d;
                        hit = kv.Value;
                    }
                }
            }
            return hit != null;
        }

        private static void Add(Dictionary<string, HashSet<CharacterRecord>> owners, string key, CharacterRecord c)
        {
            if (!owners.TryGetValue(key, out var set)) owners[key] = set = new HashSet<CharacterRecord>();
            set.Add(c);
        }

        private static void Bump(Dictionary<CharacterRecord, int> scores, CharacterRecord c, int by) =>
            scores[c] = scores.TryGetValue(c, out int v) ? v + by : by;
    }
}
