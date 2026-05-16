namespace GI_Subtitles.Services.Translation.Strategies
{
    /// <summary>
    /// Strategy: map a quest id to a display (title, type) pair for the
    /// overlay quest banner. The default impl reads the engine's
    /// QuestInfo index and resolves the title through TextMapEN — exactly
    /// what Genshin's current pipeline does. Per-game overrides can remap
    /// quest-type codes ("AQ" → "Archon Quest" etc.) or merge title parts.
    /// </summary>
    public interface IQuestBannerFormatter
    {
        /// <summary>Return (title, type) for <paramref name="questId"/>, or
        /// null when the id is unknown or the title hash is missing.</summary>
        (string title, string type)? Format(long questId, IDialogueGraphAccessor graph);
    }

    /// <summary>Default: resolve title via TextMapEN, forward quest-type code as-is.</summary>
    public sealed class DefaultQuestBannerFormatter : IQuestBannerFormatter
    {
        public (string title, string type)? Format(long questId, IDialogueGraphAccessor graph)
        {
            if (graph == null || questId == 0)
                return null;

            if (!graph.TryGetQuestInfo(questId, out var info))
                return null;

            if (!graph.TryGetTextMapValue(info.TitleHash.ToString(), out var title)
                || string.IsNullOrEmpty(title))
                return null;

            return (title, info.QuestType ?? string.Empty);
        }
    }
}
