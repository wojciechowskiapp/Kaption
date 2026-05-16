// ─────────────────────────────────────────────────────────────────────────────
//  DictionaryInventoryService.cs
//  ---------------------------------------------------------------------------
//  Builds the list that powers the Translations tab.
//
//  Three data sources are merged into one list of TranslationPackInfo:
//
//    1. Per-game folder       — %APPDATA%\Kaption\<Game>\TextMap<Lang>.*.gisub
//                              (v2.0.0+: single unified location for both
//                              locally-built caches and DictionarySync
//                              downloads; provenance determined by the
//                              manifest overlay in step 2).
//    2. Manifest overlay      — %APPDATA%\Kaption\manifest.json
//                              (written by DictionarySyncService; any pack
//                              whose key appears here is PaidCached).
//    3. Backend catalog       — GET /api/license/files (no filter). Lists
//                              every (game, language) pack the server knows
//                              about, with min_tier for gating.
//
//  Merge rule: PaidCached > LocalBuilt > RemoteAvailable > RemoteLocked. When
//  the same (game, lang) appears in multiple sources, we pick the "most
//  installed" representation but still attach the remote metadata (version,
//  min_tier) so the row can show "update available" style info later.
//
//  The service also writes a one-line "inventory summary" to the log at the
//  end of each scan — this is the log line that was missing and made users
//  think no cache existed when it clearly did on disk.
//
//  Threading: ScanAsync kicks off a background Task. Safe to call from UI.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Common;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Models;
using GI_Subtitles.Services.Network;
using GI_Subtitles.Services.Security;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// One pass of inventory scanning returns this. Holds the merged pack
    /// list plus a machine-readable summary so UI code doesn't have to
    /// re-count categories itself.
    /// </summary>
    public sealed class DictionaryInventoryResult
    {
        public List<TranslationPackInfo> Packs { get; } = new List<TranslationPackInfo>();

        /// <summary>Local-cache packs that still have a source file on disk — strong provenance.</summary>
        public int InstalledLocalCacheWithSource { get; internal set; }

        /// <summary>Local-cache packs with no detectable source — orphan caches.</summary>
        public int InstalledLocalCacheOrphan { get; internal set; }

        /// <summary>Convenience: all local-cache packs regardless of source presence.</summary>
        public int InstalledLocalCache => InstalledLocalCacheWithSource + InstalledLocalCacheOrphan;

        public int InstalledPaidCached { get; internal set; }
        public int RemoteAvailable { get; internal set; }
        public int RemoteLocked { get; internal set; }
        public bool RemoteQueryOk { get; internal set; }
        public string RemoteQueryError { get; internal set; }
    }

    /// <summary>
    /// Stateless scanner. Instantiate per scan — no persistent state; no IO
    /// side effects beyond (optional) HTTP GET to /api/license/files.
    /// </summary>
    public sealed partial class DictionaryInventoryService
    {
        // Canonical game tags, matching Config["Game"] values. Adding a new
        // game requires: (a) entry here, (b) matching ComboBoxItem in
        // SettingsWindow.xaml, (c) backend file_versions rows.
        private static readonly (string Tag, string Display)[] KnownGames =
        {
            ("Genshin", "Genshin Impact"),
            ("StarRail", "Honkai: Star Rail"),
        };

        // Matches "Polski" / "English" / etc. for display. Ordered deliberately
        // — the list in the UI follows this order when we can't source
        // language display names from elsewhere.
        private static readonly Dictionary<string, string> LangDisplay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "PL",  "Polski" },
            { "EN",  "English" },
            { "DE",  "Deutsch" },
            { "ES",  "Español" },
            { "FR",  "Français" },
            { "ID",  "Bahasa Indonesia" },
            { "JP",  "日本語" },
            { "KR",  "한국어" },
            { "PT",  "Português" },
            { "RU",  "Русский" },
            { "TH",  "ไทย" },
            { "VI",  "Tiếng Việt" },
            { "CHS", "简体中文" },
            { "CHT", "繁體中文" },
        };

        /// <summary>
        /// File name like "TextMap<code>PL</code>.gisub" or
        /// "TextMapEN_TextMap<code>PL</code>.v2.gisub" — we want the *target*
        /// language, which is the second TextMap in the concatenated names
        /// (paired form) and the only TextMap in the solo form.
        ///
        /// Net8: RegexOptions.Compiled → [GeneratedRegex]. Cold path (dictionary
        /// inventory enumeration), but convert for consistency + AOT-readiness.
        /// </summary>
        [GeneratedRegex(
            @"^TextMap[A-Z]+_TextMap(?<lang>[A-Z]+)(?:\.v\d+)?\.gisub$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex PairedTargetRegex();

        [GeneratedRegex(
            @"^TextMap(?<lang>[A-Z]+)\.gisub$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex SoloLangRegex();

        private static string KaptionRoot => Data.GameDataPaths.Root;

        private readonly KaptionApiClient _api;
        private readonly LicenseService _license;

        /// <param name="api">API client for the remote catalog. Null skips remote.</param>
        /// <param name="license">License service; scan uses its session JWT + effective_tier.</param>
        public DictionaryInventoryService(KaptionApiClient api, LicenseService license)
        {
            _api = api;
            _license = license;
        }

        /// <summary>
        /// Scan both local folders and (if a session is available) the backend
        /// catalog. Never throws — any failure downgrades to "local-only"
        /// results with <see cref="DictionaryInventoryResult.RemoteQueryOk"/>
        /// reporting false.
        /// </summary>
        public async Task<DictionaryInventoryResult> ScanAsync(CancellationToken ct)
        {
            var result = new DictionaryInventoryResult();
            // Key = (Game, Language) lowercase-invariant — collisions collapse.
            var byKey = new Dictionary<string, TranslationPackInfo>(StringComparer.OrdinalIgnoreCase);

            // ── 1. Local legacy folder (VoiceContentHelper writes caches here) ──
            //
            // For each (game, lang) we want to answer two questions:
            //   (a) Is there a usable matcher cache for this language pair?
            //   (b) If so, is the SOURCE that cache was built from also on disk?
            //
            // Answering (b) requires sniffing sibling files — a `TextMap<Lang>.json`
            // or `TextMap<Lang>.gisub` in the same folder is strong evidence of
            // a local build (either dev-run `tools/translate_textmap.py` or a
            // successful `DownloadButton` flow that landed a plaintext JSON).
            // No sibling → the cache is "orphaned" (migrated from an older
            // install, hand-placed, or the source was pruned) and we say so.
            foreach (var (gameTag, gameDisplay) in KnownGames)
            {
                var dir = Path.Combine(KaptionRoot, gameTag);
                if (!Directory.Exists(dir)) continue;

                // Enumerate once so we can cross-reference siblings without
                // hitting the filesystem twice per file.
                var gisubFiles = Directory.EnumerateFiles(dir, "*.gisub").ToList();
                var jsonFiles  = Directory.EnumerateFiles(dir, "*.json").ToList();

                // Detect packs from BOTH .gisub and plaintext .json. The
                // previous .gisub-only sweep missed packs that were hand-
                // placed by install-gamedata-local.sh (dev testing) or
                // that existed briefly during the startup migration window
                // — FileProtectionHelper.MigrateExistingFiles runs *after*
                // the first RefreshTranslationsAsync on launch, so a fresh
                // install appeared as zero packs until the next user-
                // triggered refresh. Including .json here closes that gap.
                //
                // Guardrails that stop TextMap{Input}.json from showing up
                // as a spurious target-language pack:
                //   1. A .json whose language equals Config["Input"] is
                //      skipped — it's the input mirror, not a target.
                //   2. A .json whose .gisub sibling already exists is
                //      skipped — the .gisub pass already counted it.
                string inputLang = (Config.Get("Input", "EN") ?? "EN").ToUpperInvariant();
                var candidates = new List<string>(gisubFiles.Count + jsonFiles.Count);
                candidates.AddRange(gisubFiles);
                foreach (var j in jsonFiles)
                {
                    string candLang = InferTargetLanguageFromFilename(Path.GetFileName(j));
                    if (candLang == null) continue;
                    if (string.Equals(candLang, inputLang, StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(Path.ChangeExtension(j, ".gisub"))) continue;
                    candidates.Add(j);
                }

                foreach (var file in candidates)
                {
                    string lang = InferTargetLanguageFromFilename(Path.GetFileName(file));
                    if (lang == null) continue;

                    var pack = EnsurePack(byKey, gameTag, gameDisplay, lang);
                    // LocalCache "wins" over nothing but loses to PaidCached later.
                    if (pack.Source != TranslationPackSource.PaidCached)
                        pack.Source = TranslationPackSource.LocalCache;

                    // Pick the biggest file for this (game, lang) — that's the
                    // dictionary proper. Smaller .gisubs in the same folder
                    // are supporting indexes (NpcNames, QuestInfo, etc.) and
                    // don't represent the language pack itself.
                    //
                    // FileInfo.Length throws if the file was deleted between
                    // EnumerateFiles and here (the narrow window during which
                    // migration is nuking .json after encrypting it). Treat
                    // that as "file gone, skip it" — the next scan will pick
                    // up the new .gisub.
                    long length;
                    DateTime lastWrite;
                    string fullName;
                    try
                    {
                        var fi = new FileInfo(file);
                        length = fi.Length;
                        lastWrite = fi.LastWriteTime;
                        fullName = fi.FullName;
                    }
                    catch (FileNotFoundException) { continue; }
                    catch (IOException) { continue; }

                    if (length > pack.LocalSize)
                    {
                        pack.LocalPath = fullName;
                        pack.LocalSize = length;
                        pack.LocalModifiedAt = lastWrite;
                    }
                }

                // Second pass: fill in provenance for each pack in this game
                // folder. Done after the .gisub loop so we work with the final
                // chosen LocalPath for each (game, lang).
                foreach (var pack in byKey.Values.Where(p => p.Game.Equals(gameTag, StringComparison.OrdinalIgnoreCase)
                                                          && p.Source == TranslationPackSource.LocalCache))
                {
                    FillLocalSourceFor(pack, dir, jsonFiles, gisubFiles);
                }
            }

            // ── 2. Paid-download provenance overlay ──
            //
            // v2.0.0+: DictionarySync writes into the same per-game folder we
            // just scanned (no dedicated `paid-dicts\` sibling anymore). The
            // manifest.json at the Kaption root is the single source of
            // truth for "this file came from our backend, not from a local
            // build". Cross-reference so the Translations tab can still
            // show "downloaded 2 h ago · v6.5".
            var manifest = DictionarySyncService.GetManifestSnapshot();
            foreach (var entry in manifest.Values)
            {
                if (entry == null || string.IsNullOrEmpty(entry.LocalPath)) continue;
                if (!File.Exists(entry.LocalPath)) continue;

                string gameTag = entry.Game;
                string gameDisplay = KnownGames.FirstOrDefault(g => g.Tag.Equals(gameTag, StringComparison.OrdinalIgnoreCase)).Display ?? gameTag;
                string lang = entry.Language?.ToUpperInvariant();
                if (string.IsNullOrEmpty(lang)) continue;

                var pack = EnsurePack(byKey, gameTag, gameDisplay, lang);
                pack.Source = TranslationPackSource.PaidCached;

                var fi = new FileInfo(entry.LocalPath);
                pack.LocalPath = fi.FullName;
                pack.LocalSize = fi.Length;
                pack.LocalModifiedAt = fi.LastWriteTime;

                // LOCAL manifest version — what's actually on disk. The real
                // remote-latest version is filled in below from the /files
                // endpoint; comparing the two drives CanUpdate on the row.
                pack.LocalVersion = entry.Version;
                pack.RemoteFileVersionId = entry.FileVersionId;
                pack.SourceFilePath = $"r2://kaption-files/dict/{gameTag.ToLowerInvariant()}/{lang.ToLowerInvariant()}/{entry.Version}.gisub-dist";
                pack.SourceFileSize = entry.FileSizeBytes;
                pack.SourceModifiedAt = DateTimeOffset.FromUnixTimeSeconds(entry.DownloadedAtUnix).LocalDateTime;
            }

            // ── 3. Remote catalog (requires an active session) ──
            var session = _license?.CurrentActivation;
            if (_api != null && session != null && !string.IsNullOrEmpty(session.DeviceSessionJwt))
            {
                try
                {
                    // Server defaults `game`/`language` to undefined when the
                    // query params are empty, so this returns the whole
                    // latest-per-(game, lang) catalog in one call.
                    var remote = await _api.GetFilesAsync(session.DeviceSessionJwt, null, null, ct)
                        .ConfigureAwait(false);

                    string userTier = session.EffectiveTier ?? "free_beta";

                    foreach (var meta in remote ?? (IReadOnlyList<FileMetadata>)Array.Empty<FileMetadata>())
                    {
                        if (string.IsNullOrWhiteSpace(meta.Game) || string.IsNullOrWhiteSpace(meta.Language))
                            continue;
                        string gameDisplay = KnownGames.FirstOrDefault(g => g.Tag.Equals(meta.Game, StringComparison.OrdinalIgnoreCase)).Display ?? meta.Game;
                        var pack = EnsurePack(byKey, meta.Game, gameDisplay, meta.Language);

                        pack.RemoteFileVersionId = meta.FileVersionId;
                        pack.RemoteVersion = meta.Version;
                        pack.RemoteSize = meta.Size;
                        pack.RemoteMinTier = meta.MinTier;
                        pack.RemoteUnlocked = TierAtLeast(userTier, meta.MinTier);

                        // Only set Source = Remote* if we don't already have
                        // a local copy. Installed files always keep their
                        // installed-from-X source.
                        if (!pack.IsInstalled)
                        {
                            pack.Source = pack.RemoteUnlocked
                                ? TranslationPackSource.RemoteAvailable
                                : TranslationPackSource.RemoteLocked;
                        }
                    }
                    result.RemoteQueryOk = true;
                }
                catch (UnauthorizedException ex)
                {
                    // Caller shows a "sign in again" banner instead of the list.
                    result.RemoteQueryError = "Session expired — sign in again to see the full catalog.";
                    Logger.Log.Warn($"Inventory: /files returned 401 — {ex.Message}");
                    // Trigger the re-login UX flow — same reasoning as in
                    // DictionarySyncService: server-side revocation between
                    // heartbeat ticks must not leave the app silently hitting
                    // 401 on every background scan.
                    try { _license?.ReportRemoteRevocation($"Inventory /files 401: {ex.Message}"); } catch { /* best-effort */ }
                }
                catch (ForbiddenException ex)
                {
                    // 403 = session is fine, tier just doesn't cover the catalog
                    // query. Show the catalog in "signed-in but no remote packs"
                    // mode so the user still sees their locally-built packs.
                    result.RemoteQueryError = "No remote packs available on your current plan.";
                    Logger.Log.Info($"Inventory: /files returned 403 — tier-gated. {ex.Message}");
                }
                catch (ApiUnavailableException ex)
                {
                    result.RemoteQueryError = "Offline — showing local packs only.";
                    Logger.Log.Warn($"Inventory: network unavailable: {ex.Message}");
                }
                catch (Exception ex)
                {
                    result.RemoteQueryError = "Server temporarily unavailable.";
                    Logger.Log.Warn($"Inventory: unexpected remote error: {ex.Message}");
                }
            }
            else
            {
                result.RemoteQueryError = "Not signed in — showing local packs only.";
            }

            // ── Finalize: order + counters + log summary ──
            foreach (var pack in byKey.Values.OrderBy(p => p.Game).ThenBy(p => p.Language))
            {
                // Back-fill display names if missing (e.g. local-only lang not in KnownGames display map).
                if (string.IsNullOrEmpty(pack.LanguageDisplayName))
                    pack.LanguageDisplayName = BuildLanguageDisplay(pack.Language);

                switch (pack.Source)
                {
                    case TranslationPackSource.LocalCache:
                        if (pack.HasLocalSource) result.InstalledLocalCacheWithSource++;
                        else                    result.InstalledLocalCacheOrphan++;
                        break;
                    case TranslationPackSource.PaidCached: result.InstalledPaidCached++; break;
                    case TranslationPackSource.RemoteAvailable: result.RemoteAvailable++; break;
                    case TranslationPackSource.RemoteLocked: result.RemoteLocked++; break;
                }
                result.Packs.Add(pack);
            }

            Logger.Log.Info(
                $"Inventory: {result.Packs.Count} translation packs — " +
                $"{result.InstalledLocalCacheWithSource} local-built (source present), " +
                $"{result.InstalledLocalCacheOrphan} local-built (orphan cache), " +
                $"{result.InstalledPaidCached} downloaded, " +
                $"{result.RemoteAvailable} remote-available, " +
                $"{result.RemoteLocked} remote-locked. " +
                $"Remote catalog: {(result.RemoteQueryOk ? "ok" : (result.RemoteQueryError ?? "skipped"))}.");

            // Per-pack provenance log so a support ticket can reconstruct
            // "where did this translation come from?" in one glance.
            //
            // Output format per installed pack, multi-line to stay readable
            // when grep'd from app.log:
            //
            //   Provenance: Genshin/PL [LocalCache]
            //     cache : %APPDATA%\Kaption\Genshin\TextMapEN_TextMapPL.v2.gisub (84.5 MB, 2026-04-13 19:52)
            //     source: %APPDATA%\Kaption\Genshin\TextMapPL.gisub (67.9 MB, 2026-03-16 14:10)
            //     origin: built locally (source file present alongside cache)
            //
            //   Provenance: Genshin/PL [PaidCached]
            //     cache : %APPDATA%\Kaption\paid-dicts\Genshin\TextMapPL.gisub (68.0 MB, 2026-05-12 10:04)
            //     source: r2://kaption-files/dict/genshin/pl/6.5.gisub-dist
            //     origin: downloaded via DictionarySync (manifest version 6.5)
            foreach (var pack in result.Packs.Where(p => p.IsInstalled))
            {
                string origin;
                switch (pack.Source)
                {
                    case TranslationPackSource.PaidCached:
                        origin = pack.LocalVersion != null
                            ? $"downloaded via DictionarySync (manifest version {pack.LocalVersion})"
                            : "downloaded via DictionarySync";
                        break;
                    case TranslationPackSource.LocalCache when pack.HasLocalSource:
                        origin = "built locally (source file present alongside cache)";
                        break;
                    case TranslationPackSource.LocalCache:
                        origin = "ORPHAN cache — source not found on disk; likely migrated from an older install or hand-placed. Kaption cannot prove where the bits came from.";
                        break;
                    default:
                        origin = pack.Source.ToString();
                        break;
                }

                Logger.Log.Info(
                    $"Provenance: {pack.Game}/{pack.Language} [{pack.Source}]\n" +
                    $"    cache : {pack.LocalPath} ({pack.SizeLabel}, {pack.LocalModifiedAt:yyyy-MM-dd HH:mm})\n" +
                    $"    source: {(string.IsNullOrEmpty(pack.SourceFilePath) ? "(none on disk)" : $"{pack.SourceFilePath}{(pack.SourceFileSize > 0 ? $" ({FormatBytes(pack.SourceFileSize)}, {pack.SourceModifiedAt:yyyy-MM-dd HH:mm})" : "")}")}\n" +
                    $"    origin: {origin}");
            }

            return result;
        }

        /// <summary>
        /// After the scan, answer "does the user have a usable pack for the
        /// configured target language, and if not, why?" Language-agnostic:
        /// the same code path will fire for Korean, Arabic, or any future
        /// target once Config["Output"] is set to that code.
        ///
        /// Returns null when the pack is ready to use (either a local cache
        /// is present OR a remote pack is available to this tier — which
        /// DictionarySync will pull on its own). Returns a non-null string
        /// describing WHY no pack is available so the caller can surface it
        /// verbatim in a ModernDialog.
        /// </summary>
        public static string ExplainMissingPack(
            DictionaryInventoryResult scan, string configuredGame, string configuredLang,
            bool isSignedIn, bool remoteCatalogOk)
        {
            if (scan == null) return null;
            if (string.IsNullOrWhiteSpace(configuredGame) || string.IsNullOrWhiteSpace(configuredLang))
                return null;

            var pack = scan.Packs.FirstOrDefault(p =>
                p.Game.Equals(configuredGame, StringComparison.OrdinalIgnoreCase) &&
                p.Language.Equals(configuredLang, StringComparison.OrdinalIgnoreCase));

            // Locally installed — nothing to warn about, even if it's an
            // orphan cache. The user has bytes on disk that the matcher can
            // load, which is what they care about right now.
            if (pack != null && pack.IsInstalled) return null;

            // Not locally installed but remote pack is available to this tier.
            // DictionarySync's background task will download it within ~10 s of
            // Loaded; no warning needed, the sync log will speak for itself.
            if (pack != null && pack.Source == TranslationPackSource.RemoteAvailable) return null;

            string langDisplay = BuildLanguageDisplay(configuredLang);
            string gameDisplay = KnownGames.FirstOrDefault(g => g.Tag.Equals(configuredGame, StringComparison.OrdinalIgnoreCase)).Display ?? configuredGame;

            // Remote pack exists but requires a higher tier — tell the user
            // exactly which tier.
            if (pack != null && pack.Source == TranslationPackSource.RemoteLocked)
            {
                string tier = pack.RemoteMinTier ?? "a higher";
                return $"{langDisplay} for {gameDisplay} is available on Kaption but requires {tier} tier. Upgrade from kaption.one/pricing to unlock it.";
            }

            // Nothing local, nothing remote, not signed in → maybe their
            // account would have a pack.
            if (!isSignedIn)
                return $"No {langDisplay} translation for {gameDisplay} is installed, and Kaption isn't signed in. Sign in to see what's available for your account.";

            // Nothing local, catalog offline.
            if (!remoteCatalogOk)
                return $"No {langDisplay} translation for {gameDisplay} is installed, and Kaption couldn't reach the server to check what's available. Check your internet connection and try again.";

            // Nothing local, catalog accessible, nothing offered → we just
            // haven't published this pack yet. Be honest about it.
            return $"No {langDisplay} translation for {gameDisplay} is installed, and Kaption hasn't published one for your tier yet. Until a pack ships, subtitles will show the original English text. Contact contact@kaption.one if you need Polish urgently.";
        }

        /// <summary>
        /// For a LocalCache pack, find the most plausible source file on disk
        /// and attach its metadata. Preference order:
        ///   1. `TextMap{Lang}.json`           (plaintext — translate_textmap.py output)
        ///   2. `TextMap{Lang}.gisub`          (machine-bound encrypted — VoiceContentHelper wrote it)
        ///   3. No sibling → leave SourceFilePath null (orphan cache).
        /// The "biggest sibling" rule would misfire here because the merged
        /// cache is itself huge; we want the SMALLER per-language file.
        /// </summary>
        private static void FillLocalSourceFor(
            TranslationPackInfo pack, string gameDir,
            IReadOnlyList<string> jsonFiles, IReadOnlyList<string> gisubFiles)
        {
            if (pack == null || string.IsNullOrEmpty(pack.LocalPath)) return;
            if (!string.IsNullOrEmpty(pack.SourceFilePath)) return; // already set

            string jsonName  = $"TextMap{pack.Language}.json";
            string gisubName = $"TextMap{pack.Language}.gisub";
            string cacheName = Path.GetFileName(pack.LocalPath);

            // JSON source beats .gisub source because it's the thing the
            // translator script produces — seeing it on disk is a clear
            // "someone built this locally" signal.
            string match = jsonFiles.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f), jsonName, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                // .gisub source is fine too (the JSON may have been encrypted
                // and removed). Avoid self-matching when the cache IS named
                // TextMap<Lang>.gisub — no useful "source vs cache" distinction
                // in that degenerate case.
                match = gisubFiles.FirstOrDefault(f =>
                {
                    string name = Path.GetFileName(f);
                    return string.Equals(name, gisubName, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(name, cacheName, StringComparison.OrdinalIgnoreCase);
                });
            }

            if (match == null) return;

            try
            {
                var fi = new FileInfo(match);
                pack.SourceFilePath = fi.FullName;
                pack.SourceFileSize = fi.Length;
                pack.SourceModifiedAt = fi.LastWriteTime;
            }
            catch
            {
                // File vanished between enumeration and FileInfo. Fine —
                // treat as no-source; the caller's worst case is a missing
                // tooltip, not a crash.
            }
        }

        /// <summary>Byte size formatter local to this service — the UI model has its own.</summary>
        private static string FormatBytes(long bytes)
        {
            const long KB = 1024, MB = KB * 1024, GB = MB * 1024;
            if (bytes >= GB) return $"{bytes / (double)GB:0.##} GB";
            if (bytes >= MB) return $"{bytes / (double)MB:0.#} MB";
            if (bytes >= KB) return $"{bytes / (double)KB:0.#} KB";
            return $"{bytes} B";
        }

        // ───────────────────────────────────────────────────────────────────
        //  Helpers
        // ───────────────────────────────────────────────────────────────────

        private static TranslationPackInfo EnsurePack(
            Dictionary<string, TranslationPackInfo> byKey,
            string gameTag, string gameDisplay, string lang)
        {
            string key = $"{gameTag.ToLowerInvariant()}/{lang.ToUpperInvariant()}";
            if (!byKey.TryGetValue(key, out var pack))
            {
                pack = new TranslationPackInfo
                {
                    Game = gameTag,
                    GameDisplayName = gameDisplay,
                    Language = lang.ToUpperInvariant(),
                    LanguageDisplayName = BuildLanguageDisplay(lang),
                };
                byKey[key] = pack;
            }
            return pack;
        }

        private static string BuildLanguageDisplay(string lang)
        {
            if (string.IsNullOrEmpty(lang)) return lang;
            string code = lang.ToUpperInvariant();
            if (LangDisplay.TryGetValue(code, out var name))
                return $"{name} ({code})";
            return code;
        }

        /// <summary>
        /// Extract the *target* language tag from a GSMX / legacy filename.
        /// Returns null when the file doesn't look like a language pack
        /// (e.g. NpcNames.gisub, QuestInfo.gisub, TextMapEN.json).
        /// </summary>
        private static string InferTargetLanguageFromFilename(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;

            var pairMatch = PairedTargetRegex().Match(fileName);
            if (pairMatch.Success) return pairMatch.Groups["lang"].Value.ToUpperInvariant();

            var soloMatch = SoloLangRegex().Match(fileName);
            if (soloMatch.Success) return soloMatch.Groups["lang"].Value.ToUpperInvariant();

            return null;
        }

        /// <summary>
        /// Tier rank matching backend/src/types.ts TIER_RANK. Kept local so
        /// a backend rank reshuffle doesn't silently alter client gating —
        /// if ranks diverge we prefer to show "locked" and let server 403 be
        /// authoritative than to mislead users.
        /// </summary>
        private static bool TierAtLeast(string current, string required)
        {
            if (string.IsNullOrEmpty(required)) return true;
            int c = RankOf(current);
            int r = RankOf(required);
            return c >= r;
        }

        private static int RankOf(string tier)
        {
            if (string.IsNullOrEmpty(tier)) return 0;
            switch (tier.ToLowerInvariant())
            {
                case "free_beta": return 0;
                case "pro":
                case "pro-30d":
                case "pro-180d":
                    return 10;
                case "admin": return 100;
                default: return 0;
            }
        }
    }
}
