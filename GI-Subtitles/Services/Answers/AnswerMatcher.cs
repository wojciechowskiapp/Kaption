using System;
using System.Collections.Generic;
using GI_Subtitles.Services.Translation;

namespace GI_Subtitles.Services.Answers
{
    /// <summary>Which rule found an option's line (design section 5); <see cref="None"/> shows a dash.</summary>
    public enum AnswerTier
    {
        None,
        /// <summary>One of the few options the dialogue graph expects after the current line.</summary>
        Graph,
        /// <summary>Exactly an option line elsewhere in the graph, all such lines with one Polish.</summary>
        OptionExact,
        /// <summary>Exactly a pack key, for a game whose graph marks no options.</summary>
        PackExact,
    }

    /// <summary>
    /// An option's match. <see cref="Key"/> identifies the option for the display (the graph node, else the English
    /// line); <see cref="Translation"/> is null when the row shows a dash.
    /// </summary>
    public readonly record struct AnswerMatch(AnswerTier Tier, string Key, string EnText, string Translation)
    {
        public bool IsDash => string.IsNullOrEmpty(Translation);
    }

    /// <summary>
    /// Matches the player's dialogue choices exactly or not at all: fuzzy only against the handful of options the
    /// graph expects for the current line, otherwise exact, otherwise a dash. Never the English, never the nearest
    /// line of the whole pack. Runs on the answers read, off the UI thread; its candidate loop does not allocate.
    /// </summary>
    public sealed class AnswerMatcher
    {
        // Fuzzy acceptance (design section 5): at most 15% of the option's length, and the next option 3 further.
        private const float FuzzyShare = 0.15f;
        private const float RunnerUpMargin = 3f;
        // Up to this many letters an option is "short": one misread letter at most, every other option 3 away.
        private const int ShortOption = 4;
        private const int StackCandidates = 32;

        private readonly IGameDialogueContext _context;
        private readonly IReadOnlyList<string> _optionRoles;
        private readonly Dictionary<string, string> _translations;
        private readonly OptimizedMatcher _pack;

        /// <param name="optionRoles">desktop.answers optionRoles. Empty: the graph marks no options, so the pack's
        /// exact keys are the only source (tier 3).</param>
        public AnswerMatcher(IGameDialogueContext context, IReadOnlyList<string> optionRoles,
            Dictionary<string, string> translations, OptimizedMatcher pack)
        {
            _context = context;
            _optionRoles = optionRoles ?? Array.Empty<string>();
            _translations = translations;
            _pack = pack;
        }

        /// <summary>
        /// One match per row, in the rows' order; an unread row is a dash. Two rows that land on one option both
        /// become dashes: one of the reads is wrong (a lost "?" reads "Okay?" as "Okay") and nothing says which.
        /// </summary>
        public AnswerMatch[] MatchAll(AnswerRowSet rows, IReadOnlyList<AnswerCandidate> candidates)
        {
            var list = rows?.Rows ?? Array.Empty<AnswerRow>();
            var matches = new AnswerMatch[list.Count];
            for (int i = 0; i < list.Count; i++)
                matches[i] = list[i].IsUnread ? default : Match(list[i].Text, candidates);

            var claimed = new bool[matches.Length];
            for (int i = 0; i < matches.Length; i++)
            {
                if (matches[i].Key == null) continue;
                for (int j = i + 1; j < matches.Length; j++)
                    if (string.Equals(matches[i].Key, matches[j].Key, StringComparison.Ordinal))
                        claimed[i] = claimed[j] = true;
            }
            for (int i = 0; i < matches.Length; i++)
                if (claimed[i]) matches[i] = default;
            return matches;
        }

