using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using GI_Subtitles.Common;
using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Services.Network
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Exceptions
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Base exception for all Kaption API failures.</summary>
    public class ApiException : Exception
    {
        public ApiException(string message) : base(message) { }
        public ApiException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>The server refused the request because the session is expired, revoked, or invalid (HTTP 401).</summary>
    public sealed class UnauthorizedException : ApiException
    {
        public UnauthorizedException(string message) : base(message) { }
    }

    /// <summary>
    /// The server recognised the session but refused the action (HTTP 403).
    /// This is NOT a re-login trigger — it means "your tier doesn't cover
    /// this" or "your IP isn't on the admin allowlist". Callers must surface
    /// a distinct message ("upgrade required", "forbidden") instead of
    /// launching the sign-in flow.
    /// </summary>
    public sealed class ForbiddenException : ApiException
    {
        public ForbiddenException(string message) : base(message) { }
    }


    /// <summary>We couldn't reach the API (DNS, connection refused, timeout, TLS failure).</summary>
    public sealed class ApiUnavailableException : ApiException
    {
        public ApiUnavailableException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Server returned a structured error response (400, 409, 422, 5xx with JSON body).
    /// <see cref="Error"/> carries the parsed payload; <see cref="StatusCode"/> the HTTP code.
    /// </summary>
    public sealed class ApiValidationException : ApiException
    {
        public HttpStatusCode StatusCode { get; }
        public ApiError Error { get; }

        public ApiValidationException(HttpStatusCode statusCode, ApiError error)
            : base(error?.Describe() ?? $"HTTP {(int)statusCode}")
        {
            StatusCode = statusCode;
            Error = error;
        }
    }

    /// <summary>
    /// The file-protection key endpoint reported the device is unknown
    /// (HTTP 404). The desktop should drop to LoginWindow and re-activate.
    /// </summary>
    public sealed class FileProtectionKeyUnavailableException : ApiException
    {
        public FileProtectionKeyUnavailableException(string message) : base(message) { }
    }

    /// <summary>
    /// The server reported that the device's pinned scheme version has been
    /// retired (HTTP 410). The desktop must re-key (purge cache, fetch new
    /// secret + scheme). Reserved for a future scheme rotation; not exercised
    /// in v1.
    /// </summary>
    public sealed class SchemeRetiredException : ApiException
    {
        public SchemeRetiredException(string message) : base(message) { }
    }

    /// <summary>
    /// Caller exceeded the per-device rate limit (HTTP 429). Wait
    /// <see cref="RetryAfter"/> and try again.
    /// </summary>
    public sealed class RateLimitedException : ApiException
    {
        public TimeSpan RetryAfter { get; }
        public RateLimitedException(TimeSpan retryAfter)
            : base($"Rate-limited; retry after {retryAfter.TotalSeconds:N0} s")
        {
            RetryAfter = retryAfter;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Narrow interface for testability
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One-method seam over <see cref="KaptionApiClient.FetchFileProtectionKeyAsync"/>.
    /// Lets <c>ServerKeyFileProtectionService</c> depend on a small surface
    /// the tests can fake without standing up the whole HTTP client.
    /// </summary>
    public interface IFileProtectionKeyClient
    {
        Task<FileProtectionKeyResponse> FetchFileProtectionKeyAsync(
            string deviceSessionJwt,
            CancellationToken ct);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Client
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Typed HttpClient wrapper for the Kaption backend.
    ///
    /// Two shared HttpClient instances (static, process-wide):
    ///   * <see cref="_sharedHttpClient"/> — short-timeout (30 s) for API calls.
    ///   * <see cref="_downloadHttpClient"/> — infinite timeout for large-file streaming.
    ///
    /// Rationale: HttpClient.Timeout applies to the whole request-response cycle
    /// including stream reads. A 67 MB dictionary download on a slow connection
    /// would trip the 30-second limit. Separate client for downloads, cancellation
    /// via CancellationToken only.
    /// </summary>
    public sealed class KaptionApiClient : IFileProtectionKeyClient
    {
        // ── File-protection policy ────────────────────────────────────────
        // Bounds the client trusts on the server-returned pbkdf2_iterations.
        // Below the minimum, derivation is too cheap (cracking is feasible
        // against a leaked .gisub on a stolen device). Above the maximum,
        // a hostile or misconfigured backend could ask the client for a
        // multi-hour KDF and freeze startup. 100k is the current value;
        // these bounds give the server a 10x rotation envelope before a
        // client-side update is needed.
        public const int MinPbkdf2Iterations = 50_000;
        public const int MaxPbkdf2Iterations = 500_000;
        public const int FileProtectionSchemeVersion = 1;

        // Shared by all API calls — matches MainWindow's existing _sharedHttpClient pattern.
        private static readonly HttpClient _sharedHttpClient = CreateHttpClient(TimeSpan.FromSeconds(30));
        private static readonly HttpClient _downloadHttpClient = CreateHttpClient(Timeout.InfiniteTimeSpan);

        private static readonly JsonSerializerSettings _requestSerializerSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
        };

        private readonly string _baseUrl;

        /// <summary>
        /// Create a client pointed at the configured API base URL
        /// (Config key <c>ApiUrl</c>, default <c>https://api.kaption.one</c>).
        /// </summary>
        public KaptionApiClient()
        {
            _baseUrl = (Config.Get("ApiUrl", "https://api.kaption.one") ?? "https://api.kaption.one")
                .TrimEnd('/');
        }

        /// <summary>Exposed for tests / alternate environments.</summary>
        public KaptionApiClient(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("baseUrl must be non-empty", nameof(baseUrl));
            _baseUrl = baseUrl.TrimEnd('/');
        }

        private static HttpClient CreateHttpClient(TimeSpan timeout)
        {
            // Net8 migration (2026-04-23): swapped HttpClientHandler for
            // SocketsHttpHandler — the modern cross-platform handler that's
            // the default on net8 anyway, but configuring it explicitly lets
            // us opt in to features HttpClientHandler hides:
            //
            //   * PooledConnectionLifetime: forces connection + DNS refresh
            //     every 5 min. Our API endpoint is Cloudflare-fronted; CF
            //     edge IPs do rotate, and the classic HttpClient DNS-pinning
            //     bug means stale connections can dig into a single edge for
            //     hours. 5-min lifetime caps the blast radius of any edge
            //     reshuffling without making every request pay the handshake
            //     cost.
            //   * EnableMultipleHttp2Connections: allows parallel H2 streams
            //     across connections when one fills up. Matters for the
            //     download handler where we hit R2 hard during sync.
            //   * AutomaticDecompression: GZip + Deflate + Brotli (Brotli
            //     added in net6 SocketsHttpHandler; our API + R2 edge both
            //     negotiate it).
            //
            // IHttpClientFactory is NOT applied — it's a DI-container shape
            // that adds complexity without benefit for a non-DI WPF app.
            // The existing static-singleton HttpClient pattern is correct
            // and already avoids socket exhaustion. IHttpClientFactory's
            // real win (SocketsHttpHandler + PooledConnectionLifetime) is
            // what we're doing here directly.
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression =
                    DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                EnableMultipleHttp2Connections = true,
            };
            var client = new HttpClient(handler) { Timeout = timeout };

            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"Kaption/{version}");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            return client;
        }

        // ───────────────────────────────────────────────────────────────────
        //  Public endpoints
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// POST /api/license/activate — exchange a short-lived device_activation_jwt
        /// (from the loopback listener) for a long-lived device_session_jwt.
        /// </summary>
        public Task<ActivateResponse> ActivateAsync(
            string deviceActivationJwt,
            MachineFingerprintPayload fingerprint,
            string deviceName,
            CancellationToken ct)
        {
            var body = new ActivateRequest(deviceActivationJwt, fingerprint, deviceName);
            return PostJsonAsync<ActivateResponse>("/api/license/activate", body, authToken: null, ct);
        }

        /// <summary>
        /// POST /api/license/heartbeat — keep the session alive and refresh expiry.
        /// Throws <see cref="UnauthorizedException"/> when the session is revoked
        /// or expired.
        ///
        /// <paramref name="activeSeconds"/> (session 26) carries the cumulative
        /// OCR-active seconds since the previous successful heartbeat. Used by
        /// the referral system to credit the user's referrer with bonus-days for
        /// real usage, not just signup. Pass <c>null</c> (or 0) when OCR hasn't
        /// run since the last tick — the field is only included on the wire when
        /// &gt;0. Server clamps at 3600.
        /// </summary>
        public Task<HeartbeatResponse> HeartbeatAsync(string sessionJwt, CancellationToken ct, int? activeSeconds = null)
        {
            if (string.IsNullOrEmpty(sessionJwt))
                throw new ArgumentException("sessionJwt required", nameof(sessionJwt));

            // Defensive clamp: the server does its own, but an over-large local
            // value usually means the accumulator wasn't reset on a prior
            // heartbeat — surface that before shipping absurd totals upstream.
            int? body = null;
            if (activeSeconds.HasValue && activeSeconds.Value > 0)
            {
                int s = activeSeconds.Value;
                if (s > 3600) s = 3600;
                body = s;
            }

            var payload = body.HasValue ? new HeartbeatRequest(body) : null;
            return PostJsonAsync<HeartbeatResponse>("/api/license/heartbeat", payload, authToken: sessionJwt, ct);
        }

        /// <summary>
        /// GET /api/license/files?game=&amp;lang= — list dictionary files available
        /// to the current user's tier. Backend wraps the array in
        /// <c>{"items":[...]}</c>; we unwrap so callers get a bare list.
        /// </summary>
        public async Task<IReadOnlyList<FileMetadata>> GetFilesAsync(
            string sessionJwt, string game, string language, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(sessionJwt))
                throw new ArgumentException("sessionJwt required", nameof(sessionJwt));

            string url = $"/api/license/files?game={Uri.EscapeDataString(game ?? string.Empty)}&lang={Uri.EscapeDataString(language ?? string.Empty)}";
            var envelope = await GetJsonAsync<ApiListEnvelope<FileMetadata>>(url, sessionJwt, ct)
                .ConfigureAwait(false);
            return envelope?.Items ?? Array.Empty<FileMetadata>();
        }

        /// <summary>
        /// GET /api/app/version — publicly accessible latest-version manifest.
        /// </summary>
        public Task<AppVersionInfo> GetAppVersionAsync(CancellationToken ct)
        {
            return GetJsonAsync<AppVersionInfo>("/api/app/version", authToken: null, ct);
        }

        /// <summary>
        /// POST /api/app/file-protection-key — fetch (or refresh) the
        /// per-device 32-byte secret used to derive AES + HMAC keys for
        /// locally-cached <c>.gisub</c> translation packs.
        ///
        /// Auth: device-session JWT (Bearer). The server stamps the row
        /// keyed by the activation id encoded in the JWT.
        ///
        /// Idempotent on the server: read-or-create per device — repeated
        /// calls within the TTL return the same secret. Rate-limited at
        /// 6 calls per 10 minutes per device.
        ///
        /// Throws:
        ///   * <see cref="UnauthorizedException"/> — session expired / revoked.
        ///   * <see cref="FileProtectionKeyUnavailableException"/> — device row missing (404).
        ///   * <see cref="ForbiddenException"/> — device explicitly revoked (403).
        ///   * <see cref="SchemeRetiredException"/> — server retired this scheme (410).
        ///   * <see cref="RateLimitedException"/> — 429; <c>Retry-After</c> in seconds.
        ///   * <see cref="InvalidDataException"/> — malformed body (wrong length, etc.).
        ///   * <see cref="ApiUnavailableException"/> — network failure after retries.
        ///
        /// CRITICAL: callers must NEVER log the raw <c>device_secret_b64</c>
        /// returned in the response. Use the
        /// <c>"deviceSecret: &lt;redacted, len={N}&gt;"</c> pattern.
        /// </summary>
        public async Task<FileProtectionKeyResponse> FetchFileProtectionKeyAsync(
            string deviceSessionJwt,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(deviceSessionJwt))
                throw new ArgumentException("deviceSessionJwt required", nameof(deviceSessionJwt));

            FileProtectionKeyResponse body;
            try
            {
                body = await PostJsonAsync<FileProtectionKeyResponse>(
                    "/api/app/file-protection-key",
                    body: null,
                    authToken: deviceSessionJwt,
                    ct).ConfigureAwait(false);
            }
            catch (ApiValidationException ex)
            {
                int status = (int)ex.StatusCode;
                if (status == 404)
                    throw new FileProtectionKeyUnavailableException("Device not registered with the server");
                if (status == 410)
                    throw new SchemeRetiredException("Server retired this file-protection scheme");
                if (status == 429)
                {
                    // The server's Retry-After header is consumed inside
                    // SendAndParseAsync's exception path — we don't have
                    // direct access to it here. 60 s matches the endpoint's
                    // 10 min / 6 calls window in the worst case.
                    throw new RateLimitedException(TimeSpan.FromSeconds(60));
                }
                throw;
            }

            ValidateFileProtectionKeyResponse(body);
            return body;
        }

        Task<FileProtectionKeyResponse> IFileProtectionKeyClient.FetchFileProtectionKeyAsync(
            string deviceSessionJwt,
            CancellationToken ct)
            => FetchFileProtectionKeyAsync(deviceSessionJwt, ct);

        // Defends the desktop against a hostile or misconfigured backend.
        private static void ValidateFileProtectionKeyResponse(FileProtectionKeyResponse b)
        {
            if (b == null) throw new InvalidDataException("file-protection-key: null response");
            if (b.Version != FileProtectionSchemeVersion)
                throw new InvalidDataException(
                    $"file-protection-key: unknown scheme version {b.Version}");
            if (b.PbkdfIterations < MinPbkdf2Iterations || b.PbkdfIterations > MaxPbkdf2Iterations)
                throw new InvalidDataException(
                    $"file-protection-key: pbkdf2 iterations {b.PbkdfIterations} " +
                    $"out of safe range [{MinPbkdf2Iterations}, {MaxPbkdf2Iterations}]");
            if (string.IsNullOrEmpty(b.DeviceSecretB64))
                throw new InvalidDataException("file-protection-key: empty device secret");
            byte[] decoded;
            try
            {
                decoded = DecodeBase64Url(b.DeviceSecretB64);
            }
            catch (FormatException)
            {
                throw new InvalidDataException("file-protection-key: device secret is not valid base64url");
            }
            if (decoded.Length != 32)
                throw new InvalidDataException(
                    $"file-protection-key: device secret wrong length ({decoded.Length}, expected 32)");
        }

        /// <summary>
        /// Decode unpadded base64url (RFC 4648 §5) — the wire format used by
        /// the file-protection-key endpoint. Standard <see cref="Convert"/>
        /// only accepts canonical base64, so we translate first.
        /// </summary>
        public static byte[] DecodeBase64Url(string input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            string s = input.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }

        /// <summary>
        /// GET /api/announcements/active — active admin broadcast banners.
        /// Public endpoint (no Authorization required). The backend tier-filters
        /// by the Cookie-based session; desktop callers receive the
        /// <c>free_beta</c> view, which is the intended baseline for "announce
        /// to everyone". Narrower per-tier targeting on the desktop is a
        /// follow-up that needs backend support for Bearer-based tier lookup.
        /// </summary>
        public async Task<IReadOnlyList<AnnouncementPublic>> GetAnnouncementsAsync(CancellationToken ct)
        {
            var envelope = await GetJsonAsync<ApiListEnvelope<AnnouncementPublic>>(
                "/api/announcements/active", authToken: null, ct).ConfigureAwait(false);
            return envelope?.Items ?? Array.Empty<AnnouncementPublic>();
        }

        /// <summary>
        /// POST /api/feedback — ship a one-off note to the developer. Backend
        /// rate-limits at 5 per IP per day, persists to D1, optionally
        /// notifies the operator via Discord webhook. The message is trimmed
        /// server-side; 1..2000 chars after trim. We also do a client-side
        /// trim + length check before calling so the user sees validation
        /// errors without a round trip.
        ///
        /// Bearer auth (device_session JWT) is accepted in addition to the
        /// cookie path used by the website, so the desktop doesn't need a
        /// separate cookie jar.
        /// </summary>
        public async Task<FeedbackResponse> SendFeedbackAsync(
            string sessionJwt,
            string message,
            string clientVersion,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(sessionJwt))
                throw new ArgumentException("sessionJwt required", nameof(sessionJwt));
            if (string.IsNullOrWhiteSpace(message))
                throw new ArgumentException("message required", nameof(message));

            var body = new FeedbackRequest(message.Trim(), clientVersion);
            var resp = await PostJsonAsync<FeedbackResponse>(
                "/api/feedback", body, sessionJwt, ct).ConfigureAwait(false);
            return resp ?? new FeedbackResponse { Ok = false };
        }

        // ───────────────────────────────────────────────────────────────────
        //  Referrals (session 26)
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// GET /api/referrals/me — fetch the user's current code, share URL,
        /// stats, and reward ladder. Returns an empty-but-valid response when
        /// the user hasn't used the referrals page yet (code field may be empty
        /// on older accounts that pre-date the system); callers who need a
        /// code for display should prefer <see cref="EnsureReferralCodeAsync"/>.
        /// </summary>
        public Task<ReferralMeResponse> GetReferralsMeAsync(string sessionJwt, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(sessionJwt))
                throw new ArgumentException("sessionJwt required", nameof(sessionJwt));
            return GetJsonAsync<ReferralMeResponse>("/api/referrals/me", sessionJwt, ct);
        }

        /// <summary>
        /// POST /api/referrals/ensure-code — idempotent create-or-return of the
        /// user's referral code. Safe to call on every dialog open; the server
        /// generates a code on first call and returns the existing one on every
        /// subsequent call. Response envelope matches <see cref="GetReferralsMeAsync"/>.
        /// </summary>
        public Task<ReferralMeResponse> EnsureReferralCodeAsync(string sessionJwt, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(sessionJwt))
                throw new ArgumentException("sessionJwt required", nameof(sessionJwt));
            return PostJsonAsync<ReferralMeResponse>("/api/referrals/ensure-code", body: null, authToken: sessionJwt, ct);
        }

        /// <summary>
        /// POST /api/referrals/attribute — attach a referrer to the current
        /// user. The server enforces:
        ///   - user is within 7 days of signup (older → 400)
        ///   - code exists (unknown → 400)
        ///   - not self-referral (→ 403)
        ///   - not already attributed (→ 409)
        /// Each of those surfaces as an <see cref="ApiValidationException"/>
        /// with a typed <see cref="ApiError"/> so the UI can show the right
        /// message. Success returns normally.
        /// </summary>
        public async Task AttributeReferralAsync(string sessionJwt, string code, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(sessionJwt))
                throw new ArgumentException("sessionJwt required", nameof(sessionJwt));
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("code required", nameof(code));

            var body = new AttributeReferralRequest(code.Trim().ToUpperInvariant());
            // Using an object return type because the server only ships
            // {"ok":true} on success — we don't need to parse it, just the
            // absence of an exception means the attribution landed.
            await PostJsonAsync<object>("/api/referrals/attribute", body, sessionJwt, ct)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// POST /api/referrals/rewards/{id}/claim — submit a reward for manual
        /// review. Delivery method is one of <c>paypal</c> / <c>amazon_gc</c> /
        /// <c>hoyolab_uid</c>; <paramref name="contactInfo"/> holds whatever the
        /// chosen method needs (email, or "UID / Region"). Callers are
        /// responsible for client-side validation — the server validates too,
        /// but a bad format here costs a round-trip.
        /// </summary>
        public async Task<ReferralReward> ClaimReferralRewardAsync(
            string sessionJwt,
            string rewardId,
            string deliveryMethod,
            string contactInfo,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(sessionJwt))
                throw new ArgumentException("sessionJwt required", nameof(sessionJwt));
            if (string.IsNullOrWhiteSpace(rewardId))
                throw new ArgumentException("rewardId required", nameof(rewardId));
            if (string.IsNullOrWhiteSpace(deliveryMethod))
                throw new ArgumentException("deliveryMethod required", nameof(deliveryMethod));
            if (string.IsNullOrWhiteSpace(contactInfo))
                throw new ArgumentException("contactInfo required", nameof(contactInfo));

            var body = new ClaimReferralRewardRequest(deliveryMethod, contactInfo);
            string path = $"/api/referrals/rewards/{Uri.EscapeDataString(rewardId)}/claim";

            var resp = await PostJsonAsync<ClaimReferralRewardResponse>(path, body, sessionJwt, ct)
                .ConfigureAwait(false);
            return resp?.Reward;
        }

        /// <summary>
        /// GET /api/license/gamedata?game= — list gamedata bundles available to
        /// the current user's tier. Gamedata bundles carry the DialogGraph /
        /// NpcNames / QuestInfo / HashToDialogs / TalkIndex used by the
        /// dialogue prediction engine. One per (game, version); no language
        /// dimension because the bundle references hash IDs, not text.
        /// </summary>
        public async Task<IReadOnlyList<GamedataMetadata>> GetGamedataAsync(
            string sessionJwt, string game, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(sessionJwt))
                throw new ArgumentException("sessionJwt required", nameof(sessionJwt));

            string url = $"/api/license/gamedata?game={Uri.EscapeDataString(game ?? string.Empty)}";
            var envelope = await GetJsonAsync<ApiListEnvelope<GamedataMetadata>>(url, sessionJwt, ct)
                .ConfigureAwait(false);
            return envelope?.Items ?? Array.Empty<GamedataMetadata>();
        }

        /// <summary>
        /// GET /api/license/download/&lt;id&gt; — stream an encrypted .gisub file to
        /// <paramref name="destinationPath"/>. Reports byte-level progress via
        /// <paramref name="progress"/> (0.0..1.0). Verifies SHA-256 against
        /// <paramref name="metadata"/> before returning.
        ///
        /// The destination is written to a <c>.part</c> temp file and atomically
        /// renamed on success — partial downloads never end up at the real path.
        /// </summary>
        public Task DownloadFileAsync(
            string sessionJwt,
            FileMetadata metadata,
            string destinationPath,
            IProgress<double> progress,
            CancellationToken ct,
            byte[] distributionKey = null)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            // Session 21 C-1 fix: FileMetadata no longer carries a `url` field —
            // the backend's FileVersionPublic doesn't send one. Derive the path
            // from the file_version_id, which is always present.
            return DownloadEncryptedAsync(
                sessionJwt,
                path: $"/api/license/download/{Uri.EscapeDataString(metadata.FileVersionId)}",
                expectedSha256: metadata.Sha256,
                expectedSize: metadata.Size,
                destinationPath: destinationPath,
                progress: progress,
                ct: ct,
                distributionKey: distributionKey);
        }

        /// <summary>
        /// GET /api/license/gamedata/download/&lt;id&gt; — stream an encrypted
        /// gamedata bundle to <paramref name="destinationPath"/>. Same wire
        /// format as <see cref="DownloadFileAsync"/> (KAPD-magic .gisub-dist,
        /// distribution-key AES-256-CBC + HMAC) — the only difference is the
        /// URL path. Callers use this when installing a bundle alongside a
        /// translation pack.
        /// </summary>
        public Task DownloadGamedataAsync(
            string sessionJwt,
            GamedataMetadata metadata,
            string destinationPath,
            IProgress<double> progress,
            CancellationToken ct,
            byte[] distributionKey = null)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            return DownloadEncryptedAsync(
                sessionJwt,
                path: $"/api/license/gamedata/download/{Uri.EscapeDataString(metadata.GamedataVersionId)}",
                expectedSha256: metadata.Sha256,
                expectedSize: metadata.Size,
                destinationPath: destinationPath,
                progress: progress,
                ct: ct,
                distributionKey: distributionKey);
        }

        /// <summary>
        /// Shared streaming + SHA-verify + distribution-cipher decrypt
        /// pipeline. Extracted in Session 24 so DownloadFileAsync and
        /// DownloadGamedataAsync can share the 150-line body without
        /// copy-paste drift. Only the URL path differs between them.
        /// </summary>
        private async Task DownloadEncryptedAsync(
            string sessionJwt,
            string path,
            string expectedSha256,
            long expectedSize,
            string destinationPath,
            IProgress<double> progress,
            CancellationToken ct,
            byte[] distributionKey)
        {
            if (string.IsNullOrEmpty(sessionJwt))
                throw new ArgumentException("sessionJwt required", nameof(sessionJwt));
            if (string.IsNullOrEmpty(destinationPath))
                throw new ArgumentException("destinationPath required", nameof(destinationPath));

            string url = ResolveUrl(path);
            string partPath = destinationPath + ".part";

            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionJwt);

                HttpResponseMessage response;
                try
                {
                    response = await _downloadHttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new ApiUnavailableException("Could not reach download server.", ex);
                }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new ApiUnavailableException("Download timed out.", ex);
                }

                using (response)
                {
                    await ThrowIfErrorAsync(response, ct).ConfigureAwait(false);

                    long? totalBytes = response.Content.Headers.ContentLength;
                    if (!totalBytes.HasValue && expectedSize > 0)
                        totalBytes = expectedSize;

                    // Ensure parent directory exists (user-writable folder).
                    var parent = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                        Directory.CreateDirectory(parent);

                    // Cleanup any stale .part file from a prior interrupted download.
                    if (File.Exists(partPath))
                    {
                        try { File.Delete(partPath); }
                        catch (IOException ex) { Logger.Log.Warn($"Could not delete stale {partPath}: {ex.Message}"); }
                    }

                    // Stall watchdog: the shared download HttpClient has
                    // Timeout.InfiniteTimeSpan because legitimate 100+ MB
                    // downloads on slow residential connections can exceed
                    // any single-number budget. That leaves a different
                    // failure mode: the TCP connection stays open but no
                    // bytes arrive (ISP flapping, Wi-Fi roam, server
                    // backlogged). Pre-session-26 the ReadAsync call hung
                    // indefinitely and the progress bar froze with no
                    // recovery path — users force-quit thinking the app
                    // crashed. Now: a linked CancellationTokenSource kicks
                    // in if no bytes arrive for StallTimeout, aborting the
                    // read with a clear exception.
                    var stallTimeout = TimeSpan.FromSeconds(30);
                    byte[] sha;
                    using (var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        // Reset the stall window on each successful read.
                        void ResetStall() { try { stallCts.CancelAfter(stallTimeout); } catch (ObjectDisposedException) { /* ct already cancelled */ } }
                        ResetStall();

                        try
                        {
                            using (var sourceStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                            using (var sha256 = SHA256.Create())
                            {
                                var buffer = new byte[64 * 1024];
                                long readTotal = 0;
                                int read;
                                while ((read = await sourceStream.ReadAsync(buffer, 0, buffer.Length, stallCts.Token).ConfigureAwait(false)) > 0)
                                {
                                    // Bytes arrived — restart the stall clock.
                                    ResetStall();
                                    await fileStream.WriteAsync(buffer, 0, read, stallCts.Token).ConfigureAwait(false);
                                    sha256.TransformBlock(buffer, 0, read, null, 0);
                                    readTotal += read;
                                    if (progress != null && totalBytes.HasValue && totalBytes.Value > 0)
                                    {
                                        double pct = (double)readTotal / totalBytes.Value;
                                        if (pct > 1.0) pct = 1.0;
                                        progress.Report(pct);
                                    }
                                }
                                sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                                sha = sha256.Hash;
                            }
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            // The stall watchdog fired — the caller didn't cancel.
                            TryDelete(partPath);
                            throw new ApiUnavailableException(
                                "Download stalled — no progress for " + stallTimeout.TotalSeconds + " seconds. Please try again.",
                                new TimeoutException("download stall"));
                        }
                        catch (OperationCanceledException)
                        {
                            // Caller cancelled — propagate cleanly.
                            TryDelete(partPath);
                            throw;
                        }
                        catch (IOException ex)
                        {
                            TryDelete(partPath);
                            throw new ApiUnavailableException("Could not write downloaded file to disk.", ex);
                        }
                    }

                    // Verify SHA-256 if provided.
                    if (!string.IsNullOrEmpty(expectedSha256))
                    {
                        string actualHex = ToHex(sha);
                        if (!string.Equals(actualHex, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            TryDelete(partPath);
                            throw new ApiException(
                                $"Downloaded file hash mismatch (expected {expectedSha256}, got {actualHex}).");
                        }
                    }

                    // Distribution-layer decryption. The server stores files in
                    // R2 as `.gisub-dist` (KAPD-magic container, AES-256-CBC +
                    // HMAC under the global distribution key). The SHA we just
                    // verified is over those encrypted bytes — same thing the
                    // attacker would see on the wire. Now we decrypt in place
                    // so callers see the plaintext, and downstream code (the
                    // machine-bound AesFileProtectionService, JSON consumers)
                    // doesn't need to know about this layer.
                    //
                    // If the caller didn't pass a distribution key, we assume
                    // the file is already plaintext (dev builds, future
                    // non-protected SKUs). The magic-byte check lets us
                    // distinguish the two without a separate flag.
                    if (distributionKey != null &&
                        GI_Subtitles.Services.Security.DistributionCipher.HasMagic(partPath))
                    {
                        try
                        {
                            GI_Subtitles.Services.Security.DistributionCipher
                                .DecryptFileInPlace(partPath, distributionKey);
                        }
                        catch (CryptographicException ex)
                        {
                            TryDelete(partPath);
                            throw new ApiException(
                                "Downloaded file failed distribution-layer decryption: " + ex.Message, ex);
                        }
                    }

                    // Atomic move: only replace the real file if everything above succeeded.
                    try
                    {
                        if (File.Exists(destinationPath))
                            File.Delete(destinationPath);
                        File.Move(partPath, destinationPath);
                    }
                    catch (IOException ex)
                    {
                        TryDelete(partPath);
                        throw new ApiUnavailableException("Could not finalize downloaded file.", ex);
                    }

                    progress?.Report(1.0);
                }
            }
        }

        // ───────────────────────────────────────────────────────────────────
        //  Transport helpers
        // ───────────────────────────────────────────────────────────────────

        private Task<T> GetJsonAsync<T>(string path, string authToken, CancellationToken ct)
        {
            string url = ResolveUrl(path);
            return SendAndParseAsync<T>(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(authToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
                return req;
            }, ct);
        }

        private Task<T> PostJsonAsync<T>(string path, object body, string authToken, CancellationToken ct)
        {
            string url = ResolveUrl(path);
            string json = body != null
                ? JsonConvert.SerializeObject(body, _requestSerializerSettings)
                : "{}";
            return SendAndParseAsync<T>(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                if (!string.IsNullOrEmpty(authToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
                // StringContent must be freshly allocated per attempt — it holds
                // a stream whose position is advanced by the first SendAsync.
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return req;
            }, ct);
        }

        /// <summary>
        /// Send a request with exponential-backoff retry for transient server
        /// failures. The caller passes a <paramref name="requestFactory"/>
        /// rather than a single <see cref="HttpRequestMessage"/> because a
        /// given request instance can only be sent once (its Content stream
        /// is consumed) — each retry builds a fresh one.
        ///
        /// What we retry:
        ///   - HTTP 5xx (server error)
        ///   - HttpRequestException (DNS / TLS / connection-refused)
        ///   - TaskCanceledException from the client-side 30 s timeout
        /// What we do NOT retry:
        ///   - Any 4xx (401/403/404/409/422 — retrying won't help, and 401
        ///     triggers the re-login flow which we don't want to thrash)
        ///   - User cancellation (ct.IsCancellationRequested)
        ///
        /// Budget: 4 total attempts with 500 ms / 1 s / 2 s sleeps between —
        /// roughly 3.5 s of back-off in the worst case, which is noticeable
        /// but still better than 4× separate manual retries from the callers.
        /// </summary>
        private async Task<T> SendAndParseAsync<T>(
            Func<HttpRequestMessage> requestFactory,
            CancellationToken ct)
        {
            const int MaxAttempts = 4;
            // Delays between attempts 1→2, 2→3, 3→4 (no delay after final).
            var backoff = new[] { 500, 1000, 2000 };

            Exception lastTransient = null;
            Uri lastUri = null;

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                using (var req = requestFactory())
                {
                    lastUri = req.RequestUri;
                    HttpResponseMessage response;
                    try
                    {
                        response = await _sharedHttpClient
                            .SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                            .ConfigureAwait(false);
                    }
                    catch (HttpRequestException ex)
                    {
                        lastTransient = ex;
                        if (attempt + 1 < MaxAttempts)
                        {
                            Logger.Log.Warn(
                                $"API {lastUri} attempt {attempt + 1}/{MaxAttempts} network error: {ex.Message} — retrying in {backoff[attempt]} ms");
                            try { await Task.Delay(backoff[attempt], ct).ConfigureAwait(false); }
                            catch (OperationCanceledException) { throw; }
                            continue;
                        }
                        throw new ApiUnavailableException(
                            "We couldn't reach Kaption's servers. Check your internet and try again.", ex);
                    }
                    catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                    {
                        // Client-side 30 s timeout (not user cancellation).
                        lastTransient = ex;
                        if (attempt + 1 < MaxAttempts)
                        {
                            Logger.Log.Warn(
                                $"API {lastUri} attempt {attempt + 1}/{MaxAttempts} timed out — retrying in {backoff[attempt]} ms");
                            try { await Task.Delay(backoff[attempt], ct).ConfigureAwait(false); }
                            catch (OperationCanceledException) { throw; }
                            continue;
                        }
                        throw new ApiUnavailableException(
                            "The request to Kaption's servers took too long. Try again in a moment.", ex);
                    }

                    using (response)
                    {
                        int status = (int)response.StatusCode;

                        // Retry on 5xx (transient server failure). Everything
                        // else — including 4xx — gets classified by
                        // ThrowIfErrorAsync below so callers see the correct
                        // exception type (ForbiddenException, not a generic
                        // "server error").
                        if (status >= 500 && attempt + 1 < MaxAttempts)
                        {
                            // Drain the body so the connection can be reused.
                            string _ = response.Content != null
                                ? await response.Content.ReadAsStringAsync().ConfigureAwait(false)
                                : null;
                            Logger.Log.Warn(
                                $"API {lastUri} attempt {attempt + 1}/{MaxAttempts} HTTP {status} — retrying in {backoff[attempt]} ms");
                            lastTransient = new HttpRequestException($"HTTP {status}");
                            try { await Task.Delay(backoff[attempt], ct).ConfigureAwait(false); }
                            catch (OperationCanceledException) { throw; }
                            continue;
                        }

                        // Classify non-success responses (including 5xx after
                        // the retry budget is exhausted) into typed exceptions.
                        await ThrowIfErrorAsync(response, ct).ConfigureAwait(false);

                        string responseText = response.Content != null
                            ? await response.Content.ReadAsStringAsync().ConfigureAwait(false)
                            : string.Empty;

                        if (string.IsNullOrWhiteSpace(responseText))
                            return default;

                        try
                        {
                            return JsonConvert.DeserializeObject<T>(responseText);
                        }
                        catch (JsonException ex)
                        {
                            Logger.Log.Error($"Failed to parse API response from {req.RequestUri}: {ex.Message}");
                            throw new ApiException("Kaption's servers returned an unexpected response.", ex);
                        }
                    }
                }
            }

            // Should be unreachable — every branch above either continues or
            // throws. Keep a defensive throw so a future edit doesn't silently
            // return default(T) on exhaustion.
            throw new ApiUnavailableException(
                "Kaption's servers did not respond after several retries.",
                lastTransient ?? new Exception($"retry budget exhausted for {lastUri}"));
        }

        private static async Task ThrowIfErrorAsync(HttpResponseMessage response, CancellationToken ct)
        {
            if (response.IsSuccessStatusCode)
                return;

            string body = null;
            try
            {
                if (response.Content != null)
                    body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Non-fatal: we still classify by status code below.
                Logger.Log.Warn($"Could not read error response body: {ex.Message}");
            }

            ApiError parsed = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try { parsed = JsonConvert.DeserializeObject<ApiError>(body); }
                catch (JsonException) { /* not JSON — leave parsed=null and use status code only */ }
            }

            int status = (int)response.StatusCode;
            Logger.Log.Warn($"API call returned HTTP {status} from {response.RequestMessage?.RequestUri}: {parsed?.Describe() ?? body ?? "(no body)"}");

            // Split 401 from 403 — prior to this, tier-denied responses
            // (e.g. free_beta user hitting a Pro-only file) triggered the
            // re-login dialog, which was confusing and scared users into
            // thinking the app was broken. Both still log the server's
            // "describe" text, but only 401 clears the session.
            if (status == 401)
            {
                throw new UnauthorizedException(parsed?.Describe()
                    ?? "Your Kaption session has expired. Please sign in again.");
            }
            if (status == 403)
            {
                throw new ForbiddenException(parsed?.Describe()
                    ?? "Your account doesn't have access to this feature.");
            }

            if (status >= 500)
            {
                throw new ApiUnavailableException(
                    "Kaption's servers reported an internal error. Please try again in a moment.",
                    new HttpRequestException($"HTTP {status}: {parsed?.Describe() ?? body}"));
            }

            throw new ApiValidationException(response.StatusCode, parsed ?? new ApiError());
        }

        private string ResolveUrl(string pathOrUrl)
        {
            if (string.IsNullOrEmpty(pathOrUrl))
                throw new ArgumentException("path or url required", nameof(pathOrUrl));
            if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return pathOrUrl;
            if (!pathOrUrl.StartsWith("/", StringComparison.Ordinal))
                pathOrUrl = "/" + pathOrUrl;
            return _baseUrl + pathOrUrl;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException ex)
            {
                Logger.Log.Warn($"Could not delete temp file {path}: {ex.Message}");
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
