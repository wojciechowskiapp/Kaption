// ─────────────────────────────────────────────────────────────────────────────
//  GameDataBootstrapService.cs
//  ---------------------------------------------------------------------------
//  First-run / self-heal orchestrator. Before this service existed, fresh
//  installs landed in a state where:
//    - TextMapEN.json was never downloaded (legacy "Pobierz dane" button was
//      the only trigger and users didn't know to click it).
//    - TextMapPL.gisub was downloaded by DictionarySync to a sibling folder
//      the matcher never read (the `paid-dicts\<game>\` path vs the
//      `<Game>\` path that VoiceContentHelper scanned).
//    - The matcher was therefore null forever, and MainWindow emitted
//      "Matcher not loaded yet, skipping translation" every OCR tick.
//
//  This service is the single entry point for "get me to a state where the
//  matcher can load". It's idempotent — safe to run on every launch — and
//  does the minimum work: conditional-GET against GitHub for public data,
//  DictionarySync against R2 for proprietary data. Both writes land at the
//  canonical `%APPDATA%\Kaption\<Game>\` location defined in
//  <see cref="GameDataPaths"/>.
//
//  Threading:
//    * All methods async, safe from any thread.
//    * Reports progress via <see cref="IProgress{T}"/>, not WPF bindings, so
//      the service has no UI dependency.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Security;
using GI_Subtitles.Services.Translation;
using GI_Subtitles.Services.Network;

namespace GI_Subtitles.Services.Data
{
    /// <summary>Summary of what the bootstrap actually did this pass.</summary>
    public sealed class GameDataBootstrapResult
    {
        /// <summary>Everything the matcher needs is on disk after this run.</summary>
        public bool Ready { get; internal set; }

        /// <summary>True if the input-language TextMap was downloaded (or refreshed).</summary>
        public bool InputDownloaded { get; internal set; }

        /// <summary>True if the output-language pack was downloaded (or refreshed).</summary>
        public bool OutputDownloaded { get; internal set; }

        /// <summary>True if the dialogue-graph auxiliary files were downloaded/rebuilt on this pass.</summary>
        public bool GraphDownloaded { get; internal set; }

        /// <summary>Human-readable reason when <see cref="Ready"/> is false.</summary>
        public string FailureReason { get; internal set; }
    }

    /// <summary>
    /// Ensures all per-game data files the matcher depends on are present
    /// before SettingsWindow.CheckDataAsync tries to build an OptimizedMatcher.
    /// </summary>
    public sealed class GameDataBootstrapService
    {
        private readonly LicenseService _license;
        private readonly IFileProtectionService _protector;

        public GameDataBootstrapService(LicenseService license, IFileProtectionService protector)
        {
            _license = license ?? throw new ArgumentNullException(nameof(license));
            _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        }