        /// <summary>The line of one option read as <paramref name="rowText"/>, or a dash.</summary>
        public AnswerMatch Match(string rowText, IReadOnlyList<AnswerCandidate> candidates)
        {
            string text = AnswerRows.CleanText(rowText);
            string norm = OptimizedMatcher.NormalizeInput(text, true);
            if (norm.Length == 0) return default;

            if (_optionRoles.Count == 0)
            {
                return _pack != null && _pack.TryGetNormalizedUnique(text, out string key, out string value)
                    ? new AnswerMatch(AnswerTier.PackExact, key, key, value)
                    : default;
            }

            if (candidates is { Count: > 0 } && TryGraph(text, norm, candidates, out var graph))
                return new AnswerMatch(AnswerTier.Graph, "n:" + graph.NodeId, graph.EnText, graph.Translation);

            // The graph lost its place, or a menu follows a menu: only an exact option line will do.
            var lines = _context?.GetOptionLinesByText(text, _optionRoles, _translations);
            if (lines is { Count: > 0 } && TryPickAlike(text, norm, lines, out var line))
                return new AnswerMatch(AnswerTier.OptionExact, line.EnText, line.EnText, line.Translation);
            return default;
        }

        private static bool TryGraph(string text, string norm, IReadOnlyList<AnswerCandidate> candidates, out AnswerCandidate pick)
        {
            pick = default;
            int count = candidates.Count;
            bool anyEqual = false;
            int longest = 0;
            for (int i = 0; i < count; i++)
            {
                string c = candidates[i].Normalized;
                if (string.IsNullOrEmpty(c)) continue;
                if (string.Equals(c, norm, StringComparison.Ordinal)) anyEqual = true;
                if (c.Length > longest) longest = c.Length;
            }
            // Equal text decides alone: alike options ("Okay", "Okay?") are told apart by their marks, or not at all.
            if (anyEqual) return TryPickAlike(text, norm, candidates, out pick);
            if (longest == 0) return false;

            float limit = MathF.Max(1f, FuzzyShare * longest) + RunnerUpMargin;
            Span<float> distances = count <= StackCandidates ? stackalloc float[StackCandidates] : new float[count];
            int best = -1;
            for (int i = 0; i < count; i++)
            {
                string c = candidates[i].Normalized;
                distances[i] = string.IsNullOrEmpty(c) ? float.MaxValue : OcrWeightedDistance.Calculate(norm, c, limit);
                if (best < 0 || distances[i] < distances[best]) best = i;
            }

            float runnerUp = float.MaxValue;
            for (int i = 0; i < count; i++)
                if (i != best && distances[i] < runnerUp) runnerUp = distances[i];

            int length = candidates[best].Normalized.Length;
            bool accept = length <= ShortOption
                ? distances[best] <= 1f && runnerUp >= RunnerUpMargin
                : distances[best] <= MathF.Max(1f, FuzzyShare * length) && runnerUp - distances[best] >= RunnerUpMargin;
            if (accept) pick = candidates[best];
            return accept;
        }

        /// <summary>
        /// Among lines that normalise like the row: those whose own text is the row's, else all of them; they must
        /// carry one Polish (a line repeated in the graph does), or the row is a dash.
        /// </summary>
        private static bool TryPickAlike(string text, string norm, IReadOnlyList<AnswerCandidate> lines, out AnswerCandidate pick)
        {
            pick = default;
            int first = -1, surface = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!string.Equals(lines[i].Normalized, norm, StringComparison.Ordinal)) continue;
                if (first < 0) first = i;
                if (surface < 0 && OptimizedMatcher.SameSurfaceText(lines[i].EnText, text)) surface = i;
            }
            if (first < 0) return false;

            bool bySurface = surface >= 0;
            int chosen = bySurface ? surface : first;
            for (int i = chosen + 1; i < lines.Count; i++)
            {
                if (!string.Equals(lines[i].Normalized, norm, StringComparison.Ordinal)) continue;
                if (bySurface && !OptimizedMatcher.SameSurfaceText(lines[i].EnText, text)) continue;
                if (!string.Equals(lines[i].Translation, lines[chosen].Translation, StringComparison.Ordinal)) return false;
            }
            pick = lines[chosen];
            return true;
        }
    }
}
