using GI_Subtitles.Services.Translation.Strategies;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// Concrete, strategy-free dialogue context used by both Genshin and Honkai:
    /// Star Rail in the 2026-04-17 refactor. Both games ship the same
    /// DialogGraph schema (content hash + next-edge list + role id) and the
    /// same quest-info shape, so no virtual overrides are needed on either
    /// side — a factory parameter only gates optional bundle-game validation.
    ///
    /// Add a per-game subclass here only when a game needs different
    /// behaviour; don't bloat this one.
    /// </summary>
    public class NormalizedDialogueContext : DialogueContextBase
    {
        private readonly string _expectedGame;

        /// <summary>
        /// Construct with default strategies. <paramref name="expectedGame"/>
        /// gates the optional BundleMeta check; pass null to accept any bundle.
        /// </summary>
        public NormalizedDialogueContext(string expectedGame = null)
        {
            _expectedGame = expectedGame;
        }

        /// <summary>
        /// Full DI entry point — useful for unit tests that want to mock
        /// disambiguation or quest formatting.
        /// </summary>
        public NormalizedDialogueContext(
            string expectedGame,
            INextNodeResolver nextResolver,
            IQuestBannerFormatter questFmt,
            INpcNameNormalizer nameNorm,
            INpcRoleDisambiguator disambig)
            : base(nextResolver, questFmt, nameNorm, disambig)
        {
            _expectedGame = expectedGame;
        }

        /// <inheritdoc/>
        protected override string ExpectedBundleGame => _expectedGame;
    }
}
