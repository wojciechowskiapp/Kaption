using GI_Subtitles.Common;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// One-stop entry point for building an <see cref="IGameDialogueContext"/>
    /// for a given game. Call sites should go through the factory so the
    /// concrete type selection stays centralised. Which strategy set a game
    /// gets is its manifest's <c>desktop.dialogueContext</c>.
    /// </summary>
    public static class GameDialogueContextFactory
    {
        /// <summary>Wire id assumed when Config has no Game key at all (pre-multi-game configs).</summary>
        private const string LegacyDefaultGame = "genshin";

        /// <summary>
        /// Build a context for <paramref name="game"/>.
        ///
        /// <para>Null or whitespace → "genshin" (preserves pre-multi-game behavior).</para>
        /// <para>A registered game (by wire id, any case, padding ignored) whose manifest says
        /// <c>"dialogueContext": "passthrough"</c> gets the passthrough strategy set, so a new game
        /// needs no edit here.</para>
        /// <para>Unknown non-empty game, or a strategy this build doesn't have → fail-closed: the
        /// returned context carries the value as <c>ExpectedBundleGame</c>, which makes the bundle-meta
        /// gate in <see cref="DialogueContextBase.Load"/> refuse any real v2 bundle. The app keeps
        /// running without prediction, and the error is loud instead of silently disabling the
        /// cross-game protection.</para>
        /// </summary>
        public static IGameDialogueContext Create(string game)
        {
            string normalized = (game ?? string.Empty).Trim().ToLowerInvariant();

            // Empty / null → legacy default. Pre-multi-game config files
            // had no Game key; treat them as Genshin.
            if (string.IsNullOrEmpty(normalized))
                return new NormalizedDialogueContext(expectedGame: LegacyDefaultGame);

            var registry = GameRegistry.Current;
            GameManifest manifest = registry.FindByWireId(normalized);
            if (manifest?.Desktop?.DialogueContext == GameDesktop.Passthrough)
            {
                // The name normalizer is the one per-game strategy today (ZZZ's middot-flanked speaker
                // reads as "-Remielle"); the factory keys it off the profile, so every game shares this arm.
                return new NormalizedDialogueContext(
                    expectedGame: normalized,
                    nextResolver: null,
                    questFmt: null,
                    nameNorm: Strategies.NpcNameNormalizerFactory.Create(normalized),
                    disambig: null);
            }

            // Fail-closed: pass the value on as the expected game so ValidateBundleMeta sees a
            // mismatch and refuses to load. User notices "prediction offline" + Error log rather than
            // silently losing the cross-game gate.
            if (manifest != null)
            {
                Logger.Log.Error(
                    $"Game '{game}' asks for dialogue strategy '{manifest.Desktop?.DialogueContext}', which this build " +
                    "doesn't have; the bundle-meta gate will refuse its bundle.");
                // Its own wire id would match its bundle; ':' never appears in a wire id, so nothing matches this.
                return new NormalizedDialogueContext(expectedGame: normalized + ":unsupported-strategy");
            }
            Logger.Log.Error(
                $"Unknown game '{game}' — using unrecognized identifier as expected bundle game; " +
                "the bundle-meta gate will refuse any real bundle. " +
                $"Fix Config[\"Game\"] to one of: {string.Join(", ", registry.WireIds)}.");
            return new NormalizedDialogueContext(expectedGame: normalized);
        }
    }
}
