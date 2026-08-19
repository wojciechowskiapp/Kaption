using System;
using System.Reflection;
using System.Threading;
using GI_Subtitles.Common;
using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Services.OCR
{
    /// <summary>
    /// Persistent "DirectML cannot run OCR fast enough on this machine" flag.
    ///
    /// Runtime recovery (MainWindow.ScheduleOcrRuntimeRecovery) and the
    /// warm-up CPU retry (SettingsWindow.LoadEngine) were both session-local:
    /// they fixed the running process and forgot everything on exit, so every
    /// launch re-rolled the DirectML dice. On the 2026-08-17 field report that
    /// meant the user saw working subtitles exactly once — the launch where
    /// warm-up happened to time out — and a pegged GPU with no subtitles on
    /// every other launch.
    ///
    /// The flag is <b>version-scoped</b>: it only suppresses the GPU while the
    /// stored version matches the running build. A new release gets exactly one
    /// fresh GPU attempt, because a release may well be the thing that fixed
    /// the stall (new ONNX Runtime, new model profile, wider warm-up).
    ///
    /// It never touches <c>UseGpuOcr</c>. That key stays the user's stated
    /// preference; the quarantine is Kaption's own observation layered on top.
    /// Re-ticking the "Use GPU acceleration" box in Settings clears it (so does
    /// the Dashboard engine banner's Retry, for the case where the CPU engine
    /// failed too). Users who want a permanent CPU pin should untick that box,
    /// which is what <c>UseGpuOcr:false</c> means.
    /// </summary>
    internal static class OcrGpuQuarantine
    {
        public const string FlagKey = "OcrGpuQuarantine";
        public const string VersionKey = "OcrGpuQuarantineVersion";
        public const string ReasonKey = "OcrGpuQuarantineReason";
        public const string TimestampKey = "OcrGpuQuarantineUtc";

        // Set on the first Engage of the process, consumed once by MainWindow
        // so the tray balloon fires exactly one time per session no matter how
        // many recovery levels run.
        private static int _pendingUserNotice;

        /// <summary>
        /// Running assembly version, the value stored in
        /// <see cref="VersionKey"/>. Matches the format used by the
        /// "=== Process start" log line so support can compare them directly.
        /// </summary>
        public static string CurrentAppVersion =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

        /// <summary>
        /// Pure decision function behind <see cref="IsActiveForCurrentVersion"/>,
        /// split out so the version-scoping rule is unit-testable without
        /// touching Config.json.
        /// </summary>
        internal static bool IsActive(bool storedFlag, string storedVersion, string currentVersion)
        {
            if (!storedFlag) return false;
            if (string.IsNullOrWhiteSpace(storedVersion)) return false;
            if (string.IsNullOrWhiteSpace(currentVersion)) return false;
            return string.Equals(storedVersion.Trim(), currentVersion.Trim(), StringComparison.Ordinal);
        }

        /// <summary>
        /// True when the GPU must stay disabled for this build. Also prunes a
        /// quarantine left over from an earlier version so the next engine load
        /// gets its one fresh DirectML attempt.
        /// </summary>
        public static bool IsActiveForCurrentVersion()
        {
            bool storedFlag = Config.Get(FlagKey, false);
            if (!storedFlag) return false;

            string storedVersion = Config.Get<string>(VersionKey, null);
            string currentVersion = CurrentAppVersion;
            if (IsActive(storedFlag, storedVersion, currentVersion))
                return true;

            Logger.Log.Info(
                $"Clearing GPU OCR quarantine from version '{storedVersion ?? "(none)"}' — " +
                $"this build is {currentVersion} and gets a fresh DirectML attempt.");
            ClearKeys();
            return false;
        }

        /// <summary>Reason recorded when the quarantine was engaged, or null.</summary>
        public static string Reason => Config.Get<string>(ReasonKey, null);

        /// <summary>ISO-8601 UTC timestamp of the engagement, or null.</summary>
        public static string EngagedUtc => Config.Get<string>(TimestampKey, null);

        /// <summary>
        /// Records that DirectML is unfit on this machine for this build.
        /// Idempotent: re-engaging with the same version keeps the original
        /// timestamp so the log keeps showing when the problem first appeared.
        /// </summary>
        public static void Engage(string reason)
        {
            try
            {
                string currentVersion = CurrentAppVersion;
                bool alreadyActive = IsActive(
                    Config.Get(FlagKey, false),
                    Config.Get<string>(VersionKey, null),
                    currentVersion);

                Config.Set(FlagKey, true);
                Config.Set(VersionKey, currentVersion);
                Config.Set(ReasonKey, reason ?? "unspecified");
                if (!alreadyActive)
                {
                    Config.Set(TimestampKey, DateTime.UtcNow.ToString("o"));
                    Interlocked.Exchange(ref _pendingUserNotice, 1);
                    Logger.Log.Warn(
                        $"GPU OCR quarantined for version {currentVersion}: {reason}. " +
                        "Kaption will load the OCR engine on the CPU until the next update, " +
                        "or until \"Use GPU acceleration\" is re-ticked in Settings.");
                }
                else
                {
                    Logger.Log.Warn($"GPU OCR quarantine refreshed for version {currentVersion}: {reason}.");
                }
            }
            catch (Exception ex)
            {
                // A failed Config write must never break engine loading — we
                // simply lose the persistence and behave like the old
                // session-local fallback.
                Logger.Log.Warn($"Could not persist the GPU OCR quarantine: {ex.Message}");
            }
        }

        /// <summary>
        /// Drops the quarantine so the next engine load tries DirectML again.
        /// Called when the user explicitly asks for a retry.
        /// </summary>
        public static void Clear(string source)
        {
            try
            {
                if (!Config.Has(FlagKey) && !Config.Has(VersionKey)) return;
                ClearKeys();
                Interlocked.Exchange(ref _pendingUserNotice, 0);
                Logger.Log.Info($"GPU OCR quarantine cleared ({source}); DirectML will be attempted on the next engine load.");
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Could not clear the GPU OCR quarantine: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns true once per process, on the first engagement, so the
        /// caller can surface a single tray notice instead of one per engine
        /// rebuild.
        /// </summary>
        public static bool TryConsumeUserNotice() =>
            Interlocked.Exchange(ref _pendingUserNotice, 0) == 1;

        private static void ClearKeys()
        {
            Config.Remove(FlagKey);
            Config.Remove(VersionKey);
            Config.Remove(ReasonKey);
            Config.Remove(TimestampKey);
        }
    }
}
