using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Globalization;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Security;
using GI_Subtitles.Services.Observability;
using static GI_Subtitles.Core.Config.Config;
using Velopack;
// NOTE: do NOT `using GI_Subtitles.Views;` — MainWindow.xaml.cs declares its own
// Logger class in that namespace, which collides with Common.Logger. View types
// are fully-qualified where needed.

namespace GI_Subtitles
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Process entry point. MUST be the first thing .NET calls — Velopack's
        /// installer/update machinery invokes the exe with hook args like
        /// <c>--veloapp-install</c>, <c>--veloapp-obsoleted</c>,
        /// <c>--veloapp-firstrun</c>, etc., and expects us to short-circuit
        /// *before* any WPF/UI state is materialised. <see cref="VelopackApp.Build"/>
        /// parses those args, runs the matching hook, and either exits the process
        /// (for one-shot modes) or returns and lets us continue with normal startup.
        ///
        /// Declared here because <c>GI-Subtitles.csproj</c> uses
        /// <c>&lt;StartupObject&gt;GI_Subtitles.App&lt;/StartupObject&gt;</c> and
        /// switches <c>App.xaml</c>'s build action to <c>Page</c> so the XAML
        /// toolchain doesn't auto-generate a competing Main.
        /// </summary>
        [STAThread]
        public static void Main(string[] args)
        {
            // Hooks intentionally minimal — all heavy lifting stays in OnStartup.
            // Logger isn't wired up yet at this point (that happens in OnStartup
            // via log4net config), so we don't log from here. If a Velopack hook
            // throws, the exception bubbles up and the installer reports it.
            VelopackApp.Build().Run();

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }

        private static Mutex _mutex = null;

        /// <summary>
        /// Process-wide licensing service. Constructed in <see cref="OnStartup"/> before
        /// any window ships. MainWindow / SettingsWindow read it directly — it's the
        /// source of truth for "is this install activated?" across the whole app.
        /// </summary>
        public static LicenseService LicenseService { get; private set; }

        /// <summary>Coarse startup progress flag the Dashboard observes while the
        /// initial translation pack is still being fetched on the background thread
        /// kicked off from <see cref="KickOffInitialDictionarySync"/>. Stays
        /// <see cref="InitialStartupStatus.DownloadingTranslations"/> until the task
        /// finishes (success OR failure — the UI must not sit on "Downloading…"
        /// forever if the network is dead).</summary>
        public enum InitialStartupStatus
        {
            /// <summary>Background sync hasn't been started for this process yet.</summary>
            Idle,
            /// <summary>Background sync is in flight — Dashboard shows a "please wait" state.</summary>
            DownloadingTranslations,
            /// <summary>Background sync finished (or was skipped because the account can't download paid packs).</summary>
            Ready,
        }

        /// <summary>Backing field for <see cref="StartupStatus"/>. Stored as an int so
        /// we can mark it <c>volatile</c> — C# doesn't let enum-typed fields use
        /// volatile directly, but enum-int round-trip is lossless. Writes come from
        /// both the UI thread (initial call in <c>OnStartup</c>) and the background
        /// thread (the sync task's <c>finally</c> that flips back to Ready). Without
        /// a memory barrier, a UI-thread reader could see a stale value indefinitely
        /// on architectures with relaxed ordering — volatile gives us the acquire
        /// semantics we need on read.</summary>
        private static volatile int _startupStatus = (int)InitialStartupStatus.Idle;

        /// <summary>Current startup progress state. UI code reads this to gate the
        /// Start button. Always safe to read from any thread.</summary>
        public static InitialStartupStatus StartupStatus => (InitialStartupStatus)_startupStatus;

        /// <summary>Fires on the UI thread whenever <see cref="StartupStatus"/> changes.
        /// SettingsWindow subscribes to refresh the Dashboard button + status badge.</summary>
        public static event EventHandler StartupStatusChanged;

        /// <summary>Handle to the background sync task kicked off from
        /// <see cref="KickOffInitialDictionarySync"/>. Non-null once activation has
        /// completed and a sync has been attempted; null on fresh boots before
        /// activation or when the app started without a license session. MainWindow's
        /// own post-load sync observes this instead of launching a duplicate.
        /// Assigned BEFORE <see cref="SetStartupStatus"/> flips to
        /// DownloadingTranslations so any reader seeing that state is guaranteed to
        /// find a non-null task to await.</summary>
        public static Task InitialSyncTask { get; private set; }

        private static int _translationUpdateRequiresRestart;
        private static string _translationUpdateSummary = string.Empty;

        /// <summary>True when startup replaced language data that had already
        /// been loaded by an earlier Kaption session. The Settings window uses
        /// this to offer a safe restart after the background update completes.</summary>
        public static bool TranslationUpdateRequiresRestart =>
            Volatile.Read(ref _translationUpdateRequiresRestart) != 0;

        public static string TranslationUpdateSummary =>
            Volatile.Read(ref _translationUpdateSummary) ?? string.Empty;

        /// <summary>
        /// Path to the per-user crash log, written directly (unbuffered) by
        /// the exception handlers below so evidence survives process death.
        /// </summary>
        private static readonly string CrashLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kaption", "crash.log");

        /// <summary>
        /// Write a fatal-crash entry directly to disk without going through log4net
        /// buffering. Used from unhandled-exception paths where the logger itself
        /// may be compromised, and from the migration code (low-frequency).
        /// Safe to call with null/garbage — swallows its own errors.
        ///
        /// Does NOT dedupe — the unhandled-exception handlers route through
        /// <see cref="ReportUnhandledException"/> which deduplicates once
        /// across all sinks. Direct callers (migration logging) write
        /// unconditionally.
        /// </summary>
        private const long CrashLogMaxBytes = 10L * 1024 * 1024; // 10 MB

        private static void WriteCrashLogDirect(string kind, string details)
        {
            WriteCrashLogDirectWithPrefix(kind, details, suppressionPrefix: null);
        }

        private static void WriteCrashLogDirectWithPrefix(string kind, string details, string suppressionPrefix)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath) ?? ".");

                // Size guard: truncate if the file has grown past the cap.
                // Crash logs are diagnostic, not forensic — a recent tail
                // beats a runaway file.
                try
                {
                    var info = new FileInfo(CrashLogPath);
                    if (info.Exists && info.Length > CrashLogMaxBytes)
                    {
                        File.WriteAllText(CrashLogPath,
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] LOG_RESET: previous file exceeded {CrashLogMaxBytes / (1024 * 1024)} MB, truncated.\n");
                    }
                }
                catch { /* best-effort */ }

                if (!string.IsNullOrEmpty(suppressionPrefix))
                {
                    File.AppendAllText(CrashLogPath,
                        $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DEDUPE: {suppressionPrefix}\n");
                }
                File.AppendAllText(CrashLogPath,
                    $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {kind}: {details}\n");
            }
            catch { /* best-effort — don't let crash logging itself throw */ }
        }

        /// <summary>
        /// Centralised unhandled-exception sink. Routes a single dedupe
        /// decision across all three reporting paths (log4net file appender,
        /// crash.log direct write, GlitchTip upload) so a repeating exception
        /// can only spam each sink once per dedupe window.
        ///
        /// Pre-CrashSpamGuard, a WPF layout exception that fired on every
        /// Measure pass could write 200 MB of identical stack traces in
        /// seconds across all three sinks. Now: first occurrence writes,
        /// subsequent occurrences within 60 s increment a counter, and the
        /// next distinct exception (or window expiry) flushes the count
        /// as a single "previous entry repeated N more time(s)" prefix.
        /// </summary>
        private static void ReportUnhandledException(
            string crashKind, string logPrefix, Exception ex, string sentryContextTag)
        {
            string details = ex?.ToString() ?? "(null)";
            string fingerprint = CrashSpamGuard.Fingerprint(crashKind, details);
            if (!CrashSpamGuard.ShouldWrite(fingerprint, out string suppressionPrefix))
                return;

            try
            {
                if (suppressionPrefix != null)
                    Logger.Log.Warn($"DEDUPE: {suppressionPrefix}");
                Logger.Log.Error($"{logPrefix}: {details}");
            }
            catch { /* logger may be torn down */ }

            WriteCrashLogDirectWithPrefix(crashKind, details, suppressionPrefix);

            try { CrashReportingService.ReportException(ex, contextTag: sentryContextTag); }
            catch { /* GlitchTip ingest failures shouldn't propagate */ }

            FlushLog4Net();
        }

        /// <summary>
        /// Multi-slot dedupe helper for repeating-exception storms. A WPF
        /// layout exception fires on every Measure pass — left unchecked it
        /// can produce hundreds of MB of identical stack traces in seconds
        /// across crash.log + app.log + GlitchTip uploads. This guard tracks
        /// the last few distinct fingerprints and suppresses re-emission
        /// within a short window, surfacing a "[N more times]" summary when
        /// the next distinct entry comes through.
        ///
        /// Lock-based (a single coarse lock around the small array). Crash
        /// logging is low-frequency by design — a few calls per second at
        /// most — so contention is a non-issue and the simpler design wins
        /// over a lock-free implementation.
        /// </summary>
        internal static class CrashSpamGuard
        {
            private const int SlotCount = 8;
            private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
            private static readonly object _lock = new object();
            private static readonly Slot[] _slots = new Slot[SlotCount];

            private struct Slot
            {
                public string Fingerprint;
                public DateTime LastSeenUtc;
                public int SuppressedCount;
            }

            /// <summary>
            /// Build a stable signature: kind + topmost stack frame (or, if
            /// there's no stack trace, the first 200 chars of <paramref
            /// name="details"/>). Stable enough that the same crash repeating
            /// produces the same fingerprint, distinct enough that two
            /// different crashes don't collide.
            /// </summary>
            public static string Fingerprint(string kind, string details)
            {
                if (string.IsNullOrEmpty(details))
                    return kind ?? "(null)";

                int atIdx = details.IndexOf("\n   at ", StringComparison.Ordinal);
                string slice;
                if (atIdx >= 0)
                {
                    int eol = details.IndexOf('\n', atIdx + 1);
                    slice = eol > atIdx
                        ? details.Substring(atIdx, eol - atIdx)
                        : details.Substring(atIdx);
                }
                else
                {
                    int len = Math.Min(details.Length, 200);
                    slice = details.Substring(0, len);
                }
                return (kind ?? "(null)") + "::" + slice;
            }

            /// <summary>
            /// Returns true when the caller should write this entry. False
            /// means "duplicate within window — suppress". On true,
            /// <paramref name="suppressionPrefix"/> is non-null when there
            /// are pending suppressed counts to flush before the new line.
            /// </summary>
            public static bool ShouldWrite(string fingerprint, out string suppressionPrefix)
            {
                suppressionPrefix = null;
                if (string.IsNullOrEmpty(fingerprint))
                    return true; // can't dedupe without a key

                DateTime now = DateTime.UtcNow;
                lock (_lock)
                {
                    int oldestIdx = 0;
                    DateTime oldestTime = DateTime.MaxValue;

                    for (int i = 0; i < SlotCount; i++)
                    {
                        if (_slots[i].Fingerprint == fingerprint)
                        {
                            // Existing slot: inside window → suppress.
                            // Outside window → flush count and re-arm.
                            if ((now - _slots[i].LastSeenUtc) < Window)
                            {
                                _slots[i].SuppressedCount++;
                                _slots[i].LastSeenUtc = now;
                                return false;
                            }
                            if (_slots[i].SuppressedCount > 0)
                            {
                                suppressionPrefix =
                                    $"previous entry repeated {_slots[i].SuppressedCount} more time(s) within the {Window.TotalSeconds:N0}s window";
                            }
                            _slots[i].LastSeenUtc = now;
                            _slots[i].SuppressedCount = 0;
                            return true;
                        }

                        if (_slots[i].LastSeenUtc < oldestTime)
                        {
                            oldestTime = _slots[i].LastSeenUtc;
                            oldestIdx = i;
                        }
                    }

                    // Not in any slot — evict the LRU. If the evicted slot
                    // has pending suppressions, surface them on the new
                    // entry so the count isn't silently lost.
                    if (_slots[oldestIdx].SuppressedCount > 0
                        && _slots[oldestIdx].Fingerprint != null)
                    {
                        suppressionPrefix =
                            $"evicted entry [{Truncate(_slots[oldestIdx].Fingerprint, 80)}] had {_slots[oldestIdx].SuppressedCount} pending suppression(s)";
                    }
                    _slots[oldestIdx].Fingerprint = fingerprint;
                    _slots[oldestIdx].LastSeenUtc = now;
                    _slots[oldestIdx].SuppressedCount = 0;
                    return true;
                }
            }

            private static string Truncate(string s, int max) =>
                string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>
        /// Force any buffered log4net appenders to flush pending entries to disk.
        /// The file appender is already configured with <c>immediateFlush=true</c>
        /// (see app.config), so this is primarily a safety net for buffering-style
        /// appenders such as the in-memory Logs-tab appender. log4net 2.0.17
        /// doesn't expose a public <c>IFlushable</c> interface, so we only handle
        /// the well-known <see cref="log4net.Appender.BufferingAppenderSkeleton"/>
        /// base type — any derived buffering appenders in our process (there are
        /// none currently, but future additions like AdoNet/Smtp would be covered).
        /// </summary>
        private static void FlushLog4Net()
        {
            try
            {
                foreach (var appender in log4net.LogManager.GetRepository().GetAppenders())
                {
                    if (appender is log4net.Appender.BufferingAppenderSkeleton buf)
                        buf.Flush();
                }
            }
            catch { /* swallow — logger state may be torn down already */ }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Suspend WPF's auto-shutdown while we drive pre-MainWindow dialogs
            // (LoginWindow + crash-reporting MessageBox). With the default
            // ShutdownMode=OnLastWindowClose, each dialog close takes
            // Application.Windows.Count to zero — no MainWindow exists yet
            // because the StartupUri loads AFTER OnStartup returns — so WPF
            // queues a Shutdown(). Then DoStartup tries to LoadComponent
            // Views/MainWindow.xaml and throws InvalidOperationException
            // "Application is shutting down". Restored below before
            // base.OnStartup so MainWindow-close terminates the app normally.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // One-shot migration: older builds let users pick CHS as source language
            // but no Chinese PaddleOCR model ships, so it silently fell back to EN.
            // Reset stranded configs so the Translations tab doesn't show a no-pill
            // selection state after the CHS option is removed from the UI.
            try
            {
                if (string.Equals(Config.Get("Input", "EN"), "CHS", StringComparison.OrdinalIgnoreCase))
                {
                    Config.Set("Input", "EN");
                }
            }
            catch { /* best-effort migration */ }

            // Process-lifecycle heartbeat. Correlates user bug reports with log files
            // when multiple instances have run.
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            Logger.Log.Info($"=== Process start: PID={pid}, version={System.Reflection.Assembly.GetExecutingAssembly().GetName().Version} ===");
            GI_Subtitles.Core.Runtime.RamDiag.LogCheckpoint("process start");
            GI_Subtitles.Core.Runtime.RamDiag.StartBackgroundMonitor();

            // Loud, unmissable notice when a developer flag is active in a shipped
            // build. DevSkipGameGate bypasses the "target game must be running"
            // gate on every OCR-start path. It exists so contributors can test
            // the pipeline without launching Genshin, not so resellers can fork
            // the binary and ship it without the gate. Logging here means the
            // flag cannot hide silently in a rebrand fork — the warning lands
            // on disk on every boot and reaches crash-reporting correlation.
            try
            {
                if (Config.Get("DevSkipGameGate", false))
                {
                    Logger.Log.Warn("DevSkipGameGate=true — game-running OCR gate is disabled. This is a developer/testing flag; disable it for end-user installs.");
                }
            }
            catch { /* best-effort warn */ }

            // Crash reporting. Reads Config["CrashReportingEnabled"] +
            // Config["SentryDsn"] and starts the Sentry SDK (pointed at our
            // GlitchTip instance — Sentry-API-compatible) only when both are
            // set. Kept before exception handlers so they can forward
            // unconditionally; the service checks consent internally.
            CrashReportingService.Initialize();

            // Ensure final flush on normal process exit so any trailing logs
            // land on disk AND any queued crash events reach GlitchTip. Sentry
            // batches events in memory; without a Shutdown() the last crash
            // before a clean quit never actually leaves the machine.
            AppDomain.CurrentDomain.ProcessExit += (s, a) =>
            {
                Logger.Log.Info($"=== Process exit (normal): PID={pid} ===");
                try { CrashReportingService.Shutdown(); } catch { /* best-effort */ }
                FlushLog4Net();
            };

            // Global exception handlers — log, notify user, prevent crash where possible.
            // Each handler writes to BOTH the structured log (via Logger) AND a direct
            // unbuffered crash.log so evidence survives even if log4net state is broken.
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                string msg = ex?.ToString() ?? args.ExceptionObject?.ToString() ?? "Unknown error";
                // Log to main log first (immediateFlush=true means this lands on disk).
                try { Logger.Log.Error($"FATAL unhandled exception (terminating={args.IsTerminating}): {msg}"); } catch { }
                // Then unbuffered direct write — guaranteed even if logger is broken.
                WriteCrashLogDirect(args.IsTerminating ? "FATAL" : "UNHANDLED", msg);
                // Forward to CrashReportingService → Sentry SDK → GlitchTip.
                // Service checks consent + DSN presence internally; when either
                // is missing it writes a local [CRASH-REPORT:*] log line and
                // returns without touching the network.
                try { CrashReportingService.ReportException(ex, contextTag: args.IsTerminating ? "fatal" : "unhandled"); } catch { }
                FlushLog4Net();
                if (args.IsTerminating)
                {
                    try
                    {
                        // Branded dialog; pops up at the top of the screen so
                        // the user sees it even if Kaption's own windows are
                        // already being torn down by the OS. The technical
                        // details sit behind an expander so ordinary users
                        // aren't greeted with a stack trace.
                        GI_Subtitles.Views.ModernDialog.Error(
                            owner: null,
                            title: "Kaption needs to close",
                            body: $"An unexpected error forced Kaption to quit. A copy of the full report was saved to:\n{CrashLogPath}",
                            technicalDetails: ex?.ToString());
                    }
                    catch { }
                }
            };
            DispatcherUnhandledException += (s, args) =>
            {
                ReportUnhandledException("UI", "Unhandled UI exception", args.Exception, "ui");
                args.Handled = true; // Prevent crash — log and continue
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                ReportUnhandledException("TASK", "Unobserved task exception", args.Exception, "task");
                args.SetObserved(); // Prevent crash
            };

            string appDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            Directory.SetCurrentDirectory(appDir);
            const string appName = "Kaption";
            bool createdNew;

            _mutex = new Mutex(true, appName, out createdNew);

            if (!createdNew)
            {
                Process current = Process.GetCurrentProcess();
                foreach (var process in Process.GetProcessesByName(current.ProcessName).Where(p => p.Id != current.Id))
                {
                    process.Kill();
                }
            }

            // Register in-memory log appender for the Logs tab
            Logger.RegisterInMemoryAppender();

            // Bridge log4net → Sentry/GlitchTip. Every Logger.Log.Error /
            // Fatal call site is now automatically forwarded — including
            // exceptions swallowed in local try/catch blocks (e.g.
            // INotifyIcon.ChooseRegion) that previously never reached the
            // dashboard. Consent is checked per-Append against
            // CrashReportingService.IsEnabled, so the appender no-ops when
            // the user opted out. Must come AFTER CrashReportingService.Initialize.
            Logger.RegisterSentryAppender();

#if DEBUG
            // Debug builds: bump root level to DEBUG unconditionally so every
            // Logger.Log.Debug(...) call lands in app.log + the in-memory
            // Logs tab. Runs BEFORE ApplyConfiguredLevel so a Config.LogLevel
            // override (e.g. WARN while chasing one signal) still wins —
            // this is only the debug-build default, not a hard-code.
            {
                var repo = (log4net.Repository.Hierarchy.Hierarchy)log4net.LogManager.GetRepository();
                repo.Root.Level = log4net.Core.Level.Debug;
                repo.RaiseConfigurationChanged(System.EventArgs.Empty);
                Logger.Log.Info("Logger: DEBUG build — root level defaulted to DEBUG (Config.LogLevel still overrides).");
            }
#endif

            // Honour a user-set Config.LogLevel override (default is INFO
            // from app.config; advanced users can set DEBUG for diagnosis
            // without editing the bundled config file).
            Logger.ApplyConfiguredLevel();

            // Versioned Config.json migrations. Runs once per install
            // per version tick — safe to call on every launch, the
            // ConfigMigrationVersion pointer makes repeat runs a no-op.
            // Keep this AFTER logger setup so migration log lines land
            // in app.log, and BEFORE any UI component reads settings.
            GI_Subtitles.Core.Config.ConfigMigrations.RunAll();

            // Load UI language resources before MainWindow is created
            LoadUILanguageResources();

            // ── First-run legal acceptance (Step -1 in the install experience) ──
            // Velopack's installer is silent by design (delta updates reuse the
            // same bootstrapper, we don't want to re-ask on every patch). So
            // the EULA + Privacy Policy acceptance lives here, gated on a
            // version integer — bump EulaAcceptanceWindow.CurrentEulaVersion
            // when the text meaningfully changes and every user sees the
            // prompt again. This also rolls the crash-reporting opt-in into
            // the same dialog so we don't ask twice on a fresh install.
            if (!EnsureEulaAccepted())
            {
                Shutdown(2);
                return;
            }

            // ── Licensing gate (Step 0 in the install experience) ────────────
            // Must run BEFORE MainWindow shows. If the user has no activation on
            // file, OR their activation has hard-expired, block the main UI
            // behind the LoginWindow until they sign in (or quit).
            if (!EnsureActivated())
            {
                // User quit instead of signing in. Exit cleanly with non-zero
                // code so any parent process (installer, launcher) can tell the
                // app didn't start normally.
                Shutdown(1);
                return;
            }

            // First-run opt-in for crash reporting. Only activated users see
            // this prompt — matches the placement the task spec requires
            // ("AFTER activation gate so only activated users see it"). Note:
            // the EULA dialog above already handles the crash prompt on a
            // fresh install, so this is a safety net for users who completed
            // onboarding before the combined dialog existed.
            PromptCrashReportingIfFirstRun();

            // Attach user context so Sentry (next session) can correlate
            // crashes per-install without ever shipping PII.
            try
            {
                var activation = LicenseService?.CurrentActivation;
                if (activation != null)
                    CrashReportingService.SetUserContext(activation.ActivationId, activation.Email);
            }
            catch (Exception ex) { Logger.Log.Warn($"SetUserContext failed: {ex.Message}"); }

            // Per-device file-protection secret: foreground blocking fetch.
            // ServerKeyFileProtectionService is the only encryption path in
            // the source-available release — it depends on a 32-byte secret
            // issued per-device by /api/app/file-protection-key. We MUST
            // have one before any consumer of FileProtectionFactory is
            // constructed (matcher load, dictionary sync, settings window).
            //
            // If the user has cached one from a previous launch, this is an
            // instant no-op. Otherwise a modal "Preparing translations…"
            // dialog runs the fetch with 3 retries; failure shows Retry/Quit.
            //
            // We also wipe any leftover v1/v3 .gisub files (encrypted under
            // the retired AppSecret scheme) so the next sync re-encrypts
            // them under the per-device key.
            if (!EnsureFileProtectionSecret())
            {
                Shutdown(3);
                return;
            }
            WipeLegacyProtectedCacheFiles();

            // Kick off the first dictionary sync BEFORE MainWindow materialises so
            // the network round-trip runs in parallel with WPF window construction
            // and the OCR engine load. Previously the sync lived inside
            // MainWindow_Loaded behind a 10-second settle delay — meaning a user
            // on a fresh install could click Start before any Polish pack had
            // touched disk, and the matcher silently came up empty. Setting
            // StartupStatus to DownloadingTranslations here lets the Dashboard
            // render a "please wait" state the moment SettingsWindow shows.
            KickOffInitialDictionarySync();

            // Hand lifetime back to WPF's default. Just flipping the mode
            // doesn't trigger a shutdown — that only happens on a Window-close
            // event. Our pre-MainWindow dialogs are already closed, so this is
            // safe. From here on, closing MainWindow (the next window to
            // appear, via StartupUri) will exit the app normally.
            ShutdownMode = ShutdownMode.OnLastWindowClose;

            base.OnStartup(e);
        }

        /// <summary>
        /// Show the legal acceptance window if the user hasn't accepted the
        /// current <see cref="Views.EulaAcceptanceWindow.CurrentEulaVersion"/>.
        /// Returns true when the app may proceed, false when the user declined.
        ///
        /// Safe to call before LicenseService exists — the dialog is stateless
        /// and only touches Config. If anything goes wrong constructing or
        /// showing the window we log and fall through to the "not accepted"
        /// path; the user will see the dialog again next launch, which is
        /// better than silently bypassing a legal gate.
        /// </summary>
        private bool EnsureEulaAccepted()
        {
            try
            {
                if (Views.EulaAcceptanceWindow.IsAcceptanceCurrent())
                {
                    // Common path on every launch after the first.
                    return true;
                }

                Logger.Log.Info(
                    $"EULA gate: acceptance absent or older than v{Views.EulaAcceptanceWindow.CurrentEulaVersion}, prompting.");
                var win = new Views.EulaAcceptanceWindow();
                bool? ok = win.ShowDialog();
                if (ok != true || !win.Accepted)
                {
                    Logger.Log.Info("EULA gate: user declined or dismissed. Exiting.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"EULA gate failed to run: {ex}");
                // Don't let a buggy prompt let us through without consent —
                // fail closed.
                try
                {
                    GI_Subtitles.Views.ModernDialog.Error(
                        owner: null,
                        title: "Kaption couldn't start",
                        body: "We couldn't show the welcome screen. Please reinstall Kaption or contact contact@kaption.one.",
                        technicalDetails: ex.ToString());
                }
                catch { /* best effort */ }
                return false;
            }
        }

        /// <summary>
        /// If the user has not yet answered the crash-reporting prompt, show it
        /// once — the answer is persisted to <c>Config["CrashReportingEnabled"]</c>
        /// and the "asked" flag to <c>Config["CrashReportingPromptShown"]</c>. The
        /// dialog defaults to "No thanks" (opt-in, not opt-out) to match the body
        /// text and standard EU expectations.
        /// </summary>
        private void PromptCrashReportingIfFirstRun()
        {
            try
            {
                // Sentinel: if the user has already seen the dialog once, don't
                // re-ask. They can change their mind in Settings → General.
                if (Config.Get("CrashReportingPromptShown", false))
                    return;

                // Branded dialog, centered on screen with an upward nudge so
                // the user notices it at first launch. Primary/Secondary are
                // explicit verbs — "Yes/No" reads as a nagging question,
                // "Help Kaption/Not now" reads as a friendly invite. The
                // decision maps to a boolean:
                //   primary    → CrashReportingEnabled = true
                //   secondary/ → CrashReportingEnabled = false (explicit,
                //   dismiss      not default)
                bool optedIn = GI_Subtitles.Views.ModernDialog.Confirm(
                    owner: null,
                    title: "Help improve Kaption",
                    body: "Kaption can send anonymous crash reports when something goes wrong, so bugs get fixed faster. No personal data, no screen content — just the error and your OS version.",
                    primaryText: "Help Kaption",
                    secondaryText: "Not now",
                    severity: GI_Subtitles.Views.DialogSeverity.Question,
                    details: "You can change this any time from Settings \u203A Options.");
                Config.Set("CrashReportingEnabled", optedIn);
                Config.Set("CrashReportingPromptShown", true);
                Config.Set("CrashReportingPromptShownAtUnix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                Logger.Log.Info($"Crash reporting prompt: user chose {(optedIn ? "opt-in" : "opt-out")}.");

                // Re-read consent in the scaffold so in-process exceptions are
                // already routed correctly on this run.
                CrashReportingService.RefreshConsent();
            }
            catch (Exception ex)
            {
                // First-run prompt failure is non-fatal — default to disabled,
                // don't mark the prompt as shown so it re-runs next launch.
                Logger.Log.Warn($"Crash reporting first-run prompt failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensure the app has a valid license session before MainWindow runs.
        /// Returns true if the app is activated (existing or freshly), false if the
        /// user declined to sign in. Never throws — licensing failures are logged
        /// and surfaced via the LoginWindow UI.
        /// </summary>
        private bool EnsureActivated()
        {
            try
            {
                LicenseService = new LicenseService();
            }
            catch (Exception ex)
            {
                // Should be impossible — LicenseService ctor swallows load failures.
                Logger.Log.Error($"Could not construct LicenseService: {ex}");
                LicenseService = null;
                GI_Subtitles.Views.ModernDialog.Error(
                    owner: null,
                    title: "Kaption couldn't start",
                    body: "We couldn't initialise Kaption's licensing layer. Reinstalling the app usually resolves this.",
                    technicalDetails: ex.ToString());
                return false;
            }

            if (LicenseService.IsActivated)
            {
                Logger.Log.Info("Activation check: existing session valid, skipping login window.");
            }
            else
            {
                Logger.Log.Info($"Activation check: need sign-in (hard-expired={LicenseService.IsHardExpired}).");
                try
                {
                    var login = new Views.LoginWindow(LicenseService);
                    bool? ok = login.ShowDialog();
                    if (ok != true)
                    {
                        Logger.Log.Info("Activation: user declined sign-in, shutting down.");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log.Error($"LoginWindow failed: {ex}");
                    GI_Subtitles.Views.ModernDialog.Error(
                        owner: null,
                        title: "Couldn't open the sign-in window",
                        body: "Kaption couldn't show the sign-in window. Please close Kaption and launch it again. If the problem persists, reinstalling usually fixes it.",
                        technicalDetails: ex.ToString());
                    return false;
                }
            }

            // Start the background heartbeat + listen for runtime state changes.
            try
            {
                LicenseService.StartHeartbeatTimer();
                LicenseService.ActivationStateChanged += OnActivationStateChanged;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Could not start heartbeat timer: {ex.Message}");
            }

            return true;
        }

        /// <summary>
        /// Set <see cref="StartupStatus"/> and marshal the change event onto the UI
        /// thread. Safe to call from anywhere. Uses <c>Interlocked.Exchange</c> so
        /// the "did it actually change" check is atomic relative to other writers —
        /// without it, two background threads racing to flip to Ready could both
        /// fire the event (harmless but wasteful). Subscribers run via the WPF
        /// dispatcher so they never see a half-updated state.
        /// </summary>
        private static void SetStartupStatus(InitialStartupStatus next)
        {
            int prev = Interlocked.Exchange(ref _startupStatus, (int)next);
            if (prev == (int)next) return; // already in this state — no event needed

            var app = Current;
            if (app == null)
            {
                try { StartupStatusChanged?.Invoke(null, EventArgs.Empty); } catch { /* subscriber bug — not ours */ }
                return;
            }

            if (app.Dispatcher.CheckAccess())
            {
                try { StartupStatusChanged?.Invoke(null, EventArgs.Empty); } catch { /* subscriber bug — not ours */ }
            }
            else
            {
                try { app.Dispatcher.BeginInvoke((Action)(() => StartupStatusChanged?.Invoke(null, EventArgs.Empty))); }
                catch (Exception ex) { Logger.Log.Warn($"StartupStatusChanged marshal failed: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Serializes <see cref="KickOffInitialDictionarySync"/> against itself so
        /// a re-activation race (session revoked + re-login fires OnActivationStateChanged
        /// while an older sync is still in flight) can't end up with two overlapping
        /// syncs fighting over the manifest file. The re-entrancy check is
        /// cheap — just CAS the flag. The field is also the place where we
        /// latch the "a sync has ever been attempted" state.
        /// </summary>
        private static int _initialSyncInFlight; // 0 = idle, 1 = running

        /// <summary>
        /// Foreground blocking fetch of the per-device file-protection secret.
        /// Returns true if a usable secret is available (already cached, or
        /// freshly fetched), false if the user chose to quit. The factory
        /// in <see cref="FileProtectionFactory.Create"/> throws if no secret
        /// is provisioned, so this gate must not be skipped before any
        /// encryption consumer is built.
        ///
        /// Fast paths (no UI):
        ///   * No activation yet → return false; activation gate ran before
        ///     this and would have exited already, so this is defensive.
        ///   * Activation already has a non-expired secret → return true.
        ///   * No DeviceSessionJwt to authenticate the call → return false
        ///     (re-login on next launch).
        ///
        /// Slow path: show <see cref="FileProtectionBootstrapWindow"/> as
        /// modal pre-MainWindow dialog. The window owns the retry loop,
        /// progress UI, and Retry/Quit buttons; it returns DialogResult=true
        /// on success, false on user quit.
        /// </summary>
        private bool EnsureFileProtectionSecret()
        {
            try
            {
                var activation = LicenseService?.CurrentActivation;
                if (activation == null)
                {
                    Logger.Log.Warn("EnsureFileProtectionSecret: no activation — refusing to proceed.");
                    return false;
                }
                if (activation.HasDeviceFileProtectionSecret)
                {
                    Logger.Log.Info(
                        "EnsureFileProtectionSecret: cached secret present, fast path — no dialog. " +
                        $"(scheme={activation.DeviceFileProtectionSchemeVersion}, " +
                        $"iters={activation.DeviceFileProtectionPbkdf2Iterations}, " +
                        $"expires={activation.DeviceFileProtectionExpiresAtUnixMs})");
                    return true;
                }
                if (string.IsNullOrEmpty(activation.DeviceSessionJwt))
                {
                    Logger.Log.Warn("EnsureFileProtectionSecret: no DeviceSessionJwt — re-login required.");
                    return false;
                }

                Logger.Log.Info(
                    "EnsureFileProtectionSecret: no cached secret — showing modal bootstrap dialog. " +
                    $"(secretLen={activation.DeviceFileProtectionSecret?.Length ?? 0}, " +
                    $"scheme={activation.DeviceFileProtectionSchemeVersion}, " +
                    $"iters={activation.DeviceFileProtectionPbkdf2Iterations})");

                var dialog = new Views.FileProtectionBootstrapWindow(
                    LicenseService, activation.DeviceSessionJwt);
                bool? result = dialog.ShowDialog();

                // Verify the secret actually landed — robust against the
                // bootstrap returning success-but-not-persisted (e.g., disk
                // save failure that LicenseService.SetFileProtectionSecret
                // logs as Warn but reports as success). The factory throws
                // on missing secret, which produces an opaque crash on
                // SettingsWindow ctor — surface it loudly here instead.
                bool ok = result == true;
                bool secretLanded = LicenseService?.CurrentActivation?.HasDeviceFileProtectionSecret == true;
                Logger.Log.Info(
                    $"EnsureFileProtectionSecret: dialog returned {ok}, " +
                    $"in-memory secret present={secretLanded}.");

                if (ok && !secretLanded)
                {
                    Logger.Log.Error(
                        "EnsureFileProtectionSecret: dialog claimed success but no secret in memory — " +
                        "treating as failure to avoid an opaque downstream crash.");
                    return false;
                }
                return ok;
            }
            catch (Exception ex)
            {
                Logger.Log.Error(
                    $"EnsureFileProtectionSecret: unexpected {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sweep the user's <c>%APPDATA%\Kaption</c> tree for legacy-format
        /// <c>.gisub</c> files (v1 = retired CBC AppSecret, v3 = retired CTR
        /// AppSecret) and delete them. Files written under the new server-key
        /// scheme (v2) are kept.
        ///
        /// Why we delete instead of re-encrypt: the v1/v3 AppSecret-based
        /// scheme is retired and there is no remaining code path that can
        /// decrypt files written under it. The cache rebuilds itself on
        /// next sync — translation packs re-download from R2, matcher
        /// blobs rebuild from the dialogue graph. One-time cost on the
        /// first launch of the source-available release; ~30 s of disk
        /// I/O + a download.
        ///
        /// Idempotent — a launch where every file is already v2 is a fast
        /// directory walk with no deletes. Best-effort: any IO failure logs
        /// a warning and the rest of OnStartup continues.
        /// </summary>
        private void WipeLegacyProtectedCacheFiles()
        {
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Kaption");
                if (!Directory.Exists(root))
                    return;

                int deleted = 0;
                int kept = 0;
                foreach (string path in Directory.EnumerateFiles(
                             root, "*.gisub", SearchOption.AllDirectories))
                {
                    try
                    {
                        bool isV3 = false;
                        try { isV3 = GI_Subtitles.Services.Security.ProtectedFileFormatV3.HasV3Header(path); }
                        catch { /* fall through; non-v3 magic is fine */ }

                        if (isV3)
                        {
                            File.Delete(path);
                            deleted++;
                            continue;
                        }

                        byte version = GI_Subtitles.Services.Security.ProtectedFileFormat.PeekFormatVersion(path);
                        if (version == GI_Subtitles.Services.Security.ProtectedFileFormat.HeaderVersion1Legacy)
                        {
                            File.Delete(path);
                            deleted++;
                            continue;
                        }

                        kept++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log.Warn(
                            $"WipeLegacyProtectedCacheFiles: failed on {path}: {ex.Message}");
                    }
                }

                if (deleted > 0)
                {
                    Logger.Log.Info(
                        $"WipeLegacyProtectedCacheFiles: deleted {deleted} legacy .gisub files, kept {kept} v2 files.");
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"WipeLegacyProtectedCacheFiles: top-level: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolve the default output (target) language for the initial sync. Today
        /// (v2.0, EN→PL) we always use Polish. If/when more target languages ship,
        /// the Setup Wizard writes <c>Config["Output"]</c> explicitly, so this
        /// fallback is only relevant for the gap between install and wizard
        /// completion. Keeping the hardcoded "PL" behind a helper makes the
        /// eventual swap (e.g. to OS-locale detection) a one-line change rather
        /// than a grep-and-replace across the codebase.
        /// </summary>
        private static string ResolveInitialOutputLanguage()
        {
            // `Config.Get("Output", "PL") ?? "PL"` matches the pattern used across
            // MainWindow + SettingsWindow: empty-string keys would otherwise slip
            // past the default-value parameter and make SyncAsync no-op.
            return Config.Get("Output", "PL") ?? "PL";
        }

        /// <summary>
        /// Check public game data and sync the licensed dictionary on a background
        /// thread so the network round-trips run alongside window construction.
        /// <see cref="StartupStatus"/> goes to
        /// <see cref="InitialStartupStatus.DownloadingTranslations"/> on entry and
        /// back to <see cref="InitialStartupStatus.Ready"/> on any exit path
        /// (success, skip, or failure) so the UI never sticks on "Downloading…"
        /// if the network is flaky. Safe to call again after a re-activation —
        /// an in-flight sync short-circuits the new call.
        /// </summary>
        private static void KickOffInitialDictionarySync()
        {
            // Single-flight: if an earlier sync is still running, leave it alone.
            // Re-activation (session revoked → re-login) hits this path; we'd
            // rather let the original sync finish and pick up the new entitlements
            // on the next relaunch than risk overlapping writes to the manifest.
            if (Interlocked.CompareExchange(ref _initialSyncInFlight, 1, 0) != 0)
            {
                Logger.Log.Info("InitialDictionarySync: already in flight, skipping re-kickoff.");
                return;
            }

            try
            {
                var license = LicenseService;
                string game = Config.Get("Game", "Genshin") ?? "Genshin";
                string inputLang = Config.Get("Input", "EN") ?? "EN";
                string outputLang = ResolveInitialOutputLanguage();
                if (string.IsNullOrWhiteSpace(game) ||
                    string.IsNullOrWhiteSpace(inputLang) ||
                    string.IsNullOrWhiteSpace(outputLang))
                {
                    SetStartupStatus(InitialStartupStatus.Ready);
                    Interlocked.Exchange(ref _initialSyncInFlight, 0);
                    return;
                }

                // Assign the task BEFORE flipping the status flag so any reader
                // that observes DownloadingTranslations is guaranteed to find a
                // non-null InitialSyncTask to await. (Publish-then-announce.)
                bool hadInput = GI_Subtitles.Services.Data.GameDataPaths.HasAnyTextMap(game, inputLang);
                bool hadOutput = GI_Subtitles.Services.Data.GameDataPaths.HasAnyTextMap(game, outputLang);
                var cts = CancellationToken.None;
                var startSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var task = Task.Run(async () =>
                {
                    // Do not let a very fast no-op/error path reach Ready before
                    // InitialSyncTask and DownloadingTranslations are published.
                    await startSignal.Task.ConfigureAwait(false);
                    var updatedExisting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        // Startup is the one place where we deliberately bypass the
                        // six-hour throttle. Conditional GET keeps an unchanged launch
                        // cheap while still discovering a language update immediately.
                        var updater = new GI_Subtitles.Services.Translation.GameDataUpdateService();
                        var updateResult = await updater.CheckAndUpdateAsync(
                            game, inputLang, outputLang, cts, forceCheck: true).ConfigureAwait(false);

                        foreach (var language in updateResult.Languages)
                        {
                            if (language.Outcome != GI_Subtitles.Services.Translation.GameDataUpdateOutcome.Updated)
                                continue;

                            bool existedBefore =
                                string.Equals(language.Language, inputLang, StringComparison.OrdinalIgnoreCase) ? hadInput :
                                string.Equals(language.Language, outputLang, StringComparison.OrdinalIgnoreCase) && hadOutput;
                            if (existedBefore)
                                updatedExisting.Add($"{language.Game}/{language.Language}");
                        }

                        if (license?.IsActivated == true)
                        {
                            var sync = new GI_Subtitles.Services.Translation.DictionarySyncService(
                                new GI_Subtitles.Services.Network.KaptionApiClient(),
                                license,
                                FileProtectionFactory.Create());
                            var syncResult = await sync.SyncAsync(game, outputLang, cts).ConfigureAwait(false);
                            Logger.Log.Info(
                                $"InitialDictionarySync: done — downloaded={syncResult.Downloaded} " +
                                $"upToDate={syncResult.UpToDate} skipped={syncResult.Skipped} failed={syncResult.Failed}");
                            if (syncResult.Downloaded > 0 && hadOutput)
                                updatedExisting.Add($"{game}/{outputLang}");
                        }

                        if (updatedExisting.Count > 0)
                        {
                            Volatile.Write(ref _translationUpdateSummary, string.Join(Environment.NewLine, updatedExisting));
                            Interlocked.Exchange(ref _translationUpdateRequiresRestart, 1);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Don't let a failed sync block startup — users can still
                        // use whatever's already in the local cache, and the
                        // Translations tab has a manual retry button.
                        Logger.Log.Warn($"InitialDictionarySync failed (non-fatal): {ex.Message}");
                    }
                    finally
                    {
                        // Clear the in-flight flag BEFORE flipping the public
                        // status — that way a subscriber that immediately calls
                        // KickOffInitialDictionarySync from its handler (e.g. to
                        // force a re-sync on network reconnect) won't trip the
                        // single-flight guard.
                        Interlocked.Exchange(ref _initialSyncInFlight, 0);
                        SetStartupStatus(InitialStartupStatus.Ready);
                    }
                });
                InitialSyncTask = task;
                SetStartupStatus(InitialStartupStatus.DownloadingTranslations);
                startSignal.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Could not schedule initial dictionary sync: {ex.Message}");
                Interlocked.Exchange(ref _initialSyncInFlight, 0);
                SetStartupStatus(InitialStartupStatus.Ready);
            }
        }

        /// <summary>
        /// Runtime state-change hook — currently fires on heartbeat success,
        /// heartbeat-detected revocation, and explicit sign-out. If the session
        /// has gone away (revoked by server), block the UI behind LoginWindow
        /// until the user signs in again.
        /// </summary>
        private void OnActivationStateChanged(object sender, EventArgs e)
        {
            // Marshal to UI thread so we can open a window.
            var app = Current;
            if (app == null) return;
            if (!app.Dispatcher.CheckAccess())
            {
                try { app.Dispatcher.BeginInvoke((Action)(() => OnActivationStateChanged(sender, e))); }
                catch (Exception ex) { Logger.Log.Warn($"ActivationStateChanged marshal failed: {ex.Message}"); }
                return;
            }

            var svc = LicenseService;
            if (svc == null) return;

            // Only act when the session has gone away — brief "still valid, just refreshed"
            // heartbeats also raise this event but don't require any UI.
            if (svc.IsActivated) return;

            Logger.Log.Warn("License state became invalid at runtime — prompting re-activation.");

            // Halt OCR BEFORE the modal dialog — ShowDialog runs a nested
            // dispatcher frame, which keeps DispatcherTimers (OCRTimer, UITimer)
            // firing in the background. Without this stop, a revoked user
            // would keep getting translations behind the re-login prompt.
            try { (app.MainWindow as Views.MainWindow)?.ForceStopOcr(); }
            catch (Exception ex) { Logger.Log.Warn($"ForceStopOcr on revoke failed: {ex.Message}"); }

            try
            {
                var login = new Views.LoginWindow(svc)
                {
                    Owner = app.MainWindow,
                };
                bool? ok = login.ShowDialog();
                if (ok != true)
                {
                    Logger.Log.Info("Re-activation declined by user, shutting down.");
                    Shutdown(1);
                }
                else
                {
                    // Heartbeat timer was stopped by SignOut; restart it for the
                    // newly-minted session.
                    svc.StartHeartbeatTimer();
                    // Entitlements / distribution key can differ from the prior
                    // session (user upgraded tier on the website mid-session, or
                    // switched accounts). Re-fire the initial sync so any newly
                    // unlocked paid packs land on disk without a relaunch.
                    // Single-flight guard inside the method protects against
                    // overlap with a still-running original sync.
                    KickOffInitialDictionarySync();
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Re-activation dialog failed: {ex}");
            }
        }

        /// <summary>
        /// Load UI language resources based on Config["UILang"] before any UI components are created
        /// </summary>
        private void LoadUILanguageResources()
        {
            try
            {
                string uiLang = Config.Get("UILang", "en-US");
                var culture = new CultureInfo(uiLang);
                CultureInfo.DefaultThreadCurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;

                // Remove the default resource dictionary from App.xaml if it exists.
                // The app.xaml currently ships zh-CN as the fallback — we strip it here
                // so the chosen locale is the one actually resolved at runtime.
                var defaultRd = this.Resources.MergedDictionaries
                    .FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("Strings.zh-CN.xaml"));
                if (defaultRd != null)
                {
                    this.Resources.MergedDictionaries.Remove(defaultRd);
                }

                // First-launch auto-detection: if the user hasn't chosen a UI language yet
                // AND the OS locale is Polish, default to pl-PL. User can change later in
                // Settings. We detect "not saved" by calling Get<string> without a default —
                // it returns null for absent keys. After this runs once, UILang is persisted.
                string savedUiLang = Config.Get<string>("UILang");
                if (string.IsNullOrEmpty(savedUiLang))
                {
                    var osLocale = System.Globalization.CultureInfo.CurrentUICulture.Name;
                    if (osLocale.StartsWith("pl", StringComparison.OrdinalIgnoreCase))
                    {
                        uiLang = "pl-PL";
                        Config.Set("UILang", uiLang);
                        try
                        {
                            culture = new CultureInfo(uiLang);
                            CultureInfo.DefaultThreadCurrentCulture = culture;
                            CultureInfo.DefaultThreadCurrentUICulture = culture;
                        }
                        catch { /* fall through to en-US */ }
                    }
                }

                // Load en-US as the BASE dictionary first so that any key missing from the
                // selected locale cleanly falls back to English instead of surfacing the
                // raw resource key in the UI. WPF walks MergedDictionaries in reverse
                // order for lookups, so whatever we add next wins over en-US whenever the
                // key is defined. This matters most for pl-PL (first non-en locale shipped
                // on the v2.0 timeline — not every string lands in the initial drop).
                var enBase = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Resources/Strings.en-US.xaml", UriKind.Absolute)
                };
                this.Resources.MergedDictionaries.Add(enBase);

                // Only overlay a second dictionary when the requested culture differs from
                // en-US. Duplicating the same dictionary would just waste memory.
                if (!string.Equals(uiLang, "en-US", StringComparison.OrdinalIgnoreCase))
                {
                    var rd = new ResourceDictionary();
                    switch (uiLang)
                    {
                        case "pl-PL":
                            rd.Source = new Uri("pack://application:,,,/Resources/Strings.pl-PL.xaml", UriKind.Absolute);
                            break;
                        case "ja-JP":
                            rd.Source = new Uri("pack://application:,,,/Resources/Strings.ja-JP.xaml", UriKind.Absolute);
                            break;
                        case "zh-CN":
                            rd.Source = new Uri("pack://application:,,,/Resources/Strings.zh-CN.xaml", UriKind.Absolute);
                            break;
                        default:
                            // Unknown culture — keep only en-US base.
                            rd = null;
                            break;
                    }
                    if (rd != null)
                    {
                        this.Resources.MergedDictionaries.Add(rd);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Failed to load UI language resources: {ex.Message}");
                try
                {
                    // en-US is the maintained source of truth — safest fallback.
                    var rd = new ResourceDictionary();
                    rd.Source = new Uri("pack://application:,,,/Resources/Strings.en-US.xaml", UriKind.Absolute);
                    this.Resources.MergedDictionaries.Add(rd);
                }
                catch
                {
                    Logger.Log.Error($"Failed to load fallback UI language resources: {ex.Message}");
                }
            }
        }
    }
}
