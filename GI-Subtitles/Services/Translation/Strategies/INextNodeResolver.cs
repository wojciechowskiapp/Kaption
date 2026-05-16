using System;

namespace GI_Subtitles.Services.Translation.Strategies
{
    /// <summary>
    /// Strategy: resolve "given this dialog node, what are its forward edges?"
    /// Default implementation reads <see cref="DialogNode.NextDialogIds"/>
    /// verbatim — the shape Genshin and HSR both ship today. A per-game
    /// override could bolt on conditional filtering (quest state, character
    /// unlock flags, …) once we start plumbing that metadata through.
    /// </summary>
    public interface INextNodeResolver
    {
        /// <summary>
        /// Return the forward-edge ids for <paramref name="nodeId"/>, or an
        /// empty array when no successor exists. Never returns null.
        /// </summary>
        long[] Resolve(long nodeId, IDialogueGraphAccessor graph);
    }

    /// <summary>Default: straight passthrough of <see cref="DialogNode.NextDialogIds"/>.</summary>
    public sealed class GraphNextResolver : INextNodeResolver
    {
        public long[] Resolve(long nodeId, IDialogueGraphAccessor graph)
        {
            if (graph == null) return Array.Empty<long>();
            return graph.TryGetNode(nodeId, out var node) && node.NextDialogIds != null
                ? node.NextDialogIds
                : Array.Empty<long>();
        }
    }
}
