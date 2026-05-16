// ─────────────────────────────────────────────────────────────────────────────
//  CrashReportingService.cs
//  ---------------------------------------------------------------------------
//  Opt-in crash reporting. Wire-level target is GlitchTip (an open-source,
//  Sentry-API-compatible server), so we use the official Sentry .NET SDK and
//  just point its DSN at our GlitchTip instance — no custom protocol.
//
//  Why Sentry SDK against GlitchTip, not sentry.io:
//    * Zero code cost at swap time: change one DSN string.
//    * GlitchTip accepts the Sentry wire format byte-for-byte for the
//      features we actually use (exception capture, issue grouping,
//      breadcrumbs, release + environment tagging).
//    * No paid tier involved until the app genuinely outgrows 1 k events/month.
//
//  GlitchTip-specific divergence from sentry.io defaults (see StartSdk for the
//  exact option wiring + rationale comments):
//    * AutoSessionTracking = false — GlitchTip doesn't support the
//      session envelope. Leaving the Sentry default of true wastes quota.
//    * TracesSampleRate = 0 — we don't instrument transactions anywhere in
//      the WPF app; every event is an exception, so sample rate for
//      transactions is 0.
//
//  What this file actually does:
//    1. Initialize()      — reads consent + DSN from Config, calls SentrySdk.Init
//                            when the user opted in. No-op otherwise (never
//                            starts the SDK without explicit consent).
//    2. RefreshConsent()  — re-reads Config, starts or stops the SDK on the fly
//                            so toggling the Settings checkbox takes effect
//                            without a relaunch.
//    3. SetUserContext()  — attaches an anonymous user id (activation_id) so
//                            multiple crashes from one install group cleanly.
//                            Email is deliberately dropped — we never ship PII.
//    4. ReportException() — SentrySdk.CaptureException with a context tag,
//                            plus a local [CRASH-REPORT:*] log line so evidence
//                            survives even if the SDK's HTTP path is unhappy.
//    5. Shutdown()        — SentrySdk.CloseAsync with a 2 s flush window.
//
//  Privacy invariants (important — we committed to these in the opt-in copy):
//    * Never ship email, Windows username, machine name, or file paths.
//    * Never ship request bodies, Config values, or OCR text.
//    * SendDefaultPii is hard-coded false.
//    * User can flip the opt-in at any time; RefreshConsent() tears the
//      SDK down immediately.
//
//  Threading: Sentry's SDK is thread-safe by design. Our wrapper uses `volatile`
//  on primitive flags and a single _initLock around the start/stop handshake.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Reflection;
using System.Threading;
using GI_Subtitles.Common;
using GI_Subtitles.Core.Config;
using Sentry;

namespace GI_Subtitles.Services.Observability
{
    /// <summary>
    /// Process-wide, static access point for crash reporting. See file header
    /// for intended behaviour and the GlitchTip wire-format details.
    /// </summary>
    public static class CrashReportingService
    {
        // `volatile` guarantees writes from Initialize() are visible to the
        // exception-handler threads that read these fields. We don't need
        // lock-based synchronisation on reads because the worst case on a
        // race is a single duplicate log line, never a crash.
        private static volatile bool _initialized;
        private static volatile bool _enabled;
        private static volatile bool _sdkStarted;
        private static volatile string _userId;
        private static readonly object _initLock = new object();

        /// <summary>Default env name when Config["SentryEnvironment"] is not set.</summary>
        private const string DefaultEnvironment = "production";

        /// <summary>
        /// Baked-in GlitchTip DSN for the <c>kaption-desktop</c> project.
        /// DSNs are write-only tokens by design — an attacker who extracts
        /// this from the binary can only SEND events to our project, never
        /// read them. Treated as public, same trust model as a public
        /// analytics write-key. Users can override by setting
        /// <c>SentryDsn</c> in <c>Config.json</c> (enterprise: point at a
        /// private GlitchTip instance).
        ///
        /// If this DSN ever gets abused (flood from leaked binary): create a
        /// new one in the GlitchTip dashboard, deactivate the old one, swap
        /// the string here, ship a patch release. Old installs stop sending
        /// crashes until they update — acceptable cost.
        /// </summary>
        private const string DefaultDsn =
            "";

