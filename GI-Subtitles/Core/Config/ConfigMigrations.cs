using GI_Subtitles.Common;

namespace GI_Subtitles.Core.Config
{
    /// <summary>
    /// Versioned, one-shot Config.json migrations. Each migration runs at most
    /// once per install — <see cref="CurrentVersion"/> is the tip, and the
    /// value stored under <c>ConfigMigrationVersion</c> in Config.json is the
    /// level the user's machine is already at. Called early in
    /// <c>App.OnStartup</c>, after the Config static ctor has loaded the
    /// stored settings but before any UI reads them.
    ///
    /// Why not just change the default in code? Defaults only affect installs
    /// that have NEVER written the key. Users who've been running Kaption
    /// since before the default changed have the old value persisted and
    /// would silently keep it forever — a migration step is the only way to
    /// move them across without forcing them to open Settings.
    ///
    /// Adding a new migration:
    ///   1. Bump <see cref="CurrentVersion"/>.
    ///   2. Add a <c>case N:</c> in <see cref="RunAll"/> that performs the
    ///      change. Keep each case idempotent (safe to re-run). If a user
    ///      already has the new value, the case should be a no-op.
    ///   3. Prefer "only touch stale old default" guards over blanket
    ///      rewrites — don't clobber a setting the user explicitly tuned.
    /// </summary>
    public static class ConfigMigrations
    {
        /// <summary>Current migration tip. Bump when adding a new case.</summary>
        private const int CurrentVersion = 4;

        private const string VersionKey = "ConfigMigrationVersion";

        /// <summary>
        /// Run every migration between the stored version (exclusive) and
        /// <see cref="CurrentVersion"/> (inclusive). Safe to call on every
        /// launch — the version gate makes repeat runs a no-op.
        /// </summary>
        public static void RunAll()
        {
            int storedVersion;
            try
            {
                storedVersion = Config.Get<int>(VersionKey, 0);
            }
            catch
            {
                // A corrupt ConfigMigrationVersion (e.g. hand-edited to a
                // non-int) shouldn't hard-fail startup — re-run from zero.
                storedVersion = 0;
            }

            if (storedVersion >= CurrentVersion) return;

            Logger.Log.Info(
                $"ConfigMigrations: running {storedVersion + 1}..{CurrentVersion}");

            for (int v = storedVersion + 1; v <= CurrentVersion; v++)
            {
                try
                {
                    ApplyMigration(v);
                    Config.Set(VersionKey, v);
                }
                catch (System.Exception ex)
                {
                    // Don't block startup if a migration blows up — log and
                    // leave the version pointer where it was so the next
                    // launch retries. A persistent failure is preferable to
                    // silently corrupting the user's settings.
                    Logger.Log.Warn(
                        $"ConfigMigrations: migration v{v} threw ({ex.GetType().Name}: {ex.Message}); " +
                        $"keeping stored version at {v - 1}, will retry next launch.");
                    return;
                }
            }

            Logger.Log.Info($"ConfigMigrations: up-to-date at v{CurrentVersion}.");
        }

        private static void ApplyMigration(int version)
        {
            switch (version)
            {
                case 1:
                    Migration1_OcrInterval_200_to_100();
                    return;

                case 2:
                    Migration2_OcrInterval_Defer_To_GameProfile();
                    return;

                case 3:
                    Migration3_UiRefreshInterval_200_to_150();
                    return;

                case 4:
                    Migration4_ResetPacingToMeasuredProfiles();
                    return;

                // Future migrations go here:
                //   case 5: Migration5_SomethingElse(); return;

                default:
                    Logger.Log.Warn($"ConfigMigrations: unknown version {version} — nothing to apply.");
                    return;
            }
        }

        /// <summary>
        /// OcrInterval migration (2026-04-18). Originally written to flip
        /// the old 200 ms default down to 100 ms; replaced with a REMOVE the
        /// same day after per-game OCR profiles landed in
        /// <c>GameRegionProfile</c> + <c>GameOcrTuning</c>. Leaving a stale
        /// OcrInterval key pinned in Config.json would override the per-game
        /// profile on every launch — Genshin users would get 100 ms and HSR
        /// users would also get 100 ms instead of HSR's tuned 60 ms.
        ///
        /// Removing the key (rather than rewriting it) restores the "never
        /// touched" state so the per-game profile default takes effect.
        /// Only removes when the stored value is EXACTLY the old default
        /// (200). A user who deliberately set 150/300/500 keeps that value
        /// because they explicitly tuned it for their machine and we should
        /// respect that over a per-game recommendation.
        /// </summary>
        private static void Migration1_OcrInterval_200_to_100()
        {
            const string key = "OcrInterval";
            if (!Config.Has(key))
            {
                Logger.Log.Info("ConfigMigrations v1: OcrInterval not set — per-game profile will apply.");
                return;
            }

            int current = Config.Get<int>(key, 0);
            if (current == 200)
            {
                Config.Remove(key);
                Logger.Log.Info("ConfigMigrations v1: OcrInterval=200 removed — per-game profile (Genshin=100ms, HSR=60ms) now applies.");
            }
            else
            {
                Logger.Log.Info($"ConfigMigrations v1: OcrInterval={current} is user-tuned; keeping, profile default ignored.");
            }
        }

