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
        ///   1. Public input-language TextMap from GitHub (GameDataUpdateService)
        ///      — skipped for games with no upstream mirror, where the gamedata
        ///      bundle in step 3 is the source and step 1's check is deferred
        ///      until after it has run.
        ///   2. Public output-language TextMap from GitHub — only if the
        ///      language is mirrored upstream (DE/ES/FR/…, not PL).
        ///   3. Proprietary output-language <c>.gisub</c> from R2 — only for
        ///      languages the backend serves (currently PL) — then the gamedata
        ///      bundle, which for ZZZ also carries TextMapEN.json.
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

            // ── Step 1: input TextMap ────────────────────────────────────
            //
            // Two sources exist, and which one applies is decided by
            // GameDataUpdateService.IsUpstreamMirrored — the single predicate
            // that also stops two writers ever racing for this file:
            //
            //   mirrored     → a public mirror is authoritative (Genshin, HSR).
            //                  Fetch it here, and a still-missing file after
            //                  the fetch is fatal: nothing later in this method
            //                  can produce one.
            //   not mirrored → the gamedata bundle carries it (ZZZ, whose
            //                  dialogue ids are minted by
            //                  tools/build-gamedata-zzz.cjs and exist in no
            //                  upstream file). That bundle arrives in step 3,
            //                  so failing here would guarantee we never get it
            //                  — the exact "installs cleanly, resolves nothing"
            //                  shape this pipeline is built to avoid.
            //
            // The post-step-3 re-check below is what makes the second branch
            // safe: we still refuse to report Ready without the file, we just
            // ask the question after the source that provides it has run.
            bool inputMirrored = GameDataUpdateService.IsUpstreamMirrored(game, inputLang);
            progress?.Report((5, $"Checking input language ({inputLang})..."));
            bool haveInput = File.Exists(GameDataPaths.TextMapJson(game, inputLang));

            if (!inputMirrored)
            {
                Logger.Log.Info(
                    $"Bootstrap: {game}/{inputLang.ToUpperInvariant()} has no upstream mirror — " +
                    $"TextMap{inputLang.ToUpperInvariant()}.json ships inside the gamedata bundle " +
                    (haveInput ? "(already installed)." : "(step 3 will install it)."));
            }
            else if (!haveInput)
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

            if (inputMirrored && !File.Exists(GameDataPaths.TextMapJson(game, inputLang)))
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

            // ── Step 1 (deferred half): the bundle-carried input TextMap ──
            //
            // For a game with no upstream mirror, step 3 was the only source of
            // TextMap<Input>.json. Ask now. Without this the method would
            // happily report Ready with no way to resolve a single dialogue
            // hash, and the only symptom would be subtitles that never appear.
            if (!inputMirrored && !File.Exists(GameDataPaths.TextMapJson(game, inputLang)))
            {
                // Two very different causes land here and they need different
                // instructions. A bundle carries exactly ONE source language —
                // English, because EN is the key space the builder mints ids
                // for — so if TextMapEN.json is sitting there, the bundle
                // arrived fine and the user has simply picked a source language
                // this game cannot offer. Pointing that user at the sync log
                // sends them hunting for an outage that never happened.
                bool bundleLanded = File.Exists(GameDataPaths.TextMapJson(game, "EN"));

                result.FailureReason = bundleLanded
                    ? $"{game} only has English source text — its dialogue ids are minted at build " +
                      $"time and no {inputLang.ToUpperInvariant()} version exists. Set the source " +
                      "language to English in Settings."
                    : $"TextMap{inputLang.ToUpperInvariant()}.json for {game} ships inside the gamedata " +
                      "bundle, and no bundle was installed. Check the gamedata sync log lines above " +
                      "(no bundle published for this game, offline, or tier-gated).";
                Logger.Log.Warn($"Bootstrap: {result.FailureReason}");
                return result;
            }

            progress?.Report((100, "Language data ready."));
            result.Ready = true;
            return result;
        }
    }
}
