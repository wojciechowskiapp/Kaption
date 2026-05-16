using System;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// Single dialogue node as read from the on-disk DialogGraph.gisub.
    /// Promoted to a top-level public struct during the 2026-04-17 refactor
    /// so strategy implementations can reference it without reaching into
    /// engine internals.
    /// </summary>
    /// <remarks>
    /// Dialog / talk / quest IDs are <see cref="long"/> (routinely exceed
    /// Int32.MaxValue in recent patches). TextMap hashes are <see cref="ulong"/>
    /// — HSR uses xxhash64 which is UNSIGNED 64-bit; ~half of all values
    /// exceed <c>Int64.MaxValue</c> and would throw on <c>Convert.ToInt64</c>.
    /// </remarks>
    public struct DialogNode
    {
        /// <summary>TextMap hash of the dialog line body (xxhash64 on HSR).</summary>
        public ulong ContentHash;

        /// <summary>TextMap hash of the speaker-name field (0 for player lines).</summary>
        public ulong NameHash;

        /// <summary>Forward edges. Empty when this is a terminal node.</summary>
        public long[] NextDialogIds;

        /// <summary>Genshin: "NPC" / "PLAYER" / "BLACK_SCREEN" / …</summary>
        public string RoleType;

        /// <summary>Stable NPC role id; joins with NpcNames.gisub.</summary>
        public string RoleId;

        /// <summary>
        /// English dialogue text pre-resolved from <see cref="ContentHash"/>
        /// against TextMapEN at graph-load time. Null when the hash didn't
        /// resolve (cross-version drift, stripped line).
        ///
        /// Phase-3 RAM win: by caching the resolved string on the node we
        /// can drop the ~90–110 MB TextMapEN dictionary after load. The
        /// node backing array keeps a reference to each string that's
        /// actually reachable from the graph (~200k entries), so the
        /// unreferenced ~400k TextMapEN strings become GC garbage.
        /// </summary>
        public string EnText;
    }

    /// <summary>
    /// A talk group — the game-engine concept that bundles a chain of dialog
    /// nodes under one conversation and optionally ties it to a quest.
    /// </summary>
    public struct TalkNode
    {
        /// <summary>First dialog node of the chain.</summary>
        public long InitDialog;

        /// <summary>Owning quest id (0 when the talk isn't quest-scoped).</summary>
        public long QuestId;

        /// <summary>Sibling / successor talk groups that may follow this one.</summary>
        public long[] NextTalks;
    }

    /// <summary>
    /// Read-only projection of the engine state that strategies are allowed
    /// to see. Keeps the strategy surface minimal: nothing about caches,
    /// chain progression, or configuration leaks through.
    /// </summary>
    public interface IDialogueGraphAccessor
    {
        /// <summary>Currently-active dialog id, or 0 when idle.</summary>
        long ActiveDialogId { get; }

        /// <summary>Look up a dialog node by id.</summary>
        bool TryGetNode(long id, out DialogNode node);

        /// <summary>Look up a talk group by id.</summary>
        bool TryGetTalkNode(long id, out TalkNode node);

        /// <summary>Resolve a role id to its English display name.</summary>
        bool TryGetNpcName(string roleId, out string displayName);

        /// <summary>Resolve a quest id to its title hash + type code.</summary>
        bool TryGetQuestInfo(long questId, out (ulong TitleHash, string QuestType) info);

        /// <summary>
        /// Resolve a TextMap hash (stringified) to the English text.
        ///
        /// Post-phase-3: the backing dictionary only carries hashes the graph
        /// itself references (QuestInfo titles primarily — node content hashes
        /// are inlined on <see cref="DialogNode.EnText"/>). Arbitrary hashes
        /// that weren't referenced at load time return false.
        /// </summary>
        bool TryGetTextMapValue(string hashStr, out string text);
    }
}
