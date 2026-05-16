using System;
using System.Collections.Generic;

namespace GI_Subtitles.Services.Translation.Strategies
{
    /// <summary>
    /// Strategy: given several dialog nodes that all share a ContentHash
    /// (the same English line spoken by different NPCs in different talks),
    /// pick the one that best fits the current conversation context.
    /// Critical when chain prediction loses lock and needs to re-sync.
    /// </summary>
    public interface INpcRoleDisambiguator
    {
        /// <summary>
        /// Return the best candidate id from <paramref name="candidateIds"/>.
        /// Callers treat the list as non-empty; implementations should always
        /// return one of the inputs (fallback to the first when nothing
        /// clearly wins).
        /// </summary>
        long Disambiguate(List<long> candidateIds, string detectedNpcName, IDialogueGraphAccessor graph);
    }

    /// <summary>
    /// Default: prefer candidates reachable from the current active node
    /// within 5 levels of forward traversal, falling back to the first
    /// candidate whose role name contains (or is contained in) the detected
    /// NPC name.
    /// Identical to the pre-refactor <c>DisambiguateDialog</c> method body.
    /// </summary>
    public sealed class NpcNameDisambiguator : INpcRoleDisambiguator
    {
        /// <summary>Matches the original 5-level lookahead used in the
        /// monolithic engine, which in turn mirrors the chain-cache depth.</summary>
        private const int LookaheadLevels = 5;

        public long Disambiguate(List<long> candidateIds, string detectedNpcName, IDialogueGraphAccessor graph)
        {
            if (candidateIds == null || candidateIds.Count == 0)
                return 0;
            if (candidateIds.Count == 1)
                return candidateIds[0];
            if (graph == null)
                return candidateIds[0];

            // Priority 1: prefer candidates reachable from the active dialog.
            // Walk up to LookaheadLevels deep to match chain-cache depth.
            long activeId = graph.ActiveDialogId;
            if (activeId != 0 && graph.TryGetNode(activeId, out var activeNode))
            {
                var candidateSet = new HashSet<long>(candidateIds);
                var frontier = new List<long>(activeNode.NextDialogIds ?? Array.Empty<long>());

                for (int level = 0; level < LookaheadLevels && frontier.Count > 0; level++)
                {
                    foreach (long fid in frontier)
                    {
                        if (candidateSet.Contains(fid))
                            return fid;
                    }
                    var nextFrontier = new List<long>();
                    foreach (long fid in frontier)
                    {
                        if (graph.TryGetNode(fid, out var fNode) && fNode.NextDialogIds != null)
                        {
                            foreach (long nid in fNode.NextDialogIds)
                                nextFrontier.Add(nid);
                        }
                    }
                    frontier = nextFrontier;
                }
            }

            // Priority 2: match by NPC display name (case-insensitive contains).
            if (!string.IsNullOrEmpty(detectedNpcName))
            {
                string normNpc = detectedNpcName.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(normNpc))
                {
                    foreach (long id in candidateIds)
                    {
                        if (!graph.TryGetNode(id, out var node)) continue;
                        string roleId = node.RoleId;
                        if (string.IsNullOrEmpty(roleId)) continue;
                        if (!graph.TryGetNpcName(roleId, out var name) || string.IsNullOrEmpty(name))
                            continue;

                        string nameLower = name.ToLowerInvariant();
                        if (nameLower.Contains(normNpc) || normNpc.Contains(nameLower))
                            return id;
                    }
                }
            }

            return candidateIds[0];
        }
    }
}
