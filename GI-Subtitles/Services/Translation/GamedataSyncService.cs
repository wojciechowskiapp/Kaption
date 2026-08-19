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
//    5. If the bundle carries the OPTIONAL sixth section `textmap_en`, write it
//       to TextMapEN.json as PLAINTEXT (see below).
//    6. Wipe the plaintext bundle temp file.
//    7. Update manifest.
//
//  Where the files go (same paths DialogueContextEngine.Load already reads):
//    %APPDATA%\Kaption\<Game>\DialogGraph.gisub
//    %APPDATA%\Kaption\<Game>\NpcNames.gisub
//    %APPDATA%\Kaption\<Game>\QuestInfo.gisub
//    %APPDATA%\Kaption\<Game>\HashToDialogs.gisub
//    %APPDATA%\Kaption\<Game>\TalkIndex.gisub
//    %APPDATA%\Kaption\<Game>\TextMapEN.json    (only when `textmap_en` present)
//    %APPDATA%\Kaption\gamedata-manifest.json   (state tracker)
//
//  ── The `textmap_en` section (added for Zenless Zone Zero) ──────────────────
//
//  Genshin and HSR key their TextMap by a uint64 xxhash, so `dialog_graph.h`
//  IS that hash and the public upstream TextMapEN.json resolves it. ZZZ keys
//  its upstream TextMap by human-readable strings
//  ("Main_Chat_Chapter01_3000024_01") while DialogueContextBase reads `h` as a
//  ulong, so tools/build-gamedata-zzz.cjs mints its own numeric ids — and the
//  only map that can resolve them is the one that same build produced.
//
//  Rather than publish that map as a second R2 object with its own version
//  number (two artifacts that must agree forever — the exact shape of bug the
//  Small/Medium TextMap shard split keeps causing), the builder emits it as a
//  section of the bundle. One object, one D1 row, one download, and the two
//  halves cannot drift because they are the same file.
//
//  Two properties of this write are load-bearing:
//    * PLAINTEXT, not SaveProtectedJson. DialogueContextBase.LoadCore reads
//      textMapEnPath with File.Exists + File.OpenRead and never consults
//      FileProtectionHelper for it. A .gisub here would be invisible.
//    * SINGLE WRITER. For a game whose bundle carries this section,
//      GameDataUpdateService.IsUpstreamMirrored must be false for every
//      language, so nothing else ever writes TextMapEN.json for that game.
//      GameDataBootstrapService relies on the same predicate to know it must
//      not hard-fail on a missing TextMapEN before this sync has run.
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
                && LocalBundleIsComplete(game, existing.TextMapEnFromBundle))
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

            // Parse + split — see InstallBundleFromFile.
            //
            // The call is wrapped in try/finally with an unconditional
            // TryDelete(tmpBundle). The bundle is briefly on disk as plaintext
            // JSON between decrypt (DownloadEncryptedAsync) and per-section
            // machine-bound re-encrypt (SaveProtectedJson), and we don't want a
            // crash mid-split to leave that plaintext lying around — it'd
            // defeat the point of the machine-bound re-encryption the rest of
            // the pipeline does.
            BundleInstallResult install;
            try
            {
                install = InstallBundleFromFile(tmpBundle, game, _protector);
            }
            finally
            {
                TryDelete(tmpBundle);
            }

            if (!install.Success)
            {
                result.Failed = true;
                result.Message = install.Message;
                return result;
            }

            long splitMs = install.SplitMs;
            bool textMapEnInstalled = install.TextMapEnInstalled;

            manifest[key] = new GamedataManifestEntry
            {
                Game = game,
                Version = latest.Version,
                GamedataVersionId = latest.GamedataVersionId,
                FileSha256 = latest.Sha256,
                DownloadedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                FileSizeBytes = latest.Size,
                TextMapEnFromBundle = textMapEnInstalled,
            };
            SaveManifest(manifest);

            watch.Stop();
            Logger.Log.Info(
                $"GamedataSync: installed {game} v{latest.Version} — " +
                $"split in {splitMs} ms, total {watch.ElapsedMilliseconds} ms" +
                (textMapEnInstalled ? ", TextMapEN.json written from bundle." : "."));

            result.Downloaded = true;
            return result;
        }

        /// <summary>
        /// Bundle key carrying the numeric-keyed EN TextMap that resolves
        /// <c>dialog_graph.h</c>. Optional and ZZZ-only today — see the file
        /// header. Kept as a constant so the builder, this service and
        /// <c>tools/split-bundle-local.cjs</c> can be grepped as one unit.
        /// </summary>
        internal const string TextMapEnSection = "textmap_en";

        /// <summary>Outcome of one <see cref="InstallBundleFromFile"/> call.</summary>
        internal sealed class BundleInstallResult
        {
            public bool Success { get; internal set; }
            public string Message { get; internal set; }
            public long SplitMs { get; internal set; }
            /// <summary>True when the bundle carried <see cref="TextMapEnSection"/>
            /// and <c>TextMapEN.json</c> was written from it.</summary>
            public bool TextMapEnInstalled { get; internal set; }
        }

        /// <summary>
        /// Validate a plaintext bundle on disk and split it into the per-file
        /// outputs <c>DialogueContextEngine.Load</c> reads. Does no network and
        /// no manifest I/O, so tests can drive it directly with a hand-built
        /// bundle — which is the only way to cover the "bundle installs, then
        /// resolves nothing" failure mode without a live licence session.
        ///
        /// Leaves the game folder untouched on every rejection path.
        ///
        /// Static and protector-injected on purpose: a test needs neither a
        /// <see cref="KaptionApiClient"/> nor a <see cref="LicenseService"/> to
        /// exercise it.
        /// </summary>
        internal static BundleInstallResult InstallBundleFromFile(
            string bundlePath, string game, FileProtectionHelper protector)
        {
            if (protector == null) throw new ArgumentNullException(nameof(protector));
            var outcome = new BundleInstallResult();

            GameDataPaths.EnsureGameDir(game);

            // `textmap_en` is staged next to its destination and only moved
            // into place after every gate passes. The section arrives BEFORE
            // `extension.game` in the byte stream, so writing it eagerly would
            // contaminate the game folder with a bundle we are about to reject.
            string stagedTextMapEn = Path.Combine(
                GameDataPaths.GameDir(game), "TextMapEN.json.bundle.tmp");
            TryDelete(stagedTextMapEn);

            try
            {
                JObject bundle;
                try
                {
                    bundle = ReadBundleStagingTextMap(bundlePath, stagedTextMapEn, out bool hasTextMapEn);
                    if (!hasTextMapEn) stagedTextMapEn = null;
                }
                catch (Exception ex)
                {
                    Logger.Log.Error($"GamedataSync: bundle parse failed: {ex.Message}");
                    outcome.Message = "Bundle parse failed — the download may be corrupt. Please retry.";
                    return outcome;
                }

                var bundleVersion = bundle.Value<int?>("bundle_version") ?? 0;
                // v1: original Genshin-only format, no extension field.
                // v2: adds `extension.game` so DialogueContextBase can gate
                // load on matching game identity. Both write the same five
                // split files; v2 additionally drops a BundleMeta.json sidecar
                // and may carry the optional `textmap_en` section.
                if (bundleVersion != 1 && bundleVersion != 2)
                {
                    Logger.Log.Warn($"GamedataSync: unknown bundle_version={bundleVersion}; refusing to install.");
                    outcome.Message = "Bundle format is newer than this Kaption build supports. Update the app.";
                    return outcome;
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
                    outcome.Message = "Bundle is for a different game than requested. Please retry.";
                    return outcome;
                }

                // Map bundle keys → on-disk filenames. All five must be
                // present; if any is missing the build is broken and we
                // shouldn't half-install it. `textmap_en` is deliberately NOT
                // in this list: Genshin/HSR bundles and every v1 bundle
                // legitimately omit it.
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
                        outcome.Message = $"Bundle is missing '{k}'. Retry or contact support.";
                        return outcome;
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
                        protector.SaveProtectedJson(jsonPath, bundle[k]);
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
                        protector.SaveProtectedJson(GameDataPaths.BundleMetaJson(game), meta);
                    }

                    // Optional sixth section — see the file header. Committed
                    // LAST, after the five required files are on disk, so a
                    // half-installed bundle never leaves a fresh TextMapEN
                    // pointing at a stale graph.
                    //
                    // A bundle that HAD the section but couldn't land it fails
                    // the whole install. Reporting success would stamp the
                    // manifest, and the next launch would see a matching
                    // version + sha and skip the re-download — leaving a graph
                    // whose every hash resolves to nothing, permanently.
                    if (stagedTextMapEn != null)
                    {
                        bool committed = CommitStagedTextMapEn(game, stagedTextMapEn);
                        stagedTextMapEn = null; // ownership moved; don't delete in finally
                        if (!committed)
                        {
                            outcome.Message =
                                "Could not write TextMapEN.json from the bundle. Retry, or free up " +
                                "disk space if that's the problem.";
                            return outcome;
                        }
                        outcome.TextMapEnInstalled = true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log.Error($"GamedataSync: section write failed: {ex.Message}");
                    outcome.Message = ex.Message;
                    return outcome;
                }
                splitWatch.Stop();

                outcome.SplitMs = splitWatch.ElapsedMilliseconds;
                outcome.Success = true;
                return outcome;
            }
            finally
            {
                // Only non-null here if we bailed before committing it.
                if (stagedTextMapEn != null) TryDelete(stagedTextMapEn);
            }
        }

        /// <summary>
        /// Parse the bundle into a JObject, EXCEPT for
        /// <see cref="TextMapEnSection"/>, which is streamed straight to
        /// <paramref name="stagePath"/> without ever entering the DOM.
        ///
        /// That section is ~22 MB of flat string→string JSON for ZZZ; putting
        /// it through <c>JObject.Load</c> would add roughly 100 MB of
        /// transient JProperty/JValue heap to a startup-path operation for no
        /// benefit, since its only destination is a file. Every other section
        /// is read exactly as <c>JObject.Load</c> read it before, so
        /// Genshin/HSR bundles behave identically.
        /// </summary>
        private static JObject ReadBundleStagingTextMap(
            string bundlePath, string stagePath, out bool hasTextMapEn)
        {
            hasTextMapEn = false;
            var bundle = new JObject();

            using (var sr = new StreamReader(bundlePath, Encoding.UTF8))
            using (var jr = new JsonTextReader(sr))
            {
                // TextMap values are arbitrary game dialogue. Newtonsoft's
                // default DateParseHandling would rewrite anything that looks
                // like a timestamp into a normalised DateTime on the way
                // through, silently altering the text we key OCR matches on.
                jr.DateParseHandling = DateParseHandling.None;

                if (!jr.Read() || jr.TokenType != JsonToken.StartObject)
                    throw new JsonReaderException("Bundle root is not a JSON object.");

                while (jr.Read() && jr.TokenType == JsonToken.PropertyName)
                {
                    string name = (string)jr.Value;
                    if (!jr.Read())
                        throw new JsonReaderException($"Bundle truncated after property '{name}'.");

                    if (string.Equals(name, TextMapEnSection, StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(stagePath));
                        using (var sw = new StreamWriter(stagePath, false, new UTF8Encoding(false)))
                        using (var jw = new JsonTextWriter(sw) { Formatting = Formatting.None })
                        {
                            // Copies the current token and all its children
                            // straight from reader to writer; leaves the
                            // reader on the section's closing token.
                            jw.WriteToken(jr, writeChildren: true);
                        }
                        hasTextMapEn = true;
                    }
                    else
                    {
                        bundle[name] = JToken.ReadFrom(jr);
                    }
                }
            }

            return bundle;
        }

        /// <summary>
        /// Move the staged TextMapEN into place and drop every cache derived
        /// from the previous one. Returns false (and logs) on failure; the
        /// caller then fails the whole install so the manifest is not stamped
        /// and the next launch re-downloads.
        /// </summary>
        private static bool CommitStagedTextMapEn(string game, string stagePath)
        {
            string finalPath = GameDataPaths.TextMapJson(game, "EN");
            try
            {
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(stagePath, finalPath);

                // The sidecar belongs to the upstream-mirror path. Leaving a
                // stale one behind would let a future mirrored fetch think it
                // already has this content under an ETag that describes a
                // completely different file.
                TryDelete(GameDataPaths.TextMapMetaJson(game, "EN"));

                // Merged matcher caches and the serialized index were built
                // against the previous EN corpus. Same sweep the upstream
                // update path runs after it replaces a TextMap.
                int invalidated = GameDataUpdateService.InvalidateDownstreamCaches(
                    GameDataPaths.GameDir(game), "EN");

                long bytes = new FileInfo(finalPath).Length;
                Logger.Log.Info(
                    $"GamedataSync: wrote {Path.GetFileName(finalPath)} from bundle section " +
                    $"'{TextMapEnSection}' ({bytes:N0} bytes); invalidated {invalidated} cache file(s).");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log.Error(
                    $"GamedataSync: could not commit {TextMapEnSection} to {finalPath}: {ex.Message}");
                TryDelete(stagePath);
                return false;
            }
        }

        /// <summary>
        /// Returns true when every bundle-derived .gisub exists on disk for
        /// the game. If any is missing we need to redownload, even if the
        /// manifest says we already installed this version — user may have
        /// deleted files manually or disk may have gone bad.
        ///
        /// <paramref name="expectTextMapEn"/> comes from the manifest row and
        /// extends the same reasoning to the bundle-carried TextMapEN: a user
        /// who deletes it would otherwise be stuck with a graph whose every
        /// hash resolves to nothing, and the manifest would happily say
        /// "up-to-date" forever.
        /// </summary>
        private bool LocalBundleIsComplete(string game, bool expectTextMapEn)
        {
            if (expectTextMapEn && !File.Exists(GameDataPaths.TextMapJson(game, "EN")))
                return false;

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

            /// <summary>
            /// True when this bundle carried the <c>textmap_en</c> section and
            /// we wrote <c>TextMapEN.json</c> from it. Drives the completeness
            /// check on subsequent launches. Absent (false) on manifests
            /// written before the section existed and on Genshin/HSR, where
            /// TextMapEN comes from the public mirror instead.
            /// </summary>
            public bool TextMapEnFromBundle { get; set; }
        }
    }
}
