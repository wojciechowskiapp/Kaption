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
        private const int CurrentVersion = 2;

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

                // Future migrations go here:
                //   case 3: Migration3_SomethingElse(); return;

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
    }
}
