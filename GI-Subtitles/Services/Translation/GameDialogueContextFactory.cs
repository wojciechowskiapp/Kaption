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
                    return new NormalizedDialogueContext(expectedGame: normalized);

                default:
                    // Fail-closed: pass the unknown name as the expected
                    // game so ValidateBundleMeta sees a mismatch and refuses
                    // to load. User notices "prediction offline" + Error log
                    // rather than silently losing the cross-game gate.
                    Logger.Log.Error(
                        $"Unknown game '{game}' — using unrecognized identifier as expected bundle game; " +
                        "the bundle-meta gate will refuse any real bundle. " +
                        "Fix Config[\"Game\"] to one of: genshin, starrail.");
                    return new NormalizedDialogueContext(expectedGame: normalized);
            }
        }
    }
}
