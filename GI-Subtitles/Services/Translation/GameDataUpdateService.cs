// ─────────────────────────────────────────────────────────────────────────────
//  GameDataUpdateService.cs
//  ---------------------------------------------------------------------------
//  Keeps the public game-data `TextMap*.json` files on disk in sync with the
//  upstream mirrors (Dimbreath/animegamedata2 for Genshin, Dimbreath's
//  turnbasedgamedata GitLab for Honkai Star Rail).
//
//  The mirrors carry different language sets and different file-name shapes,
//  so IsUpstreamMirrored and ResolveUpstream are both per-game.
//
//  Zenless Zone Zero has NO runtime mirror here on purpose. Its on-disk
//  TextMapEN.json is minted by tools/build-gamedata-zzz.cjs and delivered as
//  the gamedata bundle's `textmap_en` section (GamedataSyncService); the
//  string-keyed file on git.mero.moe is a publish-time build input, not
//  something this service may ever write into a game folder. See
//  IsUpstreamMirrored for the full reasoning.
//
//  Why this exists:
//    Input-side TextMaps (what the OCR reads + what we use to resolve
//    dialogue hashes to English text) arrive from public game-data mirrors,
//    not from our backend. Historically the app downloaded them once on
//    first launch via the "Download Data" button and then never checked
//    again — meaning a user who installed before game patch 6.5 would see
//    6.4-era English strings for anything new that shipped after their
//    install, with no way to refresh short of a full reinstall.
//
//    Output-side TextMaps from the same mirror (DE, ES, FR, etc.) have the
//    same problem. Polish is NOT mirrored, so its upstream is Kaption's own
//    R2 and DictionarySyncService handles that path separately.
//
//  What this service does, per check pass:
//    1. For each (game, lang) pair in scope, locate the sibling
//       `TextMap<Lang>.meta.json` sidecar.
//    2. If the sidecar says we checked within the throttle window
//       (<see cref="CheckThrottle"/>), skip entirely — no network call.
//    3. Otherwise issue a conditional GET with If-None-Match (ETag) +
//       If-Modified-Since (Last-Modified) populated from the sidecar.
//       304 → stamp the new check timestamp in the sidecar and return.
//    4. 200 → write the response body to a `.tmp` file, verify SHA-256,
//       atomic-rename into place, delete every downstream `.gisub` cache
//       derived from this TextMap so VoiceContentHelper rebuilds from the
//       fresh plaintext on the user's next launch.
//    5. Return a `CheckResult` carrying per-lang outcomes so the caller
//       (MainWindow) can decide whether to prompt the user to restart.
//
//  Threading:
//    * All public methods are async and safe from any thread.
//    * The service holds no mutable state.
//
//  Budget awareness:
//    * Conditional GETs against raw.githubusercontent.com cost ~200 bytes of
//      request + either a 304 (empty body) or a full 65 MB payload. At
//      CheckThrottle=6h cadence that's 4 checks/day/user — negligible.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>Per-language result of an upstream check.</summary>
    public enum GameDataUpdateOutcome
    {
        /// <summary>Sidecar said we checked recently — no network call made.</summary>
        Throttled,
        /// <summary>Conditional GET returned 304 — nothing changed.</summary>
        UpToDate,
        /// <summary>Content was new; downloaded, verified, staged; caller should prompt restart.</summary>
        Updated,
        /// <summary>
        /// Upstream doesn't have this language mirrored (e.g. Polish on
        /// DimbreathBot). Not an error — caller treats as a no-op.
        /// </summary>
        NotMirrored,
        /// <summary>Network / parse / disk error. See <see cref="GameDataUpdateLanguageResult.ErrorMessage"/>.</summary>
        Failed,
    }

    public sealed class GameDataUpdateLanguageResult
    {
        public string Game { get; internal set; }
        public string Language { get; internal set; }
        public GameDataUpdateOutcome Outcome { get; internal set; }
        public string LocalPath { get; internal set; }
        public string ErrorMessage { get; internal set; }
    }

    public sealed class GameDataUpdateResult
    {
        public List<GameDataUpdateLanguageResult> Languages { get; } = new List<GameDataUpdateLanguageResult>();

        /// <summary>Convenience: true when at least one language was refreshed during this check.</summary>
        public bool AnyUpdated
        {
            get
            {
                foreach (var l in Languages)
                    if (l.Outcome == GameDataUpdateOutcome.Updated) return true;
                return false;
            }
        }
    }

    /// <summary>
    /// On-disk shape of <c>TextMap&lt;Lang&gt;.meta.json</c>. Persisted via
    /// Newtonsoft.Json for consistency with the rest of the app's config I/O.
    /// Any shape change needs to stay backwards-compatible — a stale sidecar
    /// just means we'll hit upstream once unnecessarily, which is fine.
    /// </summary>
    public sealed class GameDataMetaSidecar
    {
        [JsonProperty("source")]
        public string Source { get; set; } // e.g. "gitlab:Dimbreath/animegamedata2"

        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonProperty("etag")]
        public string Etag { get; set; }

        [JsonProperty("last_modified_header")]
        public string LastModifiedHeader { get; set; }

        [JsonProperty("downloaded_at_unix")]
        public long DownloadedAtUnix { get; set; }

        [JsonProperty("checked_at_unix")]
        public long CheckedAtUnix { get; set; }

        [JsonProperty("file_sha256")]
        public string FileSha256 { get; set; }

        [JsonProperty("file_size")]
        public long FileSize { get; set; }
    }

    /// <summary>
    /// Stateless orchestrator. See file header for full behaviour. Construct
    /// per call — no persistent state beyond the HttpClient it shares with
    /// the rest of the process.
    /// </summary>
    public sealed class GameDataUpdateService
    {
        // 6-hour throttle between upstream checks — keeps a user who
        // relaunches frequently from hammering GitHub/GitLab without
        // adding much latency to actual updates (game patches ship every
        // ~6 weeks; a 6-hour delay on picking up a new one is invisible).
        private static readonly TimeSpan CheckThrottle = TimeSpan.FromHours(6);

        // Startup deliberately performs a conditional check on every launch,
        // but an offline network or captive portal must not hold the matcher
        // gate for HttpClient's full download timeout. This deadline covers
        // only the wait for response headers; once a real update starts, the
        // body keeps streaming with the caller's token so slower connections
        // can still finish a large TextMap safely.
        private static readonly TimeSpan ResponseHeadersTimeout = TimeSpan.FromSeconds(20);

        // Same shared HttpClient as elsewhere in the app.
        private static readonly HttpClient _http = BuildHttpClient();

        private static HttpClient BuildHttpClient()
        {
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd($"Kaption-GameDataUpdate/{KaptionVersion()}");
            return c;
        }

        private static async Task<HttpResponseMessage> SendForHeadersAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                headerCts.CancelAfter(ResponseHeadersTimeout);
                return await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    headerCts.Token).ConfigureAwait(false);
            }
        }

        private static string KaptionVersion()
        {
            try { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0"; }
            catch { return "0"; }
        }

        private static readonly string KaptionRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kaption");

        // Languages Dimbreath's GitLab mirrors carry for Genshin and Star Rail.
        // User confirmed via directory listing on 2026-04-15. Polish is
        // deliberately absent — it's Kaption-exclusive and DictionarySyncService
        // owns that path.
        private static readonly HashSet<string> MirroredDimbreath =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CHS", "CHT", "DE", "EN", "ES", "FR", "ID", "IT",
                "JP", "KR", "PT", "RU", "TH", "TR", "VI",
            };

        /// <summary>
        /// Whether the given (game, lang) pair has an upstream mirror this
        /// service may fetch into
        /// <c>%APPDATA%\Kaption\&lt;Game&gt;\TextMap&lt;LANG&gt;.json</c>.
        /// Polish and any future Kaption-exclusive translation return false —
        /// they are handled by <see cref="DictionarySyncService"/>, not here.
        ///
        /// <para>This is the single routing predicate for "where does this
        /// game's on-disk TextMap come from", and three call sites depend on
        /// exactly that meaning: <see cref="ResolveUpstream"/> (returns null
        /// when it is false, making <c>CheckOneAsync</c> a no-op),
        /// <c>GameDataBootstrapService</c> step 1 (must not hard-fail on a
        /// missing input TextMap that the gamedata bundle is going to supply),
        /// and <c>GamedataSyncService</c>'s <c>textmap_en</c> section (which
        /// must be the ONLY writer of TextMapEN.json for the games that carry
        /// it).</para>
        ///
        /// <para><b>ZZZ returns false for every language, deliberately.</b>
        /// ZenlessData does mirror 13 TextMaps, but they are keyed by
        /// human-readable strings ("Main_Chat_Chapter01_3000024_01") while the
        /// desktop reads <c>dialog_graph.h</c> as a <c>ulong</c>. Kaption's ZZZ
        /// id space is therefore synthetic — minted by
        /// <c>tools/build-gamedata-zzz.cjs</c> and delivered in the bundle's
        /// <c>textmap_en</c> section — so a string-keyed upstream file dropped
        /// into that folder would resolve zero hashes while looking perfectly
        /// healthy. The upstream ZZZ TextMap is a BUILD INPUT
        /// (<c>Kaption.PublishStudio/Profiles/ZenlessZoneZeroProfile.GetTranslationDownloads</c>
        /// and <c>scripts/publish-gamedata.sh --textmap-base/--textmap-overwrite</c>
        /// both pull it from Gitea at publish time), never a runtime
        /// download.</para>
        ///
        /// <para>An unregistered game returns false, which matches what
        /// <see cref="ResolveUpstream"/> already did for it.</para>
        /// </summary>
        public static bool IsUpstreamMirrored(string game, string lang)
        {
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(lang)) return false;

            switch (game.ToLowerInvariant())
            {
                case "genshin":
                case "starrail":
                    return MirroredDimbreath.Contains(lang);
                case "zzz":
                    // Not an oversight, and not a claim about what ZenlessData
                    // carries — see the remarks above.
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Resolve the upstream URL + source label for (game, lang).
        /// Returns null when no upstream mirror is known — PL on DimbreathBot,
        /// or any ZZZ language (see <see cref="IsUpstreamMirrored"/>). Callers
        /// skip the pair.
        /// </summary>
        public static (string url, string source) ResolveUpstream(string game, string lang)
        {
            if (!IsUpstreamMirrored(game, lang)) return (null, null);

            // Keep the URL shapes in one place so this auto-check path and any
            // manual re-download path someone adds later cannot drift.
            switch ((game ?? string.Empty).ToLowerInvariant())
            {
                case "genshin":
                    return (
                        $"https://gitlab.com/Dimbreath/animegamedata2/-/raw/main/TextMap/TextMap{lang.ToUpperInvariant()}.json",
                        "gitlab:Dimbreath/animegamedata2"
                    );
                case "starrail":
                    return (
                        $"https://gitlab.com/Dimbreath/turnbasedgamedata/-/raw/main/TextMap/TextMap{lang.ToUpperInvariant()}.json?inline=false",
                        "gitlab:Dimbreath/turnbasedgamedata"
                    );
                default:
                    // Unreachable for ZZZ: IsUpstreamMirrored already returned
                    // false. There is no ZZZ arm on purpose — one would hand a
                    // string-keyed TextMap to a numeric-keyed graph. If you
                    // need the Gitea raw URL, it belongs in the publish
                    // tooling, which is where the two callers that legitimately
                    // want it already keep it.
                    return (null, null);
            }
        }

        /// <summary>
        /// Check + (if needed) refresh the public TextMaps for the user's
        /// currently configured (game, input, output) triple.
        /// </summary>
        public async Task<GameDataUpdateResult> CheckAndUpdateAsync(
            string game, string inputLang, string outputLang, CancellationToken ct,
            bool forceCheck = false)
        {
            var result = new GameDataUpdateResult();
            if (string.IsNullOrWhiteSpace(game)) return result;

            // Input language — checked whenever a mirror is the authoritative
            // source for it, because the PaddleOCR recognizer + hash-resolution
            // chain needs that mirror's canonical TextMap. CheckOneAsync
            // no-ops (NotMirrored) for a game whose TextMap comes from the
            // gamedata bundle instead, so there is exactly one writer per file.
            if (!string.IsNullOrWhiteSpace(inputLang))
                result.Languages.Add(await CheckOneAsync(game, inputLang, ct, forceCheck).ConfigureAwait(false));

            // Output language — check only when the mirror covers it. If
            // it doesn't (e.g. Polish), DictionarySyncService is the
            // authoritative update path. Avoid duplicating work.
            if (!string.IsNullOrWhiteSpace(outputLang) &&
                !string.Equals(outputLang, inputLang, StringComparison.OrdinalIgnoreCase) &&
                IsUpstreamMirrored(game, outputLang))
            {
                result.Languages.Add(await CheckOneAsync(game, outputLang, ct, forceCheck).ConfigureAwait(false));
            }

            return result;
        }

        // ────────────────────────────────────────────────────────────────────
        //  Per-file pipeline
        // ────────────────────────────────────────────────────────────────────

        private async Task<GameDataUpdateLanguageResult> CheckOneAsync(
            string game, string lang, CancellationToken ct, bool forceCheck)
        {
            var r = new GameDataUpdateLanguageResult { Game = game, Language = lang };

            var (url, source) = ResolveUpstream(game, lang);
            if (string.IsNullOrEmpty(url))
            {
                r.Outcome = GameDataUpdateOutcome.NotMirrored;
                return r;
            }

            string gameDir = Path.Combine(KaptionRoot, game);
            string jsonPath = Path.Combine(gameDir, $"TextMap{lang.ToUpperInvariant()}.json");
            string metaPath = Path.Combine(gameDir, $"TextMap{lang.ToUpperInvariant()}.meta.json");
            r.LocalPath = jsonPath;

            try
            {
                if (!Directory.Exists(gameDir)) Directory.CreateDirectory(gameDir);

                var sidecar = LoadSidecar(metaPath);

                // Throttle: if we checked recently AND we actually have the
                // JSON on disk, skip the network call entirely.
                long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!forceCheck &&
                    sidecar != null &&
                    File.Exists(jsonPath) &&
                    nowUnix - sidecar.CheckedAtUnix < CheckThrottle.TotalSeconds)
                {
                    // Upgrade path (2026-04-16+): users coming from a pre-shard
                    // build have a 15 MB TextMapEN.json on disk that was
                    // considered "current" under the old throttle. Force the
                    // Medium-shard merge once if the file looks like the
                    // Small-only shard (<30 MB). Subsequent launches are
                    // cheap because the Medium shard has its own sidecar.
                    if (string.Equals(game, "Genshin", StringComparison.OrdinalIgnoreCase))
                    {
                        long sizeOnDisk = new FileInfo(jsonPath).Length;
                        if (sizeOnDisk < 30L * 1024 * 1024)
                        {
                            Logger.Log.Info(
                                $"GameDataUpdate: {game}/{lang} throttled but local file is " +
                                $"{sizeOnDisk / 1024 / 1024} MB (Small-only) — fetching Medium shard once.");
                            try
                            {
                                await FetchAndMergeMediumShardAsync(lang, jsonPath, ct).ConfigureAwait(false);
                                InvalidateDownstreamCaches(gameDir, lang);
                            }
                            catch (Exception ex)
                            {
                                Logger.Log.Warn($"GameDataUpdate: {game}/{lang} upgrade-path Medium merge failed: {ex.Message}");
                            }
                        }
                    }

                    r.Outcome = GameDataUpdateOutcome.Throttled;
                    Logger.Log.Debug(
                        $"GameDataUpdate: {game}/{lang} throttled " +
                        $"(last checked {(nowUnix - sidecar.CheckedAtUnix) / 60} min ago).");
                    return r;
                }

                Logger.Log.Info($"GameDataUpdate: checking {game}/{lang} upstream at {url}");

                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    // Conditional GET: GitHub honours both If-None-Match and
                    // If-Modified-Since on raw.githubusercontent.com; GitLab
                    // honours If-Modified-Since. Populate whichever we have.
                    if (sidecar != null && !string.IsNullOrEmpty(sidecar.Etag))
                    {
                        try
                        {
                            req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(sidecar.Etag, isWeak: sidecar.Etag.StartsWith("W/")));
                        }
                        catch (FormatException) { /* malformed sidecar etag — ignore, let the body comparison handle it */ }
                    }
                    if (sidecar != null && !string.IsNullOrEmpty(sidecar.LastModifiedHeader)
                        && DateTimeOffset.TryParse(sidecar.LastModifiedHeader, out var lm))
                    {
                        req.Headers.IfModifiedSince = lm;
                    }

                    using (var resp = await SendForHeadersAsync(req, ct).ConfigureAwait(false))
                    {
                        if (resp.StatusCode == HttpStatusCode.NotModified)
                        {
                            // 304 — unchanged; stamp the sidecar so we honour
                            // the throttle on the next launch.
                            if (sidecar == null) sidecar = new GameDataMetaSidecar { Url = url, Source = source };
                            sidecar.CheckedAtUnix = nowUnix;
                            SaveSidecar(metaPath, sidecar);

                            // Genshin's dialogue corpus lives in a separate
                            // Medium shard whose version can change even when
                            // the Small/UI TextMap returns 304. Probe it on the
                            // same startup pass or newly added dialogue can be
                            // missed until an unrelated Small-shard update.
                            if (await RefreshGenshinMediumAfterUnchangedSmallAsync(
                                    game, lang, jsonPath, gameDir, ct).ConfigureAwait(false))
                            {
                                r.Outcome = GameDataUpdateOutcome.Updated;
                                return r;
                            }

                            r.Outcome = GameDataUpdateOutcome.UpToDate;
                            Logger.Log.Info($"GameDataUpdate: {game}/{lang} is up-to-date (304).");
                            return r;
                        }

                        if (!resp.IsSuccessStatusCode)
                        {
                            r.Outcome = GameDataUpdateOutcome.Failed;
                            r.ErrorMessage = $"HTTP {(int)resp.StatusCode} from upstream";
                            Logger.Log.Warn($"GameDataUpdate: {game}/{lang} upstream returned {(int)resp.StatusCode}.");
                            return r;
                        }

                        // Fresh content — stream to temp, hash as we go,
                        // atomic-rename on success. If something goes wrong
                        // mid-stream we don't corrupt the existing good
                        // copy.
                        string tmpPath = jsonPath + ".tmp";
                        long sizeBytes;
                        string sha256Hex;
                        try
                        {
                            using (var responseStream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                            using (var sha = SHA256.Create())
                            {
                                var buf = new byte[64 * 1024];
                                int read;
                                long total = 0;
                                while ((read = await responseStream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                                {
                                    await fileStream.WriteAsync(buf, 0, read, ct).ConfigureAwait(false);
                                    sha.TransformBlock(buf, 0, read, null, 0);
                                    total += read;
                                }
                                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                                sizeBytes = total;
                                sha256Hex = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                            }

                            // SHA early-out — if the new bytes are byte-
                            // identical to what we already have on disk, we
                            // treat as UpToDate (even though the server
                            // didn't give us a 304). This happens when
                            // GitHub mirrors trip over cache busting but
                            // the file is actually unchanged.
                            if (sidecar != null && !string.IsNullOrEmpty(sidecar.FileSha256)
                                && string.Equals(sidecar.FileSha256, sha256Hex, StringComparison.OrdinalIgnoreCase)
                                && File.Exists(jsonPath))
                            {
                                TryDelete(tmpPath);
                                sidecar.CheckedAtUnix = nowUnix;
                                SaveSidecar(metaPath, sidecar);
                                if (await RefreshGenshinMediumAfterUnchangedSmallAsync(
                                        game, lang, jsonPath, gameDir, ct).ConfigureAwait(false))
                                {
                                    r.Outcome = GameDataUpdateOutcome.Updated;
                                    return r;
                                }
                                r.Outcome = GameDataUpdateOutcome.UpToDate;
                                Logger.Log.Info($"GameDataUpdate: {game}/{lang} body matched existing sha — skipping.");
                                return r;
                            }

                            // Commit: replace the old file atomically.
                            if (File.Exists(jsonPath)) File.Delete(jsonPath);
                            File.Move(tmpPath, jsonPath);
                        }
                        catch
                        {
                            TryDelete(tmpPath);
                            throw;
                        }

                        // Sidecar update
                        string etag = null;
                        if (resp.Headers.ETag != null) etag = resp.Headers.ETag.Tag;

                        string lastModified = null;
                        if (resp.Content.Headers.LastModified.HasValue)
                            lastModified = resp.Content.Headers.LastModified.Value.ToString("R");

                        SaveSidecar(metaPath, new GameDataMetaSidecar
                        {
                            Source = source,
                            Url = url,
                            Etag = etag,
                            LastModifiedHeader = lastModified,
                            DownloadedAtUnix = nowUnix,
                            CheckedAtUnix = nowUnix,
                            FileSha256 = sha256Hex,
                            FileSize = sizeBytes,
                        });

                        // ── Genshin Medium-shard merge (2026-04-16+) ──
                        //
                        // DimbreathBot sharded its TextMap into
                        // "TextMap<LANG>.json" (Small, ~15 MB, UI/items)
                        // and "TextMap_Medium<LANG>.json" (~50 MB,
                        // dialogue). Older builds only downloaded Small,
                        // which meant ~70% of EN text (every hash that
                        // appears in DialogGraph) was missing — NPC
                        // names resolved to empty, HOT CACHE built with
                        // 0 entries, matcher got no prediction assist.
                        //
                        // We always attempt the Medium shard after a
                        // successful Small download (or when the local
                        // Small file exists but is suspiciously small,
                        // catching users who upgraded from a pre-shard
                        // build with a 15 MB file on disk). Failure is
                        // non-fatal — matcher still works on the Small
                        // shard's corpus, just without full dialogue
                        // coverage.
                        if (string.Equals(game, "Genshin", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                await FetchAndMergeMediumShardAsync(lang, jsonPath, ct).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                Logger.Log.Warn($"GameDataUpdate: {game}/{lang} Medium-shard merge failed (non-fatal): {ex.Message}");
                            }
                        }

                        // Re-stat the (potentially merged) file so the
                        // sidecar + log line show the real final size.
                        long finalSize = File.Exists(jsonPath) ? new FileInfo(jsonPath).Length : sizeBytes;

                        // Any downstream caches derived from this JSON are
                        // now stale. Blow them away so VoiceContentHelper
                        // rebuilds on next launch from the fresh bytes.
                        int invalidated = InvalidateDownstreamCaches(gameDir, lang);

                        Logger.Log.Info(
                            $"GameDataUpdate: {game}/{lang} updated — {finalSize:N0} bytes on disk " +
                            $"(Small payload was {sizeBytes:N0}, sha={sha256Hex.Substring(0, 12)}…), " +
                            $"invalidated {invalidated} cache file(s).");

                        r.Outcome = GameDataUpdateOutcome.Updated;
                        return r;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                r.Outcome = GameDataUpdateOutcome.Failed;
                r.ErrorMessage = "cancelled";
                return r;
            }
            catch (Exception ex)
            {
                r.Outcome = GameDataUpdateOutcome.Failed;
                r.ErrorMessage = ex.Message;
                Logger.Log.Warn($"GameDataUpdate: {game}/{lang} check failed: {ex.Message}");
                return r;
            }
        }

        private async Task<bool> RefreshGenshinMediumAfterUnchangedSmallAsync(
            string game, string lang, string jsonPath, string gameDir, CancellationToken ct)
        {
            if (!string.Equals(game, "Genshin", StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                bool mediumUpdated = await FetchAndMergeMediumShardAsync(
                    lang, jsonPath, ct).ConfigureAwait(false);
                if (!mediumUpdated) return false;

                int invalidated = InvalidateDownstreamCaches(gameDir, lang);
                Logger.Log.Info(
                    $"GameDataUpdate: {game}/{lang} Medium shard updated while Small was unchanged; " +
                    $"invalidated {invalidated} cache file(s).");
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.Log.Warn(
                    $"GameDataUpdate: {game}/{lang} Medium-shard check failed (non-fatal): {ex.Message}");
                return false;
            }
        }

        // ────────────────────────────────────────────────────────────────────
        //  Cache invalidation
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Delete every cached `.gisub` that could have been built from a
        /// now-stale <c>TextMap&lt;Lang&gt;.json</c> or TextMap&lt;Lang&gt;.gisub.
        /// Specifically:
        ///   * <c>TextMap&lt;Lang&gt;.gisub</c>            — encrypted sibling (only for the EN-update path; DictionarySync explicitly excludes the just-installed target)
        ///   * <c>TextMap&lt;*&gt;_TextMap&lt;Lang&gt;.*.gisub</c> — paired matcher cache
        ///   * <c>TextMap&lt;Lang&gt;_TextMap&lt;*&gt;.*.gisub</c> — reverse paired cache
        ///   * <c>*.gsmx.gisub</c>                          — serialized matcher index
        /// Logs a warning but doesn't throw on individual file failures — we
        /// want the service to complete even if one cache file is locked by
        /// an antivirus scanner.
        ///
        /// Promoted from `private` to `internal` (Session 26) so
        /// DictionarySyncService can reuse the same sweep after installing
        /// a fresh translation pack — without this, a new pack left the
        /// pre-serialized matcher index (.gsmx.gisub) pointing at the
        /// previous pack's content, and FindClosestMatch silently returned
        /// empty for every lookup. HOT CACHE still worked because it
        /// consults contentDict directly, which masked the bug until a
        /// user tried text not in the dialogue-graph chain.
        ///
        /// <paramref name="preserveTargetGisub"/> is set to <c>true</c> by
        /// DictionarySync so this sweep doesn't delete the TextMap<Lang>.gisub
        /// the caller just wrote to disk.
        /// </summary>
        internal static int InvalidateDownstreamCaches(string gameDir, string lang, bool preserveTargetGisub = false)
        {
            int removed = 0;
            if (!Directory.Exists(gameDir)) return 0;

            string upper = lang.ToUpperInvariant();
            try
            {
                foreach (var f in Directory.EnumerateFiles(gameDir, "*.gisub"))
                {
                    string name = Path.GetFileName(f);
                    bool isTargetItself = name.Equals($"TextMap{upper}.gisub", StringComparison.OrdinalIgnoreCase);
                    bool matches =
                        (isTargetItself && !preserveTargetGisub) ||
                        name.IndexOf($"_TextMap{upper}.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.StartsWith($"TextMap{upper}_", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".gsmx.gisub", StringComparison.OrdinalIgnoreCase);

                    if (!matches) continue;

                    try
                    {
                        File.Delete(f);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log.Warn($"GameDataUpdate: could not invalidate stale cache {name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"GameDataUpdate: cache-sweep error in {gameDir}: {ex.Message}");
            }
            return removed;
        }

        // ────────────────────────────────────────────────────────────────────
        //  Sidecar I/O
        // ────────────────────────────────────────────────────────────────────

        private static GameDataMetaSidecar LoadSidecar(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonConvert.DeserializeObject<GameDataMetaSidecar>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"GameDataUpdate: sidecar load failed ({path}): {ex.Message}");
                return null;
            }
        }

        private static void SaveSidecar(string path, GameDataMetaSidecar s)
        {
            try
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(s, Formatting.Indented));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"GameDataUpdate: sidecar save failed ({path}): {ex.Message}");
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { /* benign */ }
            catch (UnauthorizedAccessException) { /* benign */ }
        }

        // ────────────────────────────────────────────────────────────────────
        //  Genshin Medium-shard support (2026-04-16+)
        //
        //  DimbreathBot sharded TextMap into Small + Medium some time in
        //  early 2026. The Medium shard holds dialogue lines referenced by
        //  DialogGraph; without it DialogueContextEngine builds empty
        //  indexes and HOT CACHE is a no-op. We download Medium whenever we
        //  refresh Small (or once, on the upgrade path where the local
        //  file is suspiciously small) and merge into the canonical
        //  TextMap<LANG>.json so every downstream consumer (VoiceContent
        //  Helper, DialogueContextEngine, DialogGraphDownloader.BuildGraph)
        //  sees the full corpus transparently.
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Download the Medium shard for the given Genshin language and
        /// merge it into <paramref name="jsonPath"/>. Idempotent: if the
        /// file on disk already contains the Medium entries (size already
        /// large), the Medium sidecar's conditional GET returns 304 and we
        /// skip. Throws on network / parse failure so the caller can log.
        /// </summary>
        private async Task<bool> FetchAndMergeMediumShardAsync(string lang, string jsonPath, CancellationToken ct)
        {
            string LANG = (lang ?? string.Empty).ToUpperInvariant();
            if (string.IsNullOrEmpty(LANG)) return false;

            string mediumUrl = $"https://gitlab.com/Dimbreath/animegamedata2/-/raw/main/TextMap/TextMap_Medium{LANG}.json";
            string gameDir = Path.GetDirectoryName(jsonPath);
            string mediumSidecarPath = Path.Combine(gameDir, $"TextMap_Medium{LANG}.meta.json");
            string mediumTmp = jsonPath + ".medium.tmp";

            var mediumSidecar = LoadSidecar(mediumSidecarPath);

            using (var req = new HttpRequestMessage(HttpMethod.Get, mediumUrl))
            {
                // Conditional GET — Medium shards rarely change between
                // patches, so 304 is the common case after first install.
                if (mediumSidecar != null && !string.IsNullOrEmpty(mediumSidecar.Etag))
                {
                    try
                    {
                        req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(mediumSidecar.Etag,
                            isWeak: mediumSidecar.Etag.StartsWith("W/")));
                    }
                    catch (FormatException) { /* ignore bad sidecar */ }
                }
                if (mediumSidecar != null &&
                    !string.IsNullOrEmpty(mediumSidecar.LastModifiedHeader) &&
                    DateTimeOffset.TryParse(mediumSidecar.LastModifiedHeader, out var mediumLastModified))
                {
                    req.Headers.IfModifiedSince = mediumLastModified;
                }

                using (var resp = await SendForHeadersAsync(req, ct).ConfigureAwait(false))
                {
                    if (resp.StatusCode == HttpStatusCode.NotModified)
                    {
                        // The sidecar says we're up to date. But we must still
                        // verify jsonPath on disk actually contains the merged
                        // content (user could have manually replaced the file,
                        // or the merge failed on a previous run). The size
                        // check is cheap and catches both cases.
                        long sizeOnDisk = File.Exists(jsonPath) ? new FileInfo(jsonPath).Length : 0;
                        if (sizeOnDisk >= 30L * 1024 * 1024)
                        {
                            mediumSidecar.CheckedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            SaveSidecar(mediumSidecarPath, mediumSidecar);
                            if (Logger.IsDebugEnabled)
                            {
                                Logger.Log.Debug($"GameDataUpdate: Medium_{LANG} 304 and local file ≥30 MB — skipped.");
                            }
                            return false;
                        }
                        Logger.Log.Info($"GameDataUpdate: Medium_{LANG} 304 but local file is {sizeOnDisk / 1024 / 1024} MB — forcing re-fetch.");
                        // Fall through to re-fetch by re-issuing without
                        // If-None-Match. Rare path, not worth optimizing.
                    }

                    HttpResponseMessage fetchResp = resp;
                    HttpRequestMessage forcedReq = null;
                    try
                    {
                        if (resp.StatusCode == HttpStatusCode.NotModified)
                        {
                            forcedReq = new HttpRequestMessage(HttpMethod.Get, mediumUrl);
                            fetchResp = await SendForHeadersAsync(forcedReq, ct).ConfigureAwait(false);
                        }

                        if (!fetchResp.IsSuccessStatusCode)
                        {
                            throw new IOException($"Medium shard HTTP {(int)fetchResp.StatusCode}");
                        }

                        // Stream Medium shard to temp file.
                        using (var rs = await fetchResp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var fs = new FileStream(mediumTmp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                        {
                            await rs.CopyToAsync(fs, 65536, ct).ConfigureAwait(false);
                        }

                        // Merge: parse both, union entries (Medium wins on
                        // conflict since it holds the authoritative
                        // dialogue form), serialize combined back to
                        // jsonPath. Using JToken-valued dict tolerates the
                        // Genshin "value":"..." wrapper and plain string
                        // shapes alike.
                        string smallJson = File.ReadAllText(jsonPath);
                        string mediumJson = File.ReadAllText(mediumTmp);
                        var combined = JsonConvert.DeserializeObject<Dictionary<string, JToken>>(smallJson)
                                       ?? new Dictionary<string, JToken>();
                        var medium = JsonConvert.DeserializeObject<Dictionary<string, JToken>>(mediumJson)
                                     ?? new Dictionary<string, JToken>();

                        int added = 0, replaced = 0;
                        foreach (var kv in medium)
                        {
                            if (combined.ContainsKey(kv.Key)) replaced++;
                            else added++;
                            combined[kv.Key] = kv.Value;
                        }

                        // Atomic write: temp → swap.
                        string combinedTmp = jsonPath + ".combined.tmp";
                        File.WriteAllText(combinedTmp, JsonConvert.SerializeObject(combined));
                        if (File.Exists(jsonPath)) File.Delete(jsonPath);
                        File.Move(combinedTmp, jsonPath);
                        TryDelete(mediumTmp);

                        long finalSize = new FileInfo(jsonPath).Length;
                        Logger.Log.Info(
                            $"GameDataUpdate: merged Medium_{LANG} — {medium.Count:N0} entries " +
                            $"({added:N0} new + {replaced:N0} overrides) → {finalSize:N0} bytes total.");

                        // Update Medium sidecar.
                        string etag = fetchResp.Headers.ETag?.Tag;
                        string lastModified = fetchResp.Content.Headers.LastModified?.ToString("R");
                        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        SaveSidecar(mediumSidecarPath, new GameDataMetaSidecar
                        {
                            Source = "gitlab:Dimbreath/animegamedata2#TextMap_Medium",
                            Url = mediumUrl,
                            Etag = etag,
                            LastModifiedHeader = lastModified,
                            DownloadedAtUnix = nowUnix,
                            CheckedAtUnix = nowUnix,
                            FileSha256 = null,   // sidecar is Medium-specific; sha of combined file changes every merge
                            FileSize = 0,
                        });
                        return true;
                    }
                    finally
                    {
                        forcedReq?.Dispose();
                        if (!object.ReferenceEquals(fetchResp, resp)) fetchResp?.Dispose();
                    }
                }
            }
        }
    }
}
