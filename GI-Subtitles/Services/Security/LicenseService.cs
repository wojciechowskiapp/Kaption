using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GI_Subtitles.Common;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Network;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// Why a typed result instead of throw-on-failure: activation is a normal,
    /// recoverable outcome path with several distinct user-visible failure modes.
    /// The caller (LoginWindow) needs to route each to a different UI state,
    /// not a generic error dialog.
    /// </summary>
    public enum ActivationFailureReason
    {
        None = 0,
        UserCancelled,
        Timeout,
        NetworkError,
        ServerRejected,
        MaxDevicesReached,
        ProviderError,
        Unknown,
    }

    /// <summary>
    /// Outcome of an activation attempt. Exactly one of
    /// <see cref="Data"/> (on success) or <see cref="FailureReason"/> /
    /// <see cref="FailureMessage"/> (on failure) is meaningful.
    /// </summary>
    public sealed class ActivationResult
    {
        public bool Success { get; }
        public ActivationData Data { get; }
        public ActivationFailureReason FailureReason { get; }
        public string FailureMessage { get; }

        private ActivationResult(bool success, ActivationData data, ActivationFailureReason reason, string message)
        {
            Success = success;
            Data = data;
            FailureReason = reason;
            FailureMessage = message;
        }

        public static ActivationResult Ok(ActivationData data) =>
            new ActivationResult(true, data, ActivationFailureReason.None, null);

        public static ActivationResult Fail(ActivationFailureReason reason, string message) =>
            new ActivationResult(false, null, reason, message);
    }

    /// <summary>
    /// Orchestrates the desktop licensing lifecycle: sign-in via loopback OAuth,
    /// storage of the device session, periodic heartbeat, hard-expiry detection,
    /// sign-out.
    ///
    /// Thread-safety:
    ///   * <see cref="_current"/> is guarded by <see cref="_stateLock"/>; reads
    ///     take the lock too so readers always see a consistent snapshot.
    ///   * <see cref="ActivationStateChanged"/> is always raised on the WPF
    ///     dispatcher thread when the app has one, so UI consumers don't need
    ///     their own marshalling.
    ///
    /// Error policy: a licensing-layer failure never crashes the app. Every
    /// catch logs and either returns a typed failure or silently logs for
    /// background timers.
    /// </summary>
    public sealed class LicenseService : IDisposable
    {
        // Heartbeat every hour when active. Tweakable via Config key "HeartbeatHours".
        // Tightened from 6h in session 21 — 1h bounds the window between a
        // server-side revocation and the desktop noticing it. Combined with
        // opportunistic EnsureFreshAsync on OCR-start entry points, effective
        // noticing time is typically <1 minute of activity.
        private static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromHours(1);

        // Activation flow upper bound — if the user doesn't complete sign-in in
        // this window we give up and show an error.
        private static readonly TimeSpan ActivationTimeout = TimeSpan.FromMinutes(5);

        // Show the "session expiring soon" banner when less than this remains.
        private static readonly TimeSpan ExpiryWarningWindow = TimeSpan.FromHours(24);

        private readonly object _stateLock = new object();
        private readonly KaptionApiClient _api;

        private ActivationData _current;
        private Timer _heartbeatTimer;
        private bool _disposed;

        /// <summary>
        /// UTC clock of the last successful heartbeat. Used by
        /// <see cref="EnsureFreshAsync"/> to short-circuit opportunistic
        /// checks when a recent heartbeat already proved liveness. Not
        /// persisted — each process starts assuming stale, which is the
        /// correct default.
        /// </summary>
        private DateTime _lastHeartbeatAtUtc = DateTime.MinValue;

        /// <summary>
        /// Serializes concurrent <see cref="EnsureFreshAsync"/> callers so
        /// two simultaneous OCR-start presses don't fire duplicate heartbeats.
        /// </summary>
        private readonly SemaphoreSlim _ensureFreshGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Provider for cumulative "active OCR seconds since last heartbeat"
        /// wired up by <see cref="MainWindow"/>. Called on each heartbeat
        /// immediately before the POST so the returned number reflects right-
        /// at-ship time. The provider may block briefly (interlocked read) but
        /// MUST NOT touch the UI or do IO. Null = don't ship active_seconds —
        /// the desktop is still compatible with the old body-less heartbeat.
        ///
        /// On successful heartbeat the provider is called a second time (via
        /// <see cref="_onHeartbeatAcknowledged"/>) so the caller can reset its
        /// accumulator. Two separate callbacks — instead of a ref-returning
        /// provider — because the reset MUST only happen on success, and the
        /// happy path in RefreshHeartbeatAsync spans several awaits.
        /// </summary>
        private Func<int> _activeSecondsProvider;

        /// <summary>
        /// Paired with <see cref="_activeSecondsProvider"/>: invoked only on
        /// a successful heartbeat, receiving the value that was shipped. The
        /// caller uses this to reset its accumulator. Failure paths (network,
        /// 401, cancellation) skip this so the next heartbeat retries with
        /// the same pending seconds.
        /// </summary>
        private Action<int> _onHeartbeatAcknowledged;

        /// <summary>
        /// Count of consecutive transient heartbeat failures. Drives the
        /// accelerated retry schedule (5m / 15m / 30m) — after that we give
        /// up on the fast path and fall back to the normal 1 h cadence so
        /// we don't hammer a long-offline backend. Reset to 0 on any success.
        /// </summary>
        private int _consecutiveHeartbeatFailures;

        /// <summary>Raised (on UI thread) whenever the cached activation state changes.</summary>
        public event EventHandler ActivationStateChanged;

        public LicenseService() : this(new KaptionApiClient()) { }

        /// <summary>Test hook: inject a custom API client.</summary>
        public LicenseService(KaptionApiClient api)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));

            // Load any previously-stored activation so IsActivated is accurate
            // from the first frame.
            var loaded = ActivationStore.Load();
            lock (_stateLock) { _current = loaded; }
            if (loaded != null)
                Logger.Log.Info(
                    $"License: loaded cached activation for {loaded.Email} (expires {loaded.ExpiresAtUtc:u}, " +
                    $"hasFileProtSecret={loaded.HasDeviceFileProtectionSecret}, " +
                    $"secretLen={loaded.DeviceFileProtectionSecret?.Length ?? 0}, " +
                    $"scheme={loaded.DeviceFileProtectionSchemeVersion}).");
        }

        // ───────────────────────────────────────────────────────────────────
        //  Read-only state
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Cheap: no network, no IO. True when we have a non-null cached activation
        /// whose server-declared expiry is still in the future AND whose stored_at
        /// timestamp is not in the future (clock-rollback guard: a user who sets
        /// their system clock back can't extend offline validity that way).
        /// </summary>
        public bool IsActivated
        {
            get
            {
                lock (_stateLock)
                {
                    if (_current == null) return false;
                    var now = DateTime.UtcNow;
                    if (now >= _current.ExpiresAtUtc) return false;
                    // Clock rollback: if "now" is before the activation was stored
                    // (minus a small NTP-skew allowance), the user has wound the
                    // clock back — treat as not activated so a fresh heartbeat has
                    // to succeed before OCR runs.
                    var rollbackAllowance = TimeSpan.FromMinutes(10);
                    if (now + rollbackAllowance < _current.StoredAtUtc) return false;
                    return true;
                }
            }
        }

        /// <summary>
        /// True when we have an activation on file but it is past its server-declared
        /// expiry. The app should show the re-activate flow.
        /// </summary>
        public bool IsHardExpired
        {
            get
            {
                lock (_stateLock)
                {
                    return _current != null && DateTime.UtcNow >= _current.ExpiresAtUtc;
                }
            }
        }

        /// <summary>
        /// Snapshot of the current activation, or null if none. The returned object
        /// is the same instance held internally — callers MUST NOT mutate it.
        /// </summary>
        public ActivationData CurrentActivation
        {
            get { lock (_stateLock) return _current; }
        }

        /// <summary>
        /// Persist a server-issued file-protection secret onto the current
        /// activation (in-memory + disk). Must update the in-memory <c>_current</c>
        /// directly because the heartbeat path saves <c>_current</c> back to
        /// disk on every tick — any field the heartbeat doesn't know about is
        /// wiped. Holding <see cref="_stateLock"/> across mutate + save
        /// serialises against the heartbeat so the two writers don't lose
        /// each other's changes.
        ///
        /// Returns true if the activation existed and the secret was persisted.
        /// Returns false when no activation is loaded (bootstrap was racing
        /// LoginWindow); the caller should log + skip.
        /// </summary>
        public bool SetFileProtectionSecret(
            byte[] secret,
            long? issuedAtUnixMs,
            long? expiresAtUnixMs,
            int schemeVersion,
            int pbkdf2Iterations)
        {
            if (secret == null || secret.Length != 32)
                throw new ArgumentException("file-protection secret must be 32 bytes", nameof(secret));

            lock (_stateLock)
            {
                if (_current == null) return false;

                _current.DeviceFileProtectionSecret = secret;
                _current.DeviceFileProtectionIssuedAtUnixMs = issuedAtUnixMs;
                _current.DeviceFileProtectionExpiresAtUnixMs = expiresAtUnixMs;
                _current.DeviceFileProtectionSchemeVersion = schemeVersion;
                _current.DeviceFileProtectionPbkdf2Iterations = pbkdf2Iterations;

                try
                {
                    ActivationStore.Save(_current);
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log.Warn(
                        $"LicenseService.SetFileProtectionSecret: in-memory updated but disk save failed: {ex.Message}");
                    // The in-memory copy carries the secret; future operations
                    // through CurrentActivation will see it. The next heartbeat
                    // (or any other Save) will write it to disk.
                    return true;
                }
            }
        }

        /// <summary>
        /// Time until the hard expiry fires. Null when there's no activation.
        /// Negative when already expired. Drives the expiry-warning banner.
        /// </summary>
        public TimeSpan? TimeUntilHardExpiry
        {
            get
            {
                lock (_stateLock)
                {
                    if (_current == null) return null;
                    return _current.ExpiresAtUtc - DateTime.UtcNow;
                }
            }
        }

        /// <summary>Convenience: true when we should nudge the user to re-confirm their session.</summary>
        public bool IsNearExpiry
        {
            get
            {
                var remaining = TimeUntilHardExpiry;
                return remaining.HasValue && remaining.Value > TimeSpan.Zero && remaining.Value < ExpiryWarningWindow;
            }
        }

        // ───────────────────────────────────────────────────────────────────
        //  Activation flow
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Run the full desktop activation flow: spin up a loopback listener,
        /// open the user's browser to the Kaption activate page, wait for the
        /// callback, exchange the returned JWT for a device session.
        ///
        /// Returns a typed <see cref="ActivationResult"/>. Never throws for
        /// expected failures — only for programmer errors (null args, etc.).
        /// </summary>
        public async Task<ActivationResult> ActivateAsync(CancellationToken ct)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(LicenseService));

            Logger.Log.Info("License: starting activation flow.");

            string appUrl = Config.Get("AppUrl", "https://kaption.one") ?? "https://kaption.one";
            string deviceName = GetDeviceName();

            using (var listener = new LoopbackAuthListener())
            {
                string activationUrl;
                try
                {
                    activationUrl = listener.BuildActivationUrl(appUrl, deviceName);
                }
                catch (Exception ex)
                {
                    Logger.Log.Error($"License: could not build activation URL: {ex.Message}");
                    return ActivationResult.Fail(ActivationFailureReason.Unknown,
                        "We couldn't start the sign-in flow. Please try again.");
                }

                // Open the user's default browser. UseShellExecute=true is required on
                // .NET Framework for ProcessStartInfo to follow HTTP protocol handlers.
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = activationUrl,
                        UseShellExecute = true,
                    });
                }
                catch (Exception ex)
                {
                    Logger.Log.Error($"License: could not open browser: {ex.Message}");
                    return ActivationResult.Fail(ActivationFailureReason.Unknown,
                        "We couldn't open your browser. Please open this link manually: " + activationUrl);
                }

                // Wait for the callback.
                string deviceActivationJwt;
                try
                {
                    deviceActivationJwt = await listener.WaitForTokenAsync(ActivationTimeout, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.Log.Info("License: activation cancelled by user.");
                    return ActivationResult.Fail(ActivationFailureReason.UserCancelled,
                        "Sign-in was cancelled.");
                }
                catch (TimeoutException ex)
                {
                    Logger.Log.Warn($"License: activation timed out. {ex.Message}");
                    return ActivationResult.Fail(ActivationFailureReason.Timeout,
                        "Sign-in took too long. Please try again.");
                }
                catch (LoopbackAuthException ex)
                {
                    Logger.Log.Warn($"License: loopback auth failed: {ex.Message}");
                    return ActivationResult.Fail(ActivationFailureReason.ProviderError, ex.Message);
                }

                // Exchange the short-lived token for a device session.
                return await CompleteActivationAsync(deviceActivationJwt, deviceName, ct).ConfigureAwait(false);
            }
        }

        private async Task<ActivationResult> CompleteActivationAsync(
            string deviceActivationJwt, string deviceName, CancellationToken ct)
        {
            MachineFingerprintPayload fingerprint;
            try
            {
                var (cpu, mobo, disk) = MachineFingerprint.GetComponentHashesHex();
                fingerprint = new MachineFingerprintPayload(cpu, mobo, disk);
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"License: fingerprint computation failed: {ex.Message}");
                return ActivationResult.Fail(ActivationFailureReason.Unknown,
                    "We couldn't read this device's hardware fingerprint. Please try again.");
            }

            ActivateResponse response;
            try
            {
                response = await _api.ActivateAsync(deviceActivationJwt, fingerprint, deviceName, ct).ConfigureAwait(false);
            }
            catch (ApiUnavailableException ex)
            {
                Logger.Log.Warn($"License: network failure during activate: {ex.Message}");
                return ActivationResult.Fail(ActivationFailureReason.NetworkError, ex.Message);
            }
            catch (UnauthorizedException ex)
            {
                Logger.Log.Warn($"License: server rejected activation token: {ex.Message}");
                return ActivationResult.Fail(ActivationFailureReason.ServerRejected,
                    "Your sign-in token was rejected. Please try signing in again.");
            }
            catch (ForbiddenException ex)
            {
                // 403 during activation = server accepted the identity but the
                // account is blocked (banned, under investigation, admin-IP
                // mismatch on an admin account). Surface the server's message
                // verbatim — it already explains the blocker.
                Logger.Log.Warn($"License: activation forbidden by server: {ex.Message}");
                return ActivationResult.Fail(ActivationFailureReason.ServerRejected, ex.Message);
            }
            catch (ApiValidationException ex) when (
                // H-4 fix (session 21 review): server emits "too_many_activations".
                // Accept both spellings so a server-side rename doesn't break the UX
                // branch — the desktop owns the user-facing copy either way.
                string.Equals(ex.Error?.Error, "too_many_activations", StringComparison.Ordinal) ||
                string.Equals(ex.Error?.Error, "max_devices_reached", StringComparison.Ordinal))
            {
                Logger.Log.Warn("License: device limit reached.");
                return ActivationResult.Fail(ActivationFailureReason.MaxDevicesReached,
                    ex.Error?.Describe()
                    ?? "You've reached the 3-device limit. Open kaption.one/dashboard and sign out of one device, then try again.");
            }
            catch (ApiValidationException ex)
            {
                Logger.Log.Warn($"License: server validation error during activate: {ex.Error?.Describe() ?? ex.Message}");
                return ActivationResult.Fail(ActivationFailureReason.ServerRejected,
                    ex.Error?.Describe() ?? "The sign-in request was rejected.");
            }
            catch (ApiException ex)
            {
                Logger.Log.Warn($"License: unexpected API exception during activate: {ex.Message}");
                return ActivationResult.Fail(ActivationFailureReason.Unknown, ex.Message);
            }
            catch (OperationCanceledException)
            {
                Logger.Log.Info("License: activation HTTP call cancelled.");
                return ActivationResult.Fail(ActivationFailureReason.UserCancelled, "Sign-in was cancelled.");
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"License: unexpected failure during activate: {ex}");
                return ActivationResult.Fail(ActivationFailureReason.Unknown,
                    "Something went wrong on our end. Please try again in a moment.");
            }

            // C-1 fix (session 21): response shape is now nested (ActivationBlob + User)
            // instead of flat. All reads below go through response.User.* /
            // response.ActivationBlob.* / response.DeviceSessionToken etc.
            if (response == null
                || string.IsNullOrEmpty(response.DeviceSessionToken)
                || response.User == null
                || response.ActivationBlob == null)
            {
                Logger.Log.Error("License: server returned incomplete activation response.");
                return ActivationResult.Fail(ActivationFailureReason.Unknown,
                    "Kaption's servers returned an incomplete response. Please try again.");
            }

            // Server-reported effective_tier + paid_until are cached for UI
            // display only — they are NEVER an authorization basis on the
            // client. Every sensitive server call re-reads from D1 on the
            // server side, so a Fiddler-class tampering of these fields can
            // only make this user's own UI lie to themselves — they still
            // can't download paid files or invoke paid features without the
            // server agreeing. See ActivationData docstring for the rule.
            var data = new ActivationData
            {
                UserId = response.User.Id,
                Email = response.User.Email,
                Tier = response.User.Tier,
                EffectiveTier = response.User.EffectiveTier ?? response.User.Tier,
                PaidUntilUnix = response.User.PaidUntil,
                DeviceSessionJwt = response.DeviceSessionToken,
                UserKeyComponent = response.DecodeUserKeyComponent(),
                // Stored alongside UserKeyComponent so DistributionCipher can
                // pick it up on download without reaching back into the API
                // response. Same value for every user — safe to cache at-rest
                // (DPAPI-protected via the rest of ActivationData).
                DistributionKey = response.DecodeDistributionKey(),
                ActivationId = response.ActivationId,
                // Hard expiry is what gates offline use — soft is just the warn-banner trigger.
                ExpiresAtUnix = response.HardExpiresAt,
                StoredAtUtc = DateTime.UtcNow,
            };

            try
            {
                ActivationStore.Save(data);
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"License: could not persist activation: {ex.Message}");
                // Continue anyway — the in-memory state is valid for this session.
                // On next start the user will be prompted to re-activate.
            }

            lock (_stateLock) { _current = data; }
            RaiseStateChanged();

            Logger.Log.Info($"License: activation complete for {data.Email} (tier={data.Tier}, expires {data.ExpiresAtUtc:u}).");
            return ActivationResult.Ok(data);
        }

        // ───────────────────────────────────────────────────────────────────
        //  Heartbeat
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Start the background heartbeat timer. Safe to call multiple times —
        /// calling again resets the interval. Does nothing if there's no active
        /// session.
        /// </summary>
        public void StartHeartbeatTimer()
        {
            if (_disposed) return;

            lock (_stateLock)
            {
                if (_current == null)
                {
                    Logger.Log.Info("License: heartbeat timer not started (no active session).");
                    return;
                }

                if (_heartbeatTimer == null)
                {
                    var interval = GetHeartbeatInterval();
                    // First tick after 30s gives the UI time to settle before the
                    // first network call — prevents blocking a laggy startup even
                    // further if the network is flaky.
                    _heartbeatTimer = new Timer(HeartbeatTick, null, TimeSpan.FromSeconds(30), interval);
                    Logger.Log.Info($"License: heartbeat timer started (interval={interval}).");
                }
            }
        }

        /// <summary>
        /// Register the optional "active seconds since last heartbeat" hooks so
        /// the referral system can credit the referrer with bonus-days. Safe to
        /// call at most once per process (typically from MainWindow_Loaded).
        /// Passing null pair clears the hooks.
        ///
        /// <paramref name="provider"/> returns the cumulative active seconds to
        /// ship on the upcoming heartbeat — called immediately before the POST.
        /// <paramref name="onAcknowledged"/> is invoked only when the heartbeat
        /// round-trips successfully, with the exact value that was shipped, so
        /// the caller can reset its accumulator. A failed heartbeat retries
        /// with the same pending total next tick.
        /// </summary>
        public void SetActiveSecondsReporter(Func<int> provider, Action<int> onAcknowledged)
        {
            _activeSecondsProvider = provider;
            _onHeartbeatAcknowledged = onAcknowledged;
        }

        /// <summary>
        /// One-shot heartbeat. Returns true on success, false on any failure the
        /// caller should surface (including revocation). Internal retries and
        /// transient errors resolve to false without propagating exceptions.
        /// </summary>
        public async Task<bool> RefreshHeartbeatAsync(CancellationToken ct)
        {
            string jwt;
            lock (_stateLock)
            {
                if (_current == null) return false;
                jwt = _current.DeviceSessionJwt;
            }

            // Snapshot the pending active-seconds immediately before the POST so
            // the caller's reset (fired on success below) matches exactly what
            // we shipped — no new OCR seconds accumulated after this point can
            // be silently dropped.
            int shippedActiveSeconds = 0;
            try
            {
                if (_activeSecondsProvider != null)
                {
                    int s = _activeSecondsProvider();
                    if (s > 0) shippedActiveSeconds = s;
                }
            }
            catch (Exception ex)
            {
                // A buggy provider must never block heartbeats — log and ship 0.
                Logger.Log.Warn($"License: active-seconds provider threw: {ex.Message}");
                shippedActiveSeconds = 0;
            }

            try
            {
                var resp = await _api.HeartbeatAsync(
                    jwt,
                    ct,
                    activeSeconds: shippedActiveSeconds > 0 ? (int?)shippedActiveSeconds : null)
                    .ConfigureAwait(false);
                if (resp == null)
                {
                    Logger.Log.Warn("License: heartbeat returned empty response.");
                    return false;
                }

                lock (_stateLock)
                {
                    if (_current == null) return false;
                    // C-3 fix (session 21 review): server emits soft_expires_at +
                    // hard_expires_at. Write the HARD expiry (that's what gates
                    // offline use). Previously we read a non-existent `expires_at`
                    // field which left ExpiresAtUnix = 0 after every heartbeat and
                    // kicked the user out ~30 seconds after sign-in.
                    _current.ExpiresAtUnix = resp.HardExpiresAt;
                    // Cache fresh effective_tier + paid_until for UI display.
                    // Not an authorization basis — server re-authorizes on every
                    // protected request. Empty effective_tier = server too old;
                    // keep the previous cached value.
                    if (!string.IsNullOrEmpty(resp.EffectiveTier))
                        _current.EffectiveTier = resp.EffectiveTier;
                    _current.PaidUntilUnix = resp.PaidUntil;
                }
                try
                {
                    ActivationStore.Save(_current);
                }
                catch (Exception ex)
                {
                    Logger.Log.Warn($"License: heartbeat succeeded but save failed: {ex.Message}");
                }

                _lastHeartbeatAtUtc = DateTime.UtcNow;
                Logger.Log.Info($"License: heartbeat OK, new expiry {_current.ExpiresAtUtc:u}.");

                // Signal the caller (MainWindow) that the active-seconds it
                // provided landed — safe to reset the accumulator now. Any
                // seconds that accrued between the snapshot above and this
                // callback stay in the bucket for next heartbeat.
                if (shippedActiveSeconds > 0)
                {
                    try { _onHeartbeatAcknowledged?.Invoke(shippedActiveSeconds); }
                    catch (Exception ex) { Logger.Log.Warn($"License: onHeartbeatAcknowledged threw: {ex.Message}"); }
                }

                RaiseStateChanged();
                return true;
            }
            catch (UnauthorizedException ex)
            {
                Logger.Log.Warn($"License: heartbeat returned 401 — session revoked. {ex.Message}");
                // Session is gone. Clear in-memory, leave disk blob so the user
                // sees their last-known email in the re-login dialog; the next
                // successful activation will overwrite it anyway.
                lock (_stateLock) { _current = null; }
                try { ActivationStore.Clear(); }
                catch (Exception ioEx) { Logger.Log.Warn($"License: could not clear activation after revocation: {ioEx.Message}"); }
                RaiseStateChanged();
                return false;
            }
            catch (ApiUnavailableException ex)
            {
                // Transient — keep going, try again next tick.
                Logger.Log.Warn($"License: heartbeat network error (will retry): {ex.Message}");
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"License: unexpected heartbeat failure: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Opportunistic liveness check. Returns true immediately if a recent
        /// heartbeat (newer than <paramref name="maxAge"/>) already proved the
        /// session is live. Otherwise fires a single heartbeat inline — same
        /// semantics as <see cref="RefreshHeartbeatAsync"/>. Safe to call on
        /// every user-initiated privileged action; internal semaphore collapses
        /// concurrent callers onto a single in-flight request.
        /// </summary>
        public async Task<bool> EnsureFreshAsync(TimeSpan maxAge, CancellationToken ct)
        {
            if (_disposed) return false;

            // Fast path — no lock needed for the initial read. Worst case we
            // over-fire one heartbeat; harmless and rate-limited server-side.
            var last = _lastHeartbeatAtUtc;
            if (last != DateTime.MinValue && DateTime.UtcNow - last < maxAge)
                return true;

            // Serialize the slow path so two OCR-start clicks within a second
            // don't each trigger a heartbeat.
            try { await _ensureFreshGate.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
            try
            {
                // Re-check under the gate — another caller may have refreshed
                // while we were waiting.
                last = _lastHeartbeatAtUtc;
                if (last != DateTime.MinValue && DateTime.UtcNow - last < maxAge)
                    return true;

                return await RefreshHeartbeatAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                try { _ensureFreshGate.Release(); } catch { /* disposed */ }
            }
        }

        private async void HeartbeatTick(object state)
        {
            // Timer callback runs on a threadpool thread. Use a reasonable per-tick
            // cancellation so a hung connection doesn't stack up multiple in-flight
            // heartbeats.
            bool ok = false;
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
            {
                try
                {
                    ok = await RefreshHeartbeatAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // RefreshHeartbeatAsync already logs, but swallow anything that escaped
                    // to guarantee the timer thread survives.
                    Logger.Log.Warn($"License: heartbeat tick swallowed exception: {ex.Message}");
                }
            }

            // Accelerated-retry schedule on transient failure. A heartbeat that
            // cleared the session (UnauthorizedException path) already called
            // RaiseStateChanged and cleared _current — we detect that by
            // checking whether there's still a session to retry FOR before
            // rescheduling. Without this, a network wobble at 09:00 meant the
            // next heartbeat was at 10:00 even though the session would have
            // revalidated fine two minutes later.
            if (ok)
            {
                if (Interlocked.Exchange(ref _consecutiveHeartbeatFailures, 0) > 0)
                    Logger.Log.Info("License: heartbeat recovered.");
                return;
            }

            // If the session was cleared (revocation), don't reschedule — the
            // re-login UX already handles it.
            bool hasSession;
            lock (_stateLock) { hasSession = _current != null; }
            if (!hasSession) return;

            int failures = Interlocked.Increment(ref _consecutiveHeartbeatFailures);
            TimeSpan? retryIn = null;
            switch (failures)
            {
                case 1: retryIn = TimeSpan.FromMinutes(5);  break;
                case 2: retryIn = TimeSpan.FromMinutes(15); break;
                case 3: retryIn = TimeSpan.FromMinutes(30); break;
                // ≥4 → fall through to the normal interval the timer is
                // already scheduled on; stop logging to avoid log-flood.
            }
            if (retryIn.HasValue)
            {
                Logger.Log.Warn(
                    $"License: heartbeat transient failure #{failures} — retrying in {retryIn.Value}.");
                // Change the timer's next due time without touching its period.
                // period = interval means a successful next tick resumes the
                // normal cadence automatically.
                var interval = GetHeartbeatInterval();
                Timer t;
                lock (_stateLock) { t = _heartbeatTimer; }
                try { t?.Change(retryIn.Value, interval); }
                catch (ObjectDisposedException) { /* app shutting down */ }
            }
        }

        private static TimeSpan GetHeartbeatInterval()
        {
            int hours = Config.Get("HeartbeatHours", (int)DefaultHeartbeatInterval.TotalHours);
            if (hours <= 0) hours = 6;
            if (hours > 72) hours = 72;
            return TimeSpan.FromHours(hours);
        }

        // ───────────────────────────────────────────────────────────────────
        //  Sign out
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Remove the activation from memory AND disk. Does not call any server
        /// endpoint (there is no /logout on the contract). The session JWT will
        /// still be valid server-side until its natural expiry, but no code on
        /// this device will present it.
        /// </summary>
        public void SignOut()
        {
            Logger.Log.Info("License: sign-out requested.");
            lock (_stateLock) { _current = null; }
            try { ActivationStore.Clear(); }
            catch (Exception ex) { Logger.Log.Warn($"License: sign-out clear failed: {ex.Message}"); }

            // Stop the heartbeat — no session to refresh.
            StopHeartbeatTimer();

            RaiseStateChanged();
        }

        private void StopHeartbeatTimer()
        {
            Timer t;
            lock (_stateLock)
            {
                t = _heartbeatTimer;
                _heartbeatTimer = null;
            }
            if (t != null)
            {
                try { t.Dispose(); }
                catch (Exception ex) { Logger.Log.Warn($"License: heartbeat dispose failed: {ex.Message}"); }
            }
        }

        // ───────────────────────────────────────────────────────────────────
        //  Helpers
        // ───────────────────────────────────────────────────────────────────

        private static string GetDeviceName()
        {
            try
            {
                string host = Environment.MachineName;
                if (string.IsNullOrWhiteSpace(host)) host = "unknown-device";
                if (host.Length > 64) host = host.Substring(0, 64);
                return host;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"License: could not read hostname: {ex.Message}");
                return "unknown-device";
            }
        }

        /// <summary>
        /// Called by any non-heartbeat API path that caught a 401 — signals
        /// that the server has revoked this device's session. Idempotent:
        /// subsequent calls while already-revoked no-op. Clears the
        /// in-memory activation and fires <see cref="ActivationStateChanged"/>
        /// so <c>App.OnActivationStateChanged</c> can surface the re-login
        /// dialog. Before this existed, a revocation between heartbeat ticks
        /// meant translation-pack sync / inventory /files calls silently
        /// 401'd every few seconds while the user kept playing, with no UX
        /// feedback until the next heartbeat (up to 15 min later).
        /// </summary>
        public void ReportRemoteRevocation(string reason)
        {
            if (_disposed) return;
            bool wasActive;
            lock (_stateLock)
            {
                wasActive = _current != null;
                if (wasActive) _current = null;
            }
            if (!wasActive) return;  // already cleared — don't thrash the dialog

            Logger.Log.Warn($"License: remote revocation reported ({reason ?? "(no reason)"}) — clearing session.");
            try { ActivationStore.Clear(); }
            catch (Exception ioEx) { Logger.Log.Warn($"License: could not clear activation after remote revocation: {ioEx.Message}"); }
            try { StopHeartbeatTimer(); } catch { /* best-effort */ }
            RaiseStateChanged();
        }

        private void RaiseStateChanged()
        {
            var handler = ActivationStateChanged;
            if (handler == null) return;

            // Marshal to WPF dispatcher if available so subscribers don't have to.
            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                try
                {
                    app.Dispatcher.BeginInvoke((Action)(() => SafeInvoke(handler)));
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Log.Warn($"License: dispatcher marshal failed: {ex.Message}");
                }
            }
            SafeInvoke(handler);
        }

        private void SafeInvoke(EventHandler handler)
        {
            try { handler.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Logger.Log.Warn($"License: ActivationStateChanged subscriber threw: {ex.Message}"); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopHeartbeatTimer();
        }
    }
}
