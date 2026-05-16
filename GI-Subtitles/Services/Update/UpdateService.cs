// ─────────────────────────────────────────────────────────────────────────────
//  UpdateService.cs
//  ---------------------------------------------------------------------------
//  Auto-update orchestrator for the Kaption desktop app — Velopack-backed.
//
//  Flow:
//    1. CheckAsync()       → UpdateManager.CheckForUpdatesAsync()
//    2. DownloadAsync()    → UpdateManager.DownloadUpdatesAsync() (delta preferred)
//    3. VerifyAndInstall() → UpdateManager.ApplyUpdatesAndRestart()
//
//  Design notes:
//    * Hash verification, delta reconstruction, and atomic swap-in are done by
//      Velopack. We don't roll our own anything here — that was the point of
//      the migration.
//    * Source-of-truth for update metadata is a static JSON feed on R2 at
//      Config["UpdateFeedUrl"] (default: https://files.kaption.one/releases/stable/).
//      The legacy /api/app/version backend endpoint is no longer consulted.
//    * min_supported_version force-upgrade was dropped during the Velopack
//      migration — YAGNI for v2.0 launch. If we ever need it, the cleanest
//      re-add is a server-side "channel" trick (publish problem-release as
//      its own locked channel) rather than a client-side version gate.
//    * Public API (CheckAsync / ShouldNag / RememberSkipped / DownloadAsync /
//      VerifyAndInstall, plus UpdateCheckResult / UpdateStatus) is preserved
//      so MainWindow's update-banner wiring barely changed.
//    * Every failure path logs. Silent swallows are banned.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Common;
using GI_Subtitles.Core.Config;
using Velopack;

namespace GI_Subtitles.Services.Update
{
    /// <summary>
    /// Outcome of <see cref="UpdateService.CheckAsync"/>.
    /// </summary>
    public enum UpdateStatus
    {
        /// <summary>No newer version, or the check couldn't run (network, parse error).</summary>
        NoUpdate,

        /// <summary>A newer version is available — optional upgrade.</summary>
        Available,
    }

    /// <summary>
    /// Lightweight release metadata surfaced to the UI. Populated from Velopack's
    /// <see cref="Velopack.UpdateInfo"/>. Kept as a small DTO so MainWindow's
    /// banner/dialog code doesn't grow a Velopack dependency.
    /// </summary>
    public sealed class UpdateReleaseInfo
    {
        /// <summary>Target version, e.g. <c>"2.0.3"</c>.</summary>
        public string Version { get; internal set; }

        /// <summary>
        /// URL the "What's new" button opens. Currently a fragment link into
        /// the public changelog; once we host per-version release-notes, this
        /// can switch to the specific anchor.
        /// </summary>
        public string ReleaseNotesUrl { get; internal set; }
    }

    /// <summary>
    /// Snapshot of what Velopack reported plus a resolved status and versions.
    /// </summary>
    public sealed class UpdateCheckResult
    {
        public UpdateStatus Status { get; internal set; }
        public UpdateReleaseInfo Info { get; internal set; }
        public Version LocalVersion { get; internal set; }
        public Version RemoteVersion { get; internal set; }

        /// <summary>
        /// Velopack's own handle on the pending update — opaque to callers, but
        /// we have to thread it through DownloadAsync/VerifyAndInstall because
        /// UpdateManager keys off this exact object.
        /// </summary>
        internal UpdateInfo VelopackInfo { get; set; }
    }

    /// <summary>
    /// Auto-update orchestrator. Safe to construct on any thread — no side effects
    /// until a method is called. Instances are intended to be long-lived (one per
    /// app lifetime).
    /// </summary>
    public sealed class UpdateService
    {
        /// <summary>
        /// Default feed URL. Overridable via <c>Config["UpdateFeedUrl"]</c> so
        /// a staging/test build can point at a different R2 prefix without a
        /// rebuild. Trailing slash matters — Velopack appends
        /// <c>releases.{channel}.json</c> to whatever we give it.
        /// </summary>
        private const string DefaultFeedUrl = "https://files.kaption.one/releases/stable/";

        private readonly string _feedUrl;
        private UpdateManager _manager;

