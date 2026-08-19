using GI_Subtitles.Common;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// One-stop entry point for building an <see cref="IGameDialogueContext"/>
    /// for a given game. Call sites should go through the factory so the
    /// concrete type selection stays centralised — adding a new game with
    /// a custom strategy set is a single case statement here.
    /// </summary>
    public static class GameDialogueContextFactory
    {
        /// <summary>
        /// Build a context for <paramref name="game"/>.
        ///
        /// <para>Null or whitespace → "genshin" (preserves pre-multi-game behavior).</para>
        /// <para>Unknown non-empty game → fail-closed: the returned context
        /// carries the unknown value as <c>ExpectedBundleGame</c>, which
        /// causes the bundle-meta gate in <see cref="DialogueContextBase.Load"/>
        /// to fail for any real v2 bundle (declared game won't match the typo).
        /// The app keeps running — it just has no working prediction — and
        /// the error is surfaced loudly instead of silently disabling the
        /// cross-game protection.</para>
        /// </summary>
        public static IGameDialogueContext Create(string game)
        {
            string normalized = (game ?? string.Empty).Trim().ToLowerInvariant();

            // Empty / null → legacy default. Pre-multi-game config files
            // had no Game key; treat them as Genshin.
            if (string.IsNullOrEmpty(normalized))
                return new NormalizedDialogueContext(expectedGame: "genshin");

            switch (normalized)
            {
                case "genshin":
                case "starrail":
                // ZZZ joins the passthrough arm rather than getting its own
                // context type: no divergence in the bundle schema has been
                // proven yet, and a speculative ZzzDialogueContext with zero
                // overrides would be dead weight. Split it out only when the
                // extracted data actually needs different behaviour.
                case "zzz":
                    // The name normalizer is the one strategy that IS per-game
                    // today. ZZZ's cinematic style prints the speaker flanked by
                    // middots, which PaddleOCR reads as "-Remielle"; the default
                    // TrimNameNormalizer splits only on {' ', ',', '.'}, so that
                    // normalizes to "-remielle", misses every entry in the
                    // name-to-role index, and silently costs ZZZ its hot-cache
                    // preload and its disambiguation tie-breaker.
                    //
                    // Resolved through the factory for every game rather than
                    // branching here: it hands back TrimNameNormalizer for
                    // Genshin and Star Rail, which is byte-identical to the
                    // base constructor's own `?? new TrimNameNormalizer()`
                    // default. One code path, and game #4 needs no edit here.
                    // The remaining three strategies stay null = "use default".
                    return new NormalizedDialogueContext(
                        expectedGame: normalized,
                        nextResolver: null,
                        questFmt: null,
                        nameNorm: Strategies.NpcNameNormalizerFactory.Create(normalized),
                        disambig: null);

                default:
                    // Fail-closed: pass the unknown name as the expected
                    // game so ValidateBundleMeta sees a mismatch and refuses
                    // to load. User notices "prediction offline" + Error log
                    // rather than silently losing the cross-game gate.
                    Logger.Log.Error(
                        $"Unknown game '{game}' — using unrecognized identifier as expected bundle game; " +
                        "the bundle-meta gate will refuse any real bundle. " +
                        "Fix Config[\"Game\"] to one of: genshin, starrail, zzz.");
                    return new NormalizedDialogueContext(expectedGame: normalized);
            }
        }
    }
}