        /// <summary>
        /// True after <see cref="Initialize"/> has completed at least once.
        /// </summary>
        public static bool IsInitialized => _initialized;

        /// <summary>
        /// True when the user has opted in AND the SDK is actually running.
        /// Exception-handler call sites short-circuit on this before building
        /// any large payload — an opt-in with a missing DSN still ends up false.
        /// </summary>
        public static bool IsEnabled => _initialized && _enabled && _sdkStarted;

        /// <summary>
        /// One-shot startup. Reads the user's consent + DSN from Config, starts
        /// the Sentry SDK if both are present, and logs the outcome. Safe to
        /// call multiple times — subsequent calls go through
        /// <see cref="RefreshConsent"/> semantics (start or stop the SDK).
        /// </summary>
        public static void Initialize()
        {
            lock (_initLock)
            {
                try
                {
                    _enabled = Config.Get("CrashReportingEnabled", false);
                    // Fall back to the baked default when Config doesn't carry
                    // an explicit key. This is what makes a vanilla install
                    // report to our GlitchTip without any user action.
                    string dsn = Config.Get("SentryDsn", DefaultDsn);
                    bool hasDsn = !string.IsNullOrWhiteSpace(dsn);

                    if (_enabled && hasDsn)
                    {
                        StartSdk(dsn);
                        Logger.Log.Info(
                            "CrashReportingService initialised: consent=true, SDK started, " +
                            $"endpoint={SanitizeDsn(dsn)}");
                    }
                    else if (_enabled && !hasDsn)
                    {
                        // User opted in, but we don't have a DSN configured yet.
                        // This is the "beta without GlitchTip project yet" state —
                        // log explicitly so the mismatch isn't silent.
                        Logger.Log.Warn(
                            "CrashReportingService: user opted in, but SentryDsn is empty. " +
                            "No crashes will be reported until a DSN is configured.");
                    }
                    else
                    {
                        Logger.Log.Info(
                            $"CrashReportingService initialised: consent={_enabled}. SDK not started.");
                    }

                    _initialized = true;
                }
                catch (Exception ex)
                {
                    // Initialisation should NEVER crash the app. Log and carry on
                    // in a disabled state — users shouldn't lose their sessions
                    // because of an observability issue.
                    _enabled = false;
                    _sdkStarted = false;
                    _initialized = true;
                    Logger.Log.Warn($"CrashReportingService init failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Re-read the consent flag from Config. Call this whenever the user
        /// flips the Settings → General toggle so the change takes effect
        /// without a relaunch. Starts or stops the Sentry SDK on the fly.
        /// </summary>
        public static void RefreshConsent()
        {
            lock (_initLock)
            {
                try
                {
                    bool newValue = Config.Get("CrashReportingEnabled", false);
                    bool prev = _enabled;
                    _enabled = newValue;
                    if (prev != newValue)
                        Logger.Log.Info($"CrashReportingService consent changed: {prev} → {newValue}");

                    string dsn = Config.Get("SentryDsn", DefaultDsn);
                    bool hasDsn = !string.IsNullOrWhiteSpace(dsn);

                    if (_enabled && hasDsn && !_sdkStarted)
                    {
                        StartSdk(dsn);
                    }
                    else if ((!_enabled || !hasDsn) && _sdkStarted)
                    {
                        StopSdk();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log.Warn($"CrashReportingService.RefreshConsent failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Attach an anonymous user identifier (activation id, not email) so the
        /// backend can correlate multiple crashes from the same install without
        /// ever seeing PII. Email is accepted for API symmetry but is intentionally
        /// dropped — we never ship it to the crash backend.
        /// </summary>
        public static void SetUserContext(string userId, string email)
        {
            _userId = userId;

            Logger.Log.Debug($"CrashReportingService user context set: id={userId ?? "(null)"}");

            if (!_sdkStarted) return;

            try
            {
                // User shape is {Id} only — no Email, no Username, no IP. The
                // SDK respects SendDefaultPii=false so it won't try to enrich
                // this server-side either.
                SentrySdk.ConfigureScope(scope =>
                {
                    scope.User = new SentryUser { Id = userId };
                });
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"CrashReportingService.SetUserContext failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Report an exception. Runs on the caller's thread — intended for use
        /// from the three global exception handlers in <c>App.xaml.cs</c>. Safe
        /// to call before <see cref="Initialize"/>: early crashes are dropped
        /// silently rather than blocking termination.
        ///
        /// The local log-line with a [CRASH-REPORT:*] prefix is kept even when
        /// the SDK is running — that way bug reports that arrive via email
        /// (user sends us app.log) still contain the trace we need, and we can
        /// grep server-side post-mortem logs for the same tag.
        /// </summary>
        public static void ReportException(Exception ex, string contextTag = null)
        {
            if (ex == null) return;

            string tag = string.IsNullOrEmpty(contextTag) ? "unknown" : contextTag;

            try
            {
                // Always write the local forensic record — even when opted out,
                // the in-process log4net appender captures this for the user's
                // own app.log. That's what the Logs tab surfaces.
                Logger.Log.Error($"[CRASH-REPORT:{tag}] {ex.GetType().Name}: {ex.Message}\n{ex}");

                if (!IsEnabled) return;

                SentrySdk.CaptureException(ex, scope =>
                {
                    scope.SetTag("context", tag);
                    if (!string.IsNullOrEmpty(_userId))
                        scope.SetTag("activation_id", _userId);
                });
            }
            catch (Exception inner)
            {
                // Crash reporting itself MUST NOT crash. Swallow anything
                // that leaks out of the SDK/logger.
                try { Logger.Log.Warn($"CrashReportingService.ReportException failed: {inner.Message}"); }
                catch { /* truly last resort — give up */ }
            }
        }

        /// <summary>
        /// Flush any queued events and release SDK resources. Called from the
        /// process-exit handler in App.xaml.cs so we don't lose the last crash
        /// on the way out. The 2 s timeout matches Sentry's own recommendation
        /// for desktop apps — longer delays risk annoying users on clean shutdown.
        /// </summary>
        public static void Shutdown()
        {
            // Take the lock so a racing RefreshConsent can't call Close in
            // parallel (Sentry's SDK tolerates it but our _sdkStarted flag
            // should only flip under one owner).
            lock (_initLock)
            {
                try
                {
                    if (_sdkStarted)
                    {
                        // Synchronous Close blocks up to the SDK's configured
                        // ShutdownTimeout (default 2 s) while it drains the
                        // queue — that's the behavior we want on exit. We
                        // can't `await` in a ProcessExit handler anyway.
                        SentrySdk.Close();
                        _sdkStarted = false;
                        Logger.Log.Debug("CrashReportingService.Shutdown: SDK closed.");
                    }
                    else
                    {
                        Logger.Log.Debug("CrashReportingService.Shutdown: SDK was not running.");
                    }
                }
                catch (Exception ex)
                {
                    // Best-effort — we're already tearing down the process.
                    try { Logger.Log.Warn($"CrashReportingService.Shutdown failed: {ex.Message}"); }
                    catch { /* give up */ }
                }
            }
        }

        // ────────────────────────────────────────────────────────────────────
        //  Internals
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Kick off the Sentry SDK. Caller must hold <see cref="_initLock"/>.
        /// No-op if the SDK is already running (protects against a double
        /// <see cref="Initialize"/> re-initialising the transport and losing
        /// any in-flight events). Options chosen for a WPF desktop app with
        /// the privacy invariants documented in the file header.
        /// </summary>
        private static void StartSdk(string dsn)
        {
            if (_sdkStarted)
            {
                // Defensive: callers holding the lock shouldn't re-enter with
                // the SDK already on, but double-Initialize is a realistic
                // scenario if a caller bypasses RefreshConsent.
                return;
            }

            try
            {
                string release = GetReleaseString();
                string environment = Config.Get("SentryEnvironment", DefaultEnvironment);

                SentrySdk.Init(o =>
                {
                    o.Dsn = dsn;
                    o.Release = release;                  // e.g. "Kaption@2.0.0.0"
                    o.Environment = environment;          // "production" / "beta" / "dev"

                    // Privacy: never enrich with OS username, local IP, or
                    // request bodies. We opt-in only to what's already in
                    // the caught Exception.
                    o.SendDefaultPii = false;

                    // Stack traces: always include, even for non-throw paths
                    // (breadcrumbs/messages) — costs nothing at crash-report
                    // volumes and makes grouping heuristics much sharper.
                    o.AttachStacktrace = true;

                    // GlitchTip-specific: they explicitly don't support the
                    // session-tracking envelope shape ("auto_session_tracking
                    // — Set to false. GlitchTip does not support session
                    // tracking."). Leaving the Sentry default of `true` here
                    // sends extra envelopes GlitchTip drops server-side,
                    // wasting our free-tier quota. Disabled outright.
                    o.AutoSessionTracking = false;

                    // We don't instrument transactions anywhere in the
                    // desktop app — every event we ship is an exception.
                    // Setting the sample rate to 0 explicitly disables
                    // the SDK's opportunistic transaction collection so we
                    // never send a transaction envelope GlitchTip would
                    // either drop or count against the free-tier budget.
                    // GlitchTip docs recommend 0.01 for apps that *do*
                    // instrument; ours doesn't, so 0 is correct here.
                    o.TracesSampleRate = 0;

                    // Disable Sentry's own diagnostic logger — it pipes into
                    // System.Diagnostics.Trace by default, which we don't use,
                    // and it would double-log transport errors that already
                    // land via the SDK's regular `OnError` path.
                    o.Debug = false;

                    // Reasonable upper bounds — desktop apps rarely generate
                    // more than a handful of events per session.
                    o.MaxBreadcrumbs = 50;

                    // Redact exception messages before send: free-form .NET
                    // exception messages occasionally include absolute file
                    // paths (IOException) which can leak the Windows username
                    // (e.g. C:\Users\<name>\...). BeforeSend lets us scrub
                    // without turning off the rest of Sentry's enrichment.
                    o.SetBeforeSend((SentryEvent e, SentryHint _) =>
                    {
                        try { ScrubUserPath(e); }
                        catch { /* never block the send path on a scrub failure */ }
                        return e;
                    });
                });

                _sdkStarted = true;
            }
            catch (Exception ex)
            {
                _sdkStarted = false;
                Logger.Log.Warn($"CrashReportingService.StartSdk failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Replace the current Windows username in any free-form text on the
        /// event with a literal "<user>" marker. Covers the most common PII
        /// leak vector in .NET exception messages (IOException surfaces the
        /// full offending path). We deliberately do NOT run a general-purpose
        /// regex sweep — that would be a footgun on user-submitted content.
        /// </summary>
        private static void ScrubUserPath(SentryEvent e)
        {
            string userName = Environment.UserName;
            if (string.IsNullOrEmpty(userName)) return;
            string needle = $"\\{userName}\\";

            if (e.Message != null && !string.IsNullOrEmpty(e.Message.Message))
                e.Message.Message = e.Message.Message.Replace(needle, "\\<user>\\");

            if (e.SentryExceptions != null)
            {
                foreach (var ex in e.SentryExceptions)
                {
                    if (!string.IsNullOrEmpty(ex.Value))
                        ex.Value = ex.Value.Replace(needle, "\\<user>\\");
                }
            }
        }

        /// <summary>Close the SDK. Caller must hold <see cref="_initLock"/>.</summary>
        private static void StopSdk()
        {
            try
            {
                SentrySdk.Close();
                _sdkStarted = false;
                Logger.Log.Info("CrashReportingService: SDK stopped (consent withdrawn or DSN cleared).");
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"CrashReportingService.StopSdk failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Build the <c>Release</c> tag GlitchTip groups events by.
        /// Format follows Sentry's convention: <c>{project}@{version}</c>.
        /// </summary>
        private static string GetReleaseString()
        {
            try
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
                return $"Kaption@{v}";
            }
            catch
            {
                return "Kaption@unknown";
            }
        }

        /// <summary>
        /// Mask the key portion of a DSN before logging it. DSNs are designed
        /// to be public (anyone knowing it can only SEND events to the project,
        /// not read them), but we still avoid writing the full string to the
        /// Logs tab where it could end up in screenshots.
        /// </summary>
        private static string SanitizeDsn(string dsn)
        {
            if (string.IsNullOrEmpty(dsn)) return "(unset)";
            try
            {
                var u = new Uri(dsn);
                return $"{u.Scheme}://***@{u.Host}{u.AbsolutePath}";
            }
            catch
            {
                return "(set)";
            }
        }
    }
}
