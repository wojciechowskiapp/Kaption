// ─────────────────────────────────────────────────────────────────────────────
//  GamedataSyncService.cs
//  ---------------------------------------------------------------------------
//  Pulls the per-game "gamedata bundle" from the Kaption backend and splits it
//  into the five prediction-engine files DialogueContextEngine.Load expects:
//  DialogGraph.gisub, NpcNames.gisub, QuestInfo.gisub, HashToDialogs.gisub,
//  TalkIndex.gisub.
//
//  Before this service (runtime GitHub path — legacy, still used as fallback):
//    DialogGraphDownloader.DownloadAndBuild() pulled ~127 MB of ExcelBin*.json
//    from DimbreathBot on first launch, then rebuilt the five files locally.
//    This put user-side version drift between the graph (latest GitHub) and
//    the pre-merged translation pack (stuck on last R2 publish) into the
//    prediction engine — new-in-patch English lines would resolve via
//    TextMapEN but fail the pack lookup.
//
//  Now:
//    1. GET /api/license/gamedata?game=X   → list of GamedataMetadata rows
//       (latest-per-game, tier-filtered server-side).
//    2. Compare against local manifest. Skip if we already have this version.
//    3. GET /api/license/gamedata/download/<id> via
//       KaptionApiClient.DownloadGamedataAsync → plaintext bundle JSON.
//    4. Parse { dialog_graph, hash_to_dialogs, npc_names, talk_index, quest_info }
//       and split into the five expected files. Each is saved via
//       FileProtectionHelper.SaveProtectedJson so on-disk layout matches the
//       legacy DialogGraphDownloader output exactly.
//    5. Wipe the plaintext bundle temp file.
//    6. Update manifest.
//
//  Where the files go (same paths DialogueContextEngine.Load already reads):
//    %APPDATA%\Kaption\<Game>\DialogGraph.gisub
//    %APPDATA%\Kaption\<Game>\NpcNames.gisub
//    %APPDATA%\Kaption\<Game>\QuestInfo.gisub
//    %APPDATA%\Kaption\<Game>\HashToDialogs.gisub
//    %APPDATA%\Kaption\<Game>\TalkIndex.gisub
//    %APPDATA%\Kaption\gamedata-manifest.json   (state tracker)
//
//  Threading: caller invokes SyncAsync on a background Task — never blocks UI.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Data;
using GI_Subtitles.Services.Network;
using GI_Subtitles.Services.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>Outcome counters from one <see cref="GamedataSyncService.SyncAsync"/> call.</summary>
    public sealed class GamedataSyncResult
    {
        public bool Downloaded { get; internal set; }
        public bool UpToDate { get; internal set; }
        public bool Skipped { get; internal set; }
        public bool Failed { get; internal set; }
        public string Message { get; internal set; }
    }

    /// <summary>
    /// One pass of the gamedata-bundle pull for a single game. Stateless
    /// beyond the on-disk manifest — safe to construct multiple times.
    /// </summary>
    public sealed class GamedataSyncService
    {
        private static string ManifestPath => Path.Combine(GameDataPaths.Root, "gamedata-manifest.json");
        private static readonly object _manifestLock = new object();

        private readonly KaptionApiClient _api;
        private readonly LicenseService _license;
        private readonly FileProtectionHelper _protector;

        public GamedataSyncService(
            KaptionApiClient api,
            LicenseService license,
            IFileProtectionService protector)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _license = license ?? throw new ArgumentNullException(nameof(license));
            if (protector == null) throw new ArgumentNullException(nameof(protector));
            _protector = new FileProtectionHelper(protector);
        }

        /// <summary>
        /// Sync the gamedata bundle for one game. Returns a summary — success
        /// is <c>result.Downloaded || result.UpToDate</c>.
        /// </summary>
        public async Task<GamedataSyncResult> SyncAsync(string game, CancellationToken ct)
        {
            var result = new GamedataSyncResult();
            var watch = Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(game))
            {
                result.Skipped = true;
                result.Message = "GamedataSync: skipped — game not configured.";
                Logger.Log.Info(result.Message);
                return result;
            }

            var session = _license.CurrentActivation;
            if (session == null || string.IsNullOrEmpty(session.DeviceSessionJwt))
            {
                result.Skipped = true;
                result.Message = "GamedataSync: skipped — no active license session.";
                Logger.Log.Info(result.Message);
                return result;
            }

            byte[] distKey = session.DistributionKey;
            if (distKey == null || distKey.Length != 32)
            {
                result.Failed = true;
                result.Message = "GamedataSync: distribution key missing — re-activate to refresh.";
                Logger.Log.Warn(result.Message);
                return result;
            }

            // Ask the server what the latest bundle is for this game.
            IReadOnlyList<GamedataMetadata> remoteBundles;
            try
            {
                remoteBundles = await _api.GetGamedataAsync(session.DeviceSessionJwt, game, ct)
                    .ConfigureAwait(false);
            }
            catch (UnauthorizedException ex)
            {
                Logger.Log.Warn($"GamedataSync: listing returned 401 — {ex.Message}");
                try { _license?.ReportRemoteRevocation($"GamedataSync 401: {ex.Message}"); } catch { /* best-effort */ }
                result.Failed = true;
                result.Message = "Please sign in again.";
                return result;
            }
            catch (ForbiddenException ex)
            {
                // Tier-gated — not all accounts get gamedata bundles. Fall back
                // to the runtime DialogGraphDownloader path like the "no bundle
                // published" branch below.
                Logger.Log.Info($"GamedataSync: /gamedata returned 403 — tier-gated. {ex.Message}");
                result.Skipped = true;
                result.Message = "No gamedata bundle on your current plan — using runtime build.";
                return result;
            }
            catch (ApiUnavailableException ex)
            {
                // Silent: prediction engine still works with the legacy
                // DialogGraphDownloader path or a previously-installed
                // bundle. A network wobble shouldn't scare the user.
                Logger.Log.Info($"GamedataSync: offline ({ex.Message}) — using cached bundle.");
                result.Skipped = true;
                result.Message = "Offline.";
                return result;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"GamedataSync: unexpected listing failure: {ex.Message}");
                result.Failed = true;
                result.Message = ex.Message;
                return result;
            }

            var latest = remoteBundles
                .Where(b => string.Equals(b.Game, game, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(b => b.ReleasedAt)
                .FirstOrDefault();
            if (latest == null)
            {
                // No bundle published yet — desktop falls back to the legacy
                // runtime build path. Not a failure.
                result.Skipped = true;
                result.Message = $"No gamedata bundle available for {game} yet — using runtime build.";
                Logger.Log.Info($"GamedataSync: no bundle on R2 for {game}; falling back to DialogGraphDownloader.");
                return result;
            }

            var manifest = LoadManifest();
            string key = $"gamedata/{game.ToLowerInvariant()}";
            // Same "compare by sha256, not by UUID" pattern as
            // DictionarySyncService — publish-gamedata.sh UPSERTs the
            // gamedata_versions row on conflict (keeps id stable), so
            // comparing by GamedataVersionId alone would miss in-place
            // content updates. sha256 changes on every re-encrypt
            // (random AES-CBC IV), so it catches every real publish.
            // Legacy manifests without FileSha256 force a one-shot
            // re-download.
            bool shaMatches = manifest.TryGetValue(key, out var existing)
                && !string.IsNullOrEmpty(existing.FileSha256)
                && string.Equals(existing.FileSha256, latest.Sha256, StringComparison.OrdinalIgnoreCase);

            if (existing != null
                && string.Equals(existing.Version, latest.Version, StringComparison.Ordinal)
                && shaMatches
                && LocalBundleIsComplete(game))
            {
                Logger.Log.Info(
                    $"GamedataSync: up-to-date — {game} v{latest.Version} " +
                    $"already installed (downloaded {FormatRelative(existing.DownloadedAtUnix)}, sha matches).");
                result.UpToDate = true;
                return result;
            }

            // Download to a plaintext temp file. DownloadGamedataAsync handles
            // the KAPD-magic distribution-layer decrypt in place.
            GameDataPaths.EnsureGameDir(game);
            string tmpBundle = Path.Combine(GameDataPaths.GameDir(game), "gamedata-bundle.tmp");
            TryDelete(tmpBundle);

            Logger.Log.Info(
                $"GamedataSync: downloading {game} v{latest.Version} " +
                $"({latest.Size:N0} bytes, id={latest.GamedataVersionId})");
            var dlWatch = Stopwatch.StartNew();
            try
            {
                await _api.DownloadGamedataAsync(
                    session.DeviceSessionJwt,
                    latest,
                    tmpBundle,
                    progress: null,
                    ct: ct,
                    distributionKey: distKey).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"GamedataSync: download failed: {ex.Message}");
                TryDelete(tmpBundle);
                result.Failed = true;
                result.Message = ex.Message;
                return result;
            }
            dlWatch.Stop();

            long bundleBytes = File.Exists(tmpBundle) ? new FileInfo(tmpBundle).Length : 0;
            double mbPerSec = dlWatch.ElapsedMilliseconds > 0 && bundleBytes > 0
                ? (bundleBytes / 1_048_576.0) / (dlWatch.ElapsedMilliseconds / 1000.0)
                : 0;
            Logger.Log.Info(
                $"GamedataSync: fetched {bundleBytes:N0} plaintext bytes in " +
                $"{dlWatch.ElapsedMilliseconds} ms ({mbPerSec:0.#} MB/s). Splitting bundle...");

            // Parse + split. We use JObject rather than a strongly-typed DTO
            // so we don't have to know every map's value shape at compile
            // time — each top-level key's value is shipped straight through
            // to SaveProtectedJson, which re-serialises with Formatting.None.
            //
            // The whole parse/split block is wrapped in try/finally with an
            // unconditional TryDelete(tmpBundle). The bundle is briefly on
            // disk as plaintext JSON between decrypt (DownloadEncryptedAsync)
            // and per-section machine-bound re-encrypt (SaveProtectedJson),
            // and we don't want a crash mid-split to leave that plaintext
            // lying around — it'd defeat the point of the machine-bound
            // re-encryption the rest of the pipeline does.
            long splitMs = 0;
            try
            {
                JObject bundle;
                try
                {
                    using (var sr = new StreamReader(tmpBundle, Encoding.UTF8))
                    using (var jr = new JsonTextReader(sr))
                    {
                        bundle = JObject.Load(jr);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log.Error($"GamedataSync: bundle parse failed: {ex.Message}");
                    result.Failed = true;
                    result.Message = "Bundle parse failed — the download may be corrupt. Please retry.";
                    return result;
                }

                var bundleVersion = bundle.Value<int?>("bundle_version") ?? 0;
                // v1: original Genshin-only format, no extension field.
                // v2: adds `extension.game` so DialogueContextBase can gate
                // load on matching game identity. Both write the same five
                // split files; v2 additionally drops a BundleMeta.json sidecar.
                if (bundleVersion != 1 && bundleVersion != 2)
                {
                    Logger.Log.Warn($"GamedataSync: unknown bundle_version={bundleVersion}; refusing to install.");
                    result.Failed = true;
                    result.Message = "Bundle format is newer than this Kaption build supports. Update the app.";
                    return result;
                }

                // v2 game-identity gate: if the bundle declares a game, it
                // MUST match the game we asked for. Mismatch means we got
                // the wrong R2 object — refuse rather than cross-pollinate
                // the per-game data folders. v1 bundles have no extension
                // block and are tolerated (legacy).
                string bundleGame = bundle["extension"]?["game"]?.ToString();
                if (bundleVersion >= 2 && !string.IsNullOrEmpty(bundleGame) &&
                    !string.Equals(bundleGame, game, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log.Error(
                        $"GamedataSync: bundle declares game=\"{bundleGame}\" but we " +
                        $"requested game=\"{game}\". Refusing to install.");
                    result.Failed = true;
                    result.Message = "Bundle is for a different game than requested. Please retry.";
                    return result;
                }

                // Map bundle keys → on-disk filenames. All five must be
                // present; if any is missing the build is broken and we
                // shouldn't half-install it.
                var expectedSections = new (string bundleKey, string jsonPath)[]
                {
                    ("dialog_graph",    GameDataPaths.DialogGraphJson(game)),
                    ("hash_to_dialogs", GameDataPaths.HashToDialogsJson(game)),
                    ("npc_names",       GameDataPaths.NpcNamesJson(game)),
                    ("talk_index",      GameDataPaths.TalkIndexJson(game)),
                    ("quest_info",      GameDataPaths.QuestInfoJson(game)),
                };
                foreach (var (k, _) in expectedSections)
                {
                    if (bundle[k] == null)
                    {
                        Logger.Log.Error($"GamedataSync: bundle missing required section '{k}'.");
                        result.Failed = true;
                        result.Message = $"Bundle is missing '{k}'. Retry or contact support.";
                        return result;
                    }
                }

                var splitWatch = Stopwatch.StartNew();
                try
                {
                    foreach (var (k, jsonPath) in expectedSections)
                    {
                        // SaveProtectedJson serialises + encrypts machine-
                        // bound + removes any stale plaintext sibling.
                        // Exactly what the legacy DialogGraphDownloader path
                        // does, so DialogueContextEngine.Load reads the
                        // result transparently.
                        _protector.SaveProtectedJson(jsonPath, bundle[k]);
                    }

                    // v2 sidecar: persist { bundle_version, extension.game }
                    // so DialogueContextBase.ValidateBundleMeta can enforce
                    // the game-identity match at load time. v1 bundles skip
                    // this — ValidateBundleMeta tolerates a missing file.
                    if (bundleVersion >= 2)
                    {
                        var meta = new JObject
                        {
                            ["bundle_version"] = bundleVersion,
                            ["extension"] = new JObject
                            {
                                ["game"] = string.IsNullOrEmpty(bundleGame) ? game : bundleGame,
                            },
                        };
                        _protector.SaveProtectedJson(GameDataPaths.BundleMetaJson(game), meta);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log.Error($"GamedataSync: section write failed: {ex.Message}");
                    result.Failed = true;
                    result.Message = ex.Message;
                    return result;
                }
                splitWatch.Stop();
                splitMs = splitWatch.ElapsedMilliseconds;
            }
            finally
            {
                // Unconditional: plaintext bundle never lives past this call,
                // including on failure paths. Matches the "re-encrypt machine-
                // bound or nothing" invariant DictionarySync uses for
                // translation packs.
                TryDelete(tmpBundle);
            }

            manifest[key] = new GamedataManifestEntry
            {
                Game = game,
                Version = latest.Version,
                GamedataVersionId = latest.GamedataVersionId,
                FileSha256 = latest.Sha256,
                DownloadedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                FileSizeBytes = latest.Size,
            };
            SaveManifest(manifest);

            watch.Stop();
            Logger.Log.Info(
                $"GamedataSync: installed {game} v{latest.Version} — " +
                $"split in {splitMs} ms, total {watch.ElapsedMilliseconds} ms.");

            result.Downloaded = true;
            return result;
        }

        /// <summary>
        /// Returns true when every bundle-derived .gisub exists on disk for
        /// the game. If any is missing we need to redownload, even if the
        /// manifest says we already installed this version — user may have
        /// deleted files manually or disk may have gone bad.
        /// </summary>
        private bool LocalBundleIsComplete(string game)
        {
            return _protector.FileExists(GameDataPaths.DialogGraphJson(game))
                && _protector.FileExists(GameDataPaths.HashToDialogsJson(game))
                && _protector.FileExists(GameDataPaths.NpcNamesJson(game))
                && _protector.FileExists(GameDataPaths.TalkIndexJson(game))
                && _protector.FileExists(GameDataPaths.QuestInfoJson(game));
        }

        private static Dictionary<string, GamedataManifestEntry> LoadManifest()
        {
            lock (_manifestLock)
            {
                if (!File.Exists(ManifestPath))
                    return new Dictionary<string, GamedataManifestEntry>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string json = File.ReadAllText(ManifestPath);
                    var loaded = JsonConvert.DeserializeObject<Dictionary<string, GamedataManifestEntry>>(json);
                    return loaded ?? new Dictionary<string, GamedataManifestEntry>(StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    Logger.Log.Warn($"GamedataSync: manifest load failed ({ex.Message}); starting fresh.");
                    return new Dictionary<string, GamedataManifestEntry>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        private static void SaveManifest(Dictionary<string, GamedataManifestEntry> manifest)
        {
            lock (_manifestLock)
            {
                try
                {
                    GameDataPaths.EnsureRoot();
                    string tmp = ManifestPath + ".tmp";
                    File.WriteAllText(tmp, JsonConvert.SerializeObject(manifest, Formatting.Indented));
                    if (File.Exists(ManifestPath)) File.Delete(ManifestPath);
                    File.Move(tmp, ManifestPath);
                }
                catch (Exception ex)
                {
                    Logger.Log.Warn($"GamedataSync: manifest save failed: {ex.Message}");
                }
            }
        }

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

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException ex) { Logger.Log.Warn($"GamedataSync: could not delete {path}: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { Logger.Log.Warn($"GamedataSync: access denied deleting {path}: {ex.Message}"); }
        }

        /// <summary>
        /// Row shape of <c>gamedata-manifest.json</c>. Kept distinct from
        /// DictionarySync's <c>ManifestEntry</c> so a schema change on one
        /// side can't silently break the other.
        /// </summary>
        public sealed class GamedataManifestEntry
        {
            public string Game { get; set; }
            public string Version { get; set; }
            public string GamedataVersionId { get; set; }
            /// <summary>
            /// sha256 (hex) of the encrypted .gisub-dist bytes on R2.
            /// Primary "has this bundle changed?" signal — see SyncAsync
            /// for rationale. Null on manifests from builds before this
            /// field existed; treated as "force one re-download".
            /// </summary>
            public string FileSha256 { get; set; }
            public long DownloadedAtUnix { get; set; }
            public long FileSizeBytes { get; set; }
        }
    }
}
