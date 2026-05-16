using System;
using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Services.Detection
{
    /// <summary>
    /// Per-game OCR pacing resolver. Centralises the "which value do we use"
    /// decision for the OCR loop's five timing knobs so MainWindow doesn't
    /// have to inline the precedence chain at five different read sites.
    ///
    /// Resolution order (highest precedence first):
    ///   1. User-set Config.json value (<see cref="Config.Has"/> returns true).
    ///      Honoured regardless of profile — power users who hand-tuned a
    ///      value keep it across game switches.
    ///   2. Per-game profile value (non-null in <see cref="GameRegionProfile"/>).
    ///      Acts as the PER-GAME DEFAULT. Different games can recommend
    ///      different values because their text-render pacing differs.
    ///   3. Hard-coded global fallback. Last resort when a new game is added
    ///      without tuning values and the user has never touched Config.
    ///
    /// Why this matters: Genshin's typewriter pacing and HSR's instant-render
    /// pacing demand different stability windows. A single global default
    /// either leaves HSR users waiting 500 ms through empty-stability frames
    /// (lines disappear before OCR fires) or leaves Genshin users flickering
    /// on mid-typewriter frames. Per-game profiles thread the needle.
    ///
    /// Call these accessors once per OCR tick; each reads Config + profile
    /// under a lock. Cost is a dictionary lookup + a Config lock acquire,
    /// well below the tick budget.
    /// </summary>
    public static class GameOcrTuning
    {
        /// <summary>Minimum ms between OCR runs for the current game.</summary>
        public static int OcrIntervalMs() =>
            ResolveInt("OcrInterval", p => p.OcrIntervalMs, 100);

        /// <summary>Window size (in ticks) for the "stable over window" check.</summary>
        public static int StabilityWindow() =>
            ResolveInt("StabilityWindow", p => p.StabilityWindowFrames, 5);

        /// <summary>Consecutive stable frames needed when chain prediction is active.</summary>
        public static int StableFramesChain() =>
            ResolveInt("StableFramesChain", p => p.StableFramesChain, 2);

        /// <summary>Consecutive stable frames needed when no chain prediction is active.</summary>
        public static int StableFramesDefault() =>
            ResolveInt("StableFramesDefault", p => p.StableFramesDefault, 3);

        /// <summary>Seconds after which we force an OCR re-check when the screen
        /// keeps changing without ever stabilising.</summary>
        public static double ForceOcrAfterSeconds() =>
            ResolveDouble("ForceOcrAfterSeconds", p => p.ForceOcrAfterSeconds, 1.0);

        private static int ResolveInt(
            string configKey,
            Func<GameRegionProfile, int?> profileField,
            int globalFallback)
        {
            // User override wins. Skip profile if the key is explicitly set.
            if (Config.Has(configKey))
                return Config.Get<int>(configKey, globalFallback);

            var profile = GameRegionProfile.Get(Config.Get<string>("Game", "Genshin"));
            return profileField(profile) ?? globalFallback;
        }

        private static double ResolveDouble(
            string configKey,
            Func<GameRegionProfile, double?> profileField,
            double globalFallback)
        {
            if (Config.Has(configKey))
                return Config.Get<double>(configKey, globalFallback);

            var profile = GameRegionProfile.Get(Config.Get<string>("Game", "Genshin"));
            return profileField(profile) ?? globalFallback;
        }
    }
}