        /// <summary>
        /// Cleanup migration (2026-04-18): catches users who already ran v1
        /// during the brief window when v1 rewrote 200 → 100 instead of
        /// removing the key. That 100 value is NOW a stale explicit pin that
        /// would override the per-game profile (Genshin 100 / HSR 60). Drop
        /// both the old (200) and intermediate (100) defaults — anything
        /// else is treated as a user-tuned value and kept.
        ///
        /// Net effect across versions:
        ///   * Fresh install:             no key → profile wins (idempotent)
        ///   * Had 200 (pre-both):        v1 removed → profile wins
        ///   * Had 100 (v1 intermediate): v2 removes → profile wins
        ///   * Had 150 (user-tuned):      both skip → 150 kept
        /// </summary>
        private static void Migration2_OcrInterval_Defer_To_GameProfile()
        {
            const string key = "OcrInterval";
            if (!Config.Has(key))
            {
                Logger.Log.Info("ConfigMigrations v2: OcrInterval not set — per-game profile already applying.");
                return;
            }

            int current = Config.Get<int>(key, 0);
            if (current == 100 || current == 200)
            {
                Config.Remove(key);
                Logger.Log.Info($"ConfigMigrations v2: OcrInterval={current} removed — per-game profile (Genshin=100ms, HSR=60ms) now applies.");
            }
            else
            {
                Logger.Log.Info($"ConfigMigrations v2: OcrInterval={current} is user-tuned; keeping.");
            }
        }

        /// <summary>
        /// UI refresh migration (2026-08-17). The old default was 200 ms;
        /// 150 ms makes already-matched subtitles feel more immediate without
        /// increasing OCR inference frequency. Only the exact legacy default
        /// is rewritten, so explicitly tuned values remain untouched. An
        /// absent key also stays absent and picks up the new code default.
        /// </summary>
        private static void Migration3_UiRefreshInterval_200_to_150()
        {
            const string key = "UiRefreshInterval";
            int? target = GetUiRefreshMigrationTarget(
                Config.Has(key),
                Config.Get<int>(key, 0));

            if (target.HasValue)
            {
                Config.Set(key, target.Value);
                Logger.Log.Info("ConfigMigrations v3: UiRefreshInterval=200 migrated to 150 ms.");
            }
            else if (Config.Has(key))
            {
                Logger.Log.Info(
                    $"ConfigMigrations v3: UiRefreshInterval={Config.Get<int>(key, 0)} is user-tuned; keeping.");
            }
            else
            {
                Logger.Log.Info("ConfigMigrations v3: UiRefreshInterval not set — new 150 ms default will apply.");
            }
        }

        internal static int? GetUiRefreshMigrationTarget(bool keyExists, int currentValue)
            => keyExists && currentValue == 200 ? 150 : null;

        /// <summary>
        /// The OCR pacing knobs, taken from the resolver that owns them rather
        /// than re-listed here. All of them outrank <c>GameRegionProfile</c> for
        /// EVERY game when present, so a knob added to
        /// <c>GameOcrTuning.ConfigKeys</c> is cleared by the pacing reset for
        /// free — a second hand-maintained copy would have gone stale silently
        /// and left the new key pinned forever.
        /// </summary>
        internal static string[] PacingKeys =>
            GI_Subtitles.Services.Detection.GameOcrTuning.ConfigKeys;

        /// <summary>
        /// Pacing reset (2026-08-20). Drops every pinned OCR pacing key so the
        /// newly measured per-game profiles apply.
        ///
        /// <para><b>This one deliberately breaks the "only touch the stale
        /// default" rule</b> that v1 and v2 followed, and the class docstring
        /// recommends. Those migrations protected hand-tuned values because the
        /// alternative was an unmeasured guess. That is no longer the case: the
        /// pacing now comes from a benchmark over 4,700 recorded frames across
        /// eight scenes and three games, validated against simulated hardware
        /// from 1x to 5x slower. A value hand-tuned against the OLD pipeline was
        /// tuned against different behaviour — OCR itself got 3.66x faster in
        /// session 40, which moved every constraint the user was tuning around.
        /// Keeping those pins would mean the people most likely to have tuned
        /// for a slow pipeline are the only ones who never get the fix.</para>
        ///
        /// <para>Consequence worth stating plainly: this DISCARDS user
        /// settings. It runs exactly once (the version gate), logs every key it
        /// removed with its value so the choice is recoverable from app.log, and
        /// the Settings > Advanced boxes can re-pin any of them. Removing rather
        /// than rewriting is what restores the "never touched" state, so future
        /// profile changes reach the user too.</para>
        /// </summary>
        private static void Migration4_ResetPacingToMeasuredProfiles()
        {
            var cleared = new System.Collections.Generic.List<string>();

            foreach (string key in PacingKeys)
            {
                if (!Config.Has(key)) continue;

                // Read as double, NOT string. All five keys hold JSON numbers,
                // and Config.Get<string> on a number throws inside Deserialize,
                // which Config.Get catches and reports via Logger.Log.Error —
                // and the Sentry appender forwards Error to GlitchTip. Asking
                // for the wrong type here would fire a crash-report event per
                // key, per user, on upgrade, and still log "OcrInterval=?".
                double value = Config.Get<double>(key, double.NaN);
                cleared.Add(double.IsNaN(value)
                    ? key
                    : $"{key}={value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}");

                Config.Remove(key);
            }

            if (cleared.Count == 0)
            {
                Logger.Log.Info(
                    "ConfigMigrations v4: no pinned OCR pacing keys — measured per-game profiles already apply.");
                return;
            }

            Logger.Log.Info(
                $"ConfigMigrations v4: cleared {cleared.Count} pinned OCR pacing key(s) " +
                $"[{string.Join(", ", cleared)}] so the measured per-game profiles apply. " +
                "Re-pin in Settings > Advanced if a machine needs different pacing.");
        }
    }
}
