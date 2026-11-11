// ─────────────────────────────────────────────────────────────────────────────
//  DictionarySyncService.cs
//  ---------------------------------------------------------------------------
//  Pulls paid translation dictionaries from the Kaption backend and stores
//  them locally as machine-bound .gisub files.
//
//  Pipeline per file:
//
//    1. GET /api/license/files?game=X&lang=Y      → list of FileMetadata (server
//                                                    has already gated on
//                                                    effective_tier)
//    2. Compare against local manifest. Skip if we already have this version.
//    3. GET /api/license/download/<id> with distribution key
//         → KaptionApiClient.DownloadFileAsync writes plaintext (AES-CBC + HMAC
//           under the global distribution key was decrypted in-place by
//           DistributionCipher).
//    4. Hand plaintext to ServerKeyFileProtectionService (via FileProtectionFactory)
//       → re-encrypt machine-bound, overwrite the cached .gisub at the canonical local path.
//    5. Wipe the plaintext temp file.
//    6. Update manifest.json with the new version + downloaded_at.
//
//  Defense-in-depth on failure:
//    * Network errors → log, continue with remaining files.
//    * SHA mismatch → DownloadFileAsync throws; we log and skip the file.
//    * Disk full / permissions → log, count as failure, don't crash.
//    * Any partially-downloaded .part file is cleaned up by DownloadFileAsync.
//
//  Threading:
//    * Caller invokes SyncAsync on a background Task — never block the UI thread.
//    * The service holds no UI references.
//
//  Where the files go (v2.0.0+, unified with public game data):
//    %APPDATA%\Kaption\<Game>\TextMap<LANG>.gisub   (machine-bound)
//    %APPDATA%\Kaption\manifest.json                (state tracker)
//
//  Path construction goes through <see cref="Services.Data.GameDataPaths"/> so
//  DictionarySync writes to the same folder VoiceContentHelper reads from.
//  Before v2.0.0 these were split across `paid-dicts\<game>\` which left the
//  downloaded files orphaned; callers that used to expect that path should
//  switch to <see cref="GameDataPaths.TextMapGisub"/>.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Data;
using GI_Subtitles.Services.Network;
using GI_Subtitles.Services.Security;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>How a <see cref="DictionarySyncService.SyncAsync"/> call ended, for the player-facing line.</summary>
    public enum DictionarySyncStatus
    {
        Ok,
        NotConfigured,
        NotSignedIn,
        KeyMissing,
        Offline,
        NothingListed,
        Failed,
    }

    /// <summary>
    /// The localized line each status shows (string key plus English fallback). <see cref="DictionarySyncResult.Messages"/>
    /// and the inventory's RemoteQueryError stay English for the log; players only ever see these. Ok shows nothing.
    /// </summary>
    public static class DictionarySyncNotice
    {
        /// <summary>
        /// The Translations banner after an inventory scan. A download that left nothing installed keeps its line
        /// across the scan that follows it; otherwise the scan's own remote state decides.
        /// </summary>
        public static (string Key, string Fallback)? ForBanner(DictionarySyncStatus? download, DictionarySyncStatus scan) =>
            (download is { } d ? For(d) : null) ?? For(scan);

        public static (string Key, string Fallback)? For(DictionarySyncStatus status) => status switch
        {
            DictionarySyncStatus.NotConfigured => ("Sync_Status_NotConfigured", "Pick a game and a language first, then try again."),
            DictionarySyncStatus.NotSignedIn => ("Sync_Status_NotSignedIn", "Sign in to your Kaption account to download translation packs."),
            DictionarySyncStatus.KeyMissing => ("Sync_Status_KeyMissing", "Your sign-in needs a refresh. Sign out, sign in again and retry."),
            DictionarySyncStatus.Offline => ("Sync_Status_Offline", "Can't reach Kaption right now. The packs you already have still work."),
            DictionarySyncStatus.NothingListed => ("Sync_Status_NothingListed", "No translation pack is out for this game and language yet. It will download on its own once it's ready."),
            DictionarySyncStatus.Failed => ("Sync_Status_Failed", "The download didn't finish. Try again in a moment."),
            _ => null,
        };
    }

    /// <summary>Outcome counters from one <see cref="DictionarySyncService.SyncAsync"/> call.</summary>
    public sealed class DictionarySyncResult
    {
        /// <summary>Set at every early return; a failed pack in the download loop makes it Failed.</summary>
        public DictionarySyncStatus Status { get; internal set; }
        public int Downloaded { get; internal set; }
        public int UpToDate { get; internal set; }
        /// <summary>A new version of a pack already installed: version rewritten, no download, no index rebuild, no restart.</summary>
        public int Relabelled { get; internal set; }
        public int Skipped { get; internal set; }     // tier-gated, etc.
        public int Failed { get; internal set; }
        public List<string> Messages { get; } = new List<string>();
        /// <summary>Installed packs whose version moved in this sync, downloaded or relabelled; never a first install.</summary>
        public List<Notices.PackVersionChange> VersionChanges { get; } = new List<Notices.PackVersionChange>();

        /// <summary>True when nothing actually went wrong — failures = 0.</summary>
        public bool Ok => Failed == 0;

        /// <summary>Every listed pack was skipped (removed or gated on download): the click got nothing, so say so.</summary>
        internal void SettleAfterDownloads()
        {
            if (Status == DictionarySyncStatus.Ok && Skipped > 0 && Downloaded + UpToDate + Relabelled == 0)
                Status = DictionarySyncStatus.NothingListed;
        }
    }

    /// <summary>
    /// Orchestrates the "paid dictionary download" loop. Stateless beyond the
    /// on-disk manifest — safe to construct multiple times. Not intended to be
    /// a long-lived singleton; instantiate in the caller's scope.
    /// </summary>
    public sealed class DictionarySyncService
    {
        // Layout on disk (v2.0.0+, unified via GameDataPaths):
        //   %APPDATA%\Kaption\<Game>\TextMap<LANG>.gisub
        //   %APPDATA%\Kaption\manifest.json
        private static string ManifestPath => GameDataPaths.ManifestFile;

        /// <summary>
        /// Historical accessor kept for callers (Translations tab "open folder").
        /// Returns the Kaption root — individual packs live under per-game
        /// subfolders, not a dedicated paid-dicts folder anymore.
        /// </summary>
        public static string PaidDictionariesRoot => GameDataPaths.Root;

        /// <summary>Path to the manifest JSON (even when the file doesn't exist yet).</summary>
        public static string ManifestFilePath => ManifestPath;

        /// <summary>The pack download in flight, for the Start page and the setup window; null when idle.</summary>
        public static DownloadProgressInfo CurrentDownload { get; private set; }

        /// <summary>Raised on a pool thread about every percent of a pack download, and once with null when it ends.</summary>
        public static event Action<DownloadProgressInfo> DownloadProgress;

        public sealed record DownloadProgressInfo(string Game, string Language, double Fraction, long TotalBytes);

        private static void PublishProgress(DownloadProgressInfo info)
        {
            CurrentDownload = info;
            try { DownloadProgress?.Invoke(info); }
            catch (Exception ex) { Logger.Log.Warn($"DictionarySync: progress listener threw: {ex.Message}"); }
        }

        /// <summary>Synchronous IProgress (no SynchronizationContext hop), throttled to whole percents.</summary>
        private sealed class PackProgress : IProgress<double>
        {
            private readonly string _game, _language;
            private readonly long _total;
            private int _lastPercent = -1;

            public PackProgress(string game, string language, long total) { _game = game; _language = language; _total = total; }

            public void Report(double value)
            {
                int percent = (int)Math.Clamp(value * 100, 0, 100);
                if (percent == _lastPercent) return;
                _lastPercent = percent;
                PublishProgress(new DownloadProgressInfo(_game, _language, Math.Clamp(value, 0, 1), _total));
            }
        }

        /// <summary>
        /// Public read-only snapshot of the on-disk manifest — the Translations
        /// tab reads this to show "last downloaded" for paid-cache entries.
        /// Returns an empty dict when the file is missing.
        /// </summary>
        public static IReadOnlyDictionary<string, ManifestEntry> GetManifestSnapshot()
        {
            return LoadManifest();
        }

        /// <summary>
        /// FileVersionId of the pack installed for (game, language), or null.
        /// GamedataSync sends it as <c>?pair=</c> so the bundle matches the pack.
        /// </summary>
        public static string GetInstalledFileVersionId(string game, string language)
            => FindFileVersionId(LoadManifest(), game, language);

        internal static string FindFileVersionId(
            IReadOnlyDictionary<string, ManifestEntry> manifest, string game, string language)
        {
            if (manifest == null || string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(language))
                return null;
            // Same shape as ManifestKey: server ids are lower-case.
            string key = $"{game.ToLowerInvariant()}/{language.ToLowerInvariant()}";
            return manifest.TryGetValue(key, out var entry) && !string.IsNullOrWhiteSpace(entry?.FileVersionId)
                ? entry.FileVersionId
                : null;
        }

        private readonly KaptionApiClient _api;
        private readonly LicenseService _license;
        private readonly IFileProtectionService _protector;
        private static readonly object _manifestLock = new object();

        /// <summary>
        /// Process-wide serialisation for <see cref="SyncAsync"/>. Two callers
        /// (App.OnStartup's initial-sync kickoff and GameDataBootstrapService's
        /// first-run bootstrap) can fire concurrently on a clean install; without
        /// this gate they would both try to write the same <c>*.plain.part</c>
        /// temp file and one would fail with <c>IOException: file in use</c>.
        /// After the first caller installs the pack and updates the manifest,
        /// the second call's version-compare path sees "up-to-date" and returns
        /// almost immediately — so the lock isn't just safety, it also avoids
        /// the duplicate download.
        /// </summary>
        private static readonly SemaphoreSlim _syncGate = new SemaphoreSlim(1, 1);

        public DictionarySyncService(
            KaptionApiClient api,
            LicenseService license,
            IFileProtectionService protector)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _license = license ?? throw new ArgumentNullException(nameof(license));
            _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        }

        /// <summary>
        /// Sync paid dictionaries for one (game, language) pair. Returns a
        /// summary with counters + any human-readable messages worth surfacing.
        ///
        /// Callable from any thread. Internally kicks off network + disk IO so
        /// the caller should await on a background Task — don't run on the UI
        /// thread.
        /// </summary>
        public async Task<DictionarySyncResult> SyncAsync(
            string game, string language, CancellationToken ct)
        {
            var result = new DictionarySyncResult();
            var syncWatch = Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(language))
            {
                Logger.Log.Info("DictionarySync: skipped — game or language not configured.");
                result.Messages.Add("Sync skipped: game or language not configured.");
                result.Status = DictionarySyncStatus.NotConfigured;
                return result;
            }

            // Acquire the process-wide gate BEFORE any network / filesystem
            // work so two overlapping callers never share a *.plain.part file.
            // The second caller through the gate re-checks the manifest at
            // the top of the listing loop and typically reports every pack as
            // UpToDate — no duplicate download, no manifest write contention.
            await _syncGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {

            // Bail early when there's no session — the API would 401 anyway,
            // and skipping the network round-trip keeps offline launches quiet.
            var session = _license.CurrentActivation;
            if (session == null || string.IsNullOrEmpty(session.DeviceSessionJwt))
            {
                Logger.Log.Info("DictionarySync: skipped — no active license session.");
                result.Messages.Add("Sync skipped: not activated.");
                result.Status = DictionarySyncStatus.NotSignedIn;
                return result;
            }

            // Log the request we're about to make so the user can correlate
            // the network activity with their own actions (e.g. tier change
            // not reflected? check the tier we sent here).
            Logger.Log.Info(
                $"DictionarySync: starting for {game}/{language} " +
                $"(tier={session.EffectiveTier ?? "?"}) " +
                $"→ {GameDataPaths.GameDir(game)}");

            // The distribution key must be present to decrypt anything we
            // download. If it's missing the server response payload changed
            // shape; ship a file-shaped error rather than silently writing
            // junk to disk.
            byte[] distKey = session.DistributionKey;
            if (distKey == null || distKey.Length != 32)
            {
                result.Messages.Add("Sync skipped: distribution key missing — re-activate to refresh.");
                result.Status = DictionarySyncStatus.KeyMissing;
                Logger.Log.Warn(
                    "DictionarySync: ActivationData.DistributionKey is null/wrong-length — " +
                    "this account was activated against an older backend. Sign out + sign in.");
                return result;
            }

            // List what the server has for this (game, lang). Tier gate is
            // already applied server-side, so anything in `items` is stuff
            // this user is entitled to.
            IReadOnlyList<FileMetadata> remoteFiles;
            try
            {
                remoteFiles = await _api.GetFilesAsync(session.DeviceSessionJwt, game, language, ct)
                    .ConfigureAwait(false);
            }
            catch (UnauthorizedException ex)
            {
                Logger.Log.Warn($"DictionarySync: file listing returned 401 — session likely revoked. {ex.Message}");
                // Notify LicenseService so the re-login dialog fires from
                // App.OnActivationStateChanged. Before this, a revocation
                // between heartbeat ticks silently 401'd every /files call
                // with zero UX feedback.
                try { _license?.ReportRemoteRevocation($"DictionarySync 401: {ex.Message}"); } catch { /* best-effort */ }
                result.Messages.Add("Sync failed: please sign in again.");
                result.Status = DictionarySyncStatus.NotSignedIn;
                result.Failed++;
                return result;
            }
            catch (ForbiddenException ex)
            {
                // 403 is NOT a session problem — the user's tier just doesn't
                // grant access to the requested files. Prior to session 26 we
                // routed this through UnauthorizedException and fired the
                // re-login dialog, which terrified free-tier users for no
                // reason. Log quietly and show a neutral message.
                Logger.Log.Info($"DictionarySync: /files returned 403 — tier-gated for this account. {ex.Message}");
                result.Messages.Add("Listing returned 403: nothing this account can download.");
                result.Status = DictionarySyncStatus.NothingListed;
                return result;
            }
            catch (ApiUnavailableException ex)
            {
                Logger.Log.Warn($"DictionarySync: network unavailable: {ex.Message}");
                result.Messages.Add("Sync failed: offline. Cached dictionaries still work.");
                result.Status = DictionarySyncStatus.Offline;
                result.Failed++;
                return result;
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"DictionarySync: unexpected listing failure: {ex}");
                result.Messages.Add("Sync failed: " + ex.Message);
                result.Status = DictionarySyncStatus.Failed;
                result.Failed++;
                return result;
            }

            // A beta game with nothing listed yet: its first pack is on the way (PackAvailability).
            PackAvailability.Record(game, language, remoteFiles?.Count ?? 0);

            if (remoteFiles == null || remoteFiles.Count == 0)
            {
                // This log line is LOUD intentionally — users kept asking
                // "why do I have Polish files with no download log?". Answer:
                // the server said there was nothing to fetch (usually because
                // the account is on free_beta and paid packs require a pro
                // tier). The files they see were built locally from the
                // bundled TextMap JSON seeds, not downloaded. See
                // DictionaryInventoryService for the full provenance.
                Logger.Log.Info(
                    $"DictionarySync: server returned no files for {game}/{language} " +
                    $"(tier={session.EffectiveTier ?? "?"}). " +
                    "Existing local packs come from the bundled seed data, not the backend.");
                result.Messages.Add("No packs listed for this game and language yet.");
                result.Status = DictionarySyncStatus.NothingListed;
                return result;
            }

            Logger.Log.Info(
                $"DictionarySync: server returned {remoteFiles.Count} file(s) for {game}/{language}.");

            GameDataPaths.EnsureRoot();
            var manifest = LoadManifest();

            foreach (var meta in remoteFiles)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    await SyncOneAsync(meta, distKey, manifest, result, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (ApiValidationException ex) when ((int)ex.StatusCode == 404)
                {
                    // The server-listed pack returned 404 on download. Usually
                    // means the file was deleted between the listing and the
                    // download request (admin deprecated it, R2 key rotated,
                    // file_versions row removed). NOT a sync failure — the
                    // next listing call will omit the row and we're back in
                    // sync. Previously this hit the generic catch and raised
                    // a red "sync failed" banner for every deprecated pack,
                    // even though nothing was actually broken.
                    Logger.Log.Info(
                        $"DictionarySync: {meta.Game}/{meta.Language} v{meta.Version} returned 404 — pack removed server-side, skipping.");
                    result.Messages.Add($"{meta.Game}/{meta.Language}: no longer available (removed).");
                    result.Skipped++;
                }
                catch (ForbiddenException ex)
                {
                    // Tier changed server-side between listing and download.
                    // Treat as skipped (not failed) so the sync summary stays
                    // honest and the Translations tab shows the pack as locked.
                    Logger.Log.Info(
                        $"DictionarySync: {meta.Game}/{meta.Language} 403 on download — tier-gated: {ex.Message}");
                    result.Skipped++;
                }
                catch (Exception ex)
                {
                    Logger.Log.Error($"DictionarySync: sync of {meta.FileVersionId} threw: {ex}");
                    result.Messages.Add($"Failed to sync {meta.Game}/{meta.Language}: {ex.Message}");
                    result.Status = DictionarySyncStatus.Failed;
                    result.Failed++;
                }
            }

            result.SettleAfterDownloads();

            // A new game version's pack is news for the player: one notice per game and version (NewStoryNotices).
            // Before the manifest is saved: stopped between the two, the next sync still sees the old version, reports
            // the step again and the once-only mark keeps it to one notice. The other order would lose it for good.
            try { Notices.NewStoryNotices.Default.Offer(result.VersionChanges, DateTimeOffset.UtcNow); }
            catch (Exception ex) { Logger.Log.Warn($"DictionarySync: new story notice failed (non-fatal): {ex.Message}"); }

            SaveManifest(manifest);

            syncWatch.Stop();
            Logger.Log.Info(
                $"DictionarySync: done in {syncWatch.ElapsedMilliseconds} ms — " +
                $"downloaded={result.Downloaded} upToDate={result.UpToDate} relabelled={result.Relabelled} " +
                $"skipped={result.Skipped} failed={result.Failed}");
            return result;
            }
            finally
            {
                _syncGate.Release();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  One file
        // ─────────────────────────────────────────────────────────────────────

        private Task SyncOneAsync(
            FileMetadata meta,
            byte[] distKey,
            Dictionary<string, ManifestEntry> manifest,
            DictionarySyncResult result,
            CancellationToken ct)
        {
            var ops = new PackOps
            {
                Download = (m, plaintextPath, progress, token) => _api.DownloadFileAsync(
                    _license.CurrentActivation?.DeviceSessionJwt, m, plaintextPath, progress, token, distributionKey: distKey),
                // Streaming variant: caps peak RAM at the pipe buffer (~16 KB) instead of materialising the whole
                // plaintext (~80 MB for PL) + the whole ciphertext (~80 MB) on the LOH during re-encryption.
                Encrypt = (plaintextPath, finalPath) => _protector.EncryptFileStreaming(plaintextPath, finalPath),
                // Packs installed before 2.6 have no record: their content hash is read once from the pack itself.
                HashInstalledPack = localPath =>
                {
                    try
                    {
                        using var plain = _protector.OpenDecryptStream(localPath);
                        return PackRecord.Sha256OfStream(plain);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log.Warn($"DictionarySync: could not read the installed pack {localPath} to compare it: {ex.Message}");
                        return null;
                    }
                },
                InvalidateCaches = (game, language) => GameDataUpdateService.InvalidateDownstreamCaches(
                    GameDataPaths.GameDir(game), language, preserveTargetGisub: true),
                FinalPath = (game, language) =>
                {
                    GameDataPaths.EnsureGameDir(game);
                    return GameDataPaths.TextMapGisub(game, language.ToUpperInvariant());
                },
            };
            return SyncOneCoreAsync(meta, manifest, result, ops, ct);
        }

        /// <summary>The side effects of one pack's sync, so tests can run the decision path without a server.</summary>
        internal sealed class PackOps
        {
            /// <summary>Downloads the dist file and leaves the decrypted pack at the given path.</summary>
            public Func<FileMetadata, string, IProgress<double>, CancellationToken, Task> Download;
            /// <summary>Re-encrypts the plaintext machine-bound to the final path.</summary>
            public Action<string, string> Encrypt;
            /// <summary>SHA-256 of the installed pack's decrypted bytes, or null when it cannot be read.</summary>
            public Func<string, string> HashInstalledPack;
            /// <summary>Removes the matcher index and merged caches built from the previous pack; returns how many.</summary>
            public Func<string, string, int> InvalidateCaches;
            public Func<string, string, string> FinalPath;
        }

        internal static async Task SyncOneCoreAsync(
            FileMetadata meta,
            Dictionary<string, ManifestEntry> manifest,
            DictionarySyncResult result,
            PackOps ops,
            CancellationToken ct)
        {
            string key = ManifestKey(meta);
            ManifestEntry existing = manifest.TryGetValue(key, out var e) ? e : null;
            bool localExists = existing != null && File.Exists(existing.LocalPath);

            // "Has this pack changed?" Same version and file_sha256: nothing to do. A new version (or a re-published one)
            // is downloaded unless the pack inside is the one already installed: the server's pack_sha256 against the
            // installed pack's hash (tier 1), or the very same published file under a new version (tier 2). file_sha256
            // alone cannot tell, because every publish re-encrypts with a random IV.
            var record = localExists ? PackRecord.Read(existing.LocalPath) : null;
            bool computedHash = false;
            string computedPackSha = null;
            var (action, reason) = PackSyncDecision.Decide(existing, localExists, meta, record, () =>
            {
                computedHash = true;
                computedPackSha = ops.HashInstalledPack?.Invoke(existing.LocalPath);
                return computedPackSha;
            });

            if (action == PackSyncAction.UpToDate)
            {
                Logger.Log.Info(
                    $"DictionarySync: up-to-date — {meta.Game}/{meta.Language} v{meta.Version} " +
                    $"already cached at {existing.LocalPath} " +
                    $"(downloaded {FormatRelative(existing.DownloadedAtUnix)}, sha matches).");
                result.UpToDate++;
                return;
            }

            if (action == PackSyncAction.Relabel)
            {
                string previous = existing.Version;
                manifest[key] = new ManifestEntry
                {
                    Game = existing.Game ?? meta.Game,
                    Language = existing.Language ?? meta.Language,
                    Version = meta.Version,
                    FileVersionId = meta.FileVersionId,
                    FileSha256 = meta.Sha256,
                    LocalPath = existing.LocalPath,
                    DownloadedAtUnix = existing.DownloadedAtUnix,
                    FileSizeBytes = meta.Size,
                };
                PackRecord.Write(existing.LocalPath, record?.PackSha256 ?? (computedHash ? computedPackSha : null) ?? meta.ContentSha256,
                    meta.Sha256, meta.Size);
                Logger.Log.Info(
                    $"DictionarySync: pack unchanged, version relabelled — {meta.Game}/{meta.Language} v{previous} → v{meta.Version} " +
                    $"({reason}); no download, no index rebuild.");
                result.Relabelled++;
                NoteVersionChange(result, meta, previous);
                return;
            }

            // Targets — plaintext lands at .plain, machine-bound .gisub is the
            // permanent home. Two distinct paths so a partial re-encryption
            // can't corrupt the old cache. Path resolution goes through
            // GameDataPaths so DictionarySync writes into the same per-game
            // folder VoiceContentHelper reads from.
            string finalPath = ops.FinalPath(meta.Game, meta.Language);
            string plaintextTmp = finalPath + ".plain";

            Logger.Log.Info(
                $"DictionarySync: downloading {meta.Game}/{meta.Language} v{meta.Version} " +
                $"({meta.Size:N0} bytes, reason={reason}, id={meta.FileVersionId}) → {finalPath}");

            var fileWatch = Stopwatch.StartNew();

            // Download + distribution-cipher decrypt in one call. The file at
            // plaintextTmp will be plaintext bytes after this returns
            // (KaptionApiClient.DownloadFileAsync handles the .gisub-dist
            // decode in-place via DistributionCipher).
            PublishProgress(new DownloadProgressInfo(meta.Game, meta.Language, 0, meta.Size));
            try
            {
                await ops.Download(meta, plaintextTmp, new PackProgress(meta.Game, meta.Language, meta.Size), ct).ConfigureAwait(false);
            }
            finally
            {
                PublishProgress(null);
            }

            long downloadedBytes = File.Exists(plaintextTmp) ? new FileInfo(plaintextTmp).Length : 0;
            long downloadMs = fileWatch.ElapsedMilliseconds;
            double mbPerSec = downloadMs > 0 && downloadedBytes > 0
                ? (downloadedBytes / 1_048_576.0) / (downloadMs / 1000.0)
                : 0;
            Logger.Log.Info(
                $"DictionarySync: fetched {downloadedBytes:N0} plaintext bytes in " +
                $"{downloadMs} ms ({mbPerSec:0.#} MB/s). Re-encrypting machine-bound...");

            // The pack's own hash, while it is plaintext: the next revision with the same content is then a relabel.
            string packSha = null;
            try { packSha = File.Exists(plaintextTmp) ? PackRecord.Sha256OfFile(plaintextTmp) : null; }
            catch (Exception ex) { Logger.Log.Warn($"DictionarySync: could not hash {plaintextTmp}: {ex.Message}"); }
            if (packSha != null && meta.ContentSha256 != null && !string.Equals(packSha, meta.ContentSha256, StringComparison.OrdinalIgnoreCase))
                Logger.Log.Warn($"DictionarySync: {meta.Game}/{meta.Language} v{meta.Version} pack_sha256 {meta.ContentSha256} does not match the downloaded pack ({packSha}).");

            // Re-encrypt machine-bound to the canonical .gisub location. Any
            // copy of `finalPath` to another machine becomes garbage at this
            // point — the encryption key is derived from this machine's
            // hardware fingerprint.
            var encryptWatch = Stopwatch.StartNew();
            try
            {
                ops.Encrypt(plaintextTmp, finalPath);
            }
            finally
            {
                TryDelete(plaintextTmp);
            }
            encryptWatch.Stop();

            long finalSize = File.Exists(finalPath) ? new FileInfo(finalPath).Length : 0;
            fileWatch.Stop();
            Logger.Log.Info(
                $"DictionarySync: installed {meta.Game}/{meta.Language} v{meta.Version} — " +
                $"{finalSize:N0} bytes (encrypt {encryptWatch.ElapsedMilliseconds} ms, " +
                $"total {fileWatch.ElapsedMilliseconds} ms) at {finalPath}");
            PackRecord.Write(finalPath, packSha, meta.Sha256, meta.Size);

            // Invalidate matcher / merged-dict caches that were built from
            // the PREVIOUS pack version. Without this sweep, a fresh pack
            // lands on disk but the desktop continues loading the old
            // `.gsmx.gisub` serialised matcher — which still references
            // the old (possibly empty) dict — so FindClosestMatch silently
            // returns "" for every lookup, even though HOT CACHE (which
            // consults contentDict directly) still works. That exact
            // failure mode ate hours in Session 26; don't regress.
            //
            // preserveTargetGisub:true keeps TextMap<Lang>.gisub we just
            // wrote. Everything else (paired merged-cache, serialized
            // matcher index) gets reaped and rebuilt on next launch.
            try
            {
                int invalidated = ops.InvalidateCaches?.Invoke(meta.Game, meta.Language) ?? 0;
                if (invalidated > 0)
                {
                    Logger.Log.Info(
                        $"DictionarySync: invalidated {invalidated} downstream cache file(s) " +
                        $"(matcher index + merged-dict cache) — will rebuild on next matcher load.");
                }
            }
            catch (Exception invEx)
            {
                // Non-fatal: the size-based sanity check in the matcher
                // loader is a second line of defence against stale caches.
                Logger.Log.Warn($"DictionarySync: cache invalidation failed (non-fatal): {invEx.Message}");
            }

            manifest[key] = new ManifestEntry
            {
                Game = meta.Game,
                Language = meta.Language,
                Version = meta.Version,
                FileVersionId = meta.FileVersionId,
                FileSha256 = meta.Sha256,
                LocalPath = finalPath,
                DownloadedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                FileSizeBytes = meta.Size,
            };
            result.Downloaded++;
            NoteVersionChange(result, meta, existing?.Version);
        }

        /// <summary>Records a version move of a pack that was installed before; the notice rules decide if it is news.</summary>
        private static void NoteVersionChange(DictionarySyncResult result, FileMetadata meta, string previousVersion)
        {
            if (string.IsNullOrWhiteSpace(previousVersion) || string.Equals(previousVersion, meta.Version, StringComparison.OrdinalIgnoreCase)) return;
            result.VersionChanges.Add(new Notices.PackVersionChange(meta.Game, meta.Language, previousVersion, meta.Version));
        }

        // ───────────────────────────────────────────────────────────────────
        //  Small helper — "2 h ago", "just now", etc. for log messages.
        // ───────────────────────────────────────────────────────────────────
        private static string FormatRelative(long unixSeconds)
        {
            if (unixSeconds <= 0) return "at unknown time";
            var then = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            var age = DateTimeOffset.UtcNow - then;
            if (age.TotalSeconds < 60) return "just now";
            if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} min ago";
            if (age.TotalHours < 24) return $"{(int)age.TotalHours} h ago";
            if (age.TotalDays < 30) return $"{(int)age.TotalDays} d ago";
            return then.ToLocalTime().ToString("yyyy-MM-dd");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Manifest helpers
        // ─────────────────────────────────────────────────────────────────────

        private static Dictionary<string, ManifestEntry> LoadManifest()
        {
            lock (_manifestLock)
            {
                if (!File.Exists(ManifestPath))
                    return new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    string json = File.ReadAllText(ManifestPath);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, ManifestEntry>>(json, JsonDefaults.Options);
                    return loaded ?? new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    Logger.Log.Warn($"DictionarySync: manifest load failed ({ex.Message}); starting fresh.");
                    return new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        private static void SaveManifest(Dictionary<string, ManifestEntry> manifest)
        {
            lock (_manifestLock)
            {
                try
                {
                    GameDataPaths.EnsureRoot();
                    string tmp = ManifestPath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, JsonDefaults.Indented));
                    if (File.Exists(ManifestPath)) File.Delete(ManifestPath);
                    File.Move(tmp, ManifestPath);
                }
                catch (Exception ex)
                {
                    Logger.Log.Warn($"DictionarySync: manifest save failed: {ex.Message}");
                }
            }
        }

        private static string ManifestKey(FileMetadata meta) =>
            $"{meta.Game.ToLowerInvariant()}/{meta.Language.ToLowerInvariant()}";

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException ex) { Logger.Log.Warn($"DictionarySync: could not delete {path}: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { Logger.Log.Warn($"DictionarySync: access denied deleting {path}: {ex.Message}"); }
        }

        /// <summary>Single row in <c>manifest.json</c>. Persisted as JSON.</summary>
        public sealed class ManifestEntry
        {
            public string Game { get; set; }
            public string Language { get; set; }
            public string Version { get; set; }
            public string FileVersionId { get; set; }
            /// <summary>
            /// sha256 (hex) of the encrypted `.gisub-dist` bytes the server
            /// delivered. Used as the primary "has this pack changed?"
            /// signal — see DictionarySyncService.SyncOneAsync for rationale
            /// (UPSERT keeps FileVersionId stable, so content-addressed sha
            /// is what catches re-publishes). Null on manifests written by
            /// builds older than Session 26 — treated as "force re-download".
            /// </summary>
            public string FileSha256 { get; set; }
            public string LocalPath { get; set; }
            public long DownloadedAtUnix { get; set; }
            public long FileSizeBytes { get; set; }
        }
    }
}