        public UpdateService()
        {
            _feedUrl = Config.Get("UpdateFeedUrl", DefaultFeedUrl);
            if (string.IsNullOrWhiteSpace(_feedUrl))
                _feedUrl = DefaultFeedUrl;
        }

        /// <summary>
        /// Lazily create the UpdateManager. Construction touches the Velopack
        /// locator (which walks the install directory) so we defer until the
        /// first CheckAsync call — keeps the ctor side-effect-free.
        /// </summary>
        private UpdateManager GetOrCreateManager()
        {
            if (_manager != null) return _manager;
            _manager = new UpdateManager(_feedUrl);
            return _manager;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Kick off the version check and, on any failure, log and return a
        /// <see cref="UpdateStatus.NoUpdate"/> result. Network errors are
        /// treated as a silent no-op so we don't nag offline users.
        ///
        /// When invoked from an unpackaged dev build (no Velopack install
        /// directory alongside the exe), Velopack throws; we catch and treat
        /// that as "no update available" — same user-visible outcome.
        /// </summary>
        public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
        {
            var result = new UpdateCheckResult
            {
                Status = UpdateStatus.NoUpdate,
                LocalVersion = GetLocalVersion(),
            };

            UpdateManager mgr;
            try
            {
                mgr = GetOrCreateManager();
            }
            catch (Exception ex)
            {
                // Most common cause: running from Visual Studio / bin\Release
                // without the Velopack-packaged layout. That's dev, not prod —
                // log once and move on.
                Logger.Log.Info($"Update check skipped: no Velopack install detected ({ex.Message}).");
                return result;
            }

            if (!mgr.IsInstalled)
            {
                // Portable / unpackaged launch — same story.
                Logger.Log.Info("Update check skipped: app not running from a Velopack install.");
                return result;
            }

            UpdateInfo info;
            try
            {
                info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Offline, DNS issue, 5xx, corrupt feed — log, don't surface.
                Logger.Log.Info($"Update check failed: {ex.Message}");
                return result;
            }

            if (info == null || info.TargetFullRelease == null)
            {
                Logger.Log.Info($"Update check: local {result.LocalVersion} is current.");
                return result;
            }

            result.VelopackInfo = info;
            result.RemoteVersion = TryVersion(info.TargetFullRelease.Version?.ToString());
            result.Info = new UpdateReleaseInfo
            {
                Version = info.TargetFullRelease.Version?.ToString(),
                // We don't yet host per-version release notes — link to the
                // public changelog anchored on the target version. The landing
                // page normalises "2.0.3" → "#v-2-0-3", so both formats work.
                ReleaseNotesUrl = $"https://kaption.one/changelog#v-{info.TargetFullRelease.Version}",
            };
            result.Status = UpdateStatus.Available;

            Logger.Log.Info(
                $"Update check: local {result.LocalVersion} → remote {result.RemoteVersion} available " +
                $"(delta={info.DeltasToTarget?.Length ?? 0}).");
            return result;
        }

        /// <summary>
        /// True when the caller should actually surface UI for this update. Encodes
        /// the "remind-me-later" contract: once the user dismisses a given version,
        /// we don't show the banner again for 24 h.
        /// </summary>
        public bool ShouldNag(UpdateCheckResult result)
        {
            if (result == null) return false;
            if (result.Status == UpdateStatus.NoUpdate) return false;

            string skipped = Config.Get<string>("UpdateSkippedVersion", null);
            // Explicit long default — otherwise T infers as int from the literal
            // and JToken.ToObject<int>() would truncate timestamps 68y after epoch.
            long skippedAt = Config.Get<long>("UpdateSkippedAtUnix", 0L);

            if (string.IsNullOrEmpty(skipped) || skippedAt == 0)
                return true;

            // Different version? Always nag — newer release supersedes old skip.
            if (!string.Equals(skipped, result.Info?.Version, StringComparison.Ordinal))
                return true;

            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long elapsed = nowUnix - skippedAt;
            const long oneDaySeconds = 24L * 60L * 60L;

            return elapsed >= oneDaySeconds;
        }

        /// <summary>
        /// Persist a "skip this version, remind me tomorrow" intent.
        /// </summary>
        public void RememberSkipped(UpdateCheckResult result)
        {
            if (result?.Info?.Version == null) return;
            try
            {
                Config.Set("UpdateSkippedVersion", result.Info.Version);
                Config.Set("UpdateSkippedAtUnix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                Logger.Log.Info($"Update {result.Info.Version} marked as skipped for 24h.");
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Could not persist update skip: {ex.Message}");
            }
        }

        /// <summary>
        /// Download the pending update (delta preferred, full fallback) into
        /// Velopack's staging area, reporting progress on <paramref name="progress"/>
        /// (0.0..1.0). Returns true on success; logs and returns false on failure.
        /// </summary>
        public async Task<bool> DownloadAsync(
            UpdateCheckResult result,
            IProgress<double> progress,
            CancellationToken ct)
        {
            if (result?.VelopackInfo == null)
                throw new ArgumentException("UpdateCheckResult has no pending Velopack update.", nameof(result));

            UpdateManager mgr;
            try
            {
                mgr = GetOrCreateManager();
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Download failed: update manager unavailable ({ex.Message}).");
                return false;
            }

            // Velopack's progress callback is an int in 0..100 — adapt to the
            // 0..1 double contract our UI already speaks.
            Action<int> velopackProgress = null;
            if (progress != null)
            {
                velopackProgress = pct =>
                {
                    double normalised = pct / 100.0;
                    if (normalised < 0.0) normalised = 0.0;
                    if (normalised > 1.0) normalised = 1.0;
                    progress.Report(normalised);
                };
            }

            Logger.Log.Info(
                $"Downloading update to {result.Info?.Version} via Velopack " +
                $"(delta chain={result.VelopackInfo.DeltasToTarget?.Length ?? 0}).");

            try
            {
                await mgr.DownloadUpdatesAsync(result.VelopackInfo, velopackProgress, ct)
                    .ConfigureAwait(false);
                progress?.Report(1.0);
                Logger.Log.Info($"Update {result.Info?.Version} downloaded and staged.");
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Update download failed: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Apply the staged update and restart the app. Returns true when the
        /// restart was launched successfully — the caller should
        /// <c>Application.Current.Shutdown(0)</c> immediately after (Velopack
        /// expects the old process to be gone so it can swap the on-disk files).
        ///
        /// Velopack does the hash verification internally; we never see a bad
        /// artefact at this point. A false return means the restart call itself
        /// failed (rare — permissions, antivirus, etc).
        /// </summary>
        public bool VerifyAndInstall(UpdateCheckResult result)
        {
            if (result?.VelopackInfo == null)
                throw new ArgumentException("UpdateCheckResult has no pending Velopack update.", nameof(result));

            UpdateManager mgr;
            try
            {
                mgr = GetOrCreateManager();
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Install failed: update manager unavailable ({ex.Message}).");
                return false;
            }

            try
            {
                // This is fire-and-forget from the process's perspective: it
                // spawns the Velopack updater, which waits for us to exit, then
                // swaps files and re-launches Kaption. We return true before
                // anything actually happens on disk.
                mgr.ApplyUpdatesAndRestart(result.VelopackInfo);
                Logger.Log.Info($"Update {result.Info?.Version} install+restart requested.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"ApplyUpdatesAndRestart failed: {ex}");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Internals
        // ─────────────────────────────────────────────────────────────────────

        private static Version GetLocalVersion()
        {
            try
            {
                return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                       ?? new Version(0, 0, 0, 0);
            }
            catch
            {
                return new Version(0, 0, 0, 0);
            }
        }

        /// <summary>
        /// Parse a semver-ish string into <see cref="Version"/>. Returns
        /// <c>0.0.0.0</c> on failure so callers never see null. Velopack hands
        /// us strings like <c>"2.0.3"</c> or <c>"2.0.3+abc123"</c> (build
        /// metadata suffix) — we strip the suffix before parsing.
        /// </summary>
        private static Version TryVersion(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new Version(0, 0, 0, 0);
            // Chop off pre-release (-beta.1) and build (+abc123) metadata.
            int cut = s.IndexOfAny(new[] { '-', '+' });
            if (cut > 0) s = s.Substring(0, cut);
            return Version.TryParse(s.Trim(), out var v) ? v : new Version(0, 0, 0, 0);
        }
    }
}