        /// <summary>
        /// Idempotent: ensures that after return, the matcher has enough data
        /// on disk to build an index for the <paramref name="game"/> /
        /// <paramref name="inputLang"/> → <paramref name="outputLang"/> triple.
        ///
        /// Order of operations (each step is a no-op when the file is already
        /// current, so repeated calls are cheap):
        ///   1. Public input-language TextMap from GitHub (GameDataUpdateService).
        ///   2. Public output-language TextMap from GitHub — only if the
        ///      language is mirrored upstream (DE/ES/FR/…, not PL).
        ///   3. Proprietary output-language <c>.gisub</c> from R2 — only for
        ///      languages the backend serves (currently PL).
        ///
        /// Returns a <see cref="GameDataBootstrapResult"/>; Ready=false when
        /// something was downloaded but the output-language TextMap is still
        /// missing on disk (e.g. the user's tier doesn't include the pack).
        /// </summary>
        public async Task<GameDataBootstrapResult> EnsureReadyAsync(
            string game,
            string inputLang,
            string outputLang,
            IProgress<(int percent, string message)> progress,
            CancellationToken ct)
        {
            var result = new GameDataBootstrapResult();

            if (string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(inputLang) || string.IsNullOrWhiteSpace(outputLang))
            {
                result.FailureReason = "Game / input / output language not configured.";
                Logger.Log.Warn($"Bootstrap: skipped — {result.FailureReason}");
                return result;
            }

            GameDataPaths.EnsureGameDir(game);

            // ── Step 1: input TextMap (always public, always GitHub) ─────
            progress?.Report((5, $"Checking input language ({inputLang})..."));
            bool haveInput = File.Exists(GameDataPaths.TextMapJson(game, inputLang));
            if (!haveInput)
            {
                progress?.Report((10, $"Downloading {inputLang} language data..."));
                Logger.Log.Info($"Bootstrap: input TextMap{inputLang.ToUpperInvariant()}.json missing — fetching from GitHub.");
                try
                {
                    var updater = new GameDataUpdateService();
                    await updater.CheckAndUpdateAsync(game, inputLang, outputLang: null, ct).ConfigureAwait(false);
                    result.InputDownloaded = File.Exists(GameDataPaths.TextMapJson(game, inputLang));
                }
                catch (Exception ex)
                {
                    Logger.Log.Error($"Bootstrap: input download threw: {ex.Message}");
                    result.FailureReason = $"Input download failed: {ex.Message}";
                    return result;
                }
            }
            else
            {
                Logger.Log.Debug($"Bootstrap: input TextMap{inputLang.ToUpperInvariant()}.json already present.");
            }

            if (!File.Exists(GameDataPaths.TextMapJson(game, inputLang)))
            {
                result.FailureReason = $"Could not obtain TextMap{inputLang.ToUpperInvariant()}.json from upstream.";
                Logger.Log.Warn($"Bootstrap: {result.FailureReason}");
                return result;
            }

            // ── Step 2: output TextMap — route depends on whether the
            //    language is mirrored publicly. For Polish and any future
            //    Kaption-exclusive language, DictionarySync (R2) is the
            //    only source. For DE/ES/FR/etc., GitHub is authoritative.
            progress?.Report((35, $"Checking output language ({outputLang})..."));

            bool haveOutput = GameDataPaths.HasAnyTextMap(game, outputLang);
            bool mirrored = GameDataUpdateService.IsUpstreamMirrored(game, outputLang);

            if (!haveOutput)
            {
                if (mirrored)
                {
                    progress?.Report((45, $"Downloading {outputLang} language data..."));
                    Logger.Log.Info($"Bootstrap: output TextMap{outputLang.ToUpperInvariant()} missing — fetching from GitHub (mirrored).");
                    try
                    {
                        var updater = new GameDataUpdateService();
                        await updater.CheckAndUpdateAsync(game, inputLang, outputLang, ct).ConfigureAwait(false);
                        result.OutputDownloaded = GameDataPaths.HasAnyTextMap(game, outputLang);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log.Error($"Bootstrap: mirrored output download threw: {ex.Message}");
                        result.FailureReason = $"Output download failed: {ex.Message}";
                        return result;
                    }
                }
                else
                {
                    progress?.Report((55, $"Downloading {outputLang} translation pack..."));
                    Logger.Log.Info($"Bootstrap: output TextMap{outputLang.ToUpperInvariant()} missing and not mirrored — using DictionarySync (R2).");
                    try
                    {
                        var sync = new DictionarySyncService(
                            new KaptionApiClient(),
                            _license,
                            _protector);
                        var syncResult = await sync.SyncAsync(game, outputLang, ct).ConfigureAwait(false);
                        result.OutputDownloaded = syncResult.Downloaded > 0;

                        if (!GameDataPaths.HasAnyTextMap(game, outputLang))
                        {
                            // DictionarySync ran but the file still isn't there — usually
                            // means the user's tier doesn't cover this pack. Log loudly;
                            // the caller can surface a banner ("Polish is a paid upgrade").
                            result.FailureReason =
                                $"No pack available for {outputLang.ToUpperInvariant()} on your current tier.";
                            Logger.Log.Warn($"Bootstrap: {result.FailureReason}");
                            return result;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log.Error($"Bootstrap: DictionarySync threw: {ex.Message}");
                        result.FailureReason = $"Translation-pack sync failed: {ex.Message}";
                        return result;
                    }
                }
            }
            else
            {
                // Already have the output on disk. Still opportunistically
                // refresh: GameDataUpdateService is throttled + conditional,
                // DictionarySync compares version IDs — both are cheap when
                // nothing has changed upstream.
                Logger.Log.Debug($"Bootstrap: output TextMap{outputLang.ToUpperInvariant()} already present; opportunistic refresh.");
                try
                {
                    if (mirrored)
                    {
                        var updater = new GameDataUpdateService();
                        await updater.CheckAndUpdateAsync(game, inputLang, outputLang, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var sync = new DictionarySyncService(
                            new KaptionApiClient(),
                            _license,
                            _protector);
                        await sync.SyncAsync(game, outputLang, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    // Refresh is opportunistic — don't fail the bootstrap on
                    // network wobble when we already have cached data.
                    Logger.Log.Warn($"Bootstrap: opportunistic refresh failed (non-fatal): {ex.Message}");
                }
            }

            // ── Step 3: gamedata bundle (prediction indexes) from R2 ──
            //
            // Session 24 (2026-04-16): replaces the DialogGraphDownloader
            // runtime-rebuild path for users whose tier gets a published
            // bundle (all tiers today). GamedataSyncService pulls the
            // latest bundle for this game, splits into the 5 per-file
            // .gisub outputs DialogueContextEngine.Load already reads.
            //
            // If this step skips (no bundle published yet / offline /
            // unlicensed), DialogueContextEngine falls through to the
            // legacy DialogGraphDownloader.DownloadAndBuild path — it
            // only fires when the files don't exist, and the bundle
            // produces the same filenames, so the two paths are
            // mutually exclusive by construction.
            progress?.Report((75, "Checking dialogue prediction bundle..."));
            try
            {
                var gamedataSync = new GamedataSyncService(
                    new KaptionApiClient(),
                    _license,
                    _protector);
                var gamedataResult = await gamedataSync.SyncAsync(game, ct).ConfigureAwait(false);
                result.GraphDownloaded = gamedataResult.Downloaded;

                if (gamedataResult.Failed)
                {
                    Logger.Log.Warn(
                        $"Bootstrap: gamedata sync failed ({gamedataResult.Message}) — " +
                        "DialogueContextEngine will fall back to GitHub/runtime build.");
                }
            }
            catch (Exception ex)
            {
                // Non-fatal. Prediction engine can still work off the
                // legacy path. Log so we can trace bundle adoption.
                Logger.Log.Warn($"Bootstrap: gamedata sync threw (non-fatal): {ex.Message}");
            }

            progress?.Report((100, "Language data ready."));
            result.Ready = true;
            return result;
        }
    }
}
